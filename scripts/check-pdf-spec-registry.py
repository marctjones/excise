#!/usr/bin/env python3
"""Validate and generate the proposed ISO 32000 capability registry."""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DEFAULT = ROOT / "test-pdfs/manifests/pdf-spec-registry"


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=DEFAULT)
    parser.add_argument("--write-summary", action="store_true")
    parser.add_argument("--write-verification-gaps", type=Path)
    args = parser.parse_args()
    root = args.root
    errors: list[str] = []
    try:
        registry = load(root / "registry.json")
    except (OSError, json.JSONDecodeError) as exc:
        print(f"ERROR: cannot load registry: {exc}", file=sys.stderr)
        return 1

    required = {"schemaVersion", "status", "sources", "citationAudit", "modeDefinitions", "decisionStates", "implementationStates", "evidenceKinds", "verificationKinds", "sections", "errataPolicy", "corpusPolicy", "evidenceCollection", "rendererEvidenceMap", "evidenceMaps", "testOutcomes", "referenceToolEvidence", "scorecardGroups", "atomicFixtureEvidence"}
    missing = required - registry.keys()
    if registry.get("schemaVersion") != 1 or missing:
        errors.append(f"registry malformed (missing={sorted(missing)}, schemaVersion={registry.get('schemaVersion')!r})")

    source_ids = {item.get("id") for item in registry.get("sources", [])}
    for source in registry.get("sources", []):
        if source.get("id") in {"iso-32000-1", "iso-32000-2"}:
            digest = source.get("sha256", "")
            if not source.get("edition") or len(digest) != 64 or any(char not in "0123456789abcdef" for char in digest):
                errors.append(f"{source.get('id')}: ISO source requires edition and lowercase SHA-256 pin")
    ids: list[str] = []
    capability_count = Counter()
    decision_count = Counter()
    mode_count = Counter()
    for section in registry.get("sections", []):
        path = root / section.get("path", "")
        if not path.is_file():
            errors.append(f"section {section.get('id')!r} missing: {path}")
            continue
        try:
            document = load(path)
        except json.JSONDecodeError as exc:
            errors.append(f"invalid JSON {path}: {exc}")
            continue
        if document.get("schemaVersion") != 1 or document.get("section") != section.get("id"):
            errors.append(f"section identity mismatch: {path}")
        for cap in document.get("capabilities", []):
            cap_id = cap.get("id", "")
            ids.append(cap_id)
            required_cap = {"id", "name", "spec", "classification", "decision", "modes", "evidence", "notes", "documentation", "tracking"}
            absent = required_cap - cap.keys()
            if absent:
                errors.append(f"{cap_id or path}: missing {sorted(absent)}")
            tracking = cap.get("tracking", {})
            required_tracking = {"owner", "reviewState", "implementationRefs", "testRefs", "fixtureRefs", "corpusRefs", "referenceToolRefs", "architectureRefs", "issueRefs", "knownLimitations", "processorRoles", "supportLevel", "normativeSourcePins", "errataStatus", "lastReviewedCommit", "positiveTestRefs", "negativeOrConservationTestRefs", "explicitEvidenceGaps"}
            missing_tracking = required_tracking - tracking.keys()
            if missing_tracking:
                errors.append(f"{cap_id}: tracking missing {sorted(missing_tracking)}")
            if tracking.get("supportLevel") == "implemented-and-proven" and (not tracking.get("positiveTestRefs") or not tracking.get("negativeOrConservationTestRefs")):
                errors.append(f"{cap_id}: implemented-and-proven requires positive and negative/conservation evidence")
            if not tracking.get("positiveTestRefs") or not tracking.get("negativeOrConservationTestRefs"):
                if not tracking.get("explicitEvidenceGaps"):
                    errors.append(f"{cap_id}: incomplete test evidence requires an explicit evidence gap")
            decision = cap.get("decision", {})
            if decision.get("state") not in registry.get("decisionStates", []):
                errors.append(f"{cap_id}: unknown decision state {decision.get('state')!r}")
            decision_count[decision.get("state", "<missing>")] += 1
            for mode, state in cap.get("modes", {}).items():
                if mode not in registry.get("modeDefinitions", {}):
                    errors.append(f"{cap_id}: unknown mode {mode!r}")
                if state not in registry.get("implementationStates", []):
                    errors.append(f"{cap_id}: unknown implementation state {state!r}")
                mode_count[mode] += 1
            for ref in cap.get("spec", []):
                if ref.get("source") not in source_ids:
                    errors.append(f"{cap_id}: unknown spec source {ref.get('source')!r}")
                if not ref.get("clauses"):
                    errors.append(f"{cap_id}: spec reference has no clause")
            for evidence in cap.get("evidence", []):
                if evidence.get("kind") not in registry.get("evidenceKinds", []):
                    errors.append(f"{cap_id}: unknown evidence kind {evidence.get('kind')!r}")
            verification = cap.get("verification")
            implemented_modes = {mode for mode, state in cap.get("modes", {}).items() if state == "implemented"}
            if implemented_modes and (not verification or verification.get("status", "executable") != "executable"):
                errors.append(f"{cap_id}: implemented modes require a verification contract")
            if verification:
                verification_status = verification.get("status", "executable")
                if verification_status not in {"planned", "executable"}:
                    errors.append(f"{cap_id}: verification status must be planned or executable")
                required_modes = verification.get("requiredModes", [])
                for mode in required_modes:
                    if mode not in cap.get("modes", {}):
                        errors.append(f"{cap_id}: verification requires undeclared mode {mode!r}")
                check_kinds = set()
                for check in verification.get("checks", []):
                    kind = check.get("kind")
                    check_kinds.add(kind)
                    if kind not in registry.get("verificationKinds", []):
                        errors.append(f"{cap_id}: unknown verification kind {kind!r}")
                    for mode in check.get("modes", []):
                        if mode not in required_modes:
                            errors.append(f"{cap_id}: verification check {check.get('ref')!r} covers non-required mode {mode!r}")
                if verification_status == "executable" and verification.get("securitySensitive") and not {"security", "differential"}.issubset(check_kinds):
                    errors.append(f"{cap_id}: security-sensitive verification requires security and differential checks")
                if verification_status == "executable" and implemented_modes and not {"unit", "atomic-fixture", "differential"}.issubset(check_kinds):
                    errors.append(f"{cap_id}: implemented modes require unit, atomic-fixture, and differential checks")
            capability_count[document.get("section", "<missing>")] += 1

    duplicates = sorted(key for key, count in Counter(ids).items() if key and count > 1)
    blanks = sum(not key for key in ids)
    if duplicates:
        errors.append(f"duplicate capability IDs: {', '.join(duplicates)}")
    if blanks:
        errors.append(f"{blanks} capabilities have blank IDs")
    legacy = root / registry.get("legacySources", "")
    if not legacy.is_file():
        errors.append(f"legacy source manifest missing: {legacy}")
    errata_path = root / registry.get("errataPolicy", "")
    if not errata_path.is_file():
        errors.append(f"errata policy missing: {errata_path}")
    else:
        errata = load(errata_path)
        if errata.get("schemaVersion") != 1 or not errata.get("canonicalRequirementSource") or not errata.get("pinnedRevisions"):
            errors.append("errata policy requires canonical source and pinned candidate revisions")
        for source, revision in errata.get("pinnedRevisions", {}).items():
            matching = next((item for item in registry.get("sources", []) if item.get("id") == source), None)
            if not matching or matching.get("revision") != revision:
                errors.append(f"errata pin for {source!r} does not match registry source")
    corpus_policy_path = root / registry.get("corpusPolicy", "")
    if not corpus_policy_path.is_file():
        errors.append(f"corpus policy missing: {corpus_policy_path}")
    else:
        corpus_policy = load(corpus_policy_path)
        if corpus_policy.get("schemaVersion") != 1 or corpus_policy.get("sourceOfTruth") != "tests/corpora.tsv" or not corpus_policy.get("stabilityContracts"):
            errors.append("corpus policy requires the corpus source of truth and stability contracts")
        for contract in corpus_policy.get("stabilityContracts", []):
            for relative in contract.get("paths", []):
                if not (ROOT / relative).is_file():
                    errors.append(f"corpus stability contract references missing path: {relative}")
    evidence_collection_path = root / registry.get("evidenceCollection", "")
    if not evidence_collection_path.is_file():
        errors.append(f"evidence collection policy missing: {evidence_collection_path}")
    else:
        collection = load(evidence_collection_path)
        if collection.get("schemaVersion") != 1 or not collection.get("policy") or not collection.get("sourceRoots"):
            errors.append("evidence collection policy requires a policy and source roots")
    renderer_map_path = root / registry.get("rendererEvidenceMap", "")
    if not renderer_map_path.is_file():
        errors.append(f"renderer evidence-map policy missing: {renderer_map_path}")
    else:
        renderer_map = load(renderer_map_path)
        if renderer_map.get("schemaVersion") != 1 or not renderer_map.get("testRoot") or not renderer_map.get("facets"):
            errors.append("renderer evidence-map policy requires a test root and facets")
    evidence_maps_path = root / registry.get("evidenceMaps", "")
    if not evidence_maps_path.is_file():
        errors.append(f"evidence maps policy missing: {evidence_maps_path}")
    else:
        maps = load(evidence_maps_path)
        if maps.get("schemaVersion") != 1 or not maps.get("sourceRoots") or not maps.get("testRoots") or not maps.get("facets") or not maps.get("mapInventory"):
            errors.append("evidence maps policy requires source roots, test roots, facets, and an inventory")
        for item in maps.get("mapInventory", []):
            if not item.get("id") or not item.get("generator") or not item.get("output"):
                errors.append("every evidence-map inventory item requires id, generator, and output")
    test_outcomes_path = root / registry.get("testOutcomes", "")
    if not test_outcomes_path.is_file():
        errors.append(f"test outcome policy missing: {test_outcomes_path}")
    elif load(test_outcomes_path).get("schemaVersion") != 1:
        errors.append("test outcome policy requires schemaVersion 1")

    group_path = root / registry.get("scorecardGroups", "")
    if not group_path.is_file():
        errors.append(f"scorecard group policy missing: {group_path}")
    else:
        groups = load(group_path).get("groups", [])
        known_sections = {item["id"] for item in registry.get("sections", [])}
        grouped_sections = [section for group in groups for section in group.get("sections", [])]
        if not groups or set(grouped_sections) != known_sections or len(grouped_sections) != len(set(grouped_sections)):
            errors.append("scorecard groups must contain every registry section exactly once")

    policy_path = root / "product-policy.json"
    if not policy_path.is_file():
        errors.append(f"product policy missing: {policy_path}")
    else:
        policy = load(policy_path)
        policy_required = {"schemaVersion", "decision", "status", "specificationPolicy", "priorityCapabilities", "selectedPdf20Capabilities", "preserveOnlyOrDeferred", "blocked"}
        missing_policy = policy_required - policy.keys()
        if policy.get("schemaVersion") != 1 or missing_policy:
            errors.append(f"product policy malformed (missing={sorted(missing_policy)}, schemaVersion={policy.get('schemaVersion')!r})")
        if policy.get("status") != "accepted" or not str(policy.get("decision", "")).startswith("AD-"):
            errors.append("product policy must name an accepted architecture decision")

    benchmark_path = root / registry.get("benchmarkManifest", "")
    if not benchmark_path.is_file():
        errors.append(f"benchmark manifest missing: {benchmark_path}")
    else:
        benchmark = load(benchmark_path)
        if benchmark.get("schemaVersion") != 1 or not benchmark.get("scenarios"):
            errors.append("benchmark manifest requires schemaVersion 1 and scenarios")
        for scenario in benchmark.get("scenarios", []):
            for capability in scenario.get("capabilities", []):
                if capability not in ids:
                    errors.append(f"benchmark {scenario.get('id')!r} references unknown capability {capability!r}")
        baseline_path = benchmark_path.parent / benchmark.get("baselineManifest", "")
        if not baseline_path.is_file():
            errors.append(f"benchmark baseline manifest missing: {baseline_path}")
        else:
            baseline_ids = set(load(baseline_path).get("baselines", {}))
            scenario_ids = {item.get("id") for item in benchmark.get("scenarios", [])}
            if not baseline_ids <= scenario_ids:
                errors.append(f"benchmark baseline references unknown scenarios: {sorted(baseline_ids - scenario_ids)}")

    reference_path = root / registry.get("referenceTools", "")
    if not reference_path.is_file():
        errors.append(f"reference-tool manifest missing: {reference_path}")
    else:
        tools = load(reference_path).get("tools", [])
        if not tools:
            errors.append("reference-tool manifest requires at least one tool")
        for tool in tools:
            required_tool = {"id", "availability", "license", "command", "commandNormalization", "version", "versionCommand", "timeoutSeconds", "memoryLimitMB", "observations", "limitations", "execution"}
            missing_tool = required_tool - tool.keys()
            if missing_tool:
                errors.append(f"reference tool {tool.get('id', '<missing>')!r} missing {sorted(missing_tool)}")
            if tool.get("availability") == "available-local" and not tool.get("version"):
                errors.append(f"available reference tool {tool.get('id')!r} requires a recorded version")

    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    verification_gaps = []
    for section in registry.get("sections", []):
        document = load(root / section["path"])
        for cap in document["capabilities"]:
            if cap["decision"]["state"] not in {"required", "supported"}:
                continue
            verification = cap.get("verification")
            required_modes = set(verification.get("requiredModes", [])) if verification else set()
            for mode, state in cap["modes"].items():
                if state != "not-applicable" and mode not in required_modes:
                    verification_gaps.append({"capability": cap["id"], "mode": mode, "implementationState": state, "reason": "no verification contract"})
    if args.write_verification_gaps:
        args.write_verification_gaps.write_text(json.dumps({"schemaVersion": 1, "generatedBy": "scripts/check-pdf-spec-registry.py", "gaps": verification_gaps}, indent=2) + "\n", encoding="utf-8")
        print(f"wrote {args.write_verification_gaps}")

    summary = {
        "schemaVersion": 1,
        "generatedBy": "scripts/check-pdf-spec-registry.py",
        "registryStatus": registry["status"],
        "sections": dict(sorted(capability_count.items())),
        "capabilities": len(ids),
        "decisionStates": dict(sorted(decision_count.items())),
        "modeCoverage": dict(sorted(mode_count.items())),
        "migrationStatus": load(legacy)["status"],
        "productPolicy": load(policy_path)["decision"],
        "verificationGapCount": len(verification_gaps),
    }
    if args.write_summary:
        output = root / registry["generatedViews"]["summary"]
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        print(f"wrote {output.relative_to(ROOT)}")
    print(f"validated PDF spec registry: {len(ids)} capabilities across {len(capability_count)} sections")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
