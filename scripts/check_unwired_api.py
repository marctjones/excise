#!/usr/bin/env python3
"""Cross-reference the approved public API surface against actual call sites.

Invoked via scripts/check-unwired-api.sh — see that file's header for what this
is and, more importantly, what it is not (a candidate list, not a dead-code
prover).
"""
import argparse
import os
import re
import sys
from collections import defaultdict

SKIP_DIRS = {"obj", "bin", ".git", ".claude", "node_modules", "artifacts", "logs", "test-pdfs"}

# Identifiers that are ubiquitous language/framework surface. Counting references
# to these measures nothing.
NOISE = {
    "ToString", "Equals", "GetHashCode", "Dispose", "DisposeAsync", "CompareTo",
    "Deconstruct", "PrintMembers", "GetEnumerator", "op_Equality", "op_Inequality",
}

BASELINE_HEADER = """# Public API that nothing calls, or that only tests call.
#
# A RATCHET, not an inventory. Entries here are ACCEPTED — mostly library API
# with external consumers, framework overrides, and extension-class names that
# never appear at a call site. The gate fails on anything NEW.
#
# Regenerate: scripts/check-unwired-api.sh --update   (then review the diff)
#
# Format: assembly <TAB> state <TAB> identifier <TAB> triage-note
#   nowhere     no reference at all beyond the declaration
#   tests-only  referenced by tests, never by production  <- the dangerous one
#   triage-note why this accepted row is not being fixed in this change, or
#               which issue owns fixing it. New rows written by --update are
#               marked UNTRIAGED and fail the normal gate until reviewed.
#
# "tests-only" is the shape that shipped bugs: #908 (CffSubsetter, implemented,
# 25 test references, zero production callers, so CFF fonts ship unsubsetted)
# and #896 (RedactWithOptions, same shape, and the CLI leaked redacted terms
# into /Info and XMP for exactly as long as nothing called the safe path).
"""

COMPILER_ATTR = re.compile(r"System\.Runtime\.CompilerServices\.[A-Za-z_][A-Za-z0-9_]*")

