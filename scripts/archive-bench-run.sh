#!/usr/bin/env bash
# Archive a completed redaction-benchmark run into a datestamped, versioned
# history so performance can be tracked over time (#1185).
#
# Records WHICH excise (git commit) ran against WHICH bench version (manifest +
# design hash + case counts + tool versions), plus the run's metrics. Appends one
# compact line to the COMMITTED history (tests/redaction-bench-history.jsonl) and
# copies the full rows to a local archive (logs/redaction-benchmark/history/,
# gitignored — the committed index is the durable trend record).
#
# Usage: scripts/archive-bench-run.sh [results.jsonl] [corpus-label]
#   defaults: logs/redaction-benchmark/results.jsonl, "redaction-hard"
set -euo pipefail
cd "$(dirname "$0")/.."

RESULTS="${1:-logs/redaction-benchmark/results.jsonl}"
CORPUS="${2:-redaction-hard}"
[ -s "$RESULTS" ] || { echo "no results at $RESULTS" >&2; exit 1; }

# REDACTION_BENCH_HISTORY redirects the history line (the full-suite redaction-bench
# row writes it into its own log directory, so a GRADE run never dirties the tree;
# committing a history point stays a deliberate, manual invocation).
HISTORY="${REDACTION_BENCH_HISTORY:-tests/redaction-bench-history.jsonl}"
ARCHIVE_DIR="logs/redaction-benchmark/history"
mkdir -p "$ARCHIVE_DIR"

# --- version metadata (captured at archive time == run time) ---
TS="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
COMMIT="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
DESCRIBE="$(git describe --tags --always --dirty 2>/dev/null || echo unknown)"
DIRTY=$([ -n "$(git status --porcelain 2>/dev/null)" ] && echo true || echo false)
# bench version = hash over the manifest + design + adversarial fixture set
MANIFEST_SHA="$(cat tests/redaction-hard-cases.tsv tests/redaction-bench-design.json \
                test-pdfs/redaction-adversarial/.manifest-hash 2>/dev/null \
                | shasum -a 256 | cut -c1-16)"
DESIGN_VERSION="$(python3 -c "import json;print(json.load(open('tests/redaction-bench-design.json')).get('version','?'))" 2>/dev/null || echo '?')"
REAL_CASES="$(grep -vcE '^#|^$' tests/redaction-hard-cases.tsv 2>/dev/null || echo 0)"
SYNTH_FIXTURES="$(ls test-pdfs/redaction-adversarial/*.pdf 2>/dev/null | wc -l | tr -d ' ')"

# --- tool versions (empty string if absent) ---
tool_ver() { "$@" 2>&1 | head -1 | tr -d '"' || true; }
PYMUPDF="$(tools/vendor/xray-venv/bin/python -c 'import fitz;print(fitz.VersionBind)' 2>/dev/null || echo '')"
TESS="$(tesseract --version 2>&1 | head -1 | awk '{print $2}' || echo '')"
GS="$(gs --version 2>/dev/null || echo '')"
MUTOOL="$(mutool -v 2>&1 | head -1 | awk '{print $NF}' || echo '')"
QPDF="$(qpdf --version 2>/dev/null | head -1 | awk '{print $NF}' || echo '')"
# #1372 made leak detection TWO-ENGINE (mutool AND pdftotext; a term counts as leaked when
# EITHER reads it). The history stamped mutool's version and not poppler's, so a history point
# could not say whether one engine or two produced its verdict -- and the committed 0.969 A-
# from 2026-08-27 was silently a mutool-ONLY number. On the first two-engine run Poppler read
# 14 terms in excise's own output that mutool called clean, and mutool found ZERO the other
# way: same corpus, same code, 0.969 -> ~0.93.
POPPLER="$(pdftotext -v 2>&1 | head -1 | awk '{print $NF}' || echo '')"

ARCHIVE="$ARCHIVE_DIR/${STAMP}-${COMMIT:0:8}.jsonl"
cp "$RESULTS" "$ARCHIVE"

# --- compute metrics + emit the history line (python does the JSON) ---
python3 - "$RESULTS" "$TS" "$COMMIT" "$DESCRIBE" "$DIRTY" "$MANIFEST_SHA" \
         "$DESIGN_VERSION" "$REAL_CASES" "$SYNTH_FIXTURES" "$CORPUS" \
         "$PYMUPDF" "$TESS" "$GS" "$MUTOOL" "$QPDF" "$POPPLER" "$ARCHIVE" "$HISTORY" <<'PY'
