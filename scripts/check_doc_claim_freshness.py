#!/usr/bin/env python3
"""Catch stale numeric/inventory claims in CLAUDE.md before they mislead a reader.

Invoked via scripts/check-doc-claim-freshness.sh — see that file for the t0
wiring. Written for #936: three claims about the test suite were each true
when written and wrong by the time someone acted on them, and every one of
them was wrong in the direction that throws away verification (skip an
oracle, write an allow-list entry that can never match, delete a working
entry). #936's 2026-08-16 comment names the shared shape: the stale text was
always the "convenience" half of a passage whose other half already said
"don't trust this, measure it yourself" — a hard-coded number sitting right
next to its own warning label.

THREE CHECKS, EACH TARGETING ONE INSTANCE OF THAT SHAPE
--------------------------------------------------------

1. Reference-oracle usage counts (the issue's own acceptance criterion).
   CLAUDE.md's "File Locations Quick Reference" section annotates each
   `*ReferenceRenderer.cs` row with how many times Excise.Rendering.Tests/
   Differential actually invokes that class. This is exactly derivable —
   grep the class name — so it is derived and compared, not trusted.
   `--update` rewrites the annotated counts to match reality.

2. Milestone references. CLAUDE.md quotes backtick-wrapped `N. Title`
   milestone identifiers in the roadmap section. Each is checked against
   `tests/milestones-baseline.tsv`, a checked-in snapshot of
   `gh api repos/marctjones/excise/milestones` (regenerate with
   `--update-milestones`, which needs network + `gh` auth — deliberately
   NOT part of the default fast path, matching every other network-touching
   script in this repo staying out of t0). A milestone number/title that
   does not resolve to any baseline row is exactly "a reference to a
   milestone that does not exist" — #936's second named mutation case.

3. Undated large numbers next to the repo's own "don't trust this" idiom.
   CLAUDE.md repeatedly writes some version of "don't hard-code a number
   here, it goes stale" or "query it live" immediately next to a number —
   sometimes with a dated measurement marker (compliant), sometimes not.
   This checks, per paragraph, containing a "large" (3+ digit, or
   comma-grouped) number-shaped token: does it carry a `YYYY-MM-DD` date in
   the same paragraph? No date => the number is asserted as fact with
   nothing to tell a reader it might already be wrong.

WHAT THIS DELIBERATELY DOES NOT CHECK, AND WHY
------------------------------------------------

- It does not verify the milestones baseline itself is fresh. That needs
  network; t0 must not depend on network. `--update-milestones` refreshes
  it explicitly, like a PublicApi/*.approved.txt regeneration.
- It does not verify open/closed STATE of a referenced milestone, only that
  the milestone (number + title fragment) exists. Encoding "these three are
  closed, these four are open" out of freeform prose is exactly the kind of
  brittle wide-net parsing that turns into a gate people delete the moment
  someone rephrases a sentence; existence is the load-bearing property (a
  reference to a milestone number that was never real, or whose title was
  misquoted, is what actually misleads a planner).
- The undated-number check only fires when BOTH a fixed, short list of
  trigger phrases ("hard-code", "goes/gone stale", "query it live", "don't
  restate") AND a 3+-digit / comma-grouped number appear in the same
  markdown paragraph. It does not flag every number in the file — issue/PR
  references (`#95`), version strings (`v1.3.0`), chunk labels
  (`chunk05`), spec clause numbers (`§9.4.2`) and ordinary two-digit counts
  are excluded by construction (the lookaround excludes digits adjacent to
  `#`, a letter, `.` or `-`, and the digit-count floor excludes anything
  under 3 digits). A broader regex over every number in the file would
  fire on legitimate, stable prose constantly and become exactly the kind
  of gate the "beware" note in #936 warns about — deleted, not fixed.
- It does not check prose that describes intent, design rationale, or
  historical narrative (explicitly out of scope per the issue's
  "Acceptance" section) — only claims of fact a script can mechanically
  evaluate.
- It only reads CLAUDE.md, not README.md or other tracked docs. README-shaped
  claims already have a mechanism for the failure mode THEY are prone to —
  pinned string existence via scripts/verify-doc-claims.sh and
  Excise.App.Tests/Documentation/DocumentationClaimTests.cs. CI_GATES.md and
  CHANGELOG.md DO use the same "don't hard-code this, it goes stale" idiom
  (checked at the time this gate was written) but were left out rather than
  widened blind: CI_GATES.md's instances all correctly state NO live number
  next to the warning (the lesson already learned there), and CHANGELOG.md's
  is historical narrative — explicitly out of scope per the issue's own
  "Acceptance" section — plus one paragraph that CITES its own old wrong
  numbers ("previously said ~2400 ... now off by hundreds") as the reason to
  stop trusting round numbers. A naive port of check_undated_numbers to that
  file would flag that citation as an undated live claim; it is exactly the
  opposite. Widening needs that "previously/used to say" framing excluded
  first, which is future work, not a same-day extension.

Implemented in Python for the same reason scripts/check_unwired_api.py is:
this machine's bash is 3.2, and the milestone/paragraph extraction needs
named groups and lookaround that GNU-grep-only regex features don't give
portable bash access to.
"""
import argparse
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CLAUDE_MD = os.path.join(ROOT, "CLAUDE.md")
DIFFERENTIAL_TEST_DIR = os.path.join(ROOT, "Excise.Rendering.Tests", "Differential")
MILESTONES_BASELINE = os.path.join(ROOT, "tests", "milestones-baseline.tsv")

