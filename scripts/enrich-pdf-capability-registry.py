#!/usr/bin/env python3
"""Populate uniform leaf-level tracking fields from existing registry evidence.

This is intentionally mechanical: it does not interpret PDF specifications or
invent support.  It makes missing evidence explicit, which lets the validator
and scorecard distinguish an unknown from a claimed implementation.
"""
from __future__ import annotations

import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REGISTRY = ROOT / "test-pdfs/manifests/pdf-spec-registry"
ISSUE = re.compile(r"#(\d+)")


def refs(cap: dict, kind: str) -> list[str]:
    return [item["ref"] for item in cap.get("evidence", []) if item.get("kind") == kind]


def main() -> None:
    registry = json.loads((REGISTRY / "registry.json").read_text())
    revision = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True, capture_output=True, check=True).stdout.strip()
    source_pins = {source["id"]: source.get("sha256") or source.get("revision") or "unversioned-secondary-source" for source in registry["sources"]}
    for listed in registry["sections"]:
        path = REGISTRY / listed["path"]
        section = json.loads(path.read_text())
        for cap in section["capabilities"]:
            all_refs = [item.get("ref", "") for item in cap.get("evidence", [])] + cap.get("documentation", [])
            tracking = cap.setdefault("tracking", {})
            modes = cap["modes"]
            states = set(modes.values())
            if "implemented" in states and cap.get("verification", {}).get("status", "executable") == "executable":
                support_level = "implemented-and-proven"
            elif "implemented" in states or "partial" in states:
                support_level = "implemented-partial"
            elif cap["decision"]["state"] == "blocked":
                support_level = "intentionally-unsupported"
            elif cap["decision"]["state"] == "deferred":
                support_level = "deferred"
            elif "unimplemented" in states:
                support_level = "planned"
            else:
                support_level = "unknown"
            positive = refs(cap, "unit") + refs(cap, "atomic-fixture") + refs(cap, "corpus")
            conservation = refs(cap, "security") + refs(cap, "differential")
            tracking.update({
                "owner": "PDF capability registry",
                "reviewState": "migration-pending" if cap["decision"]["state"] in {"required", "supported"} else "policy-reviewed",
                "implementationRefs": refs(cap, "implementation"),
                "testRefs": refs(cap, "unit") + refs(cap, "security"),
                "fixtureRefs": refs(cap, "atomic-fixture"),
                "corpusRefs": refs(cap, "corpus"),
                "referenceToolRefs": ["reference-tools.json"] if refs(cap, "differential") else [],
                "architectureRefs": refs(cap, "architecture") + [ref for ref in cap.get("documentation", []) if ref.startswith("docs/architecture/") or ref.startswith("architecture/")],
                "issueRefs": sorted({f"#{match}" for ref in all_refs for match in ISSUE.findall(ref)}, key=lambda value: int(value[1:])),
                "knownLimitations": [cap["notes"]] if cap.get("notes") else [],
                "processorRoles": sorted(modes),
                "supportLevel": support_level,
                "normativeSourcePins": [{"source": ref["source"], "pin": source_pins[ref["source"]]} for ref in cap["spec"]],
                "errataStatus": "not-reviewed",
                "lastReviewedCommit": revision,
                "positiveTestRefs": positive,
                "negativeOrConservationTestRefs": conservation,
                "explicitEvidenceGaps": [] if positive and conservation else ["No paired positive and negative/conservation evidence is claimed; do not promote this capability to implemented-and-proven."],
            })
        path.write_text(json.dumps(section, indent=2) + "\n")
        print(f"enriched {path.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
