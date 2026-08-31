#!/usr/bin/env python3
"""Inventory every xUnit rendering test and assign deterministic evidence facets."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "test-pdfs/manifests/pdf-spec-registry/renderer-evidence-map.json"
OUT = ROOT / "test-pdfs/manifests/pdf-spec-registry/generated/renderer-test-evidence-map.json"
CLASS = re.compile(r"\bclass\s+(\w+)")
METHOD = re.compile(r"^\s*public\s+(?:async\s+)?(?:void|Task(?:<[^>]+>)?)\s+(\w+)\s*\(")


def test_methods(path: Path, root: Path) -> list[dict]:
    """Find source-level xUnit methods; a theory is one test contract, not N data rows."""
    current_class = None
    attributes: list[str] = []
    results = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8", errors="ignore").splitlines(), 1):
        if match := CLASS.search(line):
            current_class = match.group(1)
        stripped = line.strip()
        if stripped.startswith("[") or (attributes and not stripped.startswith("public") and not stripped.startswith("private") and not stripped.startswith("protected") and not stripped.startswith("internal") and (stripped.endswith(")") or stripped.endswith("]"))):
            attributes.append(stripped)
            continue
        if match := METHOD.match(line):
            attr = "\n".join(attributes)
            if "Fact" in attr or "Theory" in attr:
                results.append({"path": str(path.relative_to(root)), "class": current_class or "<unknown>", "method": match.group(1), "line": line_number, "xunitKind": "theory" if "Theory" in attr else "fact"})
            attributes = []
            continue
        if stripped and not stripped.startswith("//") and not stripped.startswith("["):
            attributes = []
    return results


def evidence_kind(test: dict) -> str:
    material = f"{test['path']} {test['class']} {test['method']}".lower()
    if any(word in material for word in ("differential", "reference", "parity", "visual")):
        return "differential-candidate"
    if "corpus" in material:
        return "corpus-candidate"
    if "performance" in material or "benchmark" in material:
        return "benchmark-candidate"
    return "unit-candidate"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUT)
    args = parser.parse_args()
    manifest = json.loads(MANIFEST.read_text())
    test_root = ROOT / manifest["testRoot"]
    tests = [test for path in sorted(test_root.rglob("*.cs")) for test in test_methods(path, ROOT)]
    facets = manifest["facets"]
    rows = []
    by_facet = Counter()
    by_parent_mode = Counter()
    digest = hashlib.sha256()
    for test in tests:
        subject = f"{test['path']} {test['class']} {test['method']}".lower().replace("_", "")
        assigned = [facet for facet in facets if facet["patterns"] and any(pattern in subject for pattern in facet["patterns"])]
        if not assigned:
            assigned = [next(facet for facet in facets if facet["id"] == "renderer-integration-general")]
        parent_modes = defaultdict(set)
        for facet in assigned:
            by_facet[facet["id"]] += 1
            for parent in facet["parents"]:
                parent_modes[parent].update(facet["modes"])
        test_id = f"{test['path']}::{test['class']}.{test['method']}"
        digest.update(test_id.encode())
        rows.append({"id": test_id, **test, "evidenceKind": evidence_kind(test), "facets": [facet["id"] for facet in assigned], "parentCapabilityModes": [{"capability": parent, "modes": sorted(modes)} for parent, modes in sorted(parent_modes.items())], "promotion": "review-required"})
        for parent, modes in parent_modes.items():
            for mode in modes:
                by_parent_mode[f"{parent}:{mode}"] += 1
    if not rows:
        raise SystemExit("no xUnit Fact/Theory tests found")
    result = {"schemaVersion": 1, "generatedBy": "scripts/build-renderer-test-evidence-map.py", "policy": manifest["policy"], "testRoot": manifest["testRoot"], "sourceFingerprint": digest.hexdigest(), "tests": rows, "summary": {"testMethods": len(rows), "facets": dict(sorted(by_facet.items())), "parentCapabilityModes": dict(sorted(by_parent_mode.items())), "unclassified": by_facet["renderer-integration-general"], "allTestsMapped": len(rows) == sum(1 for row in rows if row["facets"])} }
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.output} with {len(rows)} xUnit test-method evidence records")


if __name__ == "__main__":
    main()
