#!/usr/bin/env python3
"""Inventory PDF image/filter features and produce focused page manifests.

This is intentionally a lightweight corpus triage tool, not a full PDF parser.
It scans image stream dictionaries and records the filters, color spaces, masks,
decode arrays, decode parameters, and bit depths needed for spec-driven
rendering coverage.
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
INT_ENTRY_RE_TEMPLATE = rb"/%s\s+(-?\d+)"
BOOL_ENTRY_RE_TEMPLATE = rb"/%s\s+(true|false|/true|/false)\b"

FILTER_ALIASES = {
    "AHx": "ASCIIHexDecode",
    "A85": "ASCII85Decode",
    "LZW": "LZWDecode",
    "Fl": "FlateDecode",
    "RL": "RunLengthDecode",
    "CCF": "CCITTFaxDecode",
    "DCT": "DCTDecode",
}

FILTER_PARAMETER_KEYS = {
    "Predictor",
    "Colors",
    "Columns",
    "BitsPerComponent",
    "EarlyChange",
    "K",
    "BlackIs1",
    "EncodedByteAlign",
    "EndOfLine",
    "EndOfBlock",
    "DamagedRowsBeforeError",
    "ColorTransform",
    "JBIG2Globals",
}


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


def stream_dictionaries(data: bytes) -> list[tuple[bytes, bytes]]:
    dictionaries: list[tuple[bytes, bytes]] = []
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
                    end = data.find(b"endstream", match.end())
                    encoded = data[match.end():end] if end >= 0 else b""
                    encoded = encoded.lstrip(b"\r\n").rstrip(b"\r\n")
                    dictionaries.append((prefix[token.start():tokens[-1].end()], encoded))
                    break

    return dictionaries


def normalize_filter_name(name: str) -> str:
    return FILTER_ALIASES.get(name, name)


def int_entry(name: str, data: bytes) -> int | None:
    regex = re.compile(INT_ENTRY_RE_TEMPLATE % re.escape(name.encode("ascii")))
    match = regex.search(data)
    return int(match.group(1)) if match else None


def bool_entry(name: str, data: bytes) -> bool | None:
    regex = re.compile(BOOL_ENTRY_RE_TEMPLATE % re.escape(name.encode("ascii")))
    match = regex.search(data)
    if not match:
        return None
    value = match.group(1).lstrip(b"/")
    return value == b"true"


def has_name_entry(name: str, data: bytes) -> bool:
    return bool(re.search(rb"/" + re.escape(name.encode("ascii")) + rb"\b", data))


def is_filter_array(raw: bytes | None) -> bool:
    return bool(raw and raw.lstrip().startswith(b"["))


def predictor_bucket(value: int) -> str | None:
    if value == 1:
        return "1"
    if value == 2:
        return "2"
    if 10 <= value <= 15:
        return "10-15"
    return str(value)


def jpeg_markers(data: bytes) -> set[str]:
    markers: set[str] = set()
    index = 0
    while index + 3 < len(data):
        if data[index] != 0xFF:
            index += 1
            continue

        while index < len(data) and data[index] == 0xFF:
            index += 1
        if index >= len(data):
            break

        marker = data[index]
        index += 1
        if marker in {0x00, 0x01} or 0xD0 <= marker <= 0xD9:
            continue
        if index + 2 > len(data):
            break

        segment_length = int.from_bytes(data[index:index + 2], "big")
        segment_start = index + 2
        segment_end = segment_start + max(0, segment_length - 2)
        segment = data[segment_start:segment_end]

        if marker == 0xC0:
            markers.add("dct:SOF:baseline")
        elif marker == 0xC2:
            markers.add("dct:SOF:progressive")
        elif marker == 0xEE and segment.startswith(b"Adobe") and len(segment) >= 12:
            markers.add("dct:APP14")
            markers.add(f"dct:APP14Transform:{segment[11]}")

        if segment_length < 2:
            break
        index = segment_end
    return markers


def classify_stream(dictionary: bytes, encoded: bytes) -> dict[str, Any] | None:
    if not IMAGE_RE.search(dictionary):
        return None

    filter_raw: bytes | None = None
    filters: list[str] = []
    filter_match = FILTER_RE.search(dictionary)
    if filter_match:
        filter_raw = filter_match.group("value")
        filters = [normalize_filter_name(name) for name in names_from_value(filter_raw)]

    color_spaces: list[str] = []
    color_match = COLORSPACE_RE.search(dictionary)
    if color_match:
        color_spaces = names_from_value(color_match.group("value"))

    has_decode = b"/Decode" in dictionary
    has_decode_parms = b"/DecodeParms" in dictionary or b"/DP" in dictionary
    image_mask = bool(re.search(rb"/ImageMask\s+(?:true|/true)\b", dictionary))
    has_smask = b"/SMask" in dictionary
    mask_match = re.search(rb"/Mask\s*(?P<value>\[[^\]]+\]|\d+\s+\d+\s+R)", dictionary, re.S)
    has_mask = bool(mask_match) and not has_smask
    has_color_key_mask = bool(mask_match and mask_match.group("value").lstrip().startswith(b"["))
    has_interpolate = bool_entry("Interpolate", dictionary)
    width = first_number(WIDTH_RE, dictionary)
    height = first_number(HEIGHT_RE, dictionary)

    features = set()
    for item in filters:
        features.add(f"filter:{item}")
    if filter_raw and is_filter_array(filter_raw):
        features.add("filter:array")
    for raw_name in names_from_value(filter_raw or b""):
        if raw_name in FILTER_ALIASES:
            features.add(f"filter-abbrev:{raw_name}")
    for item in color_spaces:
        features.add(f"colorspace:{item}")
    if image_mask:
        features.add("image:ImageMask")
    if has_smask:
        features.add("image:SMask")
    if has_mask:
        features.add("image:Mask")
    if has_color_key_mask:
        features.add("image:ColorKeyMask")
    if has_interpolate is not None:
        features.add("image:Interpolate")
        features.add(f"image:Interpolate:{str(has_interpolate).lower()}")
    if has_decode:
        features.add("decode:ExplicitDecode")
    if has_decode_parms:
        features.add("decode:DecodeParms")

    decode_parms: dict[str, Any] = {}
    for key in FILTER_PARAMETER_KEYS:
        value = int_entry(key, dictionary)
        if value is not None:
            decode_parms[key] = value
            if key == "Predictor":
                bucket = predictor_bucket(value)
                features.add(f"decodeparms:Predictor:{bucket}")
                features.add(f"decodeparms:PredictorValue:{value}")
            elif key == "K":
                features.add("ccitt:K")
                if value == 0:
                    features.add("ccitt:K:0")
                elif value > 0:
                    features.add("ccitt:K:positive")
                else:
                    features.add("ccitt:K:negative")
            elif key == "ColorTransform":
                features.add("dct:ColorTransform")
                features.add(f"dct:ColorTransform:{value}")
            else:
                features.add(f"decodeparms:{key}")
                features.add(f"decodeparms:{key}:{value}")

        bool_value = bool_entry(key, dictionary)
        if bool_value is not None:
            decode_parms[key] = bool_value
            features.add(f"decodeparms:{key}")
            features.add(f"decodeparms:{key}:{str(bool_value).lower()}")

        if key == "JBIG2Globals" and has_name_entry(key, dictionary):
            decode_parms[key] = True
            features.add("jbig2:Globals")

    if "DCTDecode" in filters:
        features.update(jpeg_markers(encoded[:256 * 1024]))
    if "JPXDecode" in filters:
        if encoded.startswith(b"\x00\x00\x00\x0cjP  "):
            features.add("jpx:JP2Wrapper")
        elif encoded.startswith(b"\xff\x4f"):
            features.add("jpx:Codestream")

    bpc = first_number(BPC_RE, dictionary)
    if bpc is not None:
        features.add(f"bpc:{bpc}")
    if width is not None and height is not None:
        pixels = width * height
        features.add("image:Dimensions")
        if pixels >= 100_000_000:
            features.add("policy:ResourceLimit")
            features.add("policy:HugeDimensions")
        if bpc is not None:
            components = max(1, len(color_spaces) if color_spaces else 1)
            if pixels * components * max(1, bpc) / 8 >= 512 * 1024 * 1024:
                features.add("policy:LargeDecodedBytes")

    return {
        "filters": filters,
        "colorSpaces": color_spaces,
        "bitsPerComponent": bpc,
        "width": width,
        "height": height,
        "imageMask": image_mask,
        "hasSMask": has_smask,
        "hasMask": has_mask,
        "hasColorKeyMask": has_color_key_mask,
        "hasDecode": has_decode,
        "hasDecodeParms": has_decode_parms,
        "decodeParms": decode_parms,
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
    data = b""

    try:
        data = read_pdf_bytes(pdf, max_bytes)
        truncated = len(data) < pdf.stat().st_size
        if truncated:
            entry["truncatedInventory"] = True
        for index, (dictionary, encoded) in enumerate(stream_dictionaries(data), start=1):
            stream = classify_stream(dictionary, encoded)
            if stream is None:
                continue
            stream["streamIndex"] = index
            entry["imageStreams"].append(stream)
    except Exception as ex:
        entry["status"] = "ERROR"
        entry["errorType"] = type(ex).__name__
        entry["errorMessage"] = str(ex)[:240]

    features = {feature for stream in entry["imageStreams"] for feature in stream["features"]}
    if entry["imageStreams"]:
        if re.search(rb"(?:\d+(?:\.\d+)?\s+){6}cm\b(?:(?!EI\b).){0,2048}/[A-Za-z0-9_.#-]+\s+Do\b", data, re.S):
            features.add("placement:ImageCTM")
        if b"/ExtGState" in data or re.search(rb"/[A-Za-z0-9_.#-]+\s+gs\b", data):
            features.add("graphics:ExtGState")

    features = sorted(features)
    entry["features"] = features
    entry["imageStreamCount"] = len(entry["imageStreams"])
    return entry


def sha256_prefix(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()[:16]


def resolve_feature_query(matrix: dict[str, Any], feature: str | None) -> tuple[list[str], str]:
    if not feature:
        return [], "image:any"

    for requirement in matrix.get("requirements", []):
        if isinstance(requirement, dict) and requirement.get("id") == feature:
            return list(requirement.get("detectFeatures", [])), feature

    return [feature], feature


def write_page_manifest(path: Path, entries: list[dict[str, Any]], feature_filters: list[str], label: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as out:
        out.write("path\tpageNumber\tfeature\n")
        for entry in entries:
            if entry["status"] != "OK":
                continue
            features = set(entry["features"])
            if feature_filters and not any(feature in features for feature in feature_filters):
                continue
            if not features:
                continue
            # Page 0 means "all pages when page-mode=all, otherwise page 1" in
            # pdfe corpus-scan. The inventory is file-level, so this is the
            # safest way to avoid guessing which page owns a resource.
            out.write(f"{entry['path']}\t0\t{label}\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", default="test-pdfs", help="Corpus root to scan.")
    parser.add_argument("--matrix", default="test-pdfs/manifests/pdf-image-feature-matrix.json", help="Coverage matrix JSON.")
    parser.add_argument("--output", required=True, help="Inventory JSON output.")
    parser.add_argument("--page-manifest", help="Optional TSV page manifest output.")
    parser.add_argument("--feature", help="Only emit page-manifest rows for this detected feature or matrix requirement id.")
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

    feature_filters, feature_label = resolve_feature_query(matrix, args.feature)
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
    matrix_requirements = [item for item in matrix.get("requirements", []) if isinstance(item, dict) and "id" in item]
    missing_matrix_features = [feature for feature in matrix_features if feature_counts.get(feature, 0) == 0]
    missing_matrix_requirements = []
    for requirement in matrix_requirements:
        detect_features = requirement.get("detectFeatures", [])
        mode = requirement.get("detectMode", "any")
        if not detect_features:
            missing_matrix_requirements.append(requirement["id"])
        elif mode == "all":
            if not all(feature_counts.get(feature, 0) > 0 for feature in detect_features):
                missing_matrix_requirements.append(requirement["id"])
        elif not any(feature_counts.get(feature, 0) > 0 for feature in detect_features):
            missing_matrix_requirements.append(requirement["id"])

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
        "missingMatrixRequirements": missing_matrix_requirements,
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
    if missing_matrix_requirements:
        print("matrix requirements with no discovered corpus example:")
        for requirement in missing_matrix_requirements:
            print(f"  {requirement}")

    if args.page_manifest:
        write_page_manifest(Path(args.page_manifest), entries, feature_filters, feature_label)
        print(f"wrote page manifest: {args.page_manifest}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