DECL = re.compile(r"\b(?:class|interface|enum|struct|record)\s+([A-Za-z_][A-Za-z0-9_]*)")
MEMBER = re.compile(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(|\{\s*get)")


def is_test_path(path):
    """A test project, by directory name rather than by file suffix.

    The distinction matters more than it looks. RedactionService.RedactWithOptions
    — the API that motivated this script — has TWELVE references, all of them in
    RedactionServiceTests, and ZERO in production. A single reference count would
    call it healthy. "Tested but never wired up" is the shape that shipped #896's
    leak, and it is only visible when the two are counted apart.
    """
    parts = path.replace("\\", "/").split("/")
    return any(p.endswith(".Tests") or p == "tests" for p in parts)


NAMEOF = re.compile(r"\bnameof\s*\(\s*[A-Za-z_][A-Za-z0-9_.]*\s*\)")


def source_index(root):
    """Which FILES mention each identifier, plus how many times WITHIN a file.

    Two false signals have to be defeated at once, and each fix creates the
    other if applied alone.

    FALSE NEGATIVE — counting raw occurrences. A class that self-references
    inside its own file looks used. SKBitmapPool (an entire bitmap pool, zero
    callers, zero tests) slipped through on

        throw new ObjectDisposedException(nameof(SKBitmapPool));

    which is boilerplate in every IDisposable, so EVERY dead IDisposable was
    invisible.

    FALSE POSITIVE — counting only distinct FILES, which was the first fix.
    A member used legitimately inside its own declaring file contributes one
    file, indistinguishable from a bare declaration. Measured on the real
    baseline: **18 of 22** `nowhere` entries were of this kind — PdfLink's
    ExternalLink/DangerousLink factories (called at PdfLink.cs:135 and :146),
    MatchingNormalization's predicates, PdfDocumentBuilder's layout members.
    Only 4 were genuinely unreferenced. A gate whose flagged list is mostly
    wrong is a gate people stop reading, which is how the skip budget rotted
    (#854).

    So: strip `nameof(...)` first, THEN count occurrences. A self-reference
    disappears, and a real same-file call still counts. `occ` below is the
    highest per-file occurrence count in production, so 1 means "declaration
    only" and >=2 means a genuine use somewhere.
    """
    prod = defaultdict(set)
    test = defaultdict(set)
    occ = defaultdict(int)          # max per-production-file occurrences
    files = 0
    word = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if not fn.endswith((".cs", ".axaml")):
                continue
            full = os.path.join(dirpath, fn)
            is_test = is_test_path(full)
            bucket = test if is_test else prod
            files += 1
            try:
                with open(full, encoding="utf-8", errors="ignore") as fh:
                    counts = defaultdict(int)
                    for line in fh:
                        # nameof(X) is a self-reference, not a use.
                        for m in word.findall(NAMEOF.sub(" ", line)):
                            counts[m] += 1
                    for m, c in counts.items():
                        bucket[m].add(full)
                        if not is_test and c > occ[m]:
                            occ[m] = c
            except OSError:
                continue
    return prod, test, occ, files


def approved_files(root):
    out = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        if os.path.basename(dirpath) != "PublicApi":
            continue
        for fn in sorted(filenames):
            if fn.endswith(".approved.txt"):
                out.append(os.path.join(dirpath, fn))
    return out


def identifiers(path, min_len):
    """Public identifiers worth cross-referencing, with three noise classes
    excluded at the source (#913).

    Each is something this check CANNOT say anything true about, so reporting it
    only trains people to skim the list — which is how the flagged output stops
    being read. They are filtered here rather than baselined so they do not
    reappear when the baseline is regenerated.

      1. COMPILER-GENERATED ATTRIBUTES. `TupleElementNames` is emitted by the
         compiler for named tuples; nobody wrote it and nobody calls it.
         Detected by the `System.Runtime.CompilerServices.` qualifier rather
         than by name, so siblings are covered too.

         The attribute is STRIPPED FROM the line, not used to skip the line.
         Skipping the line lost three real members on the first attempt —
         `AddPolygonAnnotation`, `AddPolyLineAnnotation` and `RedactLetters`
         are declarations carrying an INLINE `[TupleElementNames(...)]`
         parameter attribute, so a line-level skip silently dropped genuine
         `tests-only` findings while looking like noise reduction.

      2. EXTENSION CLASS NAMES. `PdfNumberExtensions` is invoked as
         `someNumber.Foo()`, so the class name never appears at a call site and
         a reference count of zero is guaranteed regardless of use. Deliberate
         trade-off: a genuinely dead extension class is now invisible AS A
         CLASS — its members are still checked individually, which is where the
         real signal is anyway.

      3. OVERRIDES. `OnCreateAutomationPeer` is invoked polymorphically by
         Avalonia; no call site names it. The approved snapshot carries the
         declaration text, so `override` is detectable directly.
    """
    names = set()
    with open(path, encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            # 1. strip compiler-generated attribute names, keeping the rest of
            #    the declaration they may be sitting inside
            line = COMPILER_ATTR.sub(" ", line)
            for m in DECL.finditer(line):
                # 2. extension class names can never appear at a call site
                if m.group(1).endswith("Extensions"):
                    continue
                names.add(m.group(1))
            # 3. overrides are called by the framework, never by name
            if re.search(r"\boverride\b", line):
                continue
            for m in MEMBER.finditer(line):
                names.add(m.group(1))
    return sorted(n for n in names
                  if len(n) >= min_len and n not in NOISE and not n.startswith("op_"))


def main():
    ap = argparse.ArgumentParser(add_help=False)
    ap.add_argument("--min-length", type=int, default=8)
    ap.add_argument("--assembly", default=None)
    ap.add_argument("--quiet", action="store_true")
    ap.add_argument("--baseline", default="tests/unwired-api-baseline.tsv")
    ap.add_argument("--update", action="store_true",
                    help="rewrite the baseline from the current measurement")
    ap.add_argument("-h", "--help", action="store_true")
    args = ap.parse_args()
    if args.help:
        print(__doc__)
        return 0

    root = "."
    approved = approved_files(root)
    if not approved:
        print("FAIL: no PublicApi/*.approved.txt found — the inventory this depends on is missing.",
              file=sys.stderr)
        return 1

    print("==> indexing source")
    prod, test, occ, nfiles = source_index(root)
    print(f"    {nfiles} .cs/.axaml files indexed "
          f"({len(prod)} identifiers in production, {len(test)} in tests)")
    print("    a production reference means: mentioned in a file OTHER than the")
    print("    declaring one, OR used >=2 times within it after nameof(X) is")
    print("    stripped. Files alone gave 18/22 false positives; occurrences")
    print("    alone hid every dead IDisposable behind nameof(X).")

    found = []          # (assembly, state, name)
    total = flagged = tested_only = 0
    for path in approved:
        asm = os.path.basename(path)[: -len(".approved.txt")]
        if args.assembly and asm != args.assembly:
            continue
        names = identifiers(path, args.min_length)
        # Unreferenced in production means BOTH: no file other than the
        # declaring one mentions it, AND the declaring file mentions it only
        # once (i.e. the declaration itself, nameof already stripped).
        pf = lambda n: len(prod.get(n, ()))
        tf = lambda n: len(test.get(n, ()))
        oc = lambda n: occ.get(n, 0)
        unused_in_prod = lambda n: pf(n) <= 1 and oc(n) <= 1
        dead = [n for n in names if unused_in_prod(n) and tf(n) == 0]
        only_tests = [n for n in names if unused_in_prod(n) and tf(n) > 0]
        total += len(names)
        flagged += len(dead)
        tested_only += len(only_tests)
        found += [(asm, "nowhere", n) for n in dead]
        found += [(asm, "tests-only", n) for n in only_tests]
        print(f"\n── {asm}: {len(names)} identifiers >= {args.min_length} chars")
        print(f"     {len(dead)} referenced nowhere;  {len(only_tests)} referenced ONLY by tests")
        if not args.quiet:
            for n in dead:
                print(f"    [nowhere]    {n:<44} prodFiles={pf(n)} occ={oc(n)} testFiles={tf(n)}")
            for n in only_tests:
                print(f"    [tests-only] {n:<44} prodFiles={pf(n)} occ={oc(n)} testFiles={tf(n)}")

    print(f"\n==> of {total} identifiers: {flagged} referenced NOWHERE, "
          f"{tested_only} referenced ONLY BY TESTS")
    print("    CANDIDATES, not verdicts. Excise.Core is a library — public API")
    print("    exists for external consumers, so 'unused here' is not 'unused'.")
    print()
    print("    The [tests-only] list is the interesting one: an API that is")
    print("    implemented and tested but that no production code calls. That is")
    print("    exactly RedactionService.RedactWithOptions, which bundles the")
    print("    metadata scrub with redaction, has 12 test references and 0")
    print("    production callers — and #896 shipped a leak through the CLI")
    print("    because the safe path existed and nothing used it.")

    return baseline_verdict(found, args)


def load_baseline(path):
    known = {}
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) >= 3:
                known[(parts[0], parts[1], parts[2])] = parts[3].strip() if len(parts) >= 4 else ""
    return known


def write_baseline(path, found, previous=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    previous = previous or {}
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(BASELINE_HEADER)
        for row in sorted(found):
            note = previous.get(row) or "UNTRIAGED"
            fh.write("\t".join(row + (note,)) + "\n")


def baseline_verdict(found, args):
    """Ratchet, not a snapshot: NEW unwired API fails, accepted ones do not.

    Without this the check reports 101 pre-existing entries on every run and is
    ignored within a week — the fate of any gate that is red by default. What
    matters is the DELTA: an API added today that nothing calls.
    """
    if args.update:
        write_baseline(args.baseline, found, load_baseline(args.baseline) or {})
        print(f"\n==> baseline rewritten: {args.baseline} ({len(found)} entries)")
        print("    REVIEW THE DIFF. An entry appearing here means something was")
        print("    written and never wired up; accepting it should be a decision.")
        return 0

    known = load_baseline(args.baseline)
    if known is None:
        print(f"\nFAIL: no baseline at {args.baseline}. Create it with --update,")
        print("      review the diff, and commit it.")
        return 1

    current = set(found)
    known_rows = set(known)
    missing_triage = sorted(row for row, note in known.items()
                            if not note or note == "UNTRIAGED")
    new = sorted(current - known_rows)
    gone = sorted(known_rows - current)

    if gone:
        print(f"\n==> {len(gone)} baselined entr(y/ies) no longer unwired — good.")
        for a, st, n in gone[:10]:
            print(f"      {a}.{n} ({st})")
        print("    Run --update to drop them, so the baseline cannot hide a future one.")

    if new:
        print(f"\nFAIL: {len(new)} public member(s) newly referenced only by tests, or not at all:")
        for a, st, n in new:
            print(f"      [{st}] {a}.{n}")
        print()
        print("    Either wire it up, or accept it with --update and say why in the")
        print("    commit. #908 (CffSubsetter: implemented, 25 test refs, zero")
        print("    production callers) and #896 (RedactWithOptions, same shape,")
        print("    shipped a redaction leak) are what this is guarding against.")
        return 1

    if missing_triage:
        print(f"\nFAIL: {len(missing_triage)} baselined unwired API entr(y/ies) lack triage notes:")
        for a, st, n in missing_triage[:20]:
            print(f"      [{st}] {a}.{n}")
        print()
        print("    Add a fourth TSV column explaining why the row is accepted,")
        print("    or link the issue that owns wiring/deleting it. #931 exists")
        print("    because accepted tests-only API without triage hid real bugs.")
        return 1

    print(f"\n==> no NEW unwired API ({len(known)} baselined, all triaged)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
