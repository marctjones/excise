#!/usr/bin/env python3
"""
Split PDF32000_2008.md into multiple wiki pages by chapter AND section.
Creates cross-references between pages and a concept glossary.
Version 2: Finer granularity for large chapters.
"""

import re
import os
from collections import defaultdict

WIKI_DIR = "/home/marc/excise/wiki"

# Key concepts to cross-reference (concept -> wiki page)
CONCEPTS = {
    # Text-related
    "Tj": "PDF-Spec-09-Text-Showing-Operators",
    "TJ": "PDF-Spec-09-Text-Showing-Operators",
    "text showing operator": "PDF-Spec-09-Text-Showing-Operators",
    "text-showing operator": "PDF-Spec-09-Text-Showing-Operators",
    "Tf": "PDF-Spec-09-Text-State",
    "text state": "PDF-Spec-09-Text-State",
    "text matrix": "PDF-Spec-09-Text-Positioning",
    "Tm": "PDF-Spec-09-Text-Positioning",
    "Td": "PDF-Spec-09-Text-Positioning",
    "BT": "PDF-Spec-09-Text-Objects",
    "ET": "PDF-Spec-09-Text-Objects",
    "text object": "PDF-Spec-09-Text-Objects",
    "font": "PDF-Spec-09-Fonts",
    "glyph": "PDF-Spec-09-Fonts",
    "encoding": "PDF-Spec-09-Fonts",
    "CMap": "PDF-Spec-09-CIDFonts",
    "CIDFont": "PDF-Spec-09-CIDFonts",

    # Graphics-related
    "content stream": "PDF-Spec-07-Content-Streams",
    "graphics state": "PDF-Spec-08-Graphics-State",
    "transformation matrix": "PDF-Spec-08-Coordinate-Systems",
    "CTM": "PDF-Spec-08-Coordinate-Systems",
    "current transformation matrix": "PDF-Spec-08-Coordinate-Systems",
    "path": "PDF-Spec-08-Path-Construction",
    "clipping": "PDF-Spec-08-Clipping-Paths",
    "color space": "PDF-Spec-08-Color-Spaces",
    "image": "PDF-Spec-08-Images",
    "XObject": "PDF-Spec-08-XObjects",

    # Syntax-related
    "indirect object": "PDF-Spec-07-Objects",
    "dictionary": "PDF-Spec-07-Objects",
    "array": "PDF-Spec-07-Objects",
    "stream": "PDF-Spec-07-Streams",
    "filter": "PDF-Spec-07-Filters",
    "cross-reference": "PDF-Spec-07-File-Structure",
    "trailer": "PDF-Spec-07-File-Structure",
}

# Chapter 7 sections (Syntax)
CHAPTER_7_SECTIONS = [
    (9, 28, "PDF-Spec-07-General", "7.1 General"),
    (28, 96, "PDF-Spec-07-Lexical", "7.2 Lexical Conventions"),
    (96, 384, "PDF-Spec-07-Objects", "7.3 Objects"),
    (384, 600, "PDF-Spec-07-Filters", "7.4 Filters"),
    (600, 800, "PDF-Spec-07-File-Structure", "7.5 File Structure"),
    (800, 1200, "PDF-Spec-07-Encryption", "7.6 Encryption"),
    (1200, 1800, "PDF-Spec-07-Content-Streams", "7.7-7.9 Content Streams & Structure"),
    (1800, 2603, "PDF-Spec-07-Resources", "7.10-7.12 Resources & Extensions"),
]

# Chapter 8 sections (Graphics) - line offsets within chapter file
CHAPTER_8_SECTIONS = [
    (9, 100, "PDF-Spec-08-General", "8.1-8.2 General & Coordinate Systems"),
    (100, 400, "PDF-Spec-08-Graphics-State", "8.3-8.4 Graphics State"),
    (400, 800, "PDF-Spec-08-Path-Construction", "8.5 Path Construction & Painting"),
    (800, 1200, "PDF-Spec-08-Clipping-Paths", "8.6 Clipping Paths"),
    (1200, 1800, "PDF-Spec-08-Color-Spaces", "8.7 Color Spaces"),
    (1800, 2200, "PDF-Spec-08-Patterns", "8.8 Patterns"),
    (2200, 2600, "PDF-Spec-08-XObjects", "8.9 External Objects (XObjects)"),
    (2600, 2994, "PDF-Spec-08-Images", "8.10-8.11 Images & Form XObjects"),
]

