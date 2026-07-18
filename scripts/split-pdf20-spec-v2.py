#!/usr/bin/env python3
"""
Split large PDF 2.0 chapters into sections.
Version 2: Handle Chapters 7, 8, 9, 12, 13, 14.
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

def split_file(lines, sections, parent_name):
    """Split content into sections."""
    total_lines = len(lines)

    for i, (start, end, filename, title) in enumerate(sections):
        actual_end = min(end, total_lines)
        prev_page = sections[i-1][2] if i > 0 else None
        next_page = sections[i+1][2] if i < len(sections) - 1 else None
        content = ''.join(lines[start-1:actual_end-1])
        write_wiki_page(filename, title, content, prev_page, next_page, parent_name)

# Chapter 7 sections (Syntax) - 317KB original
# Line numbers from content (not TOC)
CHAPTER_7_SECTIONS = [
    (1954, 2016, "PDF20-Spec-07-General", "7.1 General"),
    (2016, 2320, "PDF20-Spec-07-Lexical", "7.2 Lexical Conventions"),
    (2320, 3026, "PDF20-Spec-07-Objects", "7.3 Objects"),
    (3026, 4440, "PDF20-Spec-07-Filters", "7.4 Filters"),
    (4440, 5667, "PDF20-Spec-07-File-Structure", "7.5 File Structure"),
    (5667, 7454, "PDF20-Spec-07-Encryption", "7.6 Encryption"),
    (7454, 8538, "PDF20-Spec-07-Document-Structure", "7.7 Document Structure"),
    (8538, 8811, "PDF20-Spec-07-Content-Streams", "7.8 Content Streams"),
    (8811, 8883, "PDF20-Spec-07-Common-Data", "7.9 Common Data Structures"),
    (8883, 8889, "PDF20-Spec-07-Functions", "7.10-7.11 Functions & File Specs"),
    (8889, 11086, "PDF20-Spec-07-Extensions", "7.12 Extensions Dictionary"),
]

# Chapter 8 sections (Graphics) - 369KB original
CHAPTER_8_SECTIONS = [
    (11086, 11141, "PDF20-Spec-08-General", "8.1 General"),
    (11141, 11379, "PDF20-Spec-08-Coordinate", "8.2 Coordinate Systems"),
    (11379, 11744, "PDF20-Spec-08-Graphics-State", "8.3 Graphics State"),
    (11744, 12856, "PDF20-Spec-08-Graphics-Objects", "8.4 Graphics Objects"),
    (12856, 13370, "PDF20-Spec-08-Path-Construction", "8.5 Path Construction"),
    (13370, 16086, "PDF20-Spec-08-Colour-Spaces", "8.6 Colour Spaces"),
    (16086, 18341, "PDF20-Spec-08-Patterns", "8.7 Patterns"),
    (18341, 18421, "PDF20-Spec-08-XObjects", "8.8 External Objects"),
    (18421, 21413, "PDF20-Spec-08-Images", "8.9-8.11 Images & Form XObjects"),
]

# Chapter 9 sections (Text) - 164KB original
CHAPTER_9_SECTIONS = [
    (21413, 21435, "PDF20-Spec-09-General", "9.1 General"),
    (21435, 21783, "PDF20-Spec-09-Organization", "9.2 Organization and Use of Fonts"),
    (21783, 22143, "PDF20-Spec-09-Text-State", "9.3 Text State Parameters"),
    (22143, 22524, "PDF20-Spec-09-Text-Objects", "9.4 Text Objects"),
    (22524, 22655, "PDF20-Spec-09-Text-Strings", "9.5 Text Strings"),
    (22655, 23759, "PDF20-Spec-09-Simple-Fonts", "9.6 Simple Fonts"),
    (23759, 24931, "PDF20-Spec-09-Composite-Fonts", "9.7 Composite Fonts"),
    (24931, 25619, "PDF20-Spec-09-Font-Descriptors", "9.8 Font Descriptors"),
    (25619, 26177, "PDF20-Spec-09-Embedded-Fonts", "9.9-9.10 Embedded Fonts & Extraction"),
]

# Chapter 12 sections (Interactive Features) - 434KB original
CHAPTER_12_SECTIONS = [
    (31242, 31291, "PDF20-Spec-12-General", "12.1 General"),
    (31291, 31596, "PDF20-Spec-12-Viewer-Prefs", "12.2 Viewer Preferences"),
    (31596, 32997, "PDF20-Spec-12-Doc-Navigation", "12.3 Document-Level Navigation"),
    (32997, 33646, "PDF20-Spec-12-Page-Navigation", "12.4 Page-Level Navigation"),
    (33646, 36960, "PDF20-Spec-12-Annotations", "12.5 Annotations"),
    (36960, 38974, "PDF20-Spec-12-Actions", "12.6 Actions"),
    (38974, 42110, "PDF20-Spec-12-Forms", "12.7 Forms"),
    (42110, 44294, "PDF20-Spec-12-Signatures", "12.8 Digital Signatures"),
    (44294, 44791, "PDF20-Spec-12-Measurement", "12.9 Measurement Properties"),
    (44791, 45163, "PDF20-Spec-12-Geospatial", "12.10 Geospatial Features"),
    (45163, 45341, "PDF20-Spec-12-Requirements", "12.11 Document Requirements"),
]

# Chapter 13 sections (Multimedia) - 240KB original
CHAPTER_13_SECTIONS = [
    (45341, 45674, "PDF20-Spec-13-General", "13.1 General"),
    (45674, 47674, "PDF20-Spec-13-Multimedia", "13.2 Multimedia"),
    (47674, 47808, "PDF20-Spec-13-Sounds", "13.3 Sounds"),
    (47808, 47995, "PDF20-Spec-13-Movies", "13.4 Movies"),
    (47995, 48143, "PDF20-Spec-13-Alternate", "13.5 Alternate Presentations"),
    (48143, 52348, "PDF20-Spec-13-3D", "13.6 3D Artwork"),
    (52348, 53394, "PDF20-Spec-13-Rich-Media", "13.7 Rich Media"),
]

# Chapter 14 sections (Document Interchange) - 324KB original
CHAPTER_14_SECTIONS = [
    (53394, 53461, "PDF20-Spec-14-General", "14.1 General"),
    (53461, 53502, "PDF20-Spec-14-Procedure-Sets", "14.2 Procedure Sets"),
    (53502, 53858, "PDF20-Spec-14-Metadata", "14.3 Metadata"),
    (53858, 53884, "PDF20-Spec-14-File-Identifiers", "14.4 File Identifiers"),
    (53884, 53952, "PDF20-Spec-14-Page-Piece", "14.5 Page-Piece Dictionaries"),
    (53952, 54117, "PDF20-Spec-14-Marked-Content", "14.6 Marked Content"),
    (54117, 56102, "PDF20-Spec-14-Logical-Structure", "14.7 Logical Structure"),
    (56102, 60079, "PDF20-Spec-14-Tagged-PDF", "14.8 Tagged PDF"),
    (60079, 63701, "PDF20-Spec-14-Accessibility", "14.9 Accessibility Support"),
]

def main():
    print("Splitting large PDF 2.0 chapters into sections...\n")

    lines = read_lines(INPUT_FILE)
    print(f"Read {len(lines)} lines\n")

    print("=== Chapter 7: Syntax (317KB → 11 sections) ===")
    split_file(lines, CHAPTER_7_SECTIONS, "PDF20-Spec-07-Syntax")

    print("\n=== Chapter 8: Graphics (369KB → 9 sections) ===")
    split_file(lines, CHAPTER_8_SECTIONS, "PDF20-Spec-08-Graphics")

    print("\n=== Chapter 9: Text (164KB → 9 sections) ===")
    split_file(lines, CHAPTER_9_SECTIONS, "PDF20-Spec-09-Text")

    print("\n=== Chapter 12: Interactive Features (434KB → 11 sections) ===")
    split_file(lines, CHAPTER_12_SECTIONS, "PDF20-Spec-12-Interactive")

    print("\n=== Chapter 13: Multimedia (240KB → 7 sections) ===")
    split_file(lines, CHAPTER_13_SECTIONS, "PDF20-Spec-13-Multimedia")

    print("\n=== Chapter 14: Document Interchange (324KB → 9 sections) ===")
    split_file(lines, CHAPTER_14_SECTIONS, "PDF20-Spec-14-Document")

    total_sections = (len(CHAPTER_7_SECTIONS) + len(CHAPTER_8_SECTIONS) +
                     len(CHAPTER_9_SECTIONS) + len(CHAPTER_12_SECTIONS) +
                     len(CHAPTER_13_SECTIONS) + len(CHAPTER_14_SECTIONS))

    print(f"\n{'='*50}")
    print(f"Done! Created {total_sections} section files.")
    print("\nTo remove original large chapter files:")
    print("  rm wiki/PDF20-Spec-07-Syntax.md wiki/PDF20-Spec-08-Graphics.md \\")
    print("     wiki/PDF20-Spec-09-Text.md wiki/PDF20-Spec-12-Interactive.md \\")
    print("     wiki/PDF20-Spec-13-Multimedia.md wiki/PDF20-Spec-14-Document.md")

if __name__ == "__main__":
    main()
