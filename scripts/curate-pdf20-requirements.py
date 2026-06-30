#!/usr/bin/env python3
"""Validate and normalize the curated PDF 2.0 renderer requirement matrix."""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path
from typing import Any


HARD_GATE_PROFILES = {"RendererCore", "ParserSupport", "SecurityPolicy"}
ALLOWED_PROFILES = HARD_GATE_PROFILES | {
    "PreserveOnly",
    "Interactive",
    "TaggedPdf",
    "Signatures",
    "Multimedia3D",
    "Geospatial",
}
ALLOWED_STATUSES = {
    "COVERED",
    "MISSING_UNIT",
    "MISSING_ATOMIC_PDF",
    "MISSING_CORPUS",
    "FAILING_RENDER",
    "UNSUPPORTED_TRACKED",
    "TRACKED_NON_BLOCKING",
}
REQUIRED_FIELDS = {
    "id",
    "clause",
    "area",
    "profile",
    "obligation",
    "detectFeatures",
    "requiredEvidence",
    "oracle",
    "status",
    "notes",
}


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def validate_requirement(row: dict[str, Any], index: int) -> list[str]:
    errors: list[str] = []
    missing = sorted(REQUIRED_FIELDS - set(row))
    if missing:
        errors.append(f"requirement[{index}] {row.get('id', '<missing id>')} missing fields: {', '.join(missing)}")

    profile = row.get("profile")
    if profile not in ALLOWED_PROFILES:
        errors.append(f"{row.get('id', index)} has unknown profile {profile!r}")

    status = row.get("status")
    if status not in ALLOWED_STATUSES:
        errors.append(f"{row.get('id', index)} has unknown status {status!r}")

    for key in ("id", "clause", "area", "obligation", "oracle", "status"):
        if not str(row.get(key, "")).strip():
            errors.append(f"{row.get('id', index)} has empty {key}")

    if not isinstance(row.get("detectFeatures"), list):
        errors.append(f"{row.get('id', index)} detectFeatures must be a list")
    if not isinstance(row.get("requiredEvidence"), list) or not row.get("requiredEvidence"):
        errors.append(f"{row.get('id', index)} requiredEvidence must be a non-empty list")

    if profile in HARD_GATE_PROFILES:
        if status == "TRACKED_NON_BLOCKING":
            errors.append(f"{row.get('id', index)} is hard-gated but marked TRACKED_NON_BLOCKING")
        if row.get("oracle") == "tracked-only":
            errors.append(f"{row.get('id', index)} is hard-gated but has tracked-only oracle")

    return errors


def validate_operator_inventory(matrix: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    expected = matrix.get("annexAOperators", [])
    if not isinstance(expected, list) or not expected:
        return ["annexAOperators must be a non-empty list"]

    seen = [
        row.get("operator")
        for row in matrix.get("requirements", [])
        if row.get("kind") == "content-operator"
    ]
    counts = Counter(seen)
    missing = [op for op in expected if counts[op] == 0]
    duplicates = sorted(op for op, count in counts.items() if op and count > 1)
    extras = sorted(op for op in counts if op and op not in expected)
    if missing:
        errors.append(f"Annex A operators missing from matrix: {', '.join(missing)}")
    if duplicates:
        errors.append(f"Annex A operators duplicated in matrix: {', '.join(duplicates)}")
    if extras:
        errors.append(f"operators not listed in annexAOperators: {', '.join(extras)}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--matrix", default="test-pdfs/manifests/pdf20-renderer-requirements.json")
    parser.add_argument("--image-matrix", default="test-pdfs/manifests/pdf-image-feature-matrix.json")
    parser.add_argument("--output", help="Optional normalized copy to write.")
    args = parser.parse_args()

    matrix_path = Path(args.matrix)
    matrix = load_json(matrix_path)
    errors: list[str] = []

    if matrix.get("schemaVersion") != 1:
        errors.append("schemaVersion must be 1")
    if not matrix.get("sources"):
        errors.append("sources must be non-empty")
    if not matrix.get("requirements"):
        errors.append("requirements must be non-empty")

    ids = [row.get("id") for row in matrix.get("requirements", [])]
    duplicate_ids = sorted(item for item, count in Counter(ids).items() if item and count > 1)
    if duplicate_ids:
        errors.append(f"duplicate requirement ids: {', '.join(duplicate_ids)}")

    for index, row in enumerate(matrix.get("requirements", [])):
        errors.extend(validate_requirement(row, index))
    errors.extend(validate_operator_inventory(matrix))

    image_matrix = Path(args.image_matrix)
    if not image_matrix.exists():
        errors.append(f"image/filter matrix missing: {image_matrix}")
    else:
        source_ids = {source.get("id") for source in matrix.get("sourceMatrices", []) if isinstance(source, dict)}
        if "pdf-image-feature-matrix" not in source_ids:
            errors.append("sourceMatrices must include pdf-image-feature-matrix so image/filter obligations stay represented")

    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    if args.output:
        output = Path(args.output)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(matrix, indent=2), encoding="utf-8")
        print(f"wrote normalized matrix: {output}")

    profile_counts = Counter(row["profile"] for row in matrix["requirements"])
    status_counts = Counter(row["status"] for row in matrix["requirements"])
    print(f"validated PDF 2.0 matrix: {matrix_path}")
    print(f"requirements: {len(matrix['requirements'])}")
    print("profiles: " + ", ".join(f"{k}={v}" for k, v in sorted(profile_counts.items())))
    print("statuses: " + ", ".join(f"{k}={v}" for k, v in sorted(status_counts.items())))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
