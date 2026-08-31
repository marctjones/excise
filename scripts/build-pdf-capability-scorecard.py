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


def percent(numerator: int, denominator: int):
    return None if not denominator else round(100 * numerator / denominator, 1)


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


def summary(rows, collected):
    # Deferred/preserve-only/blocked work is intentionally outside the current
    # product implementation denominator. Unknown evidence remains visible.
    target = [r for r in rows if r["decision"]["state"] in {"required", "supported"}]
    modes = [(r["id"], mode, state) for r in target for mode, state in r["modes"].items() if state != "not-applicable"]
    states = Counter(state for _, _, state in modes)
    verified = [r for r in target if r.get("verification")]
    executable = [r for r in verified if r["verification"].get("status", "executable") == "executable"]
    security = [r for r in target if r.get("verification", {}).get("securitySensitive")]
    security_ready = [r for r in security if {"security", "differential"}.issubset({c["kind"] for c in r["verification"]["checks"]})]
    readiness = [promotion_readiness(row, mode, collected) for row in target for mode, state in row["modes"].items() if state != "not-applicable"]
    return {
        "capabilities": len(rows), "targetCapabilities": len(target),
        "targetModes": len(modes), "modeStates": dict(sorted(states.items())),
        "measuredModeCoveragePercent": percent(len(modes) - states["unknown"], len(modes)),
        "implementedModeCoveragePercent": percent(states["implemented"], len(modes)),
        "promotionReadinessPercent": percent(sum(readiness), len(readiness) * 100),
        "promotionReadinessMilestones": {"discoveredOrBetter": sum(value >= 10 for value in readiness), "directTestOrBetter": sum(value >= 50 for value in readiness), "independentEvidenceOrBetter": sum(value >= 90 for value in readiness), "strictImplemented": sum(value == 100 for value in readiness)},
        "verificationPlanCoveragePercent": percent(len(verified), len(target)),
        "executableVerificationCoveragePercent": percent(len(executable), len(target)),
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
    collected = {row["id"]: row for row in evidence_collection.get("capabilities", [])} if evidence_collection else {}
    benchmarks = load(args.root / registry["benchmarkManifest"])["scenarios"]
    sections, all_rows = {}, []
    for item in registry["sections"]:
        rows = load(args.root / item["path"])["capabilities"]
        sections[item["id"]] = summary(rows, collected)
        all_rows.extend(rows)
    groups_config = load(args.root / registry["scorecardGroups"])["groups"]
    rows_by_section = {item["id"]: load(args.root / item["path"])["capabilities"] for item in registry["sections"]}
    major_categories = {}
    for group in groups_config:
        group_rows = [row for section in group["sections"] for row in rows_by_section[section]]
        major_categories[group["id"]] = {"name": group["name"], "sections": group["sections"], **summary(group_rows, collected)}
    readiness = Counter(item["status"] for item in benchmarks)
    by_id = {row["id"]: row for row in all_rows}
    workflow_ids = {"redaction": ["pdf.17.security.redaction-content-removal", "pdf.17.interactive.redaction-annotations"], "forms": ["pdf.17.interactive.forms"], "safe-save": ["pdf.17.syntax.objects", "pdf.17.document.metadata", "pdfe.product.security.privacy-clean-copy"], "rendering": ["pdf.17.content.streams", "pdf.17.graphics.images", "pdf.17.graphics.fonts", "pdf.17.transparency.model"]}
    workflows = {name: summary([by_id[item] for item in ids if item in by_id], collected) for name, ids in workflow_ids.items()}
    result = {"schemaVersion": 1, "generatedBy": "scripts/build-pdf-capability-scorecard.py", "policy": "Unknown is not credit; security gates do not compensate for unrelated coverage. Promotion readiness is evidence-backed planning progress and is never a conformance claim.", "overall": summary(all_rows, collected), "majorCategories": major_categories, "sections": sections, "workflows": workflows, "benchmarks": {"scenarios": len(benchmarks), "status": dict(sorted(readiness.items())), "readyPercent": percent(readiness["existing-harness"], len(benchmarks))}, "evidenceCollection": evidence_collection.get("summary") if evidence_collection else {"status": "not-generated"}}
    result["unplannedRequiredCapabilities"] = [r["id"] for r in all_rows if r["decision"]["state"] == "required" and not r.get("verification")]
    output = args.output or args.root / "generated/capability-scorecard.json"
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    markdown = args.markdown or args.root / "generated/capability-scorecard.md"
    lines = ["# PDF capability scorecard", "", "Unknown modes receive no credit. Security gates are non-compensating.", "", "Promotion readiness is an evidence-backed planning ladder, not a conformance claim: discovery 10, source trace 30, executable direct test 50, fixture 65, independent evidence 90, reviewed strict implementation 100.", "", f"Critical-path benchmark readiness: {result['benchmarks']['readyPercent']}% ({result['benchmarks']['status']}).", "", "| Area | Target modes | Implemented | Promotion readiness | Measured | Planned verification | Executable verification | Unknown |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for name, item in [("overall", result["overall"]), *sorted(result["sections"].items())]:
        def display(value):
            return "—" if value is None else f"{value}%"
        lines.append(f"| {name} | {item['targetModes']} | {display(item['implementedModeCoveragePercent'])} | {display(item['promotionReadinessPercent'])} | {display(item['measuredModeCoveragePercent'])} | {display(item['verificationPlanCoveragePercent'])} | {display(item['executableVerificationCoveragePercent'])} | {item['unknownModeCount']} |")
    lines.extend(["", "## Major categories", "", "| Category | Target modes | Implemented | Promotion readiness | Measured | Planned verification | Executable verification | Unknown |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"])
    for name, item in result["majorCategories"].items():
        lines.append(f"| {item['name']} | {item['targetModes']} | {display(item['implementedModeCoveragePercent'])} | {display(item['promotionReadinessPercent'])} | {display(item['measuredModeCoveragePercent'])} | {display(item['verificationPlanCoveragePercent'])} | {display(item['executableVerificationCoveragePercent'])} | {item['unknownModeCount']} |")
    lines.extend(["", "## Critical workflows", "", "| Workflow | Target modes | Implemented | Promotion readiness | Modes at >=50 | Modes at >=90 | Unknown |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: |"])
    for name, item in sorted(result["workflows"].items()):
        milestones=item['promotionReadinessMilestones']
        lines.append(f"| {name} | {item['targetModes']} | {display(item['implementedModeCoveragePercent'])} | {display(item['promotionReadinessPercent'])} | {milestones['directTestOrBetter']} | {milestones['independentEvidenceOrBetter']} | {item['unknownModeCount']} |")
    collection = result["evidenceCollection"]
    lines.extend(["", "## Evidence collection", "", "Collected candidates are discovery material, not implementation credit."])
    if "collectionStates" in collection:
        lines.append(f"All {collection['capabilities']} capability leaves have a collection record: {collection['collectionStates']}.")
    else:
        lines.append("Evidence collection has not been generated.")
    markdown.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"wrote {output.relative_to(ROOT)} and {markdown.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
