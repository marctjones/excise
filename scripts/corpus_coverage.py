#!/usr/bin/env python3
"""Like-for-like corpus coverage arithmetic (#958, #1972).

A corpus expectation manifest has one row per (corpus-relative PDF path, page).
Two different quantities hide in it and must never be compared to each other:

  PDFs   unique paths in the manifest, checkable BEFORE a scan by looking for
         the file on disk (`preflight`). An OBSERVATION used to refuse a
         silently short download; the scan itself is the gate.
  pages  (path, page) keys, checkable only AFTER a scan against the scan
         report's own keys (`pages`). This is the strict gate:
         scripts/run-exploratory-corpus.sh fails the corpus-scan step when any
         manifest page was not scanned or departed from its pinned status.

Extra unmanifested PDFs on disk are reported but never count as coverage and
never as missing. Usage:

  corpus_coverage.py preflight <corpus-dir> <manifest>
      prints: expected_pdfs present_pdfs missing_pdfs extra_pdfs manifest_pages
  corpus_coverage.py line <corpus> <present> <expected> <extra> <pages>
      prints one footer line; exit 0 = every manifest PDF present, 1 = partial
  corpus_coverage.py pages <scan-report.json> <manifest>
      prints: manifest_pages scanned_pages missing_pages   (missing keys on stderr)
"""
from __future__ import annotations

import json
import os
import sys


def read_manifest(path):
    """Return the ordered list of (pdf path, page) keys; comments/blank/bad rows skipped."""
    keys = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) < 2:
                continue
            try:
                keys.append((parts[0], int(parts[1])))
            except ValueError:
                continue
    return keys


def preflight(corpus_dir, manifest):
    keys = read_manifest(manifest)
    expected = sorted({p for p, _ in keys})
    present = [p for p in expected if os.path.isfile(os.path.join(corpus_dir, p))]
    on_disk = 0
    for _root, _dirs, files in os.walk(corpus_dir):
        on_disk += sum(1 for n in files if n.lower().endswith(".pdf"))
    extra = max(0, on_disk - len(present))
    return len(expected), len(present), len(expected) - len(present), extra, len(keys)


def scanned_keys(rows):
    scanned = set()
    for r in rows:
        p = r.get("path") or r.get("file") or r.get("pdf") or ""
        scanned.add((p, int(r.get("pageNumber", r.get("page", 1)) or 1)))
    return scanned


def missing_pages(rows, manifest):
    """Manifest (path, page) keys with no scanned row. Order is manifest order."""
    scanned = scanned_keys(rows)
    return [k for k in read_manifest(manifest) if k not in scanned]


def report_rows(report_path):
    d = json.load(open(report_path))
    return d if isinstance(d, list) else (d.get("results") or d.get("entries") or [])


def coverage_line(corpus, present, expected, extra, pages):
    """(text, complete). Complete means every manifest PDF is on disk, and the manifest is non-empty."""
    complete = expected > 0 and present == expected
    text = f"{corpus:<24} {present:>5} / {expected:>5} PDFs ({pages} manifest pages)"
    if extra:
        text += f"  +{extra} unmanifested PDFs not counted"
    return text, complete


def main(argv):
    if len(argv) == 7 and argv[1] == "line":
        text, complete = coverage_line(argv[2], *(int(a) for a in argv[3:]))
        print(text)
        return 0 if complete else 1
    if len(argv) == 4 and argv[1] == "preflight":
        print(*preflight(argv[2], argv[3]))
        return 0
    if len(argv) == 4 and argv[1] == "pages":
        rows = report_rows(argv[2])
        total = len(read_manifest(argv[3]))
        missing = missing_pages(rows, argv[3])
        for p, n in missing[:20]:
            print(f"    NOT SCANNED  {p}#p{n}", file=sys.stderr)
        print(total, total - len(missing), len(missing))
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