# Chapter 9 sections (Text)
CHAPTER_9_SECTIONS = [
    (9, 142, "PDF-Spec-09-Organization", "9.1-9.2 Organization & Use of Fonts"),
    (142, 348, "PDF-Spec-09-Text-State", "9.3 Text State Parameters & Operators"),
    (348, 507, "PDF-Spec-09-Text-Objects", "9.4 Text Objects"),
    (507, 800, "PDF-Spec-09-Fonts", "9.5-9.6 Font Data Structures & Simple Fonts"),
    (800, 1100, "PDF-Spec-09-CIDFonts", "9.7 CIDFonts & Composite Fonts"),
    (1100, 1400, "PDF-Spec-09-Font-Descriptors", "9.8 Font Descriptors"),
    (1400, 1608, "PDF-Spec-09-Encoding", "9.9-9.10 Encoding & Text Extraction"),
]

def read_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        return f.readlines()

def write_wiki_page(filename, title, content, prev_page, next_page, parent_page):
    """Write a wiki page with navigation header and footer."""
    filepath = os.path.join(WIKI_DIR, f"{filename}.md")

    nav_header = f"# {title}\n\n"
    nav_header += f"> **Part of**: [[{parent_page}]]\n\n"
    nav_header += "> **Navigation**: "
    nav_header += f"[[{prev_page}|← Previous]]" if prev_page else "← Previous"
    nav_header += f" | [[PDF-Spec-Index|Index]]"
    nav_header += f" | [[{next_page}|Next →]]" if next_page else " | Next →"
    nav_header += "\n\n---\n\n"

    nav_footer = "\n\n---\n\n"
    nav_footer += "> **Navigation**: "
    nav_footer += f"[[{prev_page}|← Previous]]" if prev_page else "← Previous"
    nav_footer += f" | [[PDF-Spec-Index|Index]]"
    nav_footer += f" | [[{next_page}|Next →]]" if next_page else " | Next →"
    nav_footer += "\n"

    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(nav_header)
        f.write(content)
        f.write(nav_footer)

    print(f"Created: {filename}.md ({len(content):,} bytes)")
    return len(content)

def split_chapter_into_sections(chapter_file, sections, chapter_name):
    """Split a chapter file into section files."""
    lines = read_file(chapter_file)
    total_lines = len(lines)
    created = []

    for i, (start, end, filename, title) in enumerate(sections):
        # Clamp end to file length
        actual_end = min(end, total_lines)

        prev_page = sections[i-1][2] if i > 0 else None
        next_page = sections[i+1][2] if i < len(sections) - 1 else None

        content = ''.join(lines[start-1:actual_end-1])
        write_wiki_page(filename, title, content, prev_page, next_page, chapter_name)
        created.append(filename)

    return created

def create_chapter_index(chapter_name, chapter_title, sections):
    """Create an index page for a chapter."""
    content = f"""This chapter covers {chapter_title.lower()}.

## Sections

"""
    for _, _, filename, title in sections:
        content += f"- [[{filename}|{title}]]\n"

    content += """
## Quick Navigation

"""
    return content

