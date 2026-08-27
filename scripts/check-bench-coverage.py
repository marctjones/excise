#!/usr/bin/env python3
"""Check how complete the redaction bench is against its design (#1185).

Reads the design (tests/redaction-bench-design.json), the real-doc manifest
(tests/redaction-hard-cases.tsv), and the synthetic adversarial corpus
(test-pdfs/redaction-adversarial/), and reports per tier x category:
target / have / gap, plus per-tier and overall completeness.

  python3 scripts/check-bench-coverage.py            # report
  python3 scripts/check-bench-coverage.py --strict   # exit 1 if any gap remains

The manifest points at gitignored corpora, so this checks the DESIGN of the
bench (which categories are populated to target), not whether the PDFs are
present on this machine — that is what the bench's own absent-file skip handles.
"""
import argparse, json, os, sys, glob, collections

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
DESIGN = os.path.join(ROOT, "tests", "redaction-bench-design.json")
MANIFEST = os.path.join(ROOT, "tests", "redaction-hard-cases.tsv")
ADV = os.path.join(ROOT, "test-pdfs", "redaction-adversarial")

# A design category whose synthetic fixture is named differently in the generator.
SYNTH_PREFIX = {"alt-text": "alt-inline", "ocg-hidden": "ocg-hidden",
                "embedded-file": "embedded-file", "actualtext-inline": "actualtext"}


def load_manifest_counts():
    counts = collections.Counter()
    if not os.path.exists(MANIFEST):
        return counts
    for line in open(MANIFEST):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 4:
            continue
        counts[parts[3].strip()] += 1        # category is column 4
    return counts


def synth_categories():
    cats = set()
    for f in glob.glob(os.path.join(ADV, "*.pdf")):
        cats.add(os.path.basename(f).split("--")[0])
    return cats


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--strict", action="store_true", help="exit 1 if any category is below target")
    args = ap.parse_args()

    design = json.load(open(DESIGN))
    have = load_manifest_counts()
    synth = synth_categories()

    grand_target = grand_have = 0
    gaps = []

    for tier, tinfo in design["tiers"].items():
        print(f"\n=== {tier.upper()} — {tinfo['purpose'][:70]}...")
        print(f"    {'category':22} {'src':10} {'have':>4} {'/':>1} {'target':>6}   status")
        print("    " + "-" * 62)
        t_target = t_have = 0
        for cat, c in tinfo["categories"].items():
            target = c["target"]
            src = c["source"]
            real_have = have.get(cat, 0)
            synth_have = 1 if ("synth" in src and SYNTH_PREFIX.get(cat, cat) in synth) else 0
            if src == "synth":
                got = synth_have
            elif src == "real+synth":
                got = real_have + synth_have
            else:
                got = real_have
            t_target += target
            t_have += min(got, target)
            grand_target += target
            grand_have += min(got, target)
            if got >= target:
                status = "OK"
            else:
                status = f"NEED {target - got}"
                gaps.append((tier, cat, target - got, src))
            bar = "#" * min(got, target) + "." * max(0, target - got)
            print(f"    {cat:22} {src:10} {got:>4} {'/':>1} {target:>6}   {bar:8} {status}")
        pct = 100.0 * t_have / t_target if t_target else 100.0
        print(f"    {'':22} {'':10} {t_have:>4} {'/':>1} {t_target:>6}   tier {pct:.0f}% complete")

    pct = 100.0 * grand_have / grand_target if grand_target else 100.0
    print(f"\n=== OVERALL: {grand_have}/{grand_target} = {pct:.0f}% of design target populated")
    print(f"    synthetic carriers present: {len(synth)} ({', '.join(sorted(synth))})")
    if gaps:
        print(f"\n    {len(gaps)} categories below target:")
        for tier, cat, need, src in gaps:
            print(f"      {tier:7} {cat:22} need {need} more ({src})")
    else:
        print("\n    all categories at or above target ✓")

    if args.strict and gaps:
        sys.exit(1)


if __name__ == "__main__":
    main()
