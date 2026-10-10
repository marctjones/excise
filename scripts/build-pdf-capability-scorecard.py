#!/usr/bin/env python3
"""Generate transparent, non-compensating PDF capability scorecards."""
from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT = ROOT / "test-pdfs/manifests/pdf-spec-registry"


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def promotion_readiness(row, mode, collected):
    """Return evidence-backed progress toward, but never equivalent to, support.

    The ladder deliberately tops out at 90 until the reviewed mode state is
    implemented.  This lets planning see distance to a strict claim without
    treating source search, a unit test, or a legacy matrix as conformance.
    """
    if row["modes"][mode] == "implemented":
        return 100
    evidence = row.get("evidence", [])
    verification = row.get("verification", {})
    checks = verification.get("checks", []) if verification.get("status") == "executable" else []
    declared_for_mode = mode in verification.get("requiredModes", [])
    has = lambda kinds: any(item.get("kind") in kinds for item in evidence)
    check = lambda kinds: any(item.get("kind") in kinds and mode in item.get("modes", []) for item in checks)
    score = 0
    if evidence or collected.get(row["id"], {}).get("candidateReferenceCount", 0):
        score += 10       # discovery: named evidence or a feature-specific candidate
    if declared_for_mode and (has({"implementation"}) or row.get("tracking", {}).get("implementationRefs")):
        score += 20       # traceable source ownership
    if check({"unit"}):
        score += 20       # executable direct assertion for this mode
    if check({"atomic-fixture"}) or (declared_for_mode and has({"atomic-fixture"})):
        score += 15       # minimal reproducible fixture
    if check({"differential", "corpus"}) or (declared_for_mode and has({"differential", "corpus"})):
        score += 25       # independent or real-world observation
    return score