def create_concept_glossary():
    """Create a glossary page linking concepts to their definitions."""
    content = """# PDF Specification Glossary

Quick reference for key PDF concepts with links to relevant specification sections.

## Text Operations

| Concept | Description | Specification |
|---------|-------------|---------------|
| **Tj** | Show text string | [[PDF-Spec-09-Text-Objects]] |
| **TJ** | Show text with kerning adjustments | [[PDF-Spec-09-Text-Objects]] |
| **Tf** | Set font and size | [[PDF-Spec-09-Text-State]] |
| **Tm** | Set text matrix | [[PDF-Spec-09-Text-Objects]] |
| **Td** | Move text position | [[PDF-Spec-09-Text-Objects]] |
| **BT/ET** | Begin/End text object | [[PDF-Spec-09-Text-Objects]] |
| Text Matrix | Positions text on page | [[PDF-Spec-09-Text-Objects]] |
| Text State | Font, size, spacing parameters | [[PDF-Spec-09-Text-State]] |

## Graphics Operations

| Concept | Description | Specification |
|---------|-------------|---------------|
| **CTM** | Current Transformation Matrix | [[PDF-Spec-08-General]] |
| **q/Q** | Save/Restore graphics state | [[PDF-Spec-08-Graphics-State]] |
| **cm** | Modify CTM | [[PDF-Spec-08-General]] |
| Content Stream | Sequence of operators | [[PDF-Spec-07-Content-Streams]] |
| Graphics State | Current drawing parameters | [[PDF-Spec-08-Graphics-State]] |
| Path | Shape for stroking/filling | [[PDF-Spec-08-Path-Construction]] |
| XObject | Reusable content (images, forms) | [[PDF-Spec-08-XObjects]] |

## Document Structure

| Concept | Description | Specification |
|---------|-------------|---------------|
| Indirect Object | Numbered, referenceable object | [[PDF-Spec-07-Objects]] |
| Dictionary | Key-value mapping | [[PDF-Spec-07-Objects]] |
| Array | Ordered collection | [[PDF-Spec-07-Objects]] |
| Stream | Binary data with dictionary | [[PDF-Spec-07-Objects]] |
| Cross-Reference Table | Object location index | [[PDF-Spec-07-File-Structure]] |
| Trailer | Document metadata | [[PDF-Spec-07-File-Structure]] |

## Fonts

| Concept | Description | Specification |
|---------|-------------|---------------|
| Simple Font | Type 1, TrueType, Type 3 | [[PDF-Spec-09-Fonts]] |
| CIDFont | CID-keyed fonts (CJK) | [[PDF-Spec-09-CIDFonts]] |
| Composite Font | Type 0 with CIDFont | [[PDF-Spec-09-CIDFonts]] |
| Encoding | Glyph-to-code mapping | [[PDF-Spec-09-Encoding]] |
| ToUnicode | Code-to-Unicode mapping | [[PDF-Spec-09-Encoding]] |
| Font Descriptor | Font metrics/flags | [[PDF-Spec-09-Font-Descriptors]] |

## See Also

- [[PDF-Text-Operators]] - Our documentation on text operators
- [[PDF-Content-Streams]] - Our documentation on content streams
- [[PDF-Coordinate-Systems]] - Coordinate system guide
- [[Redaction-Engine]] - How we use these concepts for redaction
"""

    filepath = os.path.join(WIKI_DIR, "PDF-Spec-Glossary.md")
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print("Created: PDF-Spec-Glossary.md")

