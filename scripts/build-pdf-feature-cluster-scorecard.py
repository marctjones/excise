#!/usr/bin/env python3
"""Build review-sized PDF capability clusters from the authoritative registry.

This is deliberately a drill-down view, not another source of support claims.
It groups the existing leaf-level matrix so issue #1310 can promote evidence in
small, spec-shaped batches (for example image filters or operator families).
"""
from __future__ import annotations

import json
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REG = ROOT / "test-pdfs/manifests/pdf-spec-registry"
OUT = REG / "generated/feature-cluster-scorecard.json"


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def cluster_name(section: str, capability: dict) -> str:
    """Use existing stable legacy facets without inventing feature support."""
    name = capability["name"]
    if ":" in name:
        family, value = name.split(":", 1)
        return f"{family}:{value.split('.', 1)[0]}"
    if section == "operators":
        return name.split(" ", 1)[0]
    return name.split(" — ", 1)[0]


def main():
    registry = load(REG / "registry.json")
    attribution = load(REG / "generated/evidence-attribution.json")
    by_mode = {(row["capability"], row["mode"]): row for row in attribution["modes"]}
    clusters = defaultdict(list)
    for section in registry["sections"]:
        for capability in load(REG / section["path"])["capabilities"]:
            if capability["decision"]["state"] not in {"required", "supported"}:
                continue
            clusters[(section["id"], cluster_name(section["id"], capability))].append(capability)

    rows = []
    for (section, name), capabilities in sorted(clusters.items()):
        modes = [
            (capability, mode, state)
            for capability in capabilities
            for mode, state in capability["modes"].items()
            if state != "not-applicable"
        ]
        states = Counter(state for _, _, state in modes)
        attributed = [by_mode.get((capability["id"], mode), {}) for capability, mode, _ in modes]
        rows.append({
            "section": section,
            "cluster": name,
            "capabilityIds": [capability["id"] for capability in capabilities],
            "targetModes": len(modes),
            "modeStates": dict(sorted(states.items())),
            "unknownModeCount": states["unknown"],
            "explicitContractModes": sum(bool(item.get("explicitContracts")) for item in attributed),
            "passingContractModes": sum(item.get("explicitContractStatus") == "passing" for item in attributed),
            "testCandidateModes": sum(bool(item.get("testCandidates")) for item in attributed),
        })
    result = {
        "schemaVersion": 1,
        "generatedBy": "scripts/build-pdf-feature-cluster-scorecard.py",
        "policy": "Clusters organize existing registry leaves for review. They never infer support from a name, source candidate, or test candidate.",
        "clusters": rows,
        "summary": {
            "clusters": len(rows),
            "targetModes": sum(row["targetModes"] for row in rows),
            "unknownModeCount": sum(row["unknownModeCount"] for row in rows),
        },
    }
    OUT.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {OUT.relative_to(ROOT)} with {len(rows)} review clusters")


if __name__ == "__main__":
    main()
