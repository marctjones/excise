#!/usr/bin/env python3
"""Validate normalized architecture data and derive deterministic inventory/views."""

from __future__ import annotations

import argparse
import copy
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


REPO_ROOT = Path(__file__).resolve().parents[1]
ARCHITECTURE_ROOT = REPO_ROOT / "architecture"
DEFAULT_DESIGN = ARCHITECTURE_ROOT / "design.json"
DEFAULT_INVENTORY = ARCHITECTURE_ROOT / "inventory.generated.json"
DEFAULT_ASSESSMENT = ARCHITECTURE_ROOT / "assessment.json"
DEFAULT_DECISIONS = ARCHITECTURE_ROOT / "decisions.json"
SCHEMA_ROOT = ARCHITECTURE_ROOT / "schemas"

STATUSES = {
    "implemented",
    "partial",
    "planned",
    "accepted_exception",
    "deprecated",
    "unknown",
}
STATUS_COLORS = {
    "implemented": "#d8f3dc",
    "partial": "#fff3bf",
    "planned": "#dbeafe",
    "accepted_exception": "#eadcf8",
    "deprecated": "#f8d7da",
    "unknown": "#e5e7eb",
    "target": "#dbeafe",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate normalized architecture registries and generated views."
    )
    parser.add_argument("--design", type=Path, default=DEFAULT_DESIGN)
    parser.add_argument("--inventory", type=Path, default=DEFAULT_INVENTORY)
    parser.add_argument("--assessment", type=Path, default=DEFAULT_ASSESSMENT)
    parser.add_argument("--decisions", type=Path, default=DEFAULT_DECISIONS)
    parser.add_argument("--write-inventory", action="store_true")
    parser.add_argument("--write-diagrams", action="store_true")
    parser.add_argument("--check-diagrams", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def load_json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot load {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def write_atomic(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def repo_relative(path: Path) -> str:
    return path.resolve().relative_to(REPO_ROOT).as_posix()


def repo_path(raw: str) -> Path:
    return (REPO_ROOT / raw.replace("\\", "/")).resolve()


def project_references(project_file: Path) -> list[str]:
    try:
        root = ET.parse(project_file).getroot()
    except (OSError, ET.ParseError) as exc:
        raise ValueError(f"cannot parse project {project_file}: {exc}") from exc
    references: set[str] = set()
    for node in root.iter():
        if node.tag.rsplit("}", 1)[-1] != "ProjectReference":
            continue
        include = node.attrib.get("Include")
        if include:
            references.add(repo_relative(project_file.parent / include.replace("\\", "/")))
    return sorted(references)


def classify_project(path: Path) -> str:
    relative = repo_relative(path)
    if ".Tests/" in relative or relative.endswith(".Tests.csproj"):
        return "test"
    if relative.startswith("tools/"):
        return "tool"
    if "Benchmarks" in relative:
        return "benchmark"
    if "Sample" in relative or "Demo" in relative:
        return "sample"
    return "shipping"


def current_revision() -> str:
    return subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=REPO_ROOT, check=True,
        capture_output=True, text=True,
    ).stdout.strip()


def generate_inventory(source_revision: str) -> dict:
    projects = sorted(
        path for path in REPO_ROOT.rglob("*.csproj")
        if not any(part in {"bin", "obj", ".claude"} for part in path.parts)
    )
    return {
        "$schema": "./schemas/inventory.schema.json",
        "schemaVersion": 1,
        "generator": {
            "name": "scripts/check_architecture_registry.py",
            "version": 1,
            "sourceRevision": source_revision,
        },
        "projects": [
            {
                "path": repo_relative(project),
                "classification": classify_project(project),
                "sourceRoot": repo_relative(project.parent),
                "projectReferences": project_references(project),
            }
            for project in projects
        ],
    }


def require_keys(item: dict, keys: set[str], context: str, errors: list[str]) -> None:
    for key in sorted(keys - item.keys()):
        errors.append(f"{context}: missing required key '{key}'")


def reject_keys(item: dict, allowed: set[str], context: str, errors: list[str]) -> None:
    for key in sorted(item.keys() - allowed):
        errors.append(f"{context}: unknown key '{key}'")


def unique_ids(items: list, key: str, context: str, errors: list[str]) -> dict[str, dict]:
    result: dict[str, dict] = {}
    for index, item in enumerate(items):
        if not isinstance(item, dict):
            errors.append(f"{context}[{index}]: must be an object")
            continue
        item_id = item.get(key)
        if not isinstance(item_id, str) or not item_id:
            errors.append(f"{context}[{index}]: {key} must be a non-empty string")
            continue
        if item_id in result:
            errors.append(f"{context}[{index}]: duplicate {key} '{item_id}'")
        result[item_id] = item
    return result


def validate_registry_set(
    design: dict, inventory: dict, assessment: dict, decisions: dict
) -> list[str]:
    errors: list[str] = []
    expected_schemas = {
        "design": (design, "./schemas/design.schema.json"),
        "inventory": (inventory, "./schemas/inventory.schema.json"),
        "assessment": (assessment, "./schemas/assessment.schema.json"),
        "decisions": (decisions, "./schemas/decisions.schema.json"),
    }
    for name, (document, schema_ref) in expected_schemas.items():
        if document.get("schemaVersion") != 1:
            errors.append(f"{name}: schemaVersion must be 1")
        if document.get("$schema") != schema_ref:
            errors.append(f"{name}: $schema must be {schema_ref}")
        schema_path = SCHEMA_ROOT / f"{name}.schema.json"
        if not schema_path.is_file():
            errors.append(f"{name}: missing schema {repo_relative(schema_path)}")

    design_root_keys = {
        "$schema", "schemaVersion", "designVersion", "components",
        "relationships", "workflows", "rules", "diagramViews",
    }
    require_keys(design, design_root_keys - {"$schema"}, "design", errors)
    reject_keys(design, design_root_keys, "design", errors)
    component_by_id = unique_ids(design.get("components", []), "id", "design.components", errors)
    component_ids = set(component_by_id)
    workflow_by_id = unique_ids(design.get("workflows", []), "id", "design.workflows", errors)
    workflow_ids = set(workflow_by_id)

    component_keys = {
        "id", "name", "kind", "layer", "parent", "pathRole", "sourceRoots", "projectFile",
        "allowedProjectDependencies", "targetState", "workflows", "dependsOn",
    }
    required_component_keys = {
        "id", "name", "kind", "layer", "pathRole", "sourceRoots", "targetState",
        "workflows", "dependsOn",
    }
    for component_id, component in component_by_id.items():
        context = f"design component {component_id}"
        require_keys(component, required_component_keys, context, errors)
        reject_keys(component, component_keys, context, errors)
        roots = component.get("sourceRoots", [])
        if not isinstance(roots, list) or not roots:
            errors.append(f"{context}: sourceRoots must be a non-empty array")
        else:
            for raw in roots:
                if not isinstance(raw, str) or not repo_path(raw).exists():
                    errors.append(f"{context}: source root does not exist: {raw}")
        for key in ("dependsOn", "allowedProjectDependencies"):
            for target in component.get(key, []):
                if target not in component_ids:
                    errors.append(f"{context}: {key} references unknown component '{target}'")
                if target == component_id:
                    errors.append(f"{context}: {key} contains a self-reference")
        for workflow_id in component.get("workflows", []):
            if workflow_id not in workflow_ids:
                errors.append(f"{context}: unknown workflow '{workflow_id}'")
        if "projectFile" in component and "allowedProjectDependencies" not in component:
            errors.append(f"{context}: projectFile requires allowedProjectDependencies")
        parent = component.get("parent")
        if parent is not None and parent not in component_ids:
            errors.append(f"{context}: unknown parent '{parent}'")
        if parent == component_id:
            errors.append(f"{context}: cannot be its own parent")

    for component_id, component in component_by_id.items():
        seen_parents = {component_id}
        parent = component.get("parent")
        while parent in component_by_id:
            if parent in seen_parents:
                errors.append(f"design component {component_id}: parent cycle includes '{parent}'")
                break
            seen_parents.add(parent)
            parent = component_by_id[parent].get("parent")

    owned_roots: list[tuple[str, Path]] = []
    for component_id, component in component_by_id.items():
        if component.get("pathRole") == "ownership":
            owned_roots.extend(
                (component_id, repo_path(raw)) for raw in component.get("sourceRoots", [])
            )
    for index, (left_id, left_path) in enumerate(owned_roots):
        for right_id, right_path in owned_roots[index + 1:]:
            if left_id == right_id:
                continue
            overlaps = (
                left_path == right_path
                or left_path in right_path.parents
                or right_path in left_path.parents
            )
            if overlaps:
                errors.append(
                    "design: overlapping ownership roots require one owner or an explicit "
                    f"container/evidence role: {left_id}:{repo_relative(left_path)} and "
                    f"{right_id}:{repo_relative(right_path)}"
                )

    for workflow_id, workflow in workflow_by_id.items():
        orders: list[int] = []
        for step in workflow.get("steps", []):
            if step.get("component") not in component_ids:
                errors.append(f"workflow {workflow_id}: unknown component '{step.get('component')}'")
            orders.append(step.get("order"))
        if orders != list(range(1, len(orders) + 1)):
            errors.append(f"workflow {workflow_id}: step order must be contiguous from 1")

    relationship_keys: set[tuple[str, str, str]] = set()
    for index, relationship in enumerate(design.get("relationships", [])):
        key = (relationship.get("source"), relationship.get("target"), relationship.get("type"))
        if key in relationship_keys:
            errors.append(f"design.relationships[{index}]: duplicate relationship {key}")
        relationship_keys.add(key)
        for endpoint in key[:2]:
            if endpoint not in component_ids:
                errors.append(f"design.relationships[{index}]: unknown component '{endpoint}'")

    for collection in ("rules", "diagramViews"):
        seen = unique_ids(design.get(collection, []), "id", f"design.{collection}", errors)
        for item_id, item in seen.items():
            for component_id in item.get("components", []):
                if component_id not in component_ids:
                    errors.append(f"design {collection} {item_id}: unknown component '{component_id}'")

    inventory_keys = {"$schema", "schemaVersion", "generator", "projects"}
    require_keys(inventory, inventory_keys - {"$schema"}, "inventory", errors)
    reject_keys(inventory, inventory_keys, "inventory", errors)
    project_by_path = unique_ids(inventory.get("projects", []), "path", "inventory.projects", errors)
    expected_inventory = generate_inventory(
        inventory.get("generator", {}).get("sourceRevision", "0" * 40)
    )
    if inventory != expected_inventory:
        errors.append(
            "inventory: generated project data is stale; run "
            "scripts/check_architecture_registry.py --write-inventory"
        )

    component_by_project: dict[str, str] = {}
    for component_id, component in component_by_id.items():
        project_file = component.get("projectFile")
        if not project_file:
            continue
        if project_file not in project_by_path:
            errors.append(f"design component {component_id}: projectFile not in inventory: {project_file}")
            continue
        if project_file in component_by_project:
            errors.append(
                f"design component {component_id}: projectFile also owned by {component_by_project[project_file]}"
            )
        component_by_project[project_file] = component_id

    for project_file, component_id in component_by_project.items():
        actual_refs = set(project_by_path[project_file].get("projectReferences", []))
        actual_ids = {
            component_by_project[target] for target in actual_refs
            if target in component_by_project
        }
        unregistered_shipping_refs = sorted(
            target for target in actual_refs
            if target not in component_by_project
            and project_by_path.get(target, {}).get("classification") == "shipping"
        )
        if unregistered_shipping_refs:
            errors.append(
                f"design component {component_id}: shipping ProjectReference targets lack component IDs: "
                + ", ".join(unregistered_shipping_refs)
            )
        allowed = set(component_by_id[component_id].get("allowedProjectDependencies", []))
        disallowed = sorted(actual_ids - allowed)
        if disallowed:
            errors.append(f"design component {component_id}: disallowed ProjectReference(s): {disallowed}")

    assessment_root_keys = {
        "$schema", "schemaVersion", "reviewedAt", "statusDefinitions",
        "components", "relationships", "concerns",
    }
    require_keys(assessment, assessment_root_keys - {"$schema"}, "assessment", errors)
    reject_keys(assessment, assessment_root_keys, "assessment", errors)
    missing_statuses = STATUSES - set(assessment.get("statusDefinitions", {}))
    if missing_statuses:
        errors.append("assessment: missing status definitions: " + ", ".join(sorted(missing_statuses)))

    assessment_by_component = unique_ids(
        assessment.get("components", []), "component", "assessment.components", errors
    )
    missing_assessments = sorted(component_ids - set(assessment_by_component))
    unknown_assessments = sorted(set(assessment_by_component) - component_ids)
    if missing_assessments:
        errors.append("assessment: missing components: " + ", ".join(missing_assessments))
    if unknown_assessments:
        errors.append("assessment: unknown components: " + ", ".join(unknown_assessments))
    for component_id, item in assessment_by_component.items():
        if item.get("implementationStatus") not in STATUSES:
            errors.append(f"assessment component {component_id}: invalid implementationStatus")
        if not item.get("evidence") and item.get("implementationStatus") == "implemented":
            errors.append(f"assessment component {component_id}: implemented requires evidence")

    for collection in ("relationships", "concerns"):
        for index, item in enumerate(assessment.get(collection, [])):
            referenced = (
                [item.get("source"), item.get("target")]
                if collection == "relationships" else item.get("components", [])
            )
            for component_id in referenced:
                if component_id not in component_ids:
                    errors.append(f"assessment {collection}[{index}]: unknown component '{component_id}'")
            status = item.get("implementationStatus", item.get("currentStatus"))
            if status not in STATUSES:
                errors.append(f"assessment {collection}[{index}]: invalid status '{status}'")

    decision_by_id = unique_ids(decisions.get("decisions", []), "id", "decisions", errors)
    decision_text = (REPO_ROOT / "docs/architecture/decisions.md").read_text(encoding="utf-8")
    for decision_id, decision in decision_by_id.items():
        if not re.search(rf"^## {re.escape(decision_id)}\b", decision_text, re.MULTILINE):
            errors.append(f"decision {decision_id}: prose heading is missing")
        for superseded in decision.get("supersedes", []):
            if superseded not in decision_by_id:
                errors.append(f"decision {decision_id}: supersedes unknown decision '{superseded}'")
            if superseded == decision_id:
                errors.append(f"decision {decision_id}: cannot supersede itself")
    return errors


def dot_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")


def generate_dot(design: dict, inventory: dict, assessment: dict, view: dict) -> str:
    component_by_id = {component["id"]: component for component in design["components"]}
    assessment_by_id = {item["component"]: item for item in assessment["components"]}
    component_by_project = {
        component["projectFile"]: component["id"]
        for component in design["components"] if component.get("projectFile")
    }
    inventory_by_project = {project["path"]: project for project in inventory["projects"]}
    selected = set(view["components"])
    mode = view["mode"]
    lines = [
        "// Generated by scripts/check_architecture_registry.py. Do not edit by hand.",
        "digraph excise_architecture {",
        "  graph [rankdir=BT, fontname=\"Helvetica\", labelloc=t, label=\""
        + dot_escape(view["title"]) + "\"];",
        "  node [shape=box, style=\"rounded,filled\", fontname=\"Helvetica\"];",
        "  edge [fontname=\"Helvetica\", color=\"#475569\"];",
    ]
    by_layer: dict[str, list[dict]] = {}
    for component_id in selected:
        component = component_by_id[component_id]
        by_layer.setdefault(component["layer"], []).append(component)
    for layer in sorted(by_layer):
        lines.append(f"  subgraph cluster_{dot_escape(layer)} {{")
        lines.append(f'    label="{dot_escape(layer)}";')
        lines.append('    color="#cbd5e1";')
        for component in sorted(by_layer[layer], key=lambda item: item["id"]):
            status = (
                assessment_by_id[component["id"]]["implementationStatus"]
                if mode == "current" else "target"
            )
            label = f"{component['name']}\n{status}"
            lines.append(
                f'    "{dot_escape(component["id"])}" '
                f'[label="{dot_escape(label)}", fillcolor="{STATUS_COLORS[status]}"];'
            )
        lines.append("  }")

    edges: set[tuple[str, str, str, str]] = set()
    for component_id in selected:
        component = component_by_id[component_id]
        dependencies = component.get("dependsOn", [])
        if mode == "current" and component.get("projectFile"):
            project = inventory_by_project.get(component["projectFile"], {})
            dependencies = [
                component_by_project[target] for target in project.get("projectReferences", [])
                if target in component_by_project
            ]
        for target in dependencies:
            if target in selected:
                label = "project" if component.get("projectFile") else "depends"
                edges.add((component_id, target, label, "solid"))

    relationships = design["relationships"] if mode == "target" else [
        relationship for relationship in assessment["relationships"]
        if relationship["implementationStatus"] != "deprecated"
    ]
    for relationship in relationships:
        source = relationship["source"]
        target = relationship["target"]
        if source not in selected or target not in selected:
            continue
        style = "dashed" if relationship["type"] == "must-not-depend-on" else "dotted"
        edges.add((source, target, relationship["type"], style))
    for source, target, label, style in sorted(edges):
        lines.append(
            f'  "{dot_escape(source)}" -> "{dot_escape(target)}" '
            f'[label="{dot_escape(label)}", style="{style}"];'
        )
    lines.append("}")
    return "\n".join(lines) + "\n"


def check_or_write_diagrams(
    design: dict, inventory: dict, assessment: dict, write: bool, check: bool
) -> list[str]:
    errors: list[str] = []
    for view in design["diagramViews"]:
        output = repo_path(view["output"])
        expected = generate_dot(design, inventory, assessment, view)
        if write:
            write_atomic(output, expected)
            print(f"wrote {repo_relative(output)}")
        if check:
            if not output.exists():
                errors.append(f"diagram {view['id']}: missing {view['output']}")
            elif output.read_text(encoding="utf-8") != expected:
                errors.append(
                    f"diagram {view['id']}: stale; run "
                    "scripts/check_architecture_registry.py --write-diagrams"
                )
    return errors


def run_self_test(design: dict, inventory: dict, assessment: dict, decisions: dict) -> int:
    baseline = validate_registry_set(design, inventory, assessment, decisions)
    if baseline:
        print("FAIL: real normalized registries are invalid", file=sys.stderr)
        for error in baseline:
            print(f"  - {error}", file=sys.stderr)
        return 1
    mutated_design = copy.deepcopy(design)
    mutated_design["components"][0]["dependsOn"].append("not-a-component")
    if not any(
        "not-a-component" in error
        for error in validate_registry_set(mutated_design, inventory, assessment, decisions)
    ):
        print("FAIL: dangling component mutation was not detected", file=sys.stderr)
        return 1
    overlapping_design = copy.deepcopy(design)
    overlapping_design["components"][1]["sourceRoots"] = overlapping_design["components"][2]["sourceRoots"]
    if not any(
        "overlapping ownership roots" in error
        for error in validate_registry_set(overlapping_design, inventory, assessment, decisions)
    ):
        print("FAIL: overlapping ownership mutation was not detected", file=sys.stderr)
        return 1
    mutated_inventory = copy.deepcopy(inventory)
    mutated_inventory["projects"].pop()
    if not any(
        "stale" in error
        for error in validate_registry_set(design, mutated_inventory, assessment, decisions)
    ):
        print("FAIL: inventory drift mutation was not detected", file=sys.stderr)
        return 1
    mutated_assessment = copy.deepcopy(assessment)
    mutated_assessment["components"].pop()
    if not any(
        "missing components" in error
        for error in validate_registry_set(design, inventory, mutated_assessment, decisions)
    ):
        print("FAIL: missing assessment mutation was not detected", file=sys.stderr)
        return 1
    mutated_decisions = copy.deepcopy(decisions)
    mutated_decisions["decisions"][0]["supersedes"].append("AD-999")
    if not any(
        "AD-999" in error
        for error in validate_registry_set(design, inventory, assessment, mutated_decisions)
    ):
        print("FAIL: dangling decision mutation was not detected", file=sys.stderr)
        return 1
    first = generate_dot(design, inventory, assessment, design["diagramViews"][0])
    second = generate_dot(
        copy.deepcopy(design), copy.deepcopy(inventory), copy.deepcopy(assessment),
        design["diagramViews"][0],
    )
    if first != second:
        print("FAIL: diagram generation is not deterministic", file=sys.stderr)
        return 1
    print("PASS: normalized registries reject reference, inventory, and assessment drift")
    return 0


def main() -> int:
    args = parse_args()
    try:
        design, inventory, assessment, decisions = [
            load_json(path.resolve())
            for path in (args.design, args.inventory, args.assessment, args.decisions)
        ]
    except ValueError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    if args.write_inventory:
        inventory = generate_inventory(current_revision())
        write_atomic(
            args.inventory.resolve(), json.dumps(inventory, indent=2, ensure_ascii=False) + "\n"
        )
        print(f"wrote {repo_relative(args.inventory)}")
    if args.self_test:
        return run_self_test(design, inventory, assessment, decisions)
    errors = validate_registry_set(design, inventory, assessment, decisions)
    if not errors:
        errors.extend(
            check_or_write_diagrams(
                design, inventory, assessment,
                write=args.write_diagrams, check=args.check_diagrams,
            )
        )
    if errors:
        print("FAIL: normalized architecture validation failed", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1
    print(
        f"PASS: architecture has {len(design['components'])} design components, "
        f"{len(inventory['projects'])} observed projects, "
        f"{len(design['workflows'])} workflows, "
        f"{len(assessment['concerns'])} assessed concerns, and "
        f"{len(decisions['decisions'])} decisions"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