def update_index_page():
    """Update the main index page with section links."""
    content = """# ISO 32000-1:2008 - PDF 1.7 Specification

> Complete PDF 1.7 specification split into navigable sections.

## Quick Links

- [[PDF-Spec-Glossary|📖 Concept Glossary]] - Quick reference for key concepts
- [[PDF-Spec-09-Text-Objects|📝 Text Operators]] - Tj, TJ, Td, Tm (most relevant for redaction)
- [[PDF-Spec-08-Graphics-State|🎨 Graphics State]] - q, Q, cm operators
- [[PDF-Spec-07-Content-Streams|📄 Content Streams]] - How PDF content is structured

## Chapters

### Introductory Chapters

| Chapter | Title | Size |
|---------|-------|------|
| [Front Matter](PDF-Spec-Front-Matter) | Disclaimer, Copyright | Small |
| [Chapter 1](PDF-Spec-01-Scope) | Scope | Small |
| [Chapter 2](PDF-Spec-02-Conformance) | Conformance | Small |
| [Chapter 3](PDF-Spec-03-Normative-References) | Normative References | Small |
| [Chapter 4](PDF-Spec-04-Terms-and-Definitions) | Terms and Definitions | Small |
| [Chapter 5](PDF-Spec-05-Notation) | Notation | Small |
| [Chapter 6](PDF-Spec-06-Version-Designations) | Version Designations | Small |

### Core Specification (Split into Sections)

#### Chapter 7: Syntax ⭐
*PDF file structure, objects, content streams*

| Section | Title |
|---------|-------|
| [[PDF-Spec-07-General]] | 7.1 General |
| [[PDF-Spec-07-Lexical]] | 7.2 Lexical Conventions |
| [[PDF-Spec-07-Objects]] | 7.3 Objects (dictionaries, arrays, streams) |
| [[PDF-Spec-07-Filters]] | 7.4 Filters (compression) |
| [[PDF-Spec-07-File-Structure]] | 7.5 File Structure |
| [[PDF-Spec-07-Encryption]] | 7.6 Encryption |
| [[PDF-Spec-07-Content-Streams]] | 7.7-7.9 Content Streams & Structure |
| [[PDF-Spec-07-Resources]] | 7.10-7.12 Resources & Extensions |

#### Chapter 8: Graphics ⭐
*Drawing, color, images, transformations*

| Section | Title |
|---------|-------|
| [[PDF-Spec-08-General]] | 8.1-8.2 General & Coordinate Systems |
| [[PDF-Spec-08-Graphics-State]] | 8.3-8.4 Graphics State |
| [[PDF-Spec-08-Path-Construction]] | 8.5 Path Construction & Painting |
| [[PDF-Spec-08-Clipping-Paths]] | 8.6 Clipping Paths |
| [[PDF-Spec-08-Color-Spaces]] | 8.7 Color Spaces |
| [[PDF-Spec-08-Patterns]] | 8.8 Patterns |
| [[PDF-Spec-08-XObjects]] | 8.9 External Objects (XObjects) |
| [[PDF-Spec-08-Images]] | 8.10-8.11 Images & Form XObjects |

#### Chapter 9: Text ⭐⭐ (Most Important for Redaction)
*Text operators, fonts, encoding*

| Section | Title |
|---------|-------|
| [[PDF-Spec-09-Organization]] | 9.1-9.2 Organization & Use of Fonts |
| [[PDF-Spec-09-Text-State]] | 9.3 Text State Parameters & Operators |
| [[PDF-Spec-09-Text-Objects]] | 9.4 Text Objects (BT, ET, Tj, TJ, Td, Tm) |
| [[PDF-Spec-09-Fonts]] | 9.5-9.6 Font Data Structures & Simple Fonts |
| [[PDF-Spec-09-CIDFonts]] | 9.7 CIDFonts & Composite Fonts |
| [[PDF-Spec-09-Font-Descriptors]] | 9.8 Font Descriptors |
| [[PDF-Spec-09-Encoding]] | 9.9-9.10 Encoding & Text Extraction |

### Other Chapters

| Chapter | Title |
|---------|-------|
| [Chapter 10](PDF-Spec-10-Rendering) | Rendering |
| [Chapter 11](PDF-Spec-11-Transparency) | Transparency |
| [Chapter 12](PDF-Spec-12-Interactive-Features) | Interactive Features |
| [Chapter 13](PDF-Spec-13-Multimedia-Features) | Multimedia Features |
| [Chapter 14](PDF-Spec-14-Document-Interchange) | Document Interchange |
| [Annexes A-L](PDF-Spec-Annexes) | Operator Summary, Examples |

## Key Concepts for Redaction

### Text Removal Pipeline
1. **Parse content stream** → [[PDF-Spec-07-Content-Streams]]
2. **Track graphics state** (CTM) → [[PDF-Spec-08-Graphics-State]]
3. **Track text state** (font, matrix) → [[PDF-Spec-09-Text-State]]
4. **Find text operators** (Tj, TJ) → [[PDF-Spec-09-Text-Objects]]
5. **Calculate glyph positions** → [[PDF-Spec-09-Organization]]
6. **Rebuild content stream** → [[PDF-Spec-07-Content-Streams]]

### See Also
- [[Redaction-Engine]] - How excise implements redaction
- [[PDF-Content-Streams]] - Our content stream documentation
- [[PDF-Text-Operators]] - Our text operator guide
"""

    filepath = os.path.join(WIKI_DIR, "PDF-Spec-Index.md")
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print("Updated: PDF-Spec-Index.md")

def main():
    print("Splitting large chapters into sections...\n")

    # Split Chapter 7 (Syntax)
    ch7_file = os.path.join(WIKI_DIR, "PDF-Spec-07-Syntax.md")
    if os.path.exists(ch7_file):
        print("=== Chapter 7: Syntax ===")
        split_chapter_into_sections(ch7_file, CHAPTER_7_SECTIONS, "PDF-Spec-07-Syntax")
        print()

    # Split Chapter 8 (Graphics)
    ch8_file = os.path.join(WIKI_DIR, "PDF-Spec-08-Graphics.md")
    if os.path.exists(ch8_file):
        print("=== Chapter 8: Graphics ===")
        split_chapter_into_sections(ch8_file, CHAPTER_8_SECTIONS, "PDF-Spec-08-Graphics")
        print()

    # Split Chapter 9 (Text)
    ch9_file = os.path.join(WIKI_DIR, "PDF-Spec-09-Text.md")
    if os.path.exists(ch9_file):
        print("=== Chapter 9: Text ===")
        split_chapter_into_sections(ch9_file, CHAPTER_9_SECTIONS, "PDF-Spec-09-Text")
        print()

    # Create glossary
    print("=== Creating Glossary ===")
    create_concept_glossary()
    print()

    # Update index
    print("=== Updating Index ===")
    update_index_page()
    print()

    print("Done! Created section pages for chapters 7, 8, 9 + glossary")
    print("\nNote: Original chapter files preserved. Delete them if desired:")
    print("  rm wiki/PDF-Spec-07-Syntax.md wiki/PDF-Spec-08-Graphics.md wiki/PDF-Spec-09-Text.md")

if __name__ == "__main__":
    main()
