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
# Format: assembly <TAB> state <TAB> identifier
#   nowhere     no reference at all beyond the declaration
#   tests-only  referenced by tests, never by production  <- the dangerous one
#
# "tests-only" is the shape that shipped bugs: #908 (CffSubsetter, implemented,
# 25 test references, zero production callers, so CFF fonts ship unsubsetted)
# and #896 (RedactWithOptions, same shape, and the CLI leaked redacted terms
# into /Info and XMP for exactly as long as nothing called the safe path).
"""

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


def source_index(root):
    """Identifier occurrences across .cs and .axaml, split production vs test."""
    prod = defaultdict(int)
    test = defaultdict(int)
    files = 0
    word = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if not fn.endswith((".cs", ".axaml")):
                continue
            full = os.path.join(dirpath, fn)
            bucket = test if is_test_path(full) else prod
            files += 1
            try:
                with open(full, encoding="utf-8", errors="ignore") as fh:
                    for line in fh:
                        for m in set(word.findall(line)):
                            bucket[m] += 1
            except OSError:
                continue
    return prod, test, files


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
    names = set()
    with open(path, encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            for m in DECL.finditer(line):
                names.add(m.group(1))
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
    prod, test, nfiles = source_index(root)
    print(f"    {nfiles} .cs/.axaml files indexed "
          f"({len(prod)} identifiers in production, {len(test)} in tests)")

    found = []          # (assembly, state, name)
    total = flagged = tested_only = 0
    for path in approved:
        asm = os.path.basename(path)[: -len(".approved.txt")]
        if args.assembly and asm != args.assembly:
            continue
        names = identifiers(path, args.min_length)
        # The declaration itself lives in production, so <=1 production
        # reference means nothing in the app calls it.
        dead = [n for n in names if prod.get(n, 0) <= 1 and test.get(n, 0) == 0]
        only_tests = [n for n in names if prod.get(n, 0) <= 1 and test.get(n, 0) > 0]
        total += len(names)
        flagged += len(dead)
        tested_only += len(only_tests)
        found += [(asm, "nowhere", n) for n in dead]
        found += [(asm, "tests-only", n) for n in only_tests]
        print(f"\n── {asm}: {len(names)} identifiers >= {args.min_length} chars")
        print(f"     {len(dead)} referenced nowhere;  {len(only_tests)} referenced ONLY by tests")
        if not args.quiet:
            for n in dead:
                print(f"    [nowhere]    {n:<44} prod={prod.get(n,0)} test={test.get(n,0)}")
            for n in only_tests:
                print(f"    [tests-only] {n:<44} prod={prod.get(n,0)} test={test.get(n,0)}")

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
    known = set()
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) >= 3:
                known.add((parts[0], parts[1], parts[2]))
    return known


def write_baseline(path, found):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(BASELINE_HEADER)
        for row in sorted(found):
            fh.write("\t".join(row) + "\n")


def baseline_verdict(found, args):
    """Ratchet, not a snapshot: NEW unwired API fails, accepted ones do not.

    Without this the check reports 101 pre-existing entries on every run and is
    ignored within a week — the fate of any gate that is red by default. What
    matters is the DELTA: an API added today that nothing calls.
    """
    if args.update:
        write_baseline(args.baseline, found)
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
    new = sorted(current - known)
    gone = sorted(known - current)

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

    print(f"\n==> no NEW unwired API ({len(known)} baselined)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
