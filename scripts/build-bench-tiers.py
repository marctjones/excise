#!/usr/bin/env python3
"""Build the benchmark difficulty tiers B/C/D (#1120).

Tier A (synthetic, tests/redaction-corpus) tells you what is BROKEN. These tiers
tell you whether it MATTERS on documents people actually have — the benchmark's
external validity. The load-bearing property is REPRODUCIBILITY: each tier is a
checked-in list of (corpus, relative-path, sha256), never "whatever was in the
directory", so an input set cannot drift silently between runs.

    Tier B  real-world, producer-diverse   -> tests/bench-tiers/tier-b.tsv
    Tier C  adversarial / malformed-tolerated -> tier-c.tsv  ("fails safely")
    Tier D  known-bad real redactions (x-ray hits) -> tier-d.tsv

Corpora are gitignored and fetched on demand; run this with them present. The
manifests it writes ARE checked in. Re-running with the same corpora is
deterministic (sorted, capped, seeded by content).

Usage: scripts/build-bench-tiers.py [--tier b|c|d|all]
"""
import argparse, hashlib, os, re, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CORPORA = ROOT / "test-pdfs"
OUT = ROOT / "tests" / "bench-tiers"
XRAY_PY = ROOT / "tools" / "vendor" / "xray-venv" / "bin" / "python"

# Tier B: real, produced-by-a-tool documents. Diversity over volume.
TIER_B_CORPORA = ["federal", "local-real-world", "smoke", "sample-pdfs", "pdfua", "itext", "govdocs1"]
# Tier C: malformed-but-tolerated. The property is "fails safely", not "redacts".
TIER_C_CORPORA = ["isartor", "pdfjs", "pdfium", "safedocs"]
# Tier D: sweep these for OTHER people's leaked redactions.
TIER_D_CORPORA = ["federal", "local-real-world", "smoke", "pdfua", "poppler", "govdocs1"]
# OCG (#1111): documents with real optional-content structure — the
# includeHiddenLayers path redaction defaults to. GWG's own sample set is
# membership-gated (its public URL 404s), so per no-third-party-errands we take
# the equivalent from corpora we already hold.
OCG_CORPORA = ["pdfjs", "verapdf-corpus", "ghent", "poppler", "pdfium"]
OCG_CAP = 40

B_CAP, C_CAP, D_CAP = 150, 30, 200
B_PER_PRODUCER = 6
MIN_TEXT = 200


def sha256(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for b in iter(lambda: f.read(1 << 16), b""):
            h.update(b)
    return h.hexdigest()


def pdfs(corpus: str):
    d = CORPORA / corpus
    if not d.is_dir():
        return []
    return sorted(d.rglob("*.pdf"), key=lambda p: str(p))


def text_chars(p: Path) -> int:
    for tool in (["mutool", "draw", "-F", "txt", "-o", "-", str(p), "1"],
                 ["pdftotext", "-f", "1", "-l", "1", str(p), "-"]):
        try:
            out = subprocess.run(tool, capture_output=True, timeout=30).stdout
            return sum(c.isalnum() for c in out.decode("latin-1", "ignore"))
        except Exception:
            continue
    return 0


def producer(p: Path) -> str:
    try:
        raw = p.read_bytes()
        m = re.search(rb"/Producer ?\(([^)]{0,60})", raw)
        return re.sub(r"[^a-z0-9]", "", m.group(1).decode("latin-1", "ignore").lower())[:24] if m else "unknown"
    except Exception:
        return "unknown"


def qpdf_malformed(p: Path) -> bool:
    try:
        r = subprocess.run(["qpdf", "--check", str(p)], capture_output=True, timeout=30)
        if r.returncode == 0:
            return False
        out = (r.stdout + r.stderr).decode("latin-1", "ignore").lower()
        if any(s in out for s in ("operation for file failed", "not a valid pdf", "unable to find")):
            return False   # rubble, not a tolerated stressor
        return True
    except Exception:
        return False


def xray_hits(p: Path) -> bool:
    if not XRAY_PY.exists():
        return False
    try:
        r = subprocess.run(
            [str(XRAY_PY), "-c",
             "import xray,sys;print(1 if xray.inspect(sys.argv[1]) else 0)", str(p)],
            capture_output=True, timeout=60)
        return r.stdout.strip() == b"1"
    except Exception:
        return False


def write(tier: str, rows):
    OUT.mkdir(parents=True, exist_ok=True)
    path = OUT / f"tier-{tier}.tsv"
    with path.open("w", encoding="utf-8") as f:
        f.write("# corpus\trelpath\tsha256\tnote  (#1120, reproducible selection)\n")
        for corpus, rel, sha, note in rows:
            f.write(f"{corpus}\t{rel}\t{sha}\t{note}\n")
    print(f"tier {tier}: {len(rows)} -> {path}")


def build_b():
    rows, per = [], {}
    for corpus in TIER_B_CORPORA:
        for p in pdfs(corpus):
            if len(rows) >= B_CAP:
                break
            if p.stat().st_size > 5 << 20 or text_chars(p) < MIN_TEXT:
                continue
            prod = producer(p)
            if per.get(prod, 0) >= B_PER_PRODUCER:
                continue
            per[prod] = per.get(prod, 0) + 1
            rows.append((corpus, str(p.relative_to(CORPORA / corpus)), sha256(p), f"producer:{prod}"))
    write("b", rows)


def build_c():
    rows = []
    for corpus in TIER_C_CORPORA:
        kept = 0
        for p in pdfs(corpus):
            if len(rows) >= C_CAP or kept >= C_CAP // 2:
                break
            if p.stat().st_size > 8 << 20 or not qpdf_malformed(p):
                continue
            rows.append((corpus, str(p.relative_to(CORPORA / corpus)), sha256(p), "qpdf-malformed"))
            kept += 1
    write("c", rows)


def build_d():
    if not XRAY_PY.exists():
        print("tier d: x-ray venv absent (scripts/download-xray.sh) — skipping", file=sys.stderr)
        return
    rows = []
    for corpus in TIER_D_CORPORA:
        for p in pdfs(corpus):
            if len(rows) >= D_CAP:
                break
            if xray_hits(p):
                rows.append((corpus, str(p.relative_to(CORPORA / corpus)), sha256(p), "xray-bad-redaction"))
    write("d", rows)


def has_ocg(p: Path) -> bool:
    try:
        raw = p.read_bytes()
        return b"/OCProperties" in raw or b"/OCGs" in raw
    except Exception:
        return False


def build_ocg():
    rows = []
    for corpus in OCG_CORPORA:
        for p in pdfs(corpus):
            if len(rows) >= OCG_CAP:
                break
            if p.stat().st_size > 8 << 20 or not has_ocg(p):
                continue
            rows.append((corpus, str(p.relative_to(CORPORA / corpus)), sha256(p), "optional-content"))
    write("ocg", rows)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tier", choices=["b", "c", "d", "ocg", "all"], default="all")
    a = ap.parse_args()
    if a.tier in ("b", "all"): build_b()
    if a.tier in ("c", "all"): build_c()
    if a.tier in ("d", "all"): build_d()
    if a.tier in ("ocg", "all"): build_ocg()


if __name__ == "__main__":
    main()
