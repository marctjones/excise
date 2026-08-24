#!/usr/bin/env python3
"""PyMuPDF redaction adapter for the redaction benchmark (#1121).

Usage: redact-pymupdf.py <input.pdf> <output.pdf> <term>
Exit:  0 = redacted (prints the occurrence count), 2 = tool error.

Given PyMuPDF's DOCUMENTED best usage, not a minimal one: every page,
case-insensitive search, and images touched by a redaction removed rather
than left in place. A benchmark that runs a competitor in a weaker mode than
its documentation recommends is measuring the harness author's knowledge, not
the tool.
"""
import sys

try:
    import pymupdf
except ImportError:                                   # older wheels
    import fitz as pymupdf


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: redact-pymupdf.py <in> <out> <term>", file=sys.stderr)
        return 2

    src, dst, term = sys.argv[1], sys.argv[2], sys.argv[3]

    try:
        doc = pymupdf.open(src)
    except Exception as exc:                          # noqa: BLE001
        print(f"open failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2

    removed = 0
    try:
        for page in doc:
            # TEXT_DEHYPHENATE is off deliberately: excise does not join
            # hyphenated words either, so leaving it off keeps the two tools
            # answering the same question.
            hits = page.search_for(term, flags=pymupdf.TEXT_PRESERVE_WHITESPACE)
            for rect in hits:
                page.add_redact_annot(rect)
            if hits:
                # PDF_REDACT_IMAGE_REMOVE is what the PyMuPDF docs recommend
                # when the intent is removal rather than covering.
                page.apply_redactions(images=pymupdf.PDF_REDACT_IMAGE_REMOVE)
                removed += len(hits)

        doc.save(dst, garbage=3, deflate=True)
    except Exception as exc:                          # noqa: BLE001
        print(f"redact failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2
    finally:
        doc.close()

    print(removed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
