#!/usr/bin/env python3
"""
Split remaining large PDF spec files into subsections.
Version 3: Handle Chapters 10-14 and Annexes.
"""

import os

WIKI_DIR = "/home/marc/excise/wiki"

def read_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        return f.readlines()

def write_wiki_page(filename, title, content, prev_page, next_page, parent_page):
    """Write a wiki page with navigation."""
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

    size_kb = len(content) / 1024
    print(f"  Created: {filename}.md ({size_kb:.0f}KB)")

def split_file(source_file, sections, parent_name):
    """Split a file into sections."""
    if not os.path.exists(source_file):
        print(f"  Skipping {source_file} (not found)")
        return

    lines = read_file(source_file)
    total_lines = len(lines)

    for i, (start, end, filename, title) in enumerate(sections):
        actual_end = min(end, total_lines)
        prev_page = sections[i-1][2] if i > 0 else None
        next_page = sections[i+1][2] if i < len(sections) - 1 else None
        content = ''.join(lines[start-1:actual_end-1])
        write_wiki_page(filename, title, content, prev_page, next_page, parent_name)

# Annex sections (from PDF-Spec-Annexes.md)
ANNEX_SECTIONS = [
    (7, 124, "PDF-Spec-Annex-A", "Annex A: Operator Summary"),
    (124, 178, "PDF-Spec-Annex-B", "Annex B: Operators in Type 4 Functions"),
    (178, 231, "PDF-Spec-Annex-C", "Annex C: Implementation Limits"),
    (231, 979, "PDF-Spec-Annex-D", "Annex D: Character Sets and Encodings"),
    (979, 1017, "PDF-Spec-Annex-E", "Annex E: PDF Name Registry"),
    (1017, 1573, "PDF-Spec-Annex-F", "Annex F: Linearized PDF"),
    (1573, 1661, "PDF-Spec-Annex-G", "Annex G: Example PDF Files"),
    (1661, 2019, "PDF-Spec-Annex-H", "Annex H: Compatibility and Implementation Notes"),
    (2019, 2051, "PDF-Spec-Annex-I", "Annex I: PDF Versions and Compatibility"),
    (2051, 2059, "PDF-Spec-Annex-J", "Annex J: PDF Subsets"),
    (2059, 2092, "PDF-Spec-Annex-K", "Annex K: XMP Metadata"),
    (2092, 2500, "PDF-Spec-Annex-L", "Annex L: Bibliography"),
]

# Chapter 10 sections (Rendering)
CHAPTER_10_SECTIONS = [
    (9, 52, "PDF-Spec-10-General", "10.1-10.2 General & CIE-Based Color"),
    (52, 181, "PDF-Spec-10-Color-Conversions", "10.3-10.4 Color Conversions & Transfer Functions"),
    (181, 550, "PDF-Spec-10-Halftones", "10.5 Halftones"),
    (550, 800, "PDF-Spec-10-Scan-Conversion", "10.6 Scan Conversion"),
]

# Chapter 11 sections (Transparency)
CHAPTER_11_SECTIONS = [
    (9, 200, "PDF-Spec-11-Model", "11.1-11.3 Transparency Model"),
    (200, 500, "PDF-Spec-11-Groups", "11.4 Transparency Groups"),
    (500, 800, "PDF-Spec-11-Soft-Masks", "11.5-11.6 Soft Masks & Color"),
    (800, 1200, "PDF-Spec-11-Specifying", "11.7 Specifying Transparency"),
]

# Chapter 12 sections (Interactive Features)
CHAPTER_12_SECTIONS = [
    (9, 79, "PDF-Spec-12-General", "12.1-12.2 General & Viewer Preferences"),
    (79, 288, "PDF-Spec-12-Navigation", "12.3 Document-Level Navigation"),
    (288, 462, "PDF-Spec-12-Page-Navigation", "12.4 Page-Level Navigation"),
    (462, 1100, "PDF-Spec-12-Annotations", "12.5 Annotations"),
    (1100, 1600, "PDF-Spec-12-Actions", "12.6 Actions"),
    (1600, 2200, "PDF-Spec-12-Forms", "12.7 Interactive Forms"),
    (2200, 2700, "PDF-Spec-12-Signatures", "12.8 Digital Signatures"),
    (2700, 2910, "PDF-Spec-12-Security", "12.9 Security"),
]