import json, sys, collections
(results, ts, commit, describe, dirty, manifest_sha, design_version, real_cases,
 synth, corpus, pymupdf, tess, gs, mutool, qpdf, poppler, archive, history) = sys.argv[1:]

rows = [json.loads(l) for l in open(results) if l.strip()]
ok = [r for r in rows if not r.get("error")]
tools = sorted({r["tool"] for r in rows})

# WHICH ENGINES PRODUCED THE VERDICT.
#
# `leakOracleTextMutool`/`Poppler` are non-nullable C# bools (RedactionBenchmarkRunner.cs:
# 138,140), so they serialise as JSON `true`/`false` -- NEVER `null` -- whether or not Poppler
# actually ran that row. `.get(...) is not None` on either field is therefore true on every
# run ever produced, single-engine or not; it is not a bug that missed one case, it cannot
# ever fire the other way. Caught only because a run with Poppler removed still reported
# "mutool, pdftotext".
#
# The Row type carries no per-row signal of whether Poppler was reachable (checked: no
# PopplerRan/PopplerAvailable field exists). The only trustworthy signal LEFT is whether
# pdftotext is on PATH in THIS invocation's environment -- true as long as archiving happens
# in the same environment as the run, which is the documented usage (see the header comment).
# It is imprecise for a results.jsonl copied in from a different machine or a different
# session; TODO(#1372-followup): have the runner stamp a run-level `_meta` line with the
# engines it actually reached, so this stops being an environment guess. Track that as its own
# fix rather than widening this script further.
leak_engines = ["mutool"] if any(r.get("tool") for r in ok) else []
if poppler:
    leak_engines.append("pdftotext")

# Per-tool count of cases where the two engines disagreed. A rising number means one engine is
# going blind on a carrier the other still sees -- the signal that a THIRD engine is due. Only
# meaningful when both engines actually ran; recorded regardless so a later run can see the
# trend once they do.
disagreements = {t: sum(1 for r in ok if r["tool"] == t and r.get("leakOracleTextDisagree"))
                 for t in tools}

def leaked(r):
    return bool(r.get("leakSavedBytes") or r.get("leakOracleText")
                or (r.get("leakBadRedactions", 0) > 0)
                or r.get("visualTermReadable") == 1 or r.get("imageBakedReadable") == 1)

def letter(secure_frac):
    if secure_frac is None: return None
    p = secure_frac * 100
    for thr, g in [(98,"A"),(95,"A-"),(90,"B+"),(85,"B"),(80,"B-"),(70,"C+"),(60,"C"),(50,"D"),(0,"F")]:
        if p >= thr: return g
    return "F"

# leak rate by tier x tool  (tiers only meaningful for redaction-hard corpus)
by_tier = {}
tiers = sorted({r.get("difficulty", "") for r in ok if r.get("difficulty")})
for tier in tiers:
    by_tier[tier] = {t: None for t in tools}
    for t in tools:
        sub = [r for r in ok if r.get("difficulty") == tier and r["tool"] == t]
        by_tier[tier][t] = [sum(1 for r in sub if leaked(r)), len(sub)]

# security x fidelity per tool
sf = {}
for t in tools:
    g = [r for r in ok if r["tool"] == t]
    if not g: continue
    secure = sum(1 for r in g if not leaked(r))
    rf = [r for r in g if r.get("survivingRenderDelta", -1) >= 0]
    rok = sum(1 for r in rf if r.get("survivingRenderDelta", 1) < 0.02
              and not (r.get("inputQpdfOk") and not r.get("qpdfOk")))
    tk = sum(1 for r in g if r.get("collateralFraction", 1) < 0.02
             and r.get("survivingWordsDamaged", 1) == 0)
    sf[t] = {"secure": round(secure/len(g), 3),
             "rendersOk": round(rok/len(rf), 3) if rf else None,
             "textKept": round(tk/len(g), 3), "n": len(g)}

# security letter grade per tool: overall + per tier
grades = {}
for t in tools:
    g = [r for r in ok if r["tool"] == t]
    overall = (sum(1 for r in g if not leaked(r)) / len(g)) if g else None
    tier_grades = {}
    for tier in tiers:
        sub = [r for r in g if r.get("difficulty") == tier]
        tier_grades[tier] = letter(sum(1 for r in sub if not leaked(r))/len(sub)) if sub else None
    grades[t] = {"overall": letter(overall), "byTier": tier_grades}

