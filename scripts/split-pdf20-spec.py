#!/usr/bin/env python3
"""
Split ISO 32000-2:2020 (PDF 2.0) specification into wiki pages.
Creates pages with navigation and cross-references.
"""

import os

WIKI_DIR = "/home/marc/excise/wiki"
INPUT_FILE = "/home/marc/excise/ISO-32000-2-2020.md"

def read_lines(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        return f.readlines()

def write_wiki_page(filename, title, content, prev_page, next_page, parent_page):
    """Write a wiki page with navigation."""
    filepath = os.path.join(WIKI_DIR, f"{filename}.md")

    nav_header = f"# {title}\n\n"
    nav_header += f"> **Part of**: [[{parent_page}]]\n\n"
    nav_header += "> **Navigation**: "
    nav_header += f"[[{prev_page}|← Previous]]" if prev_page else "← Previous"
    nav_header += f" | [[PDF20-Spec-Index|Index]]"
    nav_header += f" | [[{next_page}|Next →]]" if next_page else " | Next →"
    nav_header += "\n\n---\n\n"

    nav_footer = "\n\n---\n\n"
    nav_footer += "> **Navigation**: "
    nav_footer += f"[[{prev_page}|← Previous]]" if prev_page else "← Previous"
    nav_footer += f" | [[PDF20-Spec-Index|Index]]"
    nav_footer += f" | [[{next_page}|Next →]]" if next_page else " | Next →"
    nav_footer += "\n"

    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(nav_header)
        f.write(content)
        f.write(nav_footer)

    size_kb = len(content) / 1024
    print(f"  Created: {filename}.md ({size_kb:.0f}KB)")
    return size_kb

def split_chapters(lines):
    """Split into main chapter files."""
    # Chapter definitions: (start_line, end_line, filename, title)
    # Line numbers are 1-indexed
    chapters = [
        (1, 1148, "PDF20-Spec-Front-Matter", "PDF 2.0 - Front Matter"),
        (1148, 1187, "PDF20-Spec-01-Scope", "Chapter 1: Scope"),
        (1187, 1380, "PDF20-Spec-02-Normative-References", "Chapter 2: Normative References"),
        (1380, 1737, "PDF20-Spec-03-Terms", "Chapter 3: Terms and Definitions"),
        (1737, 1837, "PDF20-Spec-04-Notation", "Chapter 4: Notation"),
        (1837, 1858, "PDF20-Spec-05-Version", "Chapter 5: Version Designations"),
        (1858, 1954, "PDF20-Spec-06-Conformance", "Chapter 6: Conformance"),
        (1954, 11086, "PDF20-Spec-07-Syntax", "Chapter 7: Syntax"),
        (11086, 21413, "PDF20-Spec-08-Graphics", "Chapter 8: Graphics"),
        (21413, 26177, "PDF20-Spec-09-Text", "Chapter 9: Text"),
        (26177, 27873, "PDF20-Spec-10-Rendering", "Chapter 10: Rendering"),
        (27873, 31242, "PDF20-Spec-11-Transparency", "Chapter 11: Transparency"),
        (31242, 45341, "PDF20-Spec-12-Interactive", "Chapter 12: Interactive Features"),
        (45341, 53394, "PDF20-Spec-13-Multimedia", "Chapter 13: Multimedia"),
        (53394, 63701, "PDF20-Spec-14-Document", "Chapter 14: Document Interchange"),
    ]

    # Annexes
    annexes = [
        (63701, 64323, "PDF20-Spec-Annex-A", "Annex A: Operator Summary"),
        (64323, 64703, "PDF20-Spec-Annex-B", "Annex B: Type 4 Function Operators"),
        (64703, 64883, "PDF20-Spec-Annex-C", "Annex C: Maximising Portability"),
        (64883, 74341, "PDF20-Spec-Annex-D", "Annex D: Character Sets and Encodings"),
        (74341, 74456, "PDF20-Spec-Annex-E", "Annex E: Extending PDF"),
        (74456, 76271, "PDF20-Spec-Annex-F", "Annex F: Linearized PDF"),
        (76271, 76470, "PDF20-Spec-Annex-G", "Annex G: Linearized PDF Access"),
        (76470, 78341, "PDF20-Spec-Annex-H", "Annex H: Example PDF Files"),
        (78341, 78434, "PDF20-Spec-Annex-I", "Annex I: PDF Versions"),
        (78434, 78611, "PDF20-Spec-Annex-J", "Annex J: XObject Comparison"),
        (78611, 78783, "PDF20-Spec-Annex-K", "Annex K: XFA Forms"),
        (78783, 82265, "PDF20-Spec-Annex-L", "Annex L: Structure Element Relationships"),
        (82265, 82366, "PDF20-Spec-Annex-M", "Annex M: Structure Namespace Differences"),
        (82366, 82535, "PDF20-Spec-Annex-N", "Annex N: Halftone Best Practices"),
        (82535, 82744, "PDF20-Spec-Annex-O", "Annex O: Fragment Identifiers"),
        (82744, 82785, "PDF20-Spec-Annex-P", "Annex P: Blending Colour Space Algorithm"),
        (82785, 83010, "PDF20-Spec-Annex-Q", "Annex Q: Page Transparency Detection"),
    ]

    all_sections = chapters + annexes
    total_lines = len(lines)

    for i, (start, end, filename, title) in enumerate(all_sections):
        actual_end = min(end, total_lines)
        prev_page = all_sections[i-1][2] if i > 0 else None
        next_page = all_sections[i+1][2] if i < len(all_sections) - 1 else None
        content = ''.join(lines[start-1:actual_end-1])
        write_wiki_page(filename, title, content, prev_page, next_page, "PDF20-Spec-Index")

    return all_sections

def create_index():
    """Create the main index page."""
    content = """# ISO 32000-2:2020 - PDF 2.0 Specification

> Complete PDF 2.0 specification split into **navigable pages**.
>
> This specification includes errata from ISO 32000-2:2020/Amd 1.
> Made available by the PDF Association with sponsorship from Adobe, Apryse, and Foxit.

## Quick Links

- [[PDF20-Spec-Glossary|📖 Concept Glossary]] - Quick reference for key concepts
- [[PDF20-Spec-09-Text|📝 Text Operators]] - Tj, TJ, Td, Tm (most relevant for redaction)
- [[PDF20-Spec-08-Graphics|🎨 Graphics]] - Graphics state, paths, images
- [[PDF20-Spec-07-Syntax|📄 Syntax]] - File structure, objects, content streams
- [[PDF20-Spec-Annex-A|📋 Operator Summary]] - Complete operator reference

## What's New in PDF 2.0

Key changes from PDF 1.7:
- Deprecated features removed (XFA forms deprecated)
- Improved accessibility (Tagged PDF enhancements)
- New encryption (AES-256)
- Geospatial features
- 3D improvements
- Page-level output intents

## Chapters

### Introductory Chapters

| Chapter | Title |
|---------|-------|
| [[PDF20-Spec-Front-Matter]] | Disclaimer, Copyright, Contents |
| [[PDF20-Spec-01-Scope]] | Scope |
| [[PDF20-Spec-02-Normative-References]] | Normative References |
| [[PDF20-Spec-03-Terms]] | Terms and Definitions |
| [[PDF20-Spec-04-Notation]] | Notation |
| [[PDF20-Spec-05-Version]] | Version Designations |
| [[PDF20-Spec-06-Conformance]] | Conformance |

---

### Chapter 7: Syntax ⭐
*PDF file structure, objects, content streams*

| Link | Description |
|------|-------------|
| [[PDF20-Spec-07-Syntax]] | Complete chapter (large) |

---

### Chapter 8: Graphics ⭐
*Drawing, color, images, transformations*

| Link | Description |
|------|-------------|
| [[PDF20-Spec-08-Graphics]] | Complete chapter (large) |

---

### Chapter 9: Text ⭐⭐ (Most Important for Redaction)
*Text operators, fonts, encoding*

| Link | Description |
|------|-------------|
| [[PDF20-Spec-09-Text]] | Complete chapter |

---

### Chapter 10: Rendering

| Link | Description |
|------|-------------|
| [[PDF20-Spec-10-Rendering]] | Rendering model |

---

### Chapter 11: Transparency

| Link | Description |
|------|-------------|
| [[PDF20-Spec-11-Transparency]] | Transparency model |

---

### Chapter 12: Interactive Features

| Link | Description |
|------|-------------|
| [[PDF20-Spec-12-Interactive]] | Actions, annotations, forms |

---

### Chapter 13: Multimedia

| Link | Description |
|------|-------------|
| [[PDF20-Spec-13-Multimedia]] | Sound, video, 3D |

---

### Chapter 14: Document Interchange

| Link | Description |
|------|-------------|
| [[PDF20-Spec-14-Document]] | Metadata, accessibility, structure |

---

### Annexes

| Annex | Title |
|-------|-------|
| [[PDF20-Spec-Annex-A]] | Operator Summary ⭐ |
| [[PDF20-Spec-Annex-B]] | Type 4 Function Operators |
| [[PDF20-Spec-Annex-C]] | Maximising Portability |
| [[PDF20-Spec-Annex-D]] | Character Sets and Encodings |
| [[PDF20-Spec-Annex-E]] | Extending PDF |
| [[PDF20-Spec-Annex-F]] | Linearized PDF |
| [[PDF20-Spec-Annex-G]] | Linearized PDF Access Strategies |
| [[PDF20-Spec-Annex-H]] | Example PDF Files |
| [[PDF20-Spec-Annex-I]] | PDF Versions and Compatibility |
| [[PDF20-Spec-Annex-J]] | XObject Comparison |
| [[PDF20-Spec-Annex-K]] | XFA Forms (deprecated) |
| [[PDF20-Spec-Annex-L]] | Structure Element Relationships |
| [[PDF20-Spec-Annex-M]] | Structure Namespace Differences |
| [[PDF20-Spec-Annex-N]] | Halftone Best Practices |
| [[PDF20-Spec-Annex-O]] | Fragment Identifiers |
| [[PDF20-Spec-Annex-P]] | Blending Colour Space Algorithm |
| [[PDF20-Spec-Annex-Q]] | Page Transparency Detection |

---

## Comparison with PDF 1.7

| Feature | PDF 1.7 | PDF 2.0 |
|---------|---------|---------|
| Encryption | RC4, AES-128 | AES-256 required |
| XFA Forms | Supported | Deprecated |
| Tagged PDF | Basic | Enhanced |
| Accessibility | Limited | Full support |
| Digital Signatures | PKCS#7 | PAdES support |

## See Also

- [[PDF-Spec-Index]] - PDF 1.7 Specification (ISO 32000-1:2008)
- [[Redaction-Engine]] - How excise implements redaction
- [[PDF-Content-Streams]] - Content stream documentation

---

*This specification is ISO 32000-2:2020, made available by the PDF Association under sponsorship from Adobe, Apryse, and Foxit.*
"""
    filepath = os.path.join(WIKI_DIR, "PDF20-Spec-Index.md")
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print("Created: PDF20-Spec-Index.md")

def create_glossary():
    """Create a glossary with cross-references."""
    content = """# PDF 2.0 Glossary

> Quick reference for key PDF 2.0 concepts with links to relevant sections.

## Core Concepts

### Document Structure
| Term | Description | See |
|------|-------------|-----|
| **PDF** | Portable Document Format | [[PDF20-Spec-01-Scope]] |
| **Conforming reader** | Application that reads PDF correctly | [[PDF20-Spec-06-Conformance]] |
| **Conforming writer** | Application that creates valid PDF | [[PDF20-Spec-06-Conformance]] |
| **Cross-reference table** | Maps object numbers to byte offsets | [[PDF20-Spec-07-Syntax]] |
| **Trailer** | Points to catalog and cross-ref | [[PDF20-Spec-07-Syntax]] |

### Objects
| Term | Description | See |
|------|-------------|-----|
| **Indirect object** | Object with number and generation | [[PDF20-Spec-07-Syntax]] |
| **Direct object** | Object written inline | [[PDF20-Spec-07-Syntax]] |
| **Stream** | Sequence of bytes | [[PDF20-Spec-07-Syntax]] |
| **Dictionary** | Key-value pairs | [[PDF20-Spec-07-Syntax]] |
| **Array** | Ordered collection | [[PDF20-Spec-07-Syntax]] |

### Content Streams
| Term | Description | See |
|------|-------------|-----|
| **Content stream** | Sequence of operators | [[PDF20-Spec-07-Syntax]] |
| **Operator** | Command in content stream | [[PDF20-Spec-Annex-A]] |
| **Operand** | Argument to operator | [[PDF20-Spec-Annex-A]] |
| **Graphics state** | Current drawing parameters | [[PDF20-Spec-08-Graphics]] |

### Text
| Term | Description | See |
|------|-------------|-----|
| **Text object** | BT...ET block | [[PDF20-Spec-09-Text]] |
| **Text matrix** | Current text transformation | [[PDF20-Spec-09-Text]] |
| **Glyph** | Visual representation of character | [[PDF20-Spec-09-Text]] |
| **Font** | Collection of glyphs | [[PDF20-Spec-09-Text]] |
| **Encoding** | Character to glyph mapping | [[PDF20-Spec-Annex-D]] |

---

## Key Operators

### Text Showing
| Operator | Purpose | See |
|----------|---------|-----|
| `Tj` | Show string | [[PDF20-Spec-09-Text]] |
| `TJ` | Show array with kerning | [[PDF20-Spec-09-Text]] |
| `'` | Move to next line and show | [[PDF20-Spec-09-Text]] |
| `"` | Set spacing, move, show | [[PDF20-Spec-09-Text]] |

### Text Positioning
| Operator | Purpose | See |
|----------|---------|-----|
| `Td` | Move text position | [[PDF20-Spec-09-Text]] |
| `TD` | Move and set leading | [[PDF20-Spec-09-Text]] |
| `Tm` | Set text matrix | [[PDF20-Spec-09-Text]] |
| `T*` | Move to next line | [[PDF20-Spec-09-Text]] |

### Text State
| Operator | Purpose | See |
|----------|---------|-----|
| `Tf` | Set font and size | [[PDF20-Spec-09-Text]] |
| `Tc` | Set character spacing | [[PDF20-Spec-09-Text]] |
| `Tw` | Set word spacing | [[PDF20-Spec-09-Text]] |
| `Tz` | Set horizontal scaling | [[PDF20-Spec-09-Text]] |
| `TL` | Set leading | [[PDF20-Spec-09-Text]] |
| `Tr` | Set rendering mode | [[PDF20-Spec-09-Text]] |

### Graphics State
| Operator | Purpose | See |
|----------|---------|-----|
| `q` | Save graphics state | [[PDF20-Spec-08-Graphics]] |
| `Q` | Restore graphics state | [[PDF20-Spec-08-Graphics]] |
| `cm` | Concatenate matrix | [[PDF20-Spec-08-Graphics]] |
| `w` | Set line width | [[PDF20-Spec-08-Graphics]] |

### Path Construction
| Operator | Purpose | See |
|----------|---------|-----|
| `m` | Move to | [[PDF20-Spec-08-Graphics]] |
| `l` | Line to | [[PDF20-Spec-08-Graphics]] |
| `c` | Cubic Bézier curve | [[PDF20-Spec-08-Graphics]] |
| `re` | Rectangle | [[PDF20-Spec-08-Graphics]] |
| `h` | Close path | [[PDF20-Spec-08-Graphics]] |

### Path Painting
| Operator | Purpose | See |
|----------|---------|-----|
| `S` | Stroke | [[PDF20-Spec-08-Graphics]] |
| `f` | Fill (nonzero) | [[PDF20-Spec-08-Graphics]] |
| `f*` | Fill (even-odd) | [[PDF20-Spec-08-Graphics]] |
| `B` | Fill and stroke | [[PDF20-Spec-08-Graphics]] |

---

## PDF 2.0 New Features

| Feature | Description | See |
|---------|-------------|-----|
| **AES-256** | Required encryption | [[PDF20-Spec-07-Syntax]] |
| **Associated files** | Embedded file improvements | [[PDF20-Spec-14-Document]] |
| **Geospatial** | Geographic data support | [[PDF20-Spec-14-Document]] |
| **Page output intents** | Per-page color management | [[PDF20-Spec-14-Document]] |
| **Rich media** | Enhanced multimedia | [[PDF20-Spec-13-Multimedia]] |

---

## See Also

- [[PDF20-Spec-Index]] - Main PDF 2.0 index
- [[PDF-Spec-Glossary]] - PDF 1.7 glossary
- [[PDF20-Spec-Annex-A]] - Complete operator list
"""
    filepath = os.path.join(WIKI_DIR, "PDF20-Spec-Glossary.md")
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print("Created: PDF20-Spec-Glossary.md")

def main():
    print("Splitting PDF 2.0 specification into wiki pages...\n")

    lines = read_lines(INPUT_FILE)
    print(f"Read {len(lines)} lines from {INPUT_FILE}\n")

    # Create index
    create_index()

    # Create glossary
    create_glossary()

    # Split chapters and annexes
    print("\n=== Chapters and Annexes ===")
    all_sections = split_chapters(lines)

    print(f"\n{'='*50}")
    print(f"Done! Created {len(all_sections) + 2} wiki pages.")
    print("\nPages created:")
    print("  - PDF20-Spec-Index.md (main index)")
    print("  - PDF20-Spec-Glossary.md (concept reference)")
    print(f"  - {len(all_sections)} chapter/annex pages")

if __name__ == "__main__":
    main()