ORACLE_SECTION_START = "## File Locations Quick Reference"
MILESTONE_SECTION_START = "### Current High-Priority Issues"
HEADING_RE = re.compile(r"^#{2,3} ")

ORACLE_LINE_RE = re.compile(
    r"^(?P<pre>.*?\b)(?P<class>[A-Za-z][A-Za-z0-9]*ReferenceRenderer)\.cs\b"
    # [^#]* (not .*?) up to the '#': a trailing comment like "# see #857" must
    # not let the count-digit match backtrack past the FIRST '#' and parse an
    # issue reference as the claimed count.
    r"(?P<mid>[^#]*#\s*)(?P<count>\d+)\b(?P<post>.*)$"
)

MILESTONE_TOKEN_RE = re.compile(r"`(\d+\.\s+[^`]+)`")

TRIGGER_PHRASES = [
    "hard-code",
    "hard-coded",
    "hard-coding",
    "goes stale",
    "gone stale",
    "query it live",
    "don't restate",
    "do not restate",
]

# Excludes digits adjacent to '#', a letter, '.' or '-' on either side, so
# issue refs (#95), versions (v1.3.0), chunk labels (chunk05) and decimals
# (98.7) don't count as "claims". Comma-grouped (7,600) or bare 3+ digit
# (2694) numbers do.
NUMBER_RE = re.compile(
    r"(?<![\w.#-])(?:\d{1,3}(?:,\d{3})+|\d{3,})(?![\w.#-])"
)
DATE_RE = re.compile(r"\b\d{4}-\d{2}-\d{2}\b")


def read_lines(path):
    with open(path, encoding="utf-8") as f:
        return f.read().splitlines()


def section_bounds(lines, start_text):
    start_idx = None
    for i, line in enumerate(lines):
        if line.strip() == start_text:
            start_idx = i
            break
    if start_idx is None:
        return None
    end_idx = len(lines)
    for i in range(start_idx + 1, len(lines)):
        if HEADING_RE.match(lines[i]):
            end_idx = i
            break
    return start_idx, end_idx


def count_class_uses(classname):
    pat = re.compile(r"\b" + re.escape(classname) + r"\b")
    total = 0
    for dirpath, dirnames, filenames in os.walk(DIFFERENTIAL_TEST_DIR):
        dirnames[:] = [d for d in dirnames if d not in {"bin", "obj"}]
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            with open(os.path.join(dirpath, fn), encoding="utf-8", errors="replace") as f:
                total += len(pat.findall(f.read()))
    return total


def check_oracle_counts(lines, do_update):
    failures = []
    bounds = section_bounds(lines, ORACLE_SECTION_START)
    if bounds is None:
        return [f"CLAUDE.md: could not find section heading {ORACLE_SECTION_START!r}"]
    start, end = bounds

    if not os.path.isdir(DIFFERENTIAL_TEST_DIR):
        return [f"{DIFFERENTIAL_TEST_DIR}: directory not found — cannot verify oracle usage counts"]

    for i in range(start, end):
        m = ORACLE_LINE_RE.match(lines[i])
        if not m:
            continue
        classname = m.group("class")
        claimed = int(m.group("count"))
        actual = count_class_uses(classname)
        if claimed != actual:
            lineno = i + 1
            failures.append(
                f"CLAUDE.md:{lineno}: {classname}.cs claims {claimed} use(s) in "
                f"Excise.Rendering.Tests/Differential, actual is {actual}. "
                f"Run scripts/check-doc-claim-freshness.sh --update to refresh it."
            )
            if do_update:
                lines[i] = m.group("pre") + classname + ".cs" + m.group("mid") + str(actual) + m.group("post")

    return failures if not do_update else []


