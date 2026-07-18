#!/usr/bin/env python3
"""
Split PDF32000_2008.md into multiple wiki pages by chapter.
Creates cross-references between pages.
"""

import re
import os

INPUT_FILE = "/home/marc/excise/PDF32000_2008.md"
WIKI_DIR = "/home/marc/excise/wiki"

# Chapter definitions: (start_line, end_line_exclusive, filename, title)
# Line numbers are 1-indexed from grep output
CHAPTERS = [
    (1, 196, "PDF-Spec-Front-Matter", "PDF 1.7 Specification - Front Matter"),
    (196, 208, "PDF-Spec-01-Scope", "PDF Spec Chapter 1: Scope"),
    (208, 228, "PDF-Spec-02-Conformance", "PDF Spec Chapter 2: Conformance"),
    (228, 404, "PDF-Spec-03-Normative-References", "PDF Spec Chapter 3: Normative References"),
    (404, 700, "PDF-Spec-04-Terms-and-Definitions", "PDF Spec Chapter 4: Terms and Definitions"),
    (700, 710, "PDF-Spec-05-Notation", "PDF Spec Chapter 5: Notation"),
    (710, 714, "PDF-Spec-06-Version-Designations", "PDF Spec Chapter 6: Version Designations"),
    (714, 3316, "PDF-Spec-07-Syntax", "PDF Spec Chapter 7: Syntax"),
    (3316, 6309, "PDF-Spec-08-Graphics", "PDF Spec Chapter 8: Graphics"),
    (6309, 7917, "PDF-Spec-09-Text", "PDF Spec Chapter 9: Text"),
    (7917, 8482, "PDF-Spec-10-Rendering", "PDF Spec Chapter 10: Rendering"),
    (8482, 9591, "PDF-Spec-11-Transparency", "PDF Spec Chapter 11: Transparency"),
    (9591, 12501, "PDF-Spec-12-Interactive-Features", "PDF Spec Chapter 12: Interactive Features"),
    (12501, 13876, "PDF-Spec-13-Multimedia-Features", "PDF Spec Chapter 13: Multimedia Features"),
    (13876, 16141, "PDF-Spec-14-Document-Interchange", "PDF Spec Chapter 14: Document Interchange"),
    (16141, 18369, "PDF-Spec-Annexes", "PDF Spec Annexes A-L"),
]

def read_lines(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        return f.readlines()

def write_wiki_page(filename, title, content, prev_page, next_page):
    """Write a wiki page with navigation header and footer."""
    filepath = os.path.join(WIKI_DIR, f"{filename}.md")

    nav_header = f"# {title}\n\n"
    nav_header += "> **Navigation**: "
    nav_header += f"[← Previous]({prev_page})" if prev_page else "← Previous"
    nav_header += f" | [Index](PDF-Spec-Index)"
    nav_header += f" | [Next →]({next_page})" if next_page else " | Next →"
    nav_header += "\n\n---\n\n"

    nav_footer = "\n\n---\n\n"
    nav_footer += "> **Navigation**: "
    nav_footer += f"[← Previous]({prev_page})" if prev_page else "← Previous"
    nav_footer += f" | [Index](PDF-Spec-Index)"
    nav_footer += f" | [Next →]({next_page})" if next_page else " | Next →"
    nav_footer += "\n"

    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(nav_header)
        f.write(content)
        f.write(nav_footer)

    print(f"Created: {filename}.md ({len(content)} bytes)")

def create_index_page():
    """Create the main index page for the PDF spec."""
    content = """# ISO 32000-1:2008 - PDF 1.7 Specification

> This is the complete PDF 1.7 specification (ISO 32000-1:2008), split into chapters for easier navigation.

## Overview

PDF (Portable Document Format) is an open standard for document exchange. This specification defines PDF version 1.7, which is the basis for ISO 32000-1:2008.

## Chapters

### Front Matter
- [Front Matter](PDF-Spec-Front-Matter) - Disclaimer, Copyright, Contents

### Main Specification

| Chapter | Title | Description |
|---------|-------|-------------|
| 1 | [Scope](PDF-Spec-01-Scope) | What PDF 1.7 covers |
| 2 | [Conformance](PDF-Spec-02-Conformance) | Reader/writer requirements |
| 3 | [Normative References](PDF-Spec-03-Normative-References) | Referenced standards |
| 4 | [Terms and Definitions](PDF-Spec-04-Terms-and-Definitions) | PDF terminology |
| 5 | [Notation](PDF-Spec-05-Notation) | Syntax notation used |
| 6 | [Version Designations](PDF-Spec-06-Version-Designations) | PDF version numbering |
| 7 | [Syntax](PDF-Spec-07-Syntax) | PDF file structure, objects |
| 8 | [Graphics](PDF-Spec-08-Graphics) | Drawing, color, images |
| 9 | [Text](PDF-Spec-09-Text) | Text operators, fonts |
| 10 | [Rendering](PDF-Spec-10-Rendering) | Rendering model |
| 11 | [Transparency](PDF-Spec-11-Transparency) | Transparency model |
| 12 | [Interactive Features](PDF-Spec-12-Interactive-Features) | Actions, annotations, forms |
| 13 | [Multimedia Features](PDF-Spec-13-Multimedia-Features) | Sound, video, 3D |
| 14 | [Document Interchange](PDF-Spec-14-Document-Interchange) | Metadata, accessibility |

### Annexes
- [Annexes A-L](PDF-Spec-Annexes) - Operator summary, implementation notes, examples

## Key Chapters for Redaction

For PDF redaction work, the most relevant chapters are:

- **Chapter 7 (Syntax)** - Content streams, objects, file structure
- **Chapter 8 (Graphics)** - Path operators, clipping, color
- **Chapter 9 (Text)** - Text operators (Tj, TJ, Tf, Tm), fonts, encoding

## External Resources

- [Adobe PDF Reference](https://www.adobe.com/devnet/pdf/pdf_reference.html)
- [ISO 32000-2:2020 (PDF 2.0)](https://www.iso.org/standard/75839.html)
- [PDF Association](https://www.pdfa.org/)

---

*This specification is derived from ISO 32000-1:2008, made available by Adobe Systems under agreement with ISO.*
"""
    filepath = os.path.join(WIKI_DIR, "PDF-Spec-Index.md")
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print("Created: PDF-Spec-Index.md")

def main():
    print("Reading PDF32000_2008.md...")
    lines = read_lines(INPUT_FILE)
    print(f"Read {len(lines)} lines")

    # Create index page
    create_index_page()

    # Create chapter pages
    for i, (start, end, filename, title) in enumerate(CHAPTERS):
        # Get prev/next page names
        prev_page = CHAPTERS[i-1][2] if i > 0 else None
        next_page = CHAPTERS[i+1][2] if i < len(CHAPTERS) - 1 else None

        # Extract content (convert to 0-indexed)
        content = ''.join(lines[start-1:end-1])

        write_wiki_page(filename, title, content, prev_page, next_page)

    print(f"\nCreated {len(CHAPTERS) + 1} wiki pages")
    print("Done!")

if __name__ == "__main__":
    main()
