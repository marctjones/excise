#!/usr/bin/env python3
"""report_gates.py — the reducer behind scripts/report-gates.sh (LOCAL_GATES.md).

    scripts/report-gates.sh [LOG_DIR|--latest] [--full] [--no-gh]

A pure reducer over one runner log directory. It runs no test, no script, no
dotnet; the ONLY subprocess it may spawn is `gh issue view N --json state,title`
(10 s timeout, one call per distinct #N; after the first network/timeout
failure the rest are left unverified). Under --no-gh no gh at all, but the
remembered verdicts in logs/runner-state/known-issues/<N>.rec are still read.

Inputs
  <LOG_DIR>/plan.tsv      what the runner promised. First line
                          '# tier=<t0|t1|full|t2> planned=<n> of=<m> only=<p|-> manifest=<16hex>'
                          then 10 tab-separated columns per row:
                          name kind target filter class knownIssue prereq prereqPolicy checkpoint ratchet.
                          LEGACY plans (no header, 4 columns name kind target filter) are
                          read as tier=full, planned=of; class/knownIssue come from tests/gates.tsv.
  <LOG_DIR>/ledger.jsonl  what happened: one JSON object per line (runner_ledger_record):
                          name status rc durationSeconds sha treeDirty config recorded and optionally
                          kind target filter log trx testsExecuted class knownIssue prereq reason
                          evidenceFrom evidenceFinished evidenceLog evidenceSha.
                          status in PASS FAIL FAIL_ZERO_TESTS SKIP_CHECKPOINTED SKIPPED NO_RESULT.
  tests/gates.tsv         only to (1) list every '#N' in the tier's plan for the STALE sweep and
                          (2) fill class/knownIssue for legacy rows (Foo.chunkNN resolves to Foo).
  the trx/log of failing rows (the '#N/Substring' qualifier), the GRADE artifacts (see the
  grade_* functions, which read exactly the sources in the spec's reportSources), the newest
  prior <other LOG_DIR>/report.json of the SAME tier (delta), logs/runner-state/known-issues/<N>.rec.

Row verdicts
  PASS -> PASS; SKIP_CHECKPOINTED -> PASS 'from checkpoint <evidenceFinished>'.
  FAIL / FAIL_ZERO_TESTS, class != GRADE: knownIssue '-' -> NEW; '#N' -> KNOWN while N is OPEN or
  unverified; '#N/Sub' -> KNOWN iff the qualifier matches (test/project/project-chunked rows: at
  least one and EVERY outcome="Failed" testName in the row's trx contains Sub; script rows: the
  row's log contains Sub), else NEW naming the unmatched (max 3 shown); N CLOSED (verified now, or
  remembered CLOSED in the .rec) -> STALE. STALE is evaluated for EVERY '#N' in the tier's plan,
  passing rows included. SKIPPED -> SKIPPED with reason (never affects exit). NO_RESULT, or any
  non-PASS on a GRADE row -> 'NO DATA' in the grade block, never affects exit. A plan row with no
  ledger row -> NOT RUN. Header planned<of -> PARTIAL. A ledger/plan name the manifest does not
  declare and whose row carries no class -> 'not in manifest' (informational).

Exit codes
  2 no/unreadable ledger or plan; else 1 if any NEW or STALE; else 3 if any NOT RUN (an
  interrupted run never reads green); else 0.

Known-issue memory
  Every SUCCESSFUL gh answer is written to logs/runner-state/known-issues/<N>.rec as lines
  issue=N / state=OPEN|CLOSED / title=... / verified=<ISO8601Z> / --CKPT-OK-- (tmp+rename; trusted
  only when the sentinel is the last line and a state line exists). A .rec remembering CLOSED makes
  the row STALE even when gh is unreachable.

Output
  A <=20-line summary (header, VERDICT + class tally, only the non-PASS rows capped with
  '+N more (--full)', one IMPROVE line, the GRADES block, one footer); --full appends every row.
  Every IMPROVE/GRADE number carries a delta vs the newest prior report.json of the same tier:
  '(=)' unchanged, '(Δ ±x)' moved, '(no prior)'. GRADE artifacts not produced inside this run's
  window print their own date and '(not from this run)'. The report WRITES <LOG_DIR>/report.json
  {tier,sha,started,finished,verdict,exit,counts,rows,grades,improve} and never treats its own
  file as the prior.

Environment
  EXCISE_GATES_ROOT=<dir>  repo root override (the selftest's hermetic temp root): the reducer
                           then reads <dir>/tests/gates.tsv, <dir>/logs/runner-state and every
                           other repo-relative artifact under <dir>. Default: the parent of scripts/.

Python 3 stdlib only.
"""

from __future__ import annotations

import glob
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

# ---------------------------------------------------------------------------
# Roots and constants
# ---------------------------------------------------------------------------

ROOT = Path(os.environ.get("EXCISE_GATES_ROOT") or Path(__file__).resolve().parents[1]).resolve()
MANIFEST = ROOT / "tests" / "gates.tsv"
STATE_DIR = ROOT / "logs" / "runner-state" / "known-issues"
LOG_DIR_PATTERNS = ("test-tier_*", "full-suite_*", "release-smoke_*")
MANIFEST_COLS = ["name", "class", "tiers", "kind", "target", "filter", "ratchet", "knownIssue",
                 "prereq", "prereqPolicy", "checkpoint", "oracle", "note"]
PLAN_COLS = ["name", "kind", "target", "filter", "class", "knownIssue", "prereq", "prereqPolicy",
             "checkpoint", "ratchet"]
LEGACY_PLAN_COLS = ["name", "kind", "target", "filter"]
TIER_RANK = {"t0": 0, "t1": 1, "full": 2}
SUMMARY_LINES = 20
GH_TIMEOUT_S = 10
REC_SENTINEL = "--CKPT-OK--"
CORPORA = [("verapdf", "veraPDF"), ("pdfjs", "pdf.js"), ("pdfium", "PDFium"), ("isartor", "Isartor")]
# GRADE lines of the summary, in order, and the plan row each one grades from.
GRADE_ROWS = {
    "conformance": None,  # corpus-scan-* rows, handled per corpus
    "extraction": "extraction-parity",
    "redaction": "redaction-bench",
    "render perf": "reference-performance",
    "annotations": "annotation-bench",
    "image codecs": "image-conformance",
    "bench design": "bench-design-coverage",
}
GRADE_ROW_NAMES = {v for v in GRADE_ROWS.values() if v} | {f"corpus-scan-{c}" for c, _ in CORPORA}
PASSING = ("PASS", "SKIP_CHECKPOINTED")
FAILING = ("FAIL", "FAIL_ZERO_TESTS")


class ReportError(Exception):
    """Unreadable ledger or plan: exit 2."""


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

_TS_RE = re.compile(r"^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?\s*(Z|[+-]\d{2}:?\d{2})?$")


def parse_ts(s):
    """ISO-8601 -> aware UTC datetime (tolerates 7-digit fractions and a missing zone)."""
    if not s or not isinstance(s, str):
        return None
    m = _TS_RE.match(s.strip())
    if not m:
        return None
    y, mo, d, h, mi, sec, frac, tz = m.groups()
    micro = int((frac or "0")[:6].ljust(6, "0"))
    dt = datetime(int(y), int(mo), int(d), int(h), int(mi), int(sec), micro)
    if tz and tz != "Z":
        sign = 1 if tz[0] == "+" else -1
        digits = tz[1:].replace(":", "")
        off = timedelta(hours=int(digits[:2]), minutes=int(digits[2:4]))
        dt = dt - sign * off
    return dt.replace(tzinfo=timezone.utc)


def file_utc(path):
    try:
        return datetime.fromtimestamp(Path(path).stat().st_mtime, tz=timezone.utc)
    except OSError:
        return None


