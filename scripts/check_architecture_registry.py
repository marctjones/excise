#!/usr/bin/env python3
"""Validate the Excise architecture registry and derive deterministic DOT views."""

from __future__ import annotations

import argparse
import copy
import json
import os
from pathlib import Path
import sys
import tempfile
import xml.etree.ElementTree as ET


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_REGISTRY = REPO_ROOT / "architecture" / "registry.json"
DEFAULT_SCHEMA = REPO_ROOT / "architecture" / "registry.schema.json"
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
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate architecture/registry.json and its generated DOT views."
    )
    parser.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
    parser.add_argument("--schema", type=Path, default=DEFAULT_SCHEMA)
    parser.add_argument(
        "--write-diagrams",
        action="store_true",
        help="rewrite every diagramViews output from registry data",
    )
    parser.add_argument(
        "--check-diagrams",
        action="store_true",
        help="fail when a generated diagram is absent or differs",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="prove reference validation and diagram generation detect mutations",
    )
    return parser.parse_args()


def load_json(path: Path) -> dict:
    try:
        with path.open(encoding="utf-8") as handle:
            value = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot load {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def repo_path(raw: str) -> Path:
    return (REPO_ROOT / raw.replace("\\", "/")).resolve()


def project_references(project_file: Path) -> set[Path]:
    try:
        root = ET.parse(project_file).getroot()
    except (OSError, ET.ParseError) as exc:
        raise ValueError(f"cannot parse project {project_file}: {exc}") from exc

    references: set[Path] = set()
    for node in root.iter():
        if node.tag.rsplit("}", 1)[-1] != "ProjectReference":
            continue
        include = node.attrib.get("Include")
        if include:
            references.add(
                (project_file.parent / include.replace("\\", "/")).resolve()
            )
    return references


def require_keys(item: dict, keys: set[str], context: str, errors: list[str]) -> None:
    for key in sorted(keys - item.keys()):
        errors.append(f"{context}: missing required key '{key}'")


def validate_registry(registry: dict, schema: dict | None = None) -> list[str]:
    errors: list[str] = []
    root_keys = {
        "schemaVersion",
        "reviewedAt",
        "statusDefinitions",
        "components",
        "relationships",
        "workflows",
        "concerns",
        "architectureRules",
        "diagramViews",
    }
    require_keys(registry, root_keys, "registry", errors)
    if errors:
        return errors

    if registry["schemaVersion"] != 1:
        errors.append("registry: schemaVersion must be 1")
    if schema is not None:
        expected = schema.get("properties", {}).get("schemaVersion", {}).get("const")
        if expected != registry["schemaVersion"]:
            errors.append(
                f"registry: schemaVersion {registry['schemaVersion']} does not match schema const {expected}"
            )
        if "$defs" not in schema:
            errors.append("schema: missing $defs")

    definitions = registry.get("statusDefinitions", {})
    missing_statuses = STATUSES - set(definitions)
    if missing_statuses:
        errors.append(
            "registry: statusDefinitions missing " + ", ".join(sorted(missing_statuses))
        )

    components = registry.get("components", [])
    if not isinstance(components, list) or not components:
        errors.append("registry: components must be a non-empty array")
        return errors

    component_keys = {
        "id",
        "name",
        "kind",
        "layer",
        "paths",
        "currentStatus",
        "targetStatus",
        "summary",
        "targetState",
        "primaryWorkflows",
        "dependsOn",
        "verification",
        "knownGaps",
    }
    component_by_id: dict[str, dict] = {}
    project_by_path: dict[Path, str] = {}
    for index, component in enumerate(components):
        context = f"components[{index}]"
        if not isinstance(component, dict):
            errors.append(f"{context}: must be an object")
            continue
        require_keys(component, component_keys, context, errors)
        component_id = component.get("id")
        if not isinstance(component_id, str) or not component_id:
            errors.append(f"{context}: id must be a non-empty string")
            continue
        if component_id in component_by_id:
            errors.append(f"{context}: duplicate component id '{component_id}'")
        component_by_id[component_id] = component

        for status_key in ("currentStatus", "targetStatus"):
            if component.get(status_key) not in STATUSES:
                errors.append(
                    f"component {component_id}: invalid {status_key} '{component.get(status_key)}'"
                )

        paths = component.get("paths")
        if not isinstance(paths, list) or not paths:
            errors.append(f"component {component_id}: paths must be non-empty")
        else:
            for raw_path in paths:
                if not isinstance(raw_path, str) or not repo_path(raw_path).exists():
                    errors.append(f"component {component_id}: path does not exist: {raw_path}")

        project_file_raw = component.get("projectFile")
        if project_file_raw is not None:
            project_file = repo_path(project_file_raw)
            if not project_file.is_file():
                errors.append(
                    f"component {component_id}: projectFile does not exist: {project_file_raw}"
                )
            elif project_file in project_by_path:
                errors.append(
                    f"component {component_id}: projectFile also belongs to {project_by_path[project_file]}"
                )
            else:
                project_by_path[project_file] = component_id
            if "projectDependencies" not in component:
                errors.append(
                    f"component {component_id}: projectFile requires projectDependencies"
                )
            if "allowedProjectDependencies" not in component:
                errors.append(
                    f"component {component_id}: projectFile requires allowedProjectDependencies"
                )

    component_ids = set(component_by_id)

    for component_id, component in component_by_id.items():
        for key in ("dependsOn", "desiredDependsOn", "projectDependencies", "allowedProjectDependencies"):
            for target in component.get(key, []):
                if target not in component_ids:
                    errors.append(
                        f"component {component_id}: {key} references unknown component '{target}'"
                    )
                if target == component_id:
                    errors.append(f"component {component_id}: {key} contains a self-reference")

        project_file_raw = component.get("projectFile")
        if project_file_raw is None:
            continue
        project_file = repo_path(project_file_raw)
        if not project_file.is_file():
            continue
        actual_paths = project_references(project_file)
        unknown_paths = sorted(str(path) for path in actual_paths - set(project_by_path))
        if unknown_paths:
            errors.append(
                f"component {component_id}: ProjectReference targets are not registered components: "
                + ", ".join(unknown_paths)
            )
        actual_ids = {project_by_path[path] for path in actual_paths if path in project_by_path}
        declared_ids = set(component.get("projectDependencies", []))
        if actual_ids != declared_ids:
            errors.append(
                f"component {component_id}: projectDependencies drift; actual={sorted(actual_ids)} "
                f"declared={sorted(declared_ids)}"
            )
        disallowed = actual_ids - set(component.get("allowedProjectDependencies", []))
        if disallowed:
            errors.append(
                f"component {component_id}: disallowed ProjectReference(s): {sorted(disallowed)}"
            )

    workflows = registry.get("workflows", [])
    workflow_ids: set[str] = set()
    for index, workflow in enumerate(workflows):
        context = f"workflows[{index}]"
        if not isinstance(workflow, dict):
            errors.append(f"{context}: must be an object")
            continue
        workflow_id = workflow.get("id")
        if workflow_id in workflow_ids:
            errors.append(f"{context}: duplicate workflow id '{workflow_id}'")
        workflow_ids.add(workflow_id)
        orders: list[int] = []
        for step in workflow.get("steps", []):
            if step.get("component") not in component_ids:
                errors.append(
                    f"workflow {workflow_id}: unknown step component '{step.get('component')}'"
                )
            orders.append(step.get("order"))
        if orders != list(range(1, len(orders) + 1)):
            errors.append(f"workflow {workflow_id}: step order must be contiguous from 1")

    for component_id, component in component_by_id.items():
        for workflow_id in component.get("primaryWorkflows", []):
            if workflow_id not in workflow_ids:
                errors.append(
                    f"component {component_id}: unknown primary workflow '{workflow_id}'"
                )

    seen_relationships: set[tuple[str, str, str]] = set()
    for index, relationship in enumerate(registry.get("relationships", [])):
        context = f"relationships[{index}]"
        source = relationship.get("source")
        target = relationship.get("target")
        rel_type = relationship.get("type")
        key = (source, target, rel_type)
        if key in seen_relationships:
            errors.append(f"{context}: duplicate relationship {key}")
        seen_relationships.add(key)
        for endpoint, value in (("source", source), ("target", target)):
            if value not in component_ids:
                errors.append(f"{context}: unknown {endpoint} component '{value}'")
        for status_key in ("currentStatus", "targetStatus"):
            if relationship.get(status_key) not in STATUSES:
                errors.append(f"{context}: invalid {status_key}")

    for collection_name in ("concerns", "architectureRules"):
        seen_ids: set[str] = set()
        for index, item in enumerate(registry.get(collection_name, [])):
            item_id = item.get("id")
            if item_id in seen_ids:
                errors.append(f"{collection_name}[{index}]: duplicate id '{item_id}'")
            seen_ids.add(item_id)
            for component_id in item.get("components", []):
                if component_id not in component_ids:
                    errors.append(
                        f"{collection_name} {item_id}: unknown component '{component_id}'"
                    )

    seen_views: set[str] = set()
    for index, view in enumerate(registry.get("diagramViews", [])):
        view_id = view.get("id")
        if view_id in seen_views:
            errors.append(f"diagramViews[{index}]: duplicate id '{view_id}'")
        seen_views.add(view_id)
        for component_id in view.get("components", []):
            if component_id not in component_ids:
                errors.append(f"diagram view {view_id}: unknown component '{component_id}'")
        output = view.get("output", "")
        output_path = repo_path(output)
        generated_root = (REPO_ROOT / "architecture" / "generated").resolve()
        if output_path.suffix != ".dot" or generated_root not in output_path.parents:
            errors.append(
                f"diagram view {view_id}: output must be a .dot file under architecture/generated"
            )

    return errors


def dot_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")


def generate_dot(registry: dict, view: dict) -> str:
    component_by_id = {component["id"]: component for component in registry["components"]}
    selected = set(view["components"])
    mode = view["mode"]
    status_key = "currentStatus" if mode == "current" else "targetStatus"
    dependency_key = "dependsOn" if mode == "current" else "desiredDependsOn"
    lines = [
        "// Generated by scripts/check_architecture_registry.py. Do not edit by hand.",
        "digraph excise_architecture {",
        "  graph [rankdir=BT, fontname=\"Helvetica\", labelloc=t, label=\""
        + dot_escape(view["title"])
        + "\"];",
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
            status = component[status_key]
            label = f"{component['name']}\n{status}"
            lines.append(
                f'    "{dot_escape(component["id"])}" '
                f'[label="{dot_escape(label)}", fillcolor="{STATUS_COLORS[status]}"];'
            )
        lines.append("  }")

    edges: set[tuple[str, str, str, str]] = set()
    for component_id in selected:
        component = component_by_id[component_id]
        dependencies = component.get(dependency_key, component.get("dependsOn", []))
        if component.get("projectFile"):
            dependencies = component.get("projectDependencies", dependencies)
        for target in dependencies:
            if target in selected:
                label = "project" if component.get("projectFile") else "depends"
                edges.add((component_id, target, label, "solid"))

    for relationship in registry["relationships"]:
        source = relationship["source"]
        target = relationship["target"]
        if source not in selected or target not in selected:
            continue
        status = relationship[status_key]
        if mode == "target" and status == "deprecated":
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


def check_or_write_diagrams(registry: dict, write: bool, check: bool) -> list[str]:
    errors: list[str] = []
    for view in registry["diagramViews"]:
        output = repo_path(view["output"])
        expected = generate_dot(registry, view)
        if write:
            write_atomic(output, expected)
            print(f"wrote {output.relative_to(REPO_ROOT)}")
        if check:
            if not output.exists():
                errors.append(f"diagram {view['id']}: missing {view['output']}")
            elif output.read_text(encoding="utf-8") != expected:
                errors.append(
                    f"diagram {view['id']}: {view['output']} is stale; run "
                    "scripts/check_architecture_registry.py --write-diagrams"
                )
    return errors


def run_self_test(registry: dict, schema: dict) -> int:
    baseline_errors = validate_registry(registry, schema)
    if baseline_errors:
        print("FAIL: self-test fixture (the real registry) is invalid:", file=sys.stderr)
        for error in baseline_errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    mutated = copy.deepcopy(registry)
    mutated["components"][0]["dependsOn"].append("not-a-real-component")
    mutation_errors = validate_registry(mutated, schema)
    if not any("not-a-real-component" in error for error in mutation_errors):
        print("FAIL: unknown-component mutation was not detected", file=sys.stderr)
        return 1

    first = generate_dot(registry, registry["diagramViews"][0])
    second = generate_dot(copy.deepcopy(registry), registry["diagramViews"][0])
    if first != second:
        print("FAIL: diagram generation is not deterministic", file=sys.stderr)
        return 1

    changed = copy.deepcopy(registry)
    changed["components"][0]["currentStatus"] = "partial"
    if generate_dot(changed, changed["diagramViews"][0]) == first:
        print("FAIL: status mutation did not change the diagram", file=sys.stderr)
        return 1

    print("PASS: architecture registry rejects broken references and renders deterministically")
    return 0


def main() -> int:
    args = parse_args()
    try:
        registry = load_json(args.registry.resolve())
        schema = load_json(args.schema.resolve())
    except ValueError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1

    if args.self_test:
        return run_self_test(registry, schema)

    errors = validate_registry(registry, schema)
    if not errors:
        errors.extend(
            check_or_write_diagrams(
                registry,
                write=args.write_diagrams,
                check=args.check_diagrams,
            )
        )

    if errors:
        print("FAIL: architecture registry validation failed:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    print(
        f"PASS: architecture registry has {len(registry['components'])} components, "
        f"{len(registry['workflows'])} workflows, {len(registry['concerns'])} concerns, "
        f"and {len(registry['diagramViews'])} deterministic diagrams"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