def summary(rows, collected, attribution=None, mode_filter=None):
    # Deferred/preserve-only/blocked work is intentionally outside the current
    # product implementation denominator. Unknown evidence remains visible.
    target = [r for r in rows if r["decision"]["state"] in {"required", "supported"}]
    modes = [(r["id"], mode, state) for r in target for mode, state in r["modes"].items()
             if state != "not-applicable" and (mode_filter is None or mode in mode_filter)]
    states = Counter(state for _, _, state in modes)
    verified = [r for r in target if r.get("verification")]
    executable = [r for r in verified if r["verification"].get("status", "executable") == "executable"]
    security = [r for r in target if r.get("verification", {}).get("securitySensitive")]
    security_ready = [r for r in security if {"security", "differential"}.issubset({c["kind"] for c in r["verification"]["checks"]})]
    readiness = [promotion_readiness(row, mode, collected) for row in target for mode, state in row["modes"].items()
                 if state != "not-applicable" and (mode_filter is None or mode in mode_filter)]
    attribution = attribution or {}
    # Grade (#1346/#1347): counts built from evidence, not from a human
    # having hand-set modes[mode]=="implemented" (the "strict" measure below,
    # kept as a column). implemented/verified come only from a passing EXPLICIT contract
    # (never a testCandidate keyword match); verified additionally requires an
    # independent-oracle (differential) check among those passing.
    grades = [attribution.get((cap_id, mode), {}).get("grade", "unknown") for cap_id, mode, state in modes]
    grade_states = Counter(grades)
    return {
        "capabilities": len(rows), "targetCapabilities": len(target),
        "targetModes": len(modes), "modeStates": dict(sorted(states.items())),
        "gradeStates": dict(sorted(grade_states.items())),
        "gradedImplementedOrBetterCount": grade_states["implemented"] + grade_states["verified"],
        "gradedVerifiedCount": grade_states["verified"],
        "gradedUnknownCount": grade_states["unknown"],
        "measuredModeCount": len(modes) - states["unknown"],
        "implementedModeCount": states["implemented"],
        "promotionReadinessMilestones": {"discoveredOrBetter": sum(value >= 10 for value in readiness), "directTestOrBetter": sum(value >= 50 for value in readiness), "independentEvidenceOrBetter": sum(value >= 90 for value in readiness), "strictImplemented": sum(value == 100 for value in readiness)},
        "verificationPlanCount": len(verified),
        "executableVerificationCount": len(executable),
        "securityGate": {"target": len(security), "ready": len(security_ready), "pass": len(security) == len(security_ready)},
        "unknownModeCount": states["unknown"],
        "capabilityIds": [r["id"] for r in rows],
        "excludedCapabilityIds": [r["id"] for r in rows if r["decision"]["state"] in {"deferred", "preserve-only", "blocked"}]
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=DEFAULT)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--markdown", type=Path)
    args = parser.parse_args()
    registry = load(args.root / "registry.json")
    evidence_collection_path = args.root / "generated/evidence-collection.json"
    evidence_collection = load(evidence_collection_path) if evidence_collection_path.is_file() else None
    attribution_path = args.root / "generated/evidence-attribution.json"
    attribution = load(attribution_path) if attribution_path.is_file() else None
    attribution_by_mode = {(row["capability"], row["mode"]): row for row in attribution.get("modes", [])} if attribution else {}
    collected = {row["id"]: row for row in evidence_collection.get("capabilities", [])} if evidence_collection else {}
    benchmarks = load(args.root / registry["benchmarkManifest"])["scenarios"]
    sections, all_rows = {}, []
    for item in registry["sections"]:
        rows = load(args.root / item["path"])["capabilities"]
        sections[item["id"]] = summary(rows, collected, attribution_by_mode)
        all_rows.extend(rows)
    groups_config = load(args.root / registry["scorecardGroups"])["groups"]
    rows_by_section = {item["id"]: load(args.root / item["path"])["capabilities"] for item in registry["sections"]}
    major_categories = {}
    for group in groups_config:
        group_rows = [row for section in group["sections"] for row in rows_by_section[section]]
        major_categories[group["id"]] = {"name": group["name"], "sections": group["sections"], **summary(group_rows, collected, attribution_by_mode)}
    readiness = Counter(item["status"] for item in benchmarks)
    by_id = {row["id"]: row for row in all_rows}
    workflow_specs = {
        "redaction": {
            "ids": ["pdf.17.security.redaction-content-removal"],
            "modes": {"preserve", "render", "extract", "mutate", "write"},
        },
        "redaction-annotations": {
            "ids": ["pdf.17.interactive.redaction-annotations"],
            "modes": None,
        },
        "forms": {
            "ids": ["pdf.17.interactive.forms"],
            "modes": None,
        },
        "safe-save": {
            "ids": ["pdf.17.syntax.objects", "pdf.17.document.metadata", "pdfe.product.security.privacy-clean-copy"],
            "modes": {"preserve", "write"},
        },
        "rendering": {
            "ids": ["pdf.17.content.streams", "pdf.17.graphics.images", "pdf.17.graphics.fonts", "pdf.17.transparency.model"],
            "modes": {"render"},
        },
    }
    workflows = {
        name: summary([by_id[item] for item in spec["ids"] if item in by_id], collected, attribution_by_mode, spec["modes"])
        for name, spec in workflow_specs.items()
    }
    result = {"schemaVersion": 1, "generatedBy": "scripts/build-pdf-capability-scorecard.py", "policy": "Unknown is not credit; security gates do not compensate for unrelated coverage. Promotion readiness is evidence-backed planning progress and is never a conformance claim.", "percentagePolicy": "No overall or per-category percentage is generated (#1739, #1758). Raw counts and per-row state only; unmeasured is a first-class state.", "workflowScopePolicy": "A workflow score includes only the processor roles required by that user workflow. For example, rendering measures render modes, while separate capability/section scores retain parse, preserve, mutate, write, and authoring gaps.", "majorCategories": major_categories, "sections": sections, "workflows": workflows, "benchmarks": {"scenarios": len(benchmarks), "status": dict(sorted(readiness.items())), "existingHarness": readiness["existing-harness"]}, "evidenceCollection": evidence_collection.get("summary") if evidence_collection else {"status": "not-generated"}, "testAttribution": attribution.get("summary") if attribution else {"status":"not-generated"}}
    result["unplannedRequiredCapabilities"] = [r["id"] for r in all_rows if r["decision"]["state"] == "required" and not r.get("verification")]
    output = args.output or args.root / "generated/capability-scorecard.json"
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    markdown = args.markdown or args.root / "generated/capability-scorecard.md"
    lines = ["# PDF capability scorecard", "", "Raw counts only: no overall or per-category percentage is generated (#1739, #1758). Implemented/Verified/Unknown (#1346/#1347) are graded from evidence: implemented requires a passing explicit test contract, verified additionally requires an independent-oracle (differential) check among those passing, unknown is everything else (unmeasured is a first-class state) — a discovered testCandidate keyword match never earns credit on its own. Strict counts modes a reviewer has set to implemented.", "", "Section and category rows retain every required/supported role; workflow rows include only the processor roles that workflow needs.", "", f"Critical-path benchmark scenarios: {result['benchmarks']['scenarios']} ({result['benchmarks']['status']}).", "", "| Area | Target modes | Implemented | Verified | Unknown | Strict implemented | Strict unknown |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for name, item in sorted(result["sections"].items()):
        lines.append(f"| {name} | {item['targetModes']} | {item['gradedImplementedOrBetterCount']} | {item['gradedVerifiedCount']} | {item['gradedUnknownCount']} | {item['implementedModeCount']} | {item['unknownModeCount']} |")
    lines.extend(["", "## Major categories", "", "| Category | Target modes | Strict implemented | Measured | Target capabilities | Planned verification | Executable verification | Unknown |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"])
    for name, item in result["majorCategories"].items():
        lines.append(f"| {item['name']} | {item['targetModes']} | {item['implementedModeCount']} | {item['measuredModeCount']} | {item['targetCapabilities']} | {item['verificationPlanCount']} | {item['executableVerificationCount']} | {item['unknownModeCount']} |")
    lines.extend(["", "## Critical workflows", "", "| Workflow | Target modes | Strict implemented | Modes at >=50 | Modes at >=90 | Unknown |", "| --- | ---: | ---: | ---: | ---: | ---: |"])
    for name, item in sorted(result["workflows"].items()):
        milestones=item['promotionReadinessMilestones']
        lines.append(f"| {name} | {item['targetModes']} | {item['implementedModeCount']} | {milestones['directTestOrBetter']} | {milestones['independentEvidenceOrBetter']} | {item['unknownModeCount']} |")
    collection = result["evidenceCollection"]
    lines.extend(["", "## Evidence collection", "", "Collected candidates are discovery material, not implementation credit."])
    if "collectionStates" in collection:
        lines.append(f"All {collection['capabilities']} capability leaves have a collection record: {collection['collectionStates']}.")
    else:
        lines.append("Evidence collection has not been generated.")
    attributed=result['testAttribution']
    if 'targetModes' in attributed:
        lines.extend(["", "## Test and benchmark attribution", "", f"Explicit test contracts: {attributed['modesWithExplicitContracts']}/{attributed['targetModes']}; passing recorded contracts: {attributed['modesWithPassingExplicitContracts']}/{attributed['targetModes']}; candidate test coverage: {attributed['modesWithTestCandidates']}/{attributed['targetModes']}. Benchmark harnesses: {attributed['benchmarkScenariosWithHarness']}/{attributed['benchmarkScenarios']}."])
    markdown.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"wrote {output.relative_to(ROOT)} and {markdown.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
