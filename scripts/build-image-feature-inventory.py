#!/usr/bin/env python3
"""Inventory PDF image/filter features and produce focused page manifests.

This is intentionally a lightweight corpus triage tool, not a full PDF parser.
It scans image stream dictionaries and records the filters, color spaces, masks,
decode arrays, and bit depths needed for spec-driven rendering coverage.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


NAME_RE = rb"/([A-Za-z0-9_.#-]+)"
IMAGE_RE = re.compile(rb"/Subtype\s*/Image\b")
FILTER_RE = re.compile(rb"/Filter\s*(?P<value>\[[^\]]+\]|/[A-Za-z0-9_.#-]+)", re.S)
COLORSPACE_RE = re.compile(rb"/(?:ColorSpace|CS)\s*(?P<value>\[[^\]]+\]|/[A-Za-z0-9_.#-]+|\d+\s+\d+\s+R)", re.S)
BPC_RE = re.compile(rb"/BitsPerComponent\s+(\d+)")
WIDTH_RE = re.compile(rb"/(?:Width|W)\s+(\d+)")
HEIGHT_RE = re.compile(rb"/(?:Height|H)\s+(\d+)")


def pdf_paths(corpus: Path) -> list[Path]:
    return sorted(
        p for p in corpus.rglob("*.pdf")
        if ".git" not in p.parts and p.is_file()
    )


def rel_path(corpus: Path, pdf: Path) -> str:
    return pdf.relative_to(corpus).as_posix()


def names_from_value(raw: bytes) -> list[str]:
    return [decode_name(match.group(1)) for match in re.finditer(NAME_RE, raw)]


def decode_name(raw: bytes) -> str:
    text = raw.decode("latin-1", errors="replace")
    return re.sub(r"#([0-9A-Fa-f]{2})", lambda m: chr(int(m.group(1), 16)), text)


def first_number(regex: re.Pattern[bytes], data: bytes) -> int | None:
    match = regex.search(data)
    return int(match.group(1)) if match else None


def read_pdf_bytes(path: Path, max_bytes: int) -> bytes:
    size = path.stat().st_size
    if size > max_bytes:
        # Large print/color suites are valuable but should not pin huge memory
        # while inventorying. The stream dictionaries are typically near the
        # image streams, so a full scan is best; this bound is a safety valve.
        with path.open("rb") as f:
            return f.read(max_bytes)
    return path.read_bytes()


def stream_dictionaries(data: bytes) -> list[bytes]:
    dictionaries: list[bytes] = []
    for match in re.finditer(rb"\bstream\b", data):
        prefix = data[max(0, match.start() - 1024 * 1024):match.start()].rstrip()
        if not prefix.endswith(b">>"):
            continue

        tokens = list(re.finditer(rb"<<|>>", prefix))
        if not tokens or tokens[-1].group(0) != b">>":
            continue

        depth = 1
        for token in reversed(tokens[:-1]):
            if token.group(0) == b">>":
                depth += 1
            else:
                depth -= 1
                if depth == 0:
                    dictionaries.append(prefix[token.start():tokens[-1].end()])
                    break

    return dictionaries


def classify_stream(dictionary: bytes) -> dict[str, Any] | None:
    if not IMAGE_RE.search(dictionary):
        return None

    filters: list[str] = []
    filter_match = FILTER_RE.search(dictionary)
    if filter_match:
        filters = names_from_value(filter_match.group("value"))

    color_spaces: list[str] = []
    color_match = COLORSPACE_RE.search(dictionary)
    if color_match:
        color_spaces = names_from_value(color_match.group("value"))

    has_decode = b"/Decode" in dictionary
    has_decode_parms = b"/DecodeParms" in dictionary or b"/DP" in dictionary
    image_mask = bool(re.search(rb"/ImageMask\s+(?:true|/true)\b", dictionary))
    has_smask = b"/SMask" in dictionary
    has_mask = bool(re.search(rb"/Mask\b", dictionary)) and not has_smask

    features = set()
    for item in filters:
        features.add(f"filter:{item}")
    for item in color_spaces:
        features.add(f"colorspace:{item}")
    if image_mask:
        features.add("image:ImageMask")
    if has_smask:
        features.add("image:SMask")
    if has_mask:
        features.add("image:Mask")
    if has_decode:
        features.add("decode:ExplicitDecode")
    if has_decode_parms:
        features.add("decode:DecodeParms")

    bpc = first_number(BPC_RE, dictionary)
    if bpc is not None:
        features.add(f"bpc:{bpc}")

    return {
        "filters": filters,
        "colorSpaces": color_spaces,
        "bitsPerComponent": bpc,
        "width": first_number(WIDTH_RE, dictionary),
        "height": first_number(HEIGHT_RE, dictionary),
        "imageMask": image_mask,
        "hasSMask": has_smask,
        "hasMask": has_mask,
        "hasDecode": has_decode,
        "hasDecodeParms": has_decode_parms,
        "features": sorted(features),
    }


def classify_pdf(corpus: Path, pdf: Path, max_bytes: int) -> dict[str, Any]:
    relative = rel_path(corpus, pdf)
    entry: dict[str, Any] = {
        "path": relative,
        "size": pdf.stat().st_size,
        "sha256Prefix": sha256_prefix(pdf),
        "status": "OK",
        "imageStreams": [],
        "features": [],
    }

    try:
        data = read_pdf_bytes(pdf, max_bytes)
        truncated = len(data) < pdf.stat().st_size
        if truncated:
            entry["truncatedInventory"] = True
        for index, dictionary in enumerate(stream_dictionaries(data), start=1):
            stream = classify_stream(dictionary)
            if stream is None:
                continue
            stream["streamIndex"] = index
            entry["imageStreams"].append(stream)
    except Exception as ex:
        entry["status"] = "ERROR"
        entry["errorType"] = type(ex).__name__
        entry["errorMessage"] = str(ex)[:240]

    features = sorted({feature for stream in entry["imageStreams"] for feature in stream["features"]})
    entry["features"] = features
    entry["imageStreamCount"] = len(entry["imageStreams"])
    return entry


def sha256_prefix(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()[:16]


def write_page_manifest(path: Path, entries: list[dict[str, Any]], feature: str | None) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as out:
        out.write("path\tpageNumber\tfeature\n")
        for entry in entries:
            if entry["status"] != "OK":
                continue
            features = set(entry["features"])
            if feature and feature not in features:
                continue
            if not features:
                continue
            # Page 0 means "all pages when page-mode=all, otherwise page 1" in
            # pdfe corpus-scan. The inventory is file-level, so this is the
            # safest way to avoid guessing which page owns a resource.
            out.write(f"{entry['path']}\t0\t{feature or 'image:any'}\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", default="test-pdfs", help="Corpus root to scan.")
    parser.add_argument("--matrix", default="test-pdfs/manifests/pdf-image-feature-matrix.json", help="Coverage matrix JSON.")
    parser.add_argument("--output", required=True, help="Inventory JSON output.")
    parser.add_argument("--page-manifest", help="Optional TSV page manifest output.")
    parser.add_argument("--feature", help="Only emit page-manifest rows for this feature id.")
    parser.add_argument("--max-pdf-bytes", type=int, default=512 * 1024 * 1024, help="Safety cap per PDF during inventory.")
    args = parser.parse_args()

    corpus = Path(args.corpus).resolve()
    if not corpus.is_dir():
        print(f"corpus not found: {corpus}", file=sys.stderr)
        return 2

    matrix_path = Path(args.matrix)
    matrix: dict[str, Any] = {}
    if matrix_path.exists():
        matrix = json.loads(matrix_path.read_text(encoding="utf-8"))

    entries = [classify_pdf(corpus, pdf, args.max_pdf_bytes) for pdf in pdf_paths(corpus)]
    feature_counts: Counter[str] = Counter()
    filter_counts: Counter[str] = Counter()
    color_counts: Counter[str] = Counter()
    files_by_feature: dict[str, list[str]] = defaultdict(list)

    for entry in entries:
        for feature in entry["features"]:
            feature_counts[feature] += 1
            files_by_feature[feature].append(entry["path"])
            if feature.startswith("filter:"):
                filter_counts[feature.removeprefix("filter:")] += 1
            if feature.startswith("colorspace:"):
                color_counts[feature.removeprefix("colorspace:")] += 1

    matrix_features = [item["id"] for item in matrix.get("features", []) if isinstance(item, dict) and "id" in item]
    missing_matrix_features = [feature for feature in matrix_features if feature_counts.get(feature, 0) == 0]

    report = {
        "schemaVersion": 1,
        "corpus": str(corpus),
        "matrix": str(matrix_path),
        "pdfCount": len(entries),
        "imagePdfCount": sum(1 for entry in entries if entry["imageStreamCount"] > 0),
        "imageStreamCount": sum(entry["imageStreamCount"] for entry in entries),
        "featureCounts": dict(sorted(feature_counts.items())),
        "filterCounts": dict(sorted(filter_counts.items())),
        "colorSpaceCounts": dict(sorted(color_counts.items())),
        "missingMatrixFeatures": missing_matrix_features,
        "filesByFeature": {key: sorted(value) for key, value in sorted(files_by_feature.items())},
        "entries": entries,
    }

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"wrote inventory: {output}")
    print(f"PDFs: {report['pdfCount']}")
    print(f"PDFs with image streams: {report['imagePdfCount']}")
    print(f"image streams: {report['imageStreamCount']}")
    print("top filters:")
    for name, count in filter_counts.most_common(12):
        print(f"  {name}: {count}")
    if missing_matrix_features:
        print("matrix features with no discovered corpus example:")
        for feature in missing_matrix_features:
            print(f"  {feature}")

    if args.page_manifest:
        write_page_manifest(Path(args.page_manifest), entries, args.feature)
        print(f"wrote page manifest: {args.page_manifest}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