def load_milestones_baseline():
    if not os.path.isfile(MILESTONES_BASELINE):
        return None
    rows = []
    with open(MILESTONES_BASELINE, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) != 3:
                continue
            rows.append(tuple(parts))
    return rows


def check_milestone_refs(lines):
    bounds = section_bounds(lines, MILESTONE_SECTION_START)
    if bounds is None:
        return [f"CLAUDE.md: could not find section heading {MILESTONE_SECTION_START!r}"]
    start, end = bounds

    baseline = load_milestones_baseline()
    if baseline is None:
        return [
            f"{MILESTONES_BASELINE}: not found. Generate it with "
            f"scripts/check-doc-claim-freshness.sh --update-milestones (needs `gh` + network)."
        ]

    failures = []
    for i in range(start, end):
        for m in MILESTONE_TOKEN_RE.finditer(lines[i]):
            token = m.group(1).strip()
            if not any(token in title for _num, title, _state in baseline):
                lineno = i + 1
                failures.append(
                    f"CLAUDE.md:{lineno}: milestone reference `{token}` does not match any "
                    f"row in {os.path.relpath(MILESTONES_BASELINE, ROOT)} — either the "
                    f"number/title is wrong, or the baseline is stale "
                    f"(refresh with --update-milestones)."
                )
    return failures


def check_undated_numbers(lines):
    failures = []
    # Paragraphs are blank-line-delimited blocks; track each block's first line number.
    para_start = 0
    para_lines = []
    blocks = []  # (start_line_1indexed, block_text)

    def flush():
        if para_lines:
            blocks.append((para_start + 1, "\n".join(para_lines)))

    for i, line in enumerate(lines):
        if line.strip() == "":
            flush()
            para_lines.clear()
            para_start = i + 1
        else:
            if not para_lines:
                para_start = i
            para_lines.append(line)
    flush()

    for start_line, block in blocks:
        lower = block.lower()
        if not any(phrase in lower for phrase in TRIGGER_PHRASES):
            continue
        if not NUMBER_RE.search(block):
            continue
        if DATE_RE.search(block):
            continue
        first_line = block.splitlines()[0][:80]
        failures.append(
            f"CLAUDE.md:{start_line}: paragraph pairs a hard-coded/stale-prone number with "
            f"a \"don't trust this, measure it yourself\" instruction but carries no "
            f"YYYY-MM-DD dated measurement marker — starts: {first_line!r}"
        )
    return failures


def update_milestones_baseline():
    import datetime
    import subprocess

    try:
        out = subprocess.run(
            [
                "gh", "api", "repos/marctjones/excise/milestones?state=all&per_page=100",
                "--jq", '.[] | [(.number|tostring), .title, .state] | @tsv',
            ],
            check=True, capture_output=True, text=True, cwd=ROOT,
        ).stdout
    except (subprocess.CalledProcessError, FileNotFoundError) as exc:
        print(f"FAIL: could not refresh milestones baseline via gh api: {exc}", file=sys.stderr)
        return 1

    rows = sorted(
        (line.split("\t") for line in out.splitlines() if line.strip()),
        key=lambda r: int(r[0]),
    )
    os.makedirs(os.path.dirname(MILESTONES_BASELINE), exist_ok=True)
    with open(MILESTONES_BASELINE, "w", encoding="utf-8") as f:
        today = datetime.date.today().isoformat()
        f.write(
            "# number\ttitle\tstate\n"
            "# Generated by scripts/check-doc-claim-freshness.sh --update-milestones\n"
            "# via: gh api repos/marctjones/excise/milestones?state=all\n"
            f"# refreshed: {today}\n"
        )
        for number, title, state in rows:
            f.write(f"{number}\t{title}\t{state}\n")
    print(f"==> {MILESTONES_BASELINE} refreshed ({len(rows)} milestones)")
    return 0