# REFUSE TO SILENTLY NARROW THE COMPARISON.
#
# The bench discovers its peer tools from the environment: PyMuPDF and the raster anchor
# appear only if tools/vendor/xray-venv/bin/python exists, iText only if its jars and a java
# do (RedactionBenchmarkRunner.cs:256-266). That is the right behaviour for a bench nobody can
# fully provision everywhere -- but it means a broken or missing dependency produces a
# SMALLER run that still looks complete, and archiving it would replace a five-tool grade with
# a three-tool one while the headline number moved for reasons nothing recorded.
#
# This is not hypothetical: it happened on 2026-09-07. A recursive purge of directories named
# `bin` removed tools/vendor/xray-venv/bin, so the next run silently dropped pymupdf AND the
# raster anchor -- and the raster anchor is the fidelity counterweight the whole
# security-vs-fidelity reading depends on. Nothing said a word.
#
# So: compare the peer set against the last history point and stop, unless the caller says
# they mean it. Same principle as the reference-performance bench refusing to compare a jit
# baseline against an aot run -- a narrower measurement is not a worse score, it is a
# DIFFERENT measurement, and conflating the two is how a bench starts lying.
import os
prev_peers = set()
if os.path.exists(history):
    try:
        prev = [json.loads(l) for l in open(history) if l.strip()]
        if prev:
            prev_peers = set(prev[-1].get("peerTools") or
                             [t for t in (prev[-1].get("metrics", {}).get("securityFidelity") or {})
                              if t != "excise"])
    except Exception:
        prev_peers = set()
now_peers = {t for t in tools if t != "excise"}
lost = sorted(prev_peers - now_peers)
if lost and os.environ.get("BENCH_ALLOW_NARROWER") != "1":
    sys.stderr.write(
        "\nREFUSING TO ARCHIVE: this run lost peer tool(s) the last history point had: "
        + ", ".join(lost) + "\n"
        "  had:  " + ", ".join(sorted(prev_peers)) + "\n"
        "  now:  " + ", ".join(sorted(now_peers)) + "\n"
        "A narrower run is a DIFFERENT measurement, not a worse score. Provision the missing\n"
        "tool (scripts/download-xray.sh for pymupdf + raster, scripts/download-itext.sh for\n"
        "itext) and re-run, or set BENCH_ALLOW_NARROWER=1 if you genuinely mean to record a\n"
        "narrower comparison.\n")
    raise SystemExit(3)

entry = {
    "timestamp": ts,
    "excise": {"commit": commit, "describe": describe, "dirty": dirty == "true"},
    "bench": {"designVersion": design_version, "manifestSha": manifest_sha,
              "corpus": corpus, "realCases": int(real_cases), "syntheticFixtures": int(synth)},
    "tools": {k: v for k, v in {
        "pymupdf": pymupdf, "tesseract": tess, "ghostscript": gs,
        "mutool": mutool, "qpdf": qpdf, "poppler": poppler}.items() if v},
    "leakEngines": leak_engines,
    "leakEngineDisagreements": disagreements,
    "peerTools": [t for t in tools if t != "excise"],
    "metrics": {"measured": len(ok), "errored": len(rows) - len(ok),
                "leakByTierTool": by_tier, "securityFidelity": sf,
                "securityGrade": grades},
    "archive": archive,
}
with open(history, "a") as f:
    f.write(json.dumps(entry) + "\n")

print(f"archived run {ts}")
print(f"  excise {describe} ({commit[:8]}{' DIRTY' if dirty=='true' else ''})")
print(f"  bench  design v{design_version} manifest {manifest_sha} "
      f"({real_cases} real + {synth} synthetic, corpus={corpus})")
print(f"  measured {len(ok)}, errored {len(rows)-len(ok)}")
for tier in tiers:
    cells = "  ".join(f"{t}={by_tier[tier][t][0]}/{by_tier[tier][t][1]}" for t in tools)
    print(f"  leak {tier:8} {cells}")
for t in tools:
    print(f"  grade {t:8} overall={grades[t]['overall']}  "
          f"tiers={' '.join(f'{k}:{v}' for k,v in grades[t]['byTier'].items())}")
print(f"  full rows -> {archive}")
print(f"  history   -> {history}")
PY