# Chapter 13 sections (Multimedia Features)
CHAPTER_13_SECTIONS = [
    (9, 400, "PDF-Spec-13-Multimedia", "13.1-13.2 Multimedia"),
    (400, 800, "PDF-Spec-13-Sounds", "13.3 Sounds"),
    (800, 1400, "PDF-Spec-13-Movies", "13.4 Movies"),
    (1400, 2000, "PDF-Spec-13-3D", "13.5-13.6 3D Artwork"),
]

# Chapter 14 sections (Document Interchange)
CHAPTER_14_SECTIONS = [
    (9, 400, "PDF-Spec-14-Metadata", "14.1-14.3 Metadata & File Identifiers"),
    (400, 800, "PDF-Spec-14-Page-Piece", "14.4-14.5 Page-Piece & Marked Content"),
    (800, 1200, "PDF-Spec-14-Structure", "14.6-14.7 Logical Structure"),
    (1200, 1800, "PDF-Spec-14-Accessibility", "14.8 Tagged PDF & Accessibility"),
    (1800, 2117, "PDF-Spec-14-Web-Capture", "14.9-14.11 Web Capture & Prepress"),
]

def main():
    print("Splitting remaining large files...\n")

    print("=== Annexes (4.8MB → 12 files) ===")
    split_file(
        os.path.join(WIKI_DIR, "PDF-Spec-Annexes.md"),
        ANNEX_SECTIONS,
        "PDF-Spec-Annexes"
    )

    print("\n=== Chapter 10: Rendering (1.4MB → 4 files) ===")
    split_file(
        os.path.join(WIKI_DIR, "PDF-Spec-10-Rendering.md"),
        CHAPTER_10_SECTIONS,
        "PDF-Spec-10-Rendering"
    )

    print("\n=== Chapter 11: Transparency (156KB → 4 files) ===")
    split_file(
        os.path.join(WIKI_DIR, "PDF-Spec-11-Transparency.md"),
        CHAPTER_11_SECTIONS,
        "PDF-Spec-11-Transparency"
    )

    print("\n=== Chapter 12: Interactive Features (1.1MB → 8 files) ===")
    split_file(
        os.path.join(WIKI_DIR, "PDF-Spec-12-Interactive-Features.md"),
        CHAPTER_12_SECTIONS,
        "PDF-Spec-12-Interactive-Features"
    )

    print("\n=== Chapter 13: Multimedia Features (1.3MB → 4 files) ===")
    split_file(
        os.path.join(WIKI_DIR, "PDF-Spec-13-Multimedia-Features.md"),
        CHAPTER_13_SECTIONS,
        "PDF-Spec-13-Multimedia-Features"
    )

    print("\n=== Chapter 14: Document Interchange (689KB → 5 files) ===")
    split_file(
        os.path.join(WIKI_DIR, "PDF-Spec-14-Document-Interchange.md"),
        CHAPTER_14_SECTIONS,
        "PDF-Spec-14-Document-Interchange"
    )

    print("\n" + "="*50)
    print("Done! Created 37 additional section files.")
    print("\nTo remove original large files:")
    print("  rm wiki/PDF-Spec-Annexes.md wiki/PDF-Spec-10-Rendering.md \\")
    print("     wiki/PDF-Spec-11-Transparency.md wiki/PDF-Spec-12-Interactive-Features.md \\")
    print("     wiki/PDF-Spec-13-Multimedia-Features.md wiki/PDF-Spec-14-Document-Interchange.md")

if __name__ == "__main__":
    main()