def self_test():
    """Prove each check fails on the mutation it exists to catch, and does not
    false-positive on real repo content or on the near-miss that bit this
    gate's own development (a decimal percentage next to a "don't restate"
    caveat, which is NOT an undated count claim). #936's own lesson — "a
    gate you never saw fail is not a gate" — applies recursively to this
    gate, so this is the codified form of the manual mutation pass that
    accompanied the change adding it, not a substitute for having done that
    pass once by hand.
    """
    failures = []

    # --- Check A: a wrong reference-oracle usage count ---
    real_class = "PdftoppmReferenceRenderer"
    actual = count_class_uses(real_class)
    bad = [ORACLE_SECTION_START, f"    ├── {real_class}.cs      #  {actual + 1}", "## Next Heading"]
    if not check_oracle_counts(bad, do_update=False):
        failures.append("check_oracle_counts did not fail on a wrong count")
    good = [ORACLE_SECTION_START, f"    ├── {real_class}.cs      #  {actual}", "## Next Heading"]
    result = check_oracle_counts(good, do_update=False)
    if result:
        failures.append(f"check_oracle_counts false-positived on a correct count: {result}")

    # --- Check B: a milestone reference that does not exist ---
    baseline = load_milestones_baseline()
    if not baseline:
        failures.append("milestones baseline missing or empty, cannot self-test check B")
    else:
        bad = [MILESTONE_SECTION_START, "`999999. A Milestone That Was Never Real`", "### Next Heading"]
        if not check_milestone_refs(bad):
            failures.append("check_milestone_refs did not fail on a nonexistent milestone")

        # Real milestone titles need the `N. Title` shape to be a valid token
        # for this check at all (see MILESTONE_TOKEN_RE) — not every baseline
        # row qualifies (e.g. "v1.3.0" doesn't), so pick one that does.
        scheme_row = next((r for r in baseline if re.match(r"^\d+\.\s", r[1])), None)
        if scheme_row is None:
            failures.append("no baseline milestone title has the numbered-scheme shape; cannot test check B's happy path")
        else:
            good = [MILESTONE_SECTION_START, f"`{scheme_row[1]}`", "### Next Heading"]
            result = check_milestone_refs(good)
            if result:
                failures.append(f"check_milestone_refs false-positived on a real milestone: {result}")

    # --- Check C: an undated number next to a "measure it yourself" instruction ---
    bad = [
        "**Widget Count**: 128745 fixtures across the scripts. Don't hard-code",
        "a number here; it goes stale.",
    ]
    if not check_undated_numbers(bad):
        failures.append("check_undated_numbers did not fail on an undated number")

    good = [
        "**Widget Count** (2026-08-15): 128745 fixtures across the scripts. Don't",
        "hard-code a number here; it goes stale.",
    ]
    result = check_undated_numbers(good)
    if result:
        failures.append(f"check_undated_numbers false-positived with a date present: {result}")

    # Regression pin: this gate's own development hit a false positive on a
    # decimal percentage ("102.6%") sitting next to a "don't restate" caveat.
    # A decimal is not an undated COUNT claim and must not trip the check.
    decimal_case = [
        "An earlier version of this note claimed 102.6% aggregate coverage.",
        "Do not restate a specific number here without re-running the gate.",
    ]
    result = check_undated_numbers(decimal_case)
    if result:
        failures.append(f"check_undated_numbers false-positived on a decimal percentage: {result}")

    # The real file, as committed, must currently be clean — pins the actual
    # fixes this change made, not just the synthetic fixtures above.
    real_lines = read_lines(CLAUDE_MD)
    real_failures = (
        check_oracle_counts(real_lines, do_update=False)
        + check_milestone_refs(real_lines)
        + check_undated_numbers(real_lines)
    )
    if real_failures:
        failures.append("CLAUDE.md as committed is not currently clean: " + "; ".join(real_failures))

    if failures:
        for msg in failures:
            print("SELF-TEST FAIL: " + msg, file=sys.stderr)
        print(f"\nFAIL: {len(failures)} self-test failure(s)", file=sys.stderr)
        return 1

    print("doc-claim freshness self-test passed (mutation + false-positive + regression cases)")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update", action="store_true",
                     help="rewrite CLAUDE.md's reference-oracle usage counts to match reality")
    ap.add_argument("--update-milestones", action="store_true",
                     help="refresh tests/milestones-baseline.tsv via `gh api` (needs network)")
    ap.add_argument("--self-test", action="store_true",
                     help="prove the gate actually fails on each mutation it exists to catch")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    if args.update_milestones:
        return update_milestones_baseline()

    lines = read_lines(CLAUDE_MD)

    oracle_failures = check_oracle_counts(lines, args.update)

    if args.update:
        with open(CLAUDE_MD, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        print("==> CLAUDE.md reference-oracle usage counts refreshed")
        return 0

    failures = []
    failures += oracle_failures
    failures += check_milestone_refs(lines)
    failures += check_undated_numbers(lines)

    if failures:
        for msg in failures:
            print(msg, file=sys.stderr)
        print(f"\nFAIL: {len(failures)} doc-claim freshness violation(s)", file=sys.stderr)
        return 1

    print("doc-claim freshness check passed (oracle counts, milestone refs, dated-number markers)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