def fmt_dur(seconds):
    s = int(round(seconds))
    if s < 60:
        return f"{s}s"
    if s < 3600:
        return f"{s // 60}m{s % 60:02d}s" if s % 60 else f"{s // 60}m"
    return f"{s // 3600}h{(s % 3600) // 60:02d}m"


def fmt_local(dt, with_date=True):
    loc = dt.astimezone()
    return loc.strftime("%Y-%m-%d %H:%M") if with_date else loc.strftime("%H:%M")


def rel(path):
    """Repo-relative rendering of a path when it is under ROOT."""
    try:
        return str(Path(path).resolve().relative_to(ROOT))
    except (ValueError, OSError):
        return str(path)


def read_text(path):
    try:
        return Path(path).read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None


def read_json(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


def to_int(v, default=None):
    try:
        return int(v)
    except (TypeError, ValueError):
        return default


def to_float(v, default=None):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def median(xs):
    xs = sorted(xs)
    n = len(xs)
    return xs[n // 2] if n % 2 else (xs[n // 2 - 1] + xs[n // 2]) / 2


def shorten(s, n):
    s = re.sub(r"\s+", " ", s or "").strip()
    return s if len(s) <= n else s[: n - 1] + "…"


def parse_known(known):
    """'#N' | '#N/Sub' | '-' -> (N or None, Sub or None)."""
    if not known or known == "-":
        return None, None
    m = re.match(r"^#(\d+)(?:/(.+))?$", known.strip())
    if not m:
        return None, None
    return m.group(1), m.group(2)


def base_name(name):
    return re.sub(r"\.chunk\d{2}$", "", name)


# ---------------------------------------------------------------------------
# Inputs: plan, ledger, manifest
# ---------------------------------------------------------------------------

def load_plan(log_dir):
    """plan.tsv -> {'tier','planned','of','only','manifest','legacy','rows':[dict]}."""
    path = Path(log_dir) / "plan.tsv"
    text = read_text(path)
    if text is None:
        raise ReportError(f"no plan: {path}")
    header = None
    rows = []
    for line in text.splitlines():
        if line.startswith("#"):
            m = re.match(r"#\s*tier=(\S+)\s+planned=(\d+)\s+of=(\d+)\s+only=(\S+)\s+manifest=(\S+)", line)
            if m and header is None:
                header = {"tier": m.group(1), "planned": int(m.group(2)), "of": int(m.group(3)),
                          "only": m.group(4), "manifest": m.group(5), "legacy": False}
            continue
        if not line.strip():
            continue
        cols = line.rstrip("\n").split("\t")
        if len(cols) >= len(PLAN_COLS):
            row = dict(zip(PLAN_COLS, cols))
        elif len(cols) >= 1:
            row = dict(zip(LEGACY_PLAN_COLS, cols + [""] * (4 - len(cols))))
            for c in PLAN_COLS[4:]:
                row[c] = None
        else:
            continue
        row["knownIssue"] = row.get("knownIssue") or None
        row["class"] = row.get("class") or None
        rows.append(row)
    if not rows:
        raise ReportError(f"plan has no rows: {path}")
    if header is None:
        header = {"tier": "full", "planned": len(rows), "of": len(rows), "only": "-",
                  "manifest": None, "legacy": True}
    header["rows"] = rows
    return header


def load_ledger(log_dir):
    """ledger.jsonl -> list of row dicts (last row per name wins, first-seen order)."""
    path = Path(log_dir) / "ledger.jsonl"
    text = read_text(path)
    if text is None:
        raise ReportError(f"no ledger: {path}")
    lines = [ln for ln in text.splitlines() if ln.strip()]
    if not lines:
        raise ReportError(f"ledger is empty: {path}")
    by_name = {}
    for i, ln in enumerate(lines):
        try:
            obj = json.loads(ln)
        except ValueError:
            if i == len(lines) - 1:
                print(f"report-gates: warning: torn last ledger line ignored: {path}", file=sys.stderr)
                continue
            raise ReportError(f"unreadable ledger line {i + 1}: {path}")
        if not isinstance(obj, dict) or "name" not in obj or "status" not in obj:
            raise ReportError(f"ledger line {i + 1} lacks name/status: {path}")
        by_name[obj["name"]] = obj
    return list(by_name.values())


def load_manifest(path=None):
    """tests/gates.tsv -> {name: row dict}. Missing manifest -> {} (reported, not fatal)."""
    text = read_text(path or MANIFEST)
    if text is None:
        return {}
    rows = {}
    seen_header = False
    for line in text.splitlines():
        if line.startswith("#") or not line.strip():
            continue
        cols = line.rstrip("\n").split("\t")
        if not seen_header:
            seen_header = True
            continue
        if len(cols) < len(MANIFEST_COLS):
            cols += ["-"] * (len(MANIFEST_COLS) - len(cols))
        row = dict(zip(MANIFEST_COLS, cols))
        rows[row["name"]] = row
    return rows


def manifest_lookup(manifest, name):
    """Row for a name; Foo.chunkNN resolves to Foo. None when undeclared."""
    return manifest.get(name) or manifest.get(base_name(name))


def tier_selects(tiers, tier):
    """Chain semantics: t0 ⊂ t1 ⊂ full; t2 only when listed."""
    for t in (tiers or "").split(","):
        t = t.strip()
        if t == tier:
            return True
        if tier in TIER_RANK and t in TIER_RANK and TIER_RANK[t] <= TIER_RANK[tier]:
            return True
    return False


# ---------------------------------------------------------------------------
# Known-issue verification (gh + .rec memory)
# ---------------------------------------------------------------------------

def read_rec(n):
    path = STATE_DIR / f"{n}.rec"
    text = read_text(path)
    if text is None:
        return None
    lines = text.splitlines()
    if not lines or lines[-1].strip() != REC_SENTINEL:
        return None
    rec = {}
    for ln in lines[:-1]:
        if "=" in ln:
            k, v = ln.split("=", 1)
            rec[k.strip()] = v.strip()
    if rec.get("state") not in ("OPEN", "CLOSED"):
        return None
    return rec


def write_rec(n, state, title):
    try:
        STATE_DIR.mkdir(parents=True, exist_ok=True)
        path = STATE_DIR / f"{n}.rec"
        tmp = STATE_DIR / f".{n}.rec.tmp{os.getpid()}"
        body = (f"issue={n}\nstate={state}\ntitle={title}\n"
                f"verified={datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')}\n{REC_SENTINEL}\n")
        with open(tmp, "w", encoding="utf-8") as fh:
            fh.write(body)
            fh.flush()
            os.fsync(fh.fileno())
        os.replace(tmp, path)
    except OSError as exc:
        print(f"report-gates: warning: could not write {n}.rec: {exc}", file=sys.stderr)


class IssueVerifier:
    """One gh call per distinct N; the .rec is memory for when gh cannot answer."""

    def __init__(self, use_gh):
        self.use_gh = use_gh
        self.gh_failed = False
        self.gh_failure = None
        self.checked = 0
        self.cache = {}

    def verify_issue(self, n):
        if n in self.cache:
            return self.cache[n]
        result = {"n": n, "state": None, "title": None, "source": None, "verified": None}
        if self.use_gh and not self.gh_failed:
            answer = self._ask_gh(n)
            if answer:
                result.update(answer)
                result["source"] = "gh"
                self.checked += 1
                write_rec(n, answer["state"], answer.get("title") or "")
        if result["state"] is None:
            rec = read_rec(n)
            if rec:
                result.update({"state": rec["state"], "title": rec.get("title"),
                               "source": "rec", "verified": rec.get("verified")})
        self.cache[n] = result
        return result

    def _ask_gh(self, n):
        env = dict(os.environ, GH_PROMPT_DISABLED="1", GH_NO_UPDATE_NOTIFIER="1", GH_PAGER="cat")
        try:
            proc = subprocess.run(["gh", "issue", "view", str(n), "--json", "state,title"],
                                  capture_output=True, text=True, timeout=GH_TIMEOUT_S,
                                  stdin=subprocess.DEVNULL, env=env,
                                  cwd=str(ROOT) if ROOT.is_dir() else None)
        except (OSError, subprocess.TimeoutExpired) as exc:
            self.gh_failed = True
            self.gh_failure = "timeout" if isinstance(exc, subprocess.TimeoutExpired) else str(exc)
            return None
        if proc.returncode != 0:
            err = (proc.stderr or "").strip()
            if re.search(r"could not resolve|not found|no issue", err, re.I):
                return None  # this issue only; gh itself is fine
            self.gh_failed = True
            self.gh_failure = shorten(err, 80) or f"rc={proc.returncode}"
            return None
        try:
            obj = json.loads(proc.stdout)
        except ValueError:
            self.gh_failed = True
            self.gh_failure = "unparseable gh output"
            return None
        state = str(obj.get("state") or "").upper()
        if state not in ("OPEN", "CLOSED"):
            return None
        return {"state": state, "title": str(obj.get("title") or "")}

    def label(self, n):
        r = self.cache.get(n) or self.verify_issue(n)
        if r["state"] is None:
            return "unverified"
        if r["source"] == "rec":
            when = (r.get("verified") or "")[:10]
            return f"{r['state']} (remembered {when})" if when else f"{r['state']} (remembered)"
        return r["state"]

    def footer(self):
        if not self.use_gh:
            return "--no-gh"
        if self.gh_failed:
            return f"gh unreachable, unverified ({self.gh_failure})" if self.gh_failure else "gh unreachable, unverified"
        n = self.checked
        return f"gh reachable, {n} issue{'s' if n != 1 else ''} checked"


# ---------------------------------------------------------------------------
# Qualifier ('#N/Substring') and row evidence
# ---------------------------------------------------------------------------

_TRX_RESULT_RE = re.compile(r"<UnitTestResult\b([^>]*)>")
_TRX_COUNTERS_RE = re.compile(r"<Counters\b([^>]*)/?>")


def trx_failed_tests(trx_path):
    """-> (failed test names or None when unreadable, counters dict)."""
    text = read_text(trx_path) if trx_path else None
    if text is None:
        return None, {}
    failed = []
    for m in _TRX_RESULT_RE.finditer(text):
        attrs = m.group(1)
        if 'outcome="Failed"' not in attrs:
            continue
        nm = re.search(r'testName="([^"]*)"', attrs)
        failed.append(nm.group(1) if nm else "?")
    counters = {}
    cm = _TRX_COUNTERS_RE.search(text)
    if cm:
        for k, v in re.findall(r'(\w+)="(\d+)"', cm.group(1)):
            counters[k] = int(v)
    return failed, counters


def log_tail_line(log_path):
    """The most telling last line of a log: the last FAIL/✗/error line, else the last non-empty."""
    text = read_text(log_path) if log_path else None
    if not text:
        return None
    lines = [ln.rstrip() for ln in text.splitlines() if ln.strip()]
    if not lines:
        return None
    for ln in reversed(lines[-40:]):
        if re.match(r"^\s*(FAIL|✗|ERROR|error:|FAILED)", ln):
            return ln.strip()
    return lines[-1].strip()


def short_test(name):
    parts = name.split(".")
    return ".".join(parts[-2:]) if len(parts) >= 2 else name


def qualifier_matches(kind, sub, trx_path, log_path):
    """-> (matched: bool, detail: str). Never vacuously true."""
    if kind in ("test", "project", "project-chunked"):
        failed, _ = trx_failed_tests(trx_path)
        if failed is None:
            return False, "no trx to check the qualifier against"
        if not failed:
            return False, "no failed test recorded in the trx (qualifier unverifiable)"
        unmatched = [t for t in failed if sub not in t]
        if unmatched:
            shown = ", ".join(short_test(t) for t in unmatched[:3])
            more = f" (+{len(unmatched) - 3})" if len(unmatched) > 3 else ""
            return False, f"{len(unmatched)} of {len(failed)} failed outside /{sub}: {shown}{more}"
        return True, f"{len(failed)} failed, all match /{sub}"
    text = read_text(log_path) if log_path else None
    if text is None:
        return False, "log missing (qualifier unverifiable)"
    if sub in text:
        return True, f"log matches /{sub}"
    return False, f"log does not contain /{sub}"


# ---------------------------------------------------------------------------
# Row classification
# ---------------------------------------------------------------------------

def row_paths(log_dir, led):
    """log/trx for a ledger row; checkpointed rows point at their evidence directory."""
    name = led["name"]
    log = led.get("log")
    trx = led.get("trx")
    if led.get("status") == "SKIP_CHECKPOINTED":
        ev = led.get("evidenceLog")
        if ev:
            ev_dir = Path(ev).parent
            log = ev
            trx = trx or str(ev_dir / f"{name}.trx")
        else:
            return None, None
    return (log or str(Path(log_dir) / f"{name}.log")), (trx or str(Path(log_dir) / f"{name}.trx"))


def classify_rows(plan, ledger, manifest, verifier, log_dir):
    """-> (rows in plan order + informational extras, counts dict, issue sweep info)."""
    tier = plan["tier"]
    led_by = {r["name"]: r for r in ledger}
    plan_names = [r["name"] for r in plan["rows"]]
    rows = []

    # The STALE sweep covers every #N the tier's plan cites: plan rows, ledger rows,
    # and the current manifest's rows for this tier (passing rows included).
    issues = {}
    for pr in plan["rows"]:
        n, _ = parse_known(pr.get("knownIssue"))
        if n:
            issues[n] = pr["name"]
    for lr in ledger:
        n, _ = parse_known(lr.get("knownIssue"))
        if n:
            issues.setdefault(n, lr["name"])
    for mr in manifest.values():
        if tier_selects(mr.get("tiers"), tier):
            n, _ = parse_known(mr.get("knownIssue"))
            if n:
                issues.setdefault(n, mr["name"])
    for n in sorted(issues, key=int):
        verifier.verify_issue(n)

    for pr in plan["rows"]:
        name = pr["name"]
        led = led_by.get(name)
        mrow = manifest_lookup(manifest, name)
        cls = (led or {}).get("class") or pr.get("class") or (mrow or {}).get("class")
        known = (led or {}).get("knownIssue") or pr.get("knownIssue") or (mrow or {}).get("knownIssue") or "-"
        kind = (led or {}).get("kind") or pr.get("kind") or (mrow or {}).get("kind") or "script"
        in_manifest = mrow is not None
        row = {"name": name, "class": cls or "-", "knownIssue": known, "kind": kind,
               "status": led.get("status") if led else None,
               "rc": to_int((led or {}).get("rc")), "duration": to_float((led or {}).get("durationSeconds"), 0.0),
               "log": None, "trx": None, "verdict": None, "issue": None, "issueLabel": None,
               "detail": "", "inManifest": in_manifest, "led": led, "notes": []}
        if led:
            row["log"], row["trx"] = row_paths(log_dir, led)
        if not in_manifest and cls is None:
            row["class"] = "-"
            row["verdict"] = "INFO"
            st = led.get("status") if led else "NOT RUN"
            row["detail"] = f"not in manifest (status {st}) — the current tests/gates.tsv declares no such row"
            rows.append(row)
            continue
        if not in_manifest:
            row["notes"].append("not in manifest")
        n, sub = parse_known(known)
        issue = verifier.cache.get(n) if n else None
        if n:
            row["issue"] = n
            row["issueLabel"] = verifier.label(n)
        status = row["status"]
        if led is None:
            row["verdict"] = "NOT RUN"
            row["detail"] = "no ledger row — the run stopped before this step (resume it)"
        elif status in PASSING:
            row["verdict"] = "PASS"
            if status == "SKIP_CHECKPOINTED":
                row["detail"] = f"from checkpoint {led.get('evidenceFinished') or '?'}"
            if n and issue and issue["state"] == "CLOSED":
                row["verdict"] = "STALE"
                row["detail"] = (f"cites {known} but #{n} is CLOSED — a passing row carrying a closed issue: "
                                 f"drop the knownIssue in tests/gates.tsv")
        elif status == "SKIPPED":
            row["verdict"] = "SKIPPED"
            row["detail"] = led.get("reason") or "prerequisite missing (policy=skip)"
        elif status == "NO_RESULT" or cls == "GRADE":
            row["verdict"] = "NO DATA"
            row["detail"] = f"{status} rc={row['rc']} (GRADE never blocks; {rel(row['log']) if row['log'] else 'no log'})"
        elif status in FAILING:
            _classify_failure(row, n, sub, issue, verifier, known)
        else:
            row["verdict"] = "NEW"
            row["detail"] = f"unknown status {status!r}"
        rows.append(row)

    # Ledger rows the plan never listed (informational).
    for lr in ledger:
        if lr["name"] in plan_names:
            continue
        rows.append({"name": lr["name"], "class": lr.get("class") or "-", "knownIssue": lr.get("knownIssue") or "-",
                     "kind": lr.get("kind") or "?", "status": lr.get("status"), "rc": to_int(lr.get("rc")),
                     "duration": to_float(lr.get("durationSeconds"), 0.0), "log": lr.get("log"), "trx": lr.get("trx"),
                     "verdict": "INFO", "issue": None, "issueLabel": None,
                     "detail": f"not in plan (status {lr.get('status')}) — recorded but never planned",
                     "inManifest": manifest_lookup(manifest, lr["name"]) is not None, "led": lr, "notes": []})

    # STALE for #N cited only by the manifest/tier (no plan row carries it) — surface once.
    cited = {r["issue"] for r in rows if r.get("issue")}
    for n, src in issues.items():
        r = verifier.cache.get(n)
        if r and r["state"] == "CLOSED" and n not in cited:
            rows.append({"name": src, "class": "-", "knownIssue": f"#{n}", "kind": "?", "status": None,
                         "rc": None, "duration": 0.0, "log": None, "trx": None, "verdict": "STALE",
                         "issue": n, "issueLabel": verifier.label(n),
                         "detail": f"tests/gates.tsv cites #{n} on {src} but it is CLOSED — drop or replace the knownIssue",
                         "inManifest": True, "led": None, "notes": []})

    counts = {k: 0 for k in ("new", "known", "stale", "skipped", "notRun", "checkpointed", "pass", "noData", "info")}
    for r in rows:
        v = r["verdict"]
        if v == "NEW":
            counts["new"] += 1
        elif v == "KNOWN":
            counts["known"] += 1
        elif v == "STALE":
            counts["stale"] += 1
        elif v == "SKIPPED":
            counts["skipped"] += 1
        elif v == "NOT RUN":
            counts["notRun"] += 1
        elif v == "PASS":
            counts["pass"] += 1
        elif v == "NO DATA":
            counts["noData"] += 1
        elif v == "INFO":
            counts["info"] += 1
        if r["status"] == "SKIP_CHECKPOINTED":
            counts["checkpointed"] += 1
    return rows, counts, issues


def _classify_failure(row, n, sub, issue, verifier, known):
    led = row["led"]
    kind = row["kind"]
    evidence = ""
    if kind in ("test", "project", "project-chunked"):
        failed, counters = trx_failed_tests(row["trx"])
        if failed:
            shown = ", ".join(short_test(t) for t in failed[:3])
            more = f" (+{len(failed) - 3})" if len(failed) > 3 else ""
            evidence = f"{len(failed)} failed: {shown}{more}"
            if counters.get("passed") is not None:
                evidence += f" ({counters.get('passed', 0)} passed, {counters.get('notExecuted', 0)} skipped)"
        elif row["status"] == "FAIL_ZERO_TESTS":
            evidence = "zero tests executed (a filter matching nothing is a failure, not a pass)"
        else:
            evidence = shorten(log_tail_line(row["log"]) or "no failed test in the trx and no log line", 100)
    else:
        evidence = shorten(log_tail_line(row["log"]) or "no log", 110)
    if n is None:
        row["verdict"] = "NEW"
        row["detail"] = f"{evidence}  ← no knownIssue: fix it, or file the issue and cite it in tests/gates.tsv"
        return
    if issue and issue["state"] == "CLOSED":
        row["verdict"] = "STALE"
        row["detail"] = f"{evidence} — cites #{n}, which is CLOSED: the acceptance has expired"
        return
    if sub is None:
        row["verdict"] = "KNOWN"
        row["detail"] = evidence
        return
    matched, why = qualifier_matches(kind, sub, row["trx"], row["log"])
    if matched:
        row["verdict"] = "KNOWN"
        row["detail"] = why if kind in ("test", "project", "project-chunked") else f"{why}: {evidence}"
    else:
        row["verdict"] = "NEW"
        row["detail"] = f"{why} — {known} does not cover this failure"


# ---------------------------------------------------------------------------
# Run window, prior report, deltas
# ---------------------------------------------------------------------------

def run_window(ledger):
    """(start, end): first step's start (recorded - duration) and the last recorded."""
    starts, ends = [], []
    for r in ledger:
        t = parse_ts(r.get("recorded"))
        if not t:
            continue
        ends.append(t)
        starts.append(t - timedelta(seconds=to_float(r.get("durationSeconds"), 0.0) or 0.0))
    if not ends:
        return None, None
    return min(starts), max(ends)


def find_prior_report(tier, log_dir):
    """Newest report.json of the same tier under ROOT/logs, excluding this LOG_DIR's own."""
    own = (Path(log_dir) / "report.json").resolve()
    best, best_key = None, None
    for pat in LOG_DIR_PATTERNS:
        for d in glob.glob(str(ROOT / "logs" / pat)):
            p = Path(d) / "report.json"
            if not p.is_file() or p.resolve() == own:
                continue
            obj = read_json(p)
            if not isinstance(obj, dict) or obj.get("tier") != tier:
                continue
            key = (obj.get("finished") or "", str(p))
            if best_key is None or key > best_key:
                best, best_key = obj, key
    return best


def delta(cur, prior, fmt="{:+.4g}"):
    """cur/prior: dict name->number. '(=)' | '(Δ …)' | '(no prior)'."""
    if not cur:
        return ""
    if prior is None:
        return "(no prior)"
    parts = []
    for k, v in cur.items():
        pv = prior.get(k) if isinstance(prior, dict) else None
        if not isinstance(v, (int, float)) or not isinstance(pv, (int, float)):
            continue
        if abs(v - pv) > 1e-9:
            parts.append((k, v - pv))
    if not parts and all(isinstance(prior.get(k), (int, float)) for k in cur if isinstance(cur[k], (int, float))):
        return "(=)"
    if not parts:
        return "(no prior)"
    if len(cur) == 1:
        return "(Δ " + fmt.format(parts[0][1]) + ")"
    return "(Δ " + ", ".join(f"{k} {fmt.format(d)}" for k, d in parts) + ")"


def stamp_label(when, start, end, this_run_text):
    """'[<this_run_text>]' inside the run window, else '[<date> (not from this run)]'."""
    if when and start and end and start <= when <= end + timedelta(minutes=5):
        return this_run_text, True
    if when:
        return f"{when.strftime('%Y-%m-%d')} (not from this run)", False
    return "undated (not from this run)", False


# ---------------------------------------------------------------------------
# GRADE extractors — each returns {'text','values','label','nodata'}
# ---------------------------------------------------------------------------

def _row_map(rows):
    return {r["name"]: r for r in rows}


def artifact_dir(rows_by, name, log_dir):
    """Where a row's artifacts live: the evidence directory for a checkpointed row, else LOG_DIR."""
    r = rows_by.get(name)
    if r and r["status"] == "SKIP_CHECKPOINTED" and r.get("log"):
        return Path(r["log"]).parent
    return Path(log_dir)


def _grade_row_nodata(rows_by, name):
    """A grade row that ran but did not PASS -> reason text, else None."""
    r = rows_by.get(name)
    if r is None:
        return None
    if r["verdict"] in ("PASS",):
        return None
    if r["verdict"] == "NOT RUN":
        return f"{name} NOT RUN"
    if r["verdict"] == "SKIPPED":
        return f"{name} SKIPPED: {shorten(r['detail'], 80)}"
    return f"{name} {r['status']} rc={r['rc']} (GRADE never blocks; {rel(r['log']) if r['log'] else 'no log'})"


_SCAN_RE = re.compile(r"excise behaves correctly on (\d+)/(\d+) \((\d+\.\d)%\)")


def grade_conformance(log_dir, rows_by):
    parts, values, oracle_note, any_found = [], {}, None, False
    for key, disp in CORPORA:
        name = f"corpus-scan-{key}"
        r = rows_by.get(name)
        log = r["log"] if r else str(Path(log_dir) / f"{name}.log")
        text = read_text(log) if log else None
        m = _SCAN_RE.findall(text) if text else None
        if r is None or r["verdict"] == "NOT RUN":
            parts.append(f"{disp} NO DATA (not run)")
            continue
        if not m:
            parts.append(f"{disp} NO DATA (no agreement line)")
            continue
        ok, total, pct = m[-1]
        any_found = True
        values[key] = float(pct)
        parts.append(f"{disp} {ok}/{total} {pct}%")
        if oracle_note is None and text:
            oracle_note = "5 oracles" if "extra-oracles=all" in text else "3 oracles"
    if not any_found:
        return {"text": "NO DATA — no corpus-scan-* agreement line in this run", "values": {}, "label": "", "nodata": True}
    return {"text": "  ".join(parts), "values": values, "label": f"{oracle_note or '? oracles'}; this run", "nodata": False}


def grade_registry():
    path = ROOT / "test-pdfs" / "manifests" / "pdf-spec-registry" / "generated" / "capability-scorecard.md"
    text = read_text(path)
    if text is None:
        return f"registry NO DATA — {rel(path)} missing — measures paperwork, not code (milestone RC22)"
    header, overall = None, None
    for ln in text.splitlines():
        if ln.startswith("| Area"):
            header = [c.strip() for c in ln.strip().strip("|").split("|")]
        elif ln.startswith("| overall |"):
            overall = [c.strip() for c in ln.strip().strip("|").split("|")]
    if not header or not overall:
        return f"registry NO DATA — no '| overall |' row in {rel(path)} — measures paperwork, not code (milestone RC22)"
    cells = dict(zip(header, overall))
    strict = cells.get("Strict", "?")
    unknown = cells.get("Unknown", "?")
    target = cells.get("Target modes", "?")
    return f"registry strict {strict} ({unknown}/{target} modes unknown) — measures paperwork, not code (milestone RC22)"


def grade_extraction(start, end, rows_by):
    nodata = _grade_row_nodata(rows_by, "extraction-parity")
    baseline_path = ROOT / "tests" / "extraction-parity" / "baseline.json"
    baseline = read_json(baseline_path)
    latest_path = ROOT / "logs" / "extraction-parity" / "latest-report.json"
    latest = read_json(latest_path)
    floor, floor_date = None, None
    if isinstance(baseline, dict):
        pages = baseline.get("pages") or {}
        vals = [to_float(p.get("coverageFloor")) for p in (pages.values() if isinstance(pages, dict) else pages)
                if isinstance(p, dict)]
        vals = [v for v in vals if v is not None]
        floor = min(vals) if vals else None
        bt = parse_ts(baseline.get("generatedUtc"))
        floor_date = bt.strftime("%Y-%m-%d") if bt else "undated"
    src, label = None, None
    lt = parse_ts(latest.get("generatedUtc")) if isinstance(latest, dict) else None
    if isinstance(latest, dict) and lt and start and end and start <= lt <= end + timedelta(minutes=5):
        src, label = latest, f"this run; floor {rel(baseline_path)} {floor_date}"
    elif isinstance(baseline, dict):
        src, label = baseline, f"baseline {rel(baseline_path)} {floor_date} (not from this run)"
    if src is None:
        why = nodata or f"neither {rel(latest_path)} (this run) nor {rel(baseline_path)}"
        return {"text": f"NO DATA — {why}", "values": {}, "label": "", "nodata": True}
    cov = to_float(src.get("aggregateCoverage"))
    pages = to_int(src.get("pageCount"), 0)
    values = {"aggregateCoverage": round(cov, 4) if cov is not None else None, "pageCount": pages, "worstFloor": floor}
    text = f"{cov:.4f} of mutool's letters over {pages} pages" if cov is not None else f"? over {pages} pages"
    if floor is not None:
        text += f", worst floor {floor:.3f}"
    if nodata:
        label = f"{label}; {nodata}"
    return {"text": text, "values": {"aggregateCoverage": values["aggregateCoverage"]}, "label": label,
            "nodata": False, "extra": values}


def _last_json_lines(path, n=2):
    text = read_text(path)
    if not text:
        return []
    out = []
    for ln in reversed([l for l in text.splitlines() if l.strip()]):
        try:
            out.append(json.loads(ln))
        except ValueError:
            continue
        if len(out) == n:
            break
    return out


def grade_redaction(log_dir, start, end, rows_by, prior_values):
    nodata = _grade_row_nodata(rows_by, "redaction-bench")
    local = artifact_dir(rows_by, "redaction-bench", log_dir) / "redaction-bench-history.jsonl"
    history = ROOT / "tests" / "redaction-bench-history.jsonl"
    if local.is_file():
        entries, src_label, from_run = _last_json_lines(local, 2), "redaction-bench, this run", True
    else:
        entries = _last_json_lines(history, 2)
        src_label, from_run = None, False
    if not entries:
        why = nodata or f"no {rel(local)} and no {rel(history)}"
        return {"text": f"NO DATA — {why}", "values": {}, "label": "", "nodata": True}
    cur = entries[0]
    metrics = cur.get("metrics") or {}
    sf = metrics.get("securityFidelity") or {}
    sg = metrics.get("securityGrade") or {}

    def tool(name):
        s = to_float((sf.get(name) or {}).get("secure"))
        g = (sg.get(name) or {}).get("overall") or "?"
        return s, g

    ex_s, ex_g = tool("excise")
    n = to_int((sf.get("excise") or {}).get("n"), 0)
    peers = [("iText", "itext"), ("PyMuPDF", "pymupdf"), ("raster", "raster")]
    peer_txt = " · ".join(f"{disp} {tool(k)[0]:.3f} {tool(k)[1]}" if tool(k)[0] is not None else f"{disp} ?"
                          for disp, k in peers)
    ts = parse_ts(cur.get("timestamp"))
    if from_run:
        label = src_label
    else:
        label = f"redaction-bench history {ts.strftime('%Y-%m-%d') if ts else 'undated'} (not from this run)"
    text = f"secure {ex_s:.3f} {ex_g}  vs {peer_txt}   n={n}" if ex_s is not None else f"secure ? {ex_g}  vs {peer_txt}   n={n}"
    values = {"exciseSecure": round(ex_s, 3) if ex_s is not None else None}
    # Δ: the prior report.json when it exists, else the previous history entry.
    dtxt = ""
    if prior_values is not None:
        dtxt = delta(values, prior_values, "{:+.3f}")
    elif len(entries) > 1 and ex_s is not None:
        prev = to_float((((entries[1].get("metrics") or {}).get("securityFidelity") or {}).get("excise") or {}).get("secure"))
        pts = parse_ts(entries[1].get("timestamp"))
        if prev is not None:
            dtxt = f"(Δ {ex_s - prev:+.3f} vs {pts.strftime('%Y-%m-%d') if pts else 'previous entry'})"
    if not dtxt:
        dtxt = "(no prior)"
    if nodata:
        label = f"{label}; {nodata}"
    return {"text": f"{text}  {dtxt}", "values": values, "label": label, "nodata": False, "delta_done": True}


def grade_render_perf(log_dir, start, end, rows_by):
    nodata = _grade_row_nodata(rows_by, "reference-performance")
    local = artifact_dir(rows_by, "reference-performance", log_dir) / "reference-performance" / "reference-performance.json"
    data, when, from_run = None, None, False
    if local.is_file():
        data = read_json(local)
        when = parse_ts((data or {}).get("generatedUtc")) or file_utc(local)
        from_run = True
    else:
        best = None
        for p in glob.glob(str(ROOT / "logs" / "reference-performance" / "*" / "reference-performance.json")):
            obj = read_json(p)
            if not isinstance(obj, dict):
                continue
            t = parse_ts(obj.get("generatedUtc")) or file_utc(p)
            if best is None or (t and t > best[0]):
                best = (t, obj)
        if best:
            when, data = best
    if not isinstance(data, dict) or not isinstance(data.get("runs"), list):
        why = nodata or f"no {rel(local)} and no logs/reference-performance/*/reference-performance.json"
        return {"text": f"NO DATA — {why}", "values": {}, "label": "", "nodata": True}
    ratios, rss, fixtures = {}, [], set()
    for run in data["runs"]:
        if run.get("status") != "OK":
            continue
        ex = run.get("exciseCli") or {}
        if ex.get("status") != "OK" or not to_float(ex.get("elapsedMs")):
            continue
        fixtures.add(run.get("fixture"))
        for ref in run.get("references") or []:
            if ref.get("status") != "OK" or not to_float(ref.get("elapsedMs")):
                continue
            ratios.setdefault(ref.get("name"), []).append(to_float(ex["elapsedMs"]) / to_float(ref["elapsedMs"]))
            if ref.get("name") == "mutool" and to_float(ex.get("peakWorkingSetBytes")) and to_float(ref.get("peakWorkingSetBytes")):
                rss.append(to_float(ex["peakWorkingSetBytes"]) / to_float(ref["peakWorkingSetBytes"]))
    if not ratios:
        return {"text": "NO DATA — reference-performance.json has no status-OK runs", "values": {}, "label": "", "nodata": True}
    disp = {"mutool": "mutool", "pdftocairo": "pdftocairo", "ghostscript": "gs", "pdfbox": "pdfbox"}
    order = ["mutool", "pdftocairo", "ghostscript", "pdfbox"] + sorted(k for k in ratios if k not in disp)
    walls = []
    values = {}
    for k in order:
        if k in ratios:
            med = median(ratios[k])
            values[f"wall_{k}"] = round(med, 2)
            walls.append(f"×{med:.1f} {disp.get(k, k)}")
    text = f"wall {' / '.join(walls)} (median of {len(fixtures)} fixtures)"
    if rss:
        rm = median(rss)
        values["rss_mutool"] = round(rm, 2)
        text += f"; RSS ×{rm:.1f} mutool"
    gate = (data.get("regressionGate") or {}).get("passed")
    text += f"; regressionGate {'PASS' if gate else 'FAIL' if gate is False else '?'}"
    label, _ = stamp_label(when, start, end, "this run") if from_run else stamp_label(when, None, None, "")
    if nodata:
        label = f"{label}; {nodata}"
    return {"text": text, "values": values, "label": label, "nodata": False}


def grade_annotations(start, end, rows_by):
    nodata = _grade_row_nodata(rows_by, "annotation-bench")
    if nodata:
        return {"text": f"NO DATA — {nodata}", "values": {}, "label": "", "nodata": True}
    best = None
    for d in glob.glob(str(ROOT / "logs" / "annotation-bench_*")):
        p = Path(d) / "summary.txt"
        if not p.is_file():
            continue
        t = file_utc(p)
        if start and t and t < start:
            continue
        if end and t and t > end + timedelta(minutes=5):
            continue
        if best is None or t > best[0]:
            best = (t, p)
    if best is None:
        return {"text": "NO DATA — no logs/annotation-bench_*/summary.txt from this run", "values": {}, "label": "", "nodata": True}
    lines = [ln.strip() for ln in (read_text(best[1]) or "").splitlines() if ln.strip()][-6:]
    return {"text": shorten(" | ".join(lines), 170), "values": {}, "label": f"{rel(best[1])}; this run", "nodata": False}


def grade_image_codecs(start, end, rows_by):
    nodata = _grade_row_nodata(rows_by, "image-conformance")
    best = None
    for p in glob.glob(str(ROOT / "logs" / "image-conformance" / "*" / "quality-report.json")):
        obj = read_json(p)
        if not isinstance(obj, dict):
            continue
        t = parse_ts(obj.get("generatedUtc")) or file_utc(p)
        if best is None or (t and t > best[0]):
            best = (t, obj, p)
    if best is None:
        why = nodata or "no logs/image-conformance/*/quality-report.json"
        return {"text": f"NO DATA — {why}", "values": {}, "label": "", "nodata": True}
    when, obj, path = best
    summary = obj.get("summary") or {}
    counts = summary.get("qualityStatusCounts") or {}
    pages = to_int(summary.get("pagesScanned"), 0)
    order = [("PIXEL_EXACT", "PIXEL_EXACT"), ("MATCHES_ACCEPTED_REFERENCE", "MATCHES_ACCEPTED"), ("FAIL", "FAIL"),
             ("NEEDS_REVIEW", "NEEDS_REVIEW"), ("NON_RENDERABLE_ACCEPTED", "NON_RENDERABLE")]
    parts, values = [], {}
    for key, disp in order:
        v = to_int(counts.get(key), 0)
        values[key] = v
        if v or key in ("PIXEL_EXACT", "FAIL"):
            parts.append(f"{disp} {v}")
    text = f"{' · '.join(parts)} of {pages} pages"
    label, _ = stamp_label(when, start, end, "image-conformance, this run")
    if nodata:
        label = f"{label}; {nodata}"
    return {"text": text, "values": values, "label": label, "nodata": False}


def grade_bench_design(log_dir, rows_by):
    nodata = _grade_row_nodata(rows_by, "bench-design-coverage")
    r = rows_by.get("bench-design-coverage")
    log = Path(r["log"]) if r and r.get("log") else Path(log_dir) / "bench-design-coverage.log"
    text = read_text(log)
    if text is None and nodata:
        return {"text": f"NO DATA — {nodata}", "values": {}, "label": "", "nodata": True}
    if text is None:
        return {"text": f"NO DATA — no {rel(log)} in this run", "values": {}, "label": "", "nodata": True}
    overall = re.search(r"OVERALL:\s*(\d+)/(\d+)\s*=\s*(\d+)%", text)
    synth = re.search(r"synthetic carriers present:\s*(\d+)", text)
    below = re.search(r"(\d+) categories below target", text)
    cells = re.findall(r"^\s+\S+\s+(?:real\+synth|real|synth)\s+(\d+)\s*/\s*(\d+)", text, re.M)
    if not overall:
        return {"text": "NO DATA — bench-design-coverage.log has no OVERALL line", "values": {}, "label": "", "nodata": True}
    have, target = int(overall.group(1)), int(overall.group(2))
    nb = int(below.group(1)) if below else 0
    values = {"have": have, "target": target, "below": nb}
    parts = [f"{have}/{target} cases vs design target"]
    if synth:
        parts.append(f"{synth.group(1)} synthetic carriers")
    parts.append(f"{nb}/{len(cells) or '?'} tier×category cells below target")
    return {"text": "; ".join(parts), "values": values, "label": "bench-design-coverage", "nodata": False}


# ---------------------------------------------------------------------------
# IMPROVE line
# ---------------------------------------------------------------------------

def improve_values(rows, log_dir, extraction_grade):
    """name -> {'verdict','value','number'} for every IMPROVE row of the plan."""
    out = {}
    for r in rows:
        if r["class"] != "IMPROVE" or r["verdict"] == "INFO":
            continue
        entry = {"verdict": r["verdict"], "status": r["status"], "value": None, "number": None}
        name = r["name"]
        if name == "extraction-parity" and extraction_grade and not extraction_grade.get("nodata"):
            cov = (extraction_grade.get("values") or {}).get("aggregateCoverage")
            if cov is not None:
                entry.update(value=f"{cov:.4f}", number=cov)
        elif name == "perf-budget":
            rep = read_json(artifact_dir({name: r}, name, log_dir) / "perf-budgets" / "perf-budget-report.json")
            wf = (rep or {}).get("workflows") if isinstance(rep, dict) else None
            if isinstance(wf, list) and wf:
                ok = sum(1 for w in wf if w.get("status") == "PASS")
                entry.update(value=f"{ok}/{len(wf)}", number=ok)
                fails = [w.get("workflow") for w in wf if w.get("status") != "PASS"]
                if fails:
                    entry["value"] += " (" + ", ".join(str(f) for f in fails[:3]) + ")"
        if entry["value"] is None and r["log"]:
            text = read_text(r["log"]) or ""
            m = re.findall(r"\((\d+) baselined", text)
            if m:
                entry.update(value=f"{m[-1]} baselined", number=int(m[-1]))
        out[name] = entry
    return out


def render_improve(imp, prior_improve):
    if not imp:
        return "IMPROVE  none in this tier"
    held, regressed, other = [], [], []
    for name, e in imp.items():
        short = "copy-whitespace" if name == "copy-whitespace-parity" else name
        prior = (prior_improve or {}).get(name) if prior_improve else None
        if e["number"] is not None:
            pv = prior.get("number") if isinstance(prior, dict) else None
            d = delta({"v": e["number"]}, {"v": pv} if isinstance(pv, (int, float)) else None, "{:+.4g}")
        else:
            d = "(=)" if isinstance(prior, dict) and prior.get("verdict") == e["verdict"] else \
                ("(no prior)" if not isinstance(prior, dict) else f"(was {prior.get('verdict')})")
        val = f" {e['value']}" if e["value"] else ""
        if e["verdict"] in ("PASS",):
            held.append(f"{short}{val} {d}")
        elif e["verdict"] in ("NEW", "KNOWN", "STALE"):
            regressed.append(f"{short}{val} [{e['verdict']}]")
        else:
            other.append(f"{short} [{e['verdict']}]")
    parts = []
    if held:
        parts.append("held: " + " · ".join(held))
    if regressed:
        parts.append("regressed: " + " · ".join(regressed))
    if other:
        parts.append(" · ".join(other))
    return "IMPROVE  " + "   ".join(parts)


# ---------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------

VERDICT_ORDER = {"NEW": 0, "KNOWN": 1, "STALE": 2, "SKIPPED": 3, "NOT RUN": 4, "INFO": 5}


def row_line(r, name_w=22):
    if r["verdict"] in ("KNOWN", "STALE") and r["issue"]:
        mid = f"#{r['issue']} {r['issueLabel']}"
    elif r["verdict"] == "SKIPPED":
        mid = "policy=skip"
    elif r["verdict"] == "NOT RUN":
        mid = "-"
    else:
        rc = f"rc={r['rc']}" if r["rc"] is not None else "rc=?"
        mid = f"{rc:<6}{fmt_dur(r['duration'] or 0):>4}"
    notes = f" [{', '.join(r['notes'])}]" if r.get("notes") else ""
    return f"{r['verdict']:<8} {r['name']:<{name_w}} {r['class']:<8} {mid:<12} {r['detail']}{notes}"


def verdict_text(counts, exit_code):
    if exit_code == 1:
        parts = []
        if counts["new"]:
            parts.append(f"{counts['new']} NEW")
        if counts["staleIssues"]:
            parts.append("STALE " + ", ".join(f"#{n}" for n in counts["staleIssues"]))
        return "FAIL — " + ", ".join(parts)
    if exit_code == 3:
        return f"INCOMPLETE — {counts['notRun']} NOT RUN"
    if counts["skipped"]:
        return f"PASS with {counts['skipped']} SKIPPED"
    return "PASS"


def class_tally(rows):
    tally = {}
    for r in rows:
        if r["verdict"] == "INFO":
            continue
        c = r["class"]
        t = tally.setdefault(c, [0, 0])
        t[1] += 1
        if r["status"] in PASSING:
            t[0] += 1
    b = tally.get("BLOCK", [0, 0])
    i = tally.get("IMPROVE", [0, 0])
    s = tally.get("SELFTEST", [0, 0])
    g = tally.get("GRADE", [0, 0])
    return (f"BLOCK {b[0]}/{b[1]} pass · IMPROVE {i[0]}/{i[1]} at-or-above floor · "
            f"SELFTEST {s[0]}/{s[1]} · GRADE {g[0]}/{g[1]} reported")


def render_summary(ctx):
    rows, counts, plan, ledger = ctx["rows"], ctx["counts"], ctx["plan"], ctx["ledger"]
    start, end = ctx["start"], ctx["end"]
    sha = ctx["sha"]
    dirty = ctx["dirty"]
    lines = []
    when = ""
    if start and end:
        same_day = start.astimezone().date() == end.astimezone().date()
        when = f"{fmt_local(start)}→{fmt_local(end, with_date=not same_day)} ({fmt_dur((end - start).total_seconds())})"
    head = f"excise gates  {plan['tier']} @{sha[:8]} (tree {'DIRTY' if dirty else 'clean'})  {when}  {rel(ctx['log_dir'])}"
    if plan["planned"] < plan["of"]:
        head += f" PARTIAL planned {plan['planned']}/{plan['of']}"
    lines.append(head)
    span = ""
    if counts["checkpointed"]:
        shas = {r["sha"] for r in ledger if r.get("sha")} | {r["evidenceSha"] for r in ledger if r.get("evidenceSha")}
        if any(r.get("evidenceSha") for r in ledger) and len(shas) > 1:
            span = f" (span {len(shas)} commits)"
    lines.append(f"VERDICT {ctx['verdict']} (exit {ctx['exit']})   {class_tally(rows)}   "
                 f"known {counts['known']} · skipped {counts['skipped']} · not-run {counts['notRun']} · "
                 f"stale {counts['stale']} · checkpointed {counts['checkpointed']}{span}")
    grade_lines = ctx["grade_lines"]
    improve_line = ctx["improve_line"]
    footer = (f"PASS {counts['pass']} rows (--full lists every row)   knownIssue verification: {ctx['verifier'].footer()}")
    budget = SUMMARY_LINES - (2 + 1 + len(grade_lines) + 1)
    listed = sorted((r for r in rows if r["verdict"] in VERDICT_ORDER), key=lambda r: (VERDICT_ORDER[r["verdict"]], rows.index(r)))
    name_w = min(max([22] + [len(r["name"]) for r in listed]), 32)
    if len(listed) > budget:
        shown = listed[: max(budget - 1, 0)]
        lines.extend(row_line(r, name_w) for r in shown)
        lines.append(f"+{len(listed) - len(shown)} more (--full)")
    else:
        lines.extend(row_line(r, name_w) for r in listed)
    lines.append(improve_line)
    lines.extend(grade_lines)
    lines.append(footer)
    return lines


def render_full(ctx):
    rows = ctx["rows"]
    lines = ["", f"{'STATUS':<18} {'VERDICT':<8} {'NAME':<32} {'RC':>3} {'DUR':>7} {'CLASS':<8} {'KNOWN':<44} LOG / TRX"]
    for r in rows:
        status = r["status"] or "-"
        rc = "-" if r["rc"] is None else str(r["rc"])
        dur = fmt_dur(r["duration"] or 0) if r["status"] else "-"
        log = rel(r["log"]) if r["log"] else "-"
        trx = rel(r["trx"]) if (r["trx"] and Path(r["trx"]).is_file()) else "-"
        lines.append(f"{status:<18} {r['verdict']:<8} {r['name']:<32} {rc:>3} {dur:>7} {r['class']:<8} {r['knownIssue']:<44} {log}  {trx}")
        if r["detail"] and (r["verdict"] != "PASS" or r["status"] == "SKIP_CHECKPOINTED"):
            lines.append(f"{'':<18} {'':<8} {'':<32}     {r['detail']}")
    return lines


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def ledger_max_recorded(path):
    text = read_text(path)
    if not text:
        return ""
    stamps = re.findall(r'"recorded":"([^"]*)"', text)
    return max(stamps) if stamps else ""


def find_latest():
    best, best_key = None, None
    for pat in LOG_DIR_PATTERNS:
        for d in glob.glob(str(ROOT / "logs" / pat)):
            p = Path(d)
            if p.is_symlink() or not p.is_dir():
                continue
            ledger = p / "ledger.jsonl"
            if not (p / "plan.tsv").is_file() or not ledger.is_file() or ledger.stat().st_size == 0:
                continue
            key = (ledger_max_recorded(ledger), p.name)
            if best_key is None or key > best_key:
                best, best_key = p, key
    return best


def usage(code):
    print("usage: scripts/report-gates.sh [LOG_DIR|--latest] [--full] [--no-gh]", file=sys.stderr)
    return code


def main(argv):
    full = "--full" in argv
    use_gh = "--no-gh" not in argv
    latest = "--latest" in argv
    positional = [a for a in argv if not a.startswith("--")]
    if any(a.startswith("--") and a not in ("--full", "--no-gh", "--latest") for a in argv):
        return usage(2)
    if latest and positional:
        return usage(2)
    if latest:
        log_dir = find_latest()
        if log_dir is None:
            print("report-gates: no logs/{test-tier_*,full-suite_*,release-smoke_*} directory with a plan.tsv "
                  "and a non-empty ledger.jsonl", file=sys.stderr)
            return 2
    elif len(positional) == 1:
        log_dir = Path(positional[0])
        if not log_dir.is_absolute():
            log_dir = (Path.cwd() / log_dir)
    else:
        return usage(2)
    log_dir = log_dir.resolve()

    try:
        plan = load_plan(log_dir)
        ledger = load_ledger(log_dir)
    except ReportError as exc:
        print(f"report-gates: {exc}", file=sys.stderr)
        return 2

    manifest = load_manifest()
    if not manifest:
        print(f"report-gates: warning: manifest missing or empty: {rel(MANIFEST)}", file=sys.stderr)
    verifier = IssueVerifier(use_gh)
    rows, counts, issues = classify_rows(plan, ledger, manifest, verifier, log_dir)

    stale_issues = sorted({r["issue"] for r in rows if r["verdict"] == "STALE" and r["issue"]}, key=int)
    counts["staleIssues"] = stale_issues
    if counts["new"] or counts["stale"]:
        exit_code = 1
    elif counts["notRun"]:
        exit_code = 3
    else:
        exit_code = 0
    verdict = verdict_text(counts, exit_code)

    start, end = run_window(ledger)
    sha = next((r.get("sha") for r in reversed(ledger) if r.get("sha")), "") or "nogit"
    dirty = any(r.get("treeDirty") == "yes" for r in ledger)
    prior = find_prior_report(plan["tier"], log_dir)
    prior_grades = (prior or {}).get("grades") or {}
    prior_improve = (prior or {}).get("improve") or {}
    rows_by = _row_map(rows)

    grades = {
        "conformance": grade_conformance(log_dir, rows_by),
        "extraction": grade_extraction(start, end, rows_by),
        "redaction": None,
        "render perf": grade_render_perf(log_dir, start, end, rows_by),
        "annotations": grade_annotations(start, end, rows_by),
        "image codecs": grade_image_codecs(start, end, rows_by),
        "bench design": grade_bench_design(log_dir, rows_by),
    }
    pr = prior_grades.get("redaction")
    grades["redaction"] = grade_redaction(log_dir, start, end, rows_by, (pr or {}).get("values") if pr else None)
    fmts = {"conformance": "{:+.1f}", "extraction": "{:+.4f}", "render perf": "{:+.2f}",
            "image codecs": "{:+d}", "bench design": "{:+d}"}
    grade_lines = ["GRADES vs reference tools"]
    for key in GRADE_ROWS:
        g = grades[key]
        text = g["text"]
        if not g.get("nodata") and not g.get("delta_done") and g.get("values"):
            pv = (prior_grades.get(key) or {}).get("values") if prior_grades.get(key) else None
            fmt = fmts.get(key, "{:+.4g}")
            d = delta(g["values"], pv, fmt)
            text = f"{text} {d}" if d else text
        if not g.get("nodata") and g.get("label"):
            text = f"{text}   [{g['label']}]"
        grade_lines.append(f"  {key:<13} {text}")
        if key == "conformance":
            grade_lines.append(f"  {'':<13} {grade_registry()}")
    # GRADE rows the plan carries that none of the named grades reads (informational, NO DATA).
    for r in rows:
        if r["class"] == "GRADE" and r["name"] not in GRADE_ROW_NAMES and r["verdict"] != "PASS":
            grade_lines.append(f"  {shorten(r['name'], 13):<13} NO DATA — {r['detail']}")

    imp = improve_values(rows, log_dir, grades["extraction"])
    improve_line = render_improve(imp, prior_improve)

    ctx = {"rows": rows, "counts": counts, "plan": plan, "ledger": ledger, "start": start, "end": end,
           "sha": sha, "dirty": dirty, "verdict": verdict, "exit": exit_code, "log_dir": log_dir,
           "grade_lines": grade_lines, "improve_line": improve_line, "verifier": verifier}
    summary = render_summary(ctx)
    out = list(summary)
    if full:
        out.extend(render_full(ctx))
    print("\n".join(out))

    report = {
        "tier": plan["tier"], "sha": sha, "treeDirty": dirty,
        "started": start.strftime("%Y-%m-%dT%H:%M:%SZ") if start else None,
        "finished": end.strftime("%Y-%m-%dT%H:%M:%SZ") if end else None,
        "verdict": verdict, "exit": exit_code, "partial": plan["planned"] < plan["of"],
        "planned": plan["planned"], "of": plan["of"], "logDir": str(log_dir),
        "generated": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "counts": {k: v for k, v in counts.items() if k != "staleIssues"},
        "staleIssues": stale_issues,
        "issues": {n: {"state": v["state"], "source": v["source"], "title": v.get("title")} for n, v in verifier.cache.items()},
        "rows": [{"name": r["name"], "status": r["status"], "verdict": r["verdict"], "rc": r["rc"],
                  "durationSeconds": r["duration"], "class": r["class"], "knownIssue": r["knownIssue"],
                  "detail": r["detail"], "log": r["log"], "trx": r["trx"]} for r in rows],
        "grades": {k: {"text": g["text"], "values": g.get("values") or {}, "label": g.get("label"), "nodata": bool(g.get("nodata"))}
                   for k, g in grades.items()},
        "improve": imp,
        "summary": summary,
    }
    try:
        tmp = log_dir / f".report.json.tmp{os.getpid()}"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(report, fh, indent=1)
            fh.write("\n")
        os.replace(tmp, log_dir / "report.json")
    except OSError as exc:
        print(f"report-gates: warning: could not write report.json: {exc}", file=sys.stderr)
    return exit_code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
