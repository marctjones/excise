#!/usr/bin/env python3
"""Generate one registry leaf per existing Annex A operator evidence record."""
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REGISTRY = ROOT / "test-pdfs/manifests/pdf-spec-registry"
SOURCE = ROOT / "test-pdfs/manifests/pdf20-operator-evidence.json"
OUTPUT = REGISTRY / "sections/operators.json"


def refs(items):
    return "; ".join(f"{item['file']}::{item['test']}" for item in items)


def main():
    source = json.loads(SOURCE.read_text(encoding="utf-8"))
    capabilities = []
    for ordinal, item in enumerate(source["operatorEvidence"], start=1):
        evidence = []
        if item.get("unitEvidence"):
            evidence.append({"kind": "unit", "ref": refs(item["unitEvidence"])})
        if item.get("atomicEvidence"):
            evidence.append({"kind": "atomic-fixture", "ref": refs(item["atomicEvidence"])})
        capabilities.append({
            "id": f"pdf.20.content.operator-{ordinal:03d}",
            "name": f"Content-stream operator {item['operator']}",
            "spec": [{"source": "iso-32000-2", "clauses": ["Annex A"]}],
            "classification": "core",
            "decision": {"state": "required", "rationale": "Each standard operator needs an explicit parse and preservation assessment; authoring remains separately scoped."},
            "modes": {"parse": "unknown", "preserve": "unknown", "render": "unknown", "write": "unknown"},
            "evidence": evidence,
            "verification": {"status": "executable", "requiredModes": ["parse", "preserve"], "checks": [
                *([{"kind": "unit", "ref": refs(item["unitEvidence"]), "modes": ["parse", "preserve"]}] if item.get("unitEvidence") else []),
                *([{"kind": "atomic-fixture", "ref": refs(item["atomicEvidence"]), "modes": ["parse", "preserve"]}] if item.get("atomicEvidence") else [])
            ]},
            "notes": f"Generated deterministically from legacy stable ID {item['id']}; operator token is {item['operator']!r}.",
            "documentation": ["test-pdfs/manifests/pdf20-operator-evidence.json"]
        })
    OUTPUT.write_text(json.dumps({"$schema": "../schemas/section.schema.json", "schemaVersion": 1, "section": "operators", "capabilities": capabilities}, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {OUTPUT.relative_to(ROOT)} with {len(capabilities)} operator leaves")


if __name__ == "__main__":
    main()
