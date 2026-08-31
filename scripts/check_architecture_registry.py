#!/usr/bin/env python3
"""Validate normalized architecture data and derive deterministic inventory/views."""

from __future__ import annotations

import argparse
from collections import defaultdict
import copy
import fnmatch
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

from validate_json_schema import validate_json_schema


REPO_ROOT = Path(__file__).resolve().parents[1]
ARCHITECTURE_ROOT = REPO_ROOT / "architecture"
DEFAULT_DESIGN = ARCHITECTURE_ROOT / "design.json"
DEFAULT_INVENTORY = ARCHITECTURE_ROOT / "inventory.generated.json"
DEFAULT_ASSESSMENT = ARCHITECTURE_ROOT / "assessment.json"
DEFAULT_DECISIONS = ARCHITECTURE_ROOT / "decisions.json"
DEFAULT_TOPOLOGY = ARCHITECTURE_ROOT / "generated/code-topology.json"
DEFAULT_COUPLING = ARCHITECTURE_ROOT / "generated/change-coupling.json"
DEFAULT_CONFORMANCE = ARCHITECTURE_ROOT / "generated/architecture-conformance.json"
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
    parser.add_argument("--topology", type=Path, default=DEFAULT_TOPOLOGY)
    parser.add_argument("--coupling", type=Path, default=DEFAULT_COUPLING)
    parser.add_argument("--conformance", type=Path, default=DEFAULT_CONFORMANCE)
    parser.add_argument("--write-inventory", action="store_true")
    parser.add_argument("--write-diagrams", action="store_true")
    parser.add_argument("--check-diagrams", action="store_true")
    parser.add_argument("--write-conformance", action="store_true")
    parser.add_argument("--check-conformance", action="store_true")
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


def schema_errors(document: dict, schema_name: str, context: str) -> list[str]:
    schema_path = SCHEMA_ROOT / schema_name
    if not schema_path.is_file():
        return [f"{context}: missing schema {repo_relative(schema_path)}"]
    try:
        schema = load_json(schema_path)
    except ValueError as exc:
        return [f"{context}: {exc}"]
    return [
        f"{context} schema: {error}"
        for error in validate_json_schema(document, schema)
    ]


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


def load_repository_scope() -> dict:
    return load_json(ARCHITECTURE_ROOT / "repository-scope.json")


def is_excluded_project(path: Path, scope: dict) -> bool:
    relative = repo_relative(path)
    for item in scope.get("excludedRoots", []):
        raw = item["path"].strip("/")
        if "/" not in raw and raw in path.parts:
            return True
        if relative == raw or relative.startswith(raw + "/"):
            return True
    return False


def classify_project(path: Path, scope: dict) -> str:
    relative = repo_relative(path)
    for rule in scope.get("projectRules", []):
        if fnmatch.fnmatchcase(relative, rule["pattern"]):
            return rule["classification"]
    return scope["defaultProjectClassification"]


def current_revision() -> str:
    return subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=REPO_ROOT, check=True,
        capture_output=True, text=True,
    ).stdout.strip()


def generate_inventory(source_revision: str) -> dict:
    scope = load_repository_scope()
    projects = sorted(
        path for path in REPO_ROOT.rglob("*.csproj")
        if not is_excluded_project(path, scope)
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
                "classification": classify_project(project, scope),
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
        else:
            errors.extend(schema_errors(document, f"{name}.schema.json", name))

    design_root_keys = {
        "$schema", "schemaVersion", "designVersion", "repositoryScope", "components",
        "relationships", "workflows", "rules", "diagramViews",
    }
    require_keys(design, design_root_keys - {"$schema"}, "design", errors)
    reject_keys(design, design_root_keys, "design", errors)
    if design.get("repositoryScope") != "architecture/repository-scope.json":
        errors.append("design: repositoryScope must be architecture/repository-scope.json")
    scope = load_repository_scope()
    if scope.get("schemaVersion") != 1:
        errors.append("repository scope: schemaVersion must be 1")
    if scope.get("$schema") != "./schemas/repository-scope.schema.json":
        errors.append("repository scope: unexpected $schema")
    errors.extend(schema_errors(
        scope, "repository-scope.schema.json", "repository scope"
    ))
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


def component_lineage(component_id: str, component_by_id: dict[str, dict]) -> list[str]:
    lineage: list[str] = []
    current: str | None = component_id
    while current is not None:
        lineage.append(current)
        current = component_by_id[current].get("parent")
    return lineage


def validate_topology_join(design: dict, inventory: dict, topology: dict) -> list[str]:
    errors: list[str] = []
    if topology.get("schemaVersion") != 4:
        errors.append("topology: schemaVersion must be 4")
        return errors
    for schema_name in ("topology.schema.json", "architecture-conformance.schema.json"):
        if not (SCHEMA_ROOT / schema_name).is_file():
            errors.append(f"topology: missing schema architecture/schemas/{schema_name}")
    errors.extend(schema_errors(topology, "topology.schema.json", "topology"))

    component_by_id = {component["id"]: component for component in design["components"]}
    inventory_by_path = {project["path"]: project for project in inventory["projects"]}
    project_by_name: dict[str, dict] = {}
    for index, project in enumerate(topology.get("projects", [])):
        name = project.get("name")
        path = project.get("path")
        if not isinstance(name, str) or not name:
            errors.append(f"topology.projects[{index}]: invalid name")
            continue
        if name in project_by_name:
            errors.append(f"topology.projects[{index}]: duplicate name '{name}'")
        project_by_name[name] = project
        registered = inventory_by_path.get(path)
        if registered is None:
            errors.append(f"topology project {name}: path not in inventory: {path}")
        elif project.get("classification") != registered["classification"]:
            errors.append(f"topology project {name}: classification differs from inventory")
        component = project.get("component")
        if component is not None and component not in component_by_id:
            errors.append(f"topology project {name}: unknown component '{component}'")

    for index, symbol in enumerate(topology.get("symbols", [])):
        project_name = symbol.get("project")
        if project_name not in project_by_name:
            errors.append(f"topology.symbols[{index}]: unknown project '{project_name}'")
        component = symbol.get("component")
        workflows = symbol.get("workflows")
        reasons = symbol.get("seedReasons")
        if not isinstance(reasons, list) or symbol.get("seed") != bool(reasons):
            errors.append(
                f"topology.symbols[{index}]: seed flag and seedReasons disagree"
            )
        if component is None:
            if workflows != []:
                errors.append(
                    f"topology.symbols[{index}]: unowned symbol must have no workflows"
                )
            continue
        if component not in component_by_id:
            errors.append(f"topology.symbols[{index}]: unknown component '{component}'")
            continue
        expected_workflows = sorted(component_by_id[component].get("workflows", []))
        if workflows != expected_workflows:
            errors.append(
                f"topology.symbols[{index}]: workflows differ from component '{component}'"
            )
    mechanisms = topology.get("seeds", {}).get("dynamicMechanisms", [])
    mechanism_names = [item.get("mechanism") for item in mechanisms]
    expected_mechanisms = {
        "xaml", "dependency-injection", "reflection", "source-generation",
        "native-interop", "scripting",
    }
    if set(mechanism_names) != expected_mechanisms or len(mechanism_names) != len(expected_mechanisms):
        errors.append(
            "topology: dynamic mechanism summary must contain each supported mechanism exactly once"
        )
    return errors


def validate_change_coupling(inventory: dict, coupling: dict) -> list[str]:
    errors = schema_errors(
        coupling, "change-coupling.schema.json", "change coupling"
    )
    expected_roots = sorted({
        project["sourceRoot"]
        for project in inventory.get("projects", [])
        if project.get("classification") == "shipping"
    })
    scope = coupling.get("scope", {})
    if scope.get("sourceRoots") != expected_roots:
        errors.append(
            "change coupling: sourceRoots differ from shipping inventory roots"
        )
    for index, item in enumerate(coupling.get("files", [])):
        path = item.get("path", "")
        if not any(path.startswith(root + "/") for root in expected_roots):
            errors.append(
                f"change coupling files[{index}]: path is outside shipping scope: {path}"
            )
    return errors


def classify_observed_dependency(
    source: str,
    target: str,
    component_by_id: dict[str, dict],
    forbidden: set[tuple[str, str]],
    accepted: set[tuple[str, str]],
) -> str:
    source_lineage = component_lineage(source, component_by_id)
    target_lineage = component_lineage(target, component_by_id)
    if any(
        forbidden_source in source_lineage and forbidden_target in target_lineage
        for forbidden_source, forbidden_target in forbidden
    ):
        return "forbidden"
    if (source, target) in accepted:
        return "accepted_exception"
    if source in target_lineage[1:] or target in source_lineage[1:]:
        return "declared"
    target_family = set(target_lineage)
    if any(
        target_family.intersection(component_by_id[item].get("dependsOn", []))
        for item in source_lineage
    ):
        return "declared"
    return "undeclared"


def generate_architecture_conformance(
    design: dict, inventory: dict, assessment: dict, topology: dict
) -> dict:
    component_by_id = {component["id"]: component for component in design["components"]}
    inventory_by_path = {project["path"]: project for project in inventory["projects"]}
    project_by_name = {project["name"]: project for project in topology["projects"]}
    forbidden = {
        (relationship["source"], relationship["target"])
        for relationship in assessment["relationships"]
        if relationship["type"] == "must-not-depend-on"
    }
    accepted = {
        (relationship["source"], relationship["target"])
        for relationship in assessment["relationships"]
        if relationship["implementationStatus"] == "accepted_exception"
    }

    type_owners: dict[str, set[str]] = defaultdict(set)
    for symbol in topology["symbols"]:
        if (
            symbol["kind"] == "type"
            and symbol["containingType"] is not None
            and symbol["component"] is not None
        ):
            type_owners[symbol["containingType"]].add(symbol["component"])

    observed: dict[tuple[str, str], dict] = {}
    unresolved: list[dict] = []
    for dependency in topology["typeDependencies"]:
        source_owners = sorted(type_owners.get(dependency["source"], set()))
        target_owners = sorted(type_owners.get(dependency["target"], set()))
        if len(source_owners) != 1 or len(target_owners) != 1:
            unresolved.append(
                {
                    "sourceType": dependency["source"],
                    "targetType": dependency["target"],
                    "references": dependency["references"],
                    "sourceOwners": source_owners,
                    "targetOwners": target_owners,
                }
            )
            continue
        source = source_owners[0]
        target = target_owners[0]
        if source == target:
            continue
        key = (source, target)
        row = observed.setdefault(
            key,
            {"references": 0, "typeDependencies": []},
        )
        row["references"] += dependency["references"]
        row["typeDependencies"].append(
            {
                "source": dependency["source"],
                "target": dependency["target"],
                "references": dependency["references"],
            }
        )

    component_dependencies: list[dict] = []
    for (source, target), row in sorted(observed.items()):
        evidence = sorted(
            row["typeDependencies"],
            key=lambda item: (-item["references"], item["source"], item["target"]),
        )
        classification = classify_observed_dependency(
            source, target, component_by_id, forbidden, accepted
        )
        component_dependencies.append(
            {
                "source": source,
                "target": target,
                "references": row["references"],
                "typeDependencyCount": len(evidence),
                "classification": classification,
                "evidence": evidence[:5] if classification != "declared" else evidence[:1],
            }
        )

    symbols_by_project: dict[str, list[dict]] = defaultdict(list)
    for symbol in topology["symbols"]:
        symbols_by_project[symbol["project"]].append(symbol)
    unowned_projects = []
    for name, symbols in sorted(symbols_by_project.items()):
        project = project_by_name[name]
        unowned = [symbol for symbol in symbols if symbol["component"] is None]
        if project["component"] is None or unowned:
            unowned_projects.append(
                {
                    "name": name,
                    "path": project["path"],
                    "classification": project["classification"],
                    "projectComponent": project["component"],
                    "unownedSymbols": len(unowned),
                }
            )

    shipping_unowned_projects = sum(
        1
        for project in topology["projects"]
        if project["classification"] == "shipping" and project["component"] is None
    )
    shipping_unowned_symbols = sum(
        1
        for symbol in topology["symbols"]
        if symbol["component"] is None
        and project_by_name[symbol["project"]]["classification"] == "shipping"
    )
    classification_counts = {
        classification: sum(
            1
            for dependency in component_dependencies
            if dependency["classification"] == classification
        )
        for classification in (
            "declared", "accepted_exception", "undeclared", "forbidden"
        )
    }

    registry_mismatches = []
    for project in topology["projects"]:
        registered = inventory_by_path.get(project["path"])
        if registered is None:
            registry_mismatches.append(
                {"project": project["path"], "reason": "missing_inventory_project"}
            )
        elif registered["classification"] != project["classification"]:
            registry_mismatches.append(
                {
                    "project": project["path"],
                    "reason": "classification_mismatch",
                    "topology": project["classification"],
                    "inventory": registered["classification"],
                }
            )

    return {
        "$schema": "../schemas/architecture-conformance.schema.json",
        "schemaVersion": 1,
        "generator": "scripts/check_architecture_registry.py",
        "sourceRevision": topology["sourceRevision"],
        "inputs": {
            "designVersion": design["designVersion"],
            "topologySchemaVersion": topology["schemaVersion"],
        },
        "summary": {
            "analyzedProjects": len(topology["projects"]),
            "retainedSymbols": len(topology["symbols"]),
            "observedComponentDependencies": len(component_dependencies),
            "declaredDependencies": classification_counts["declared"],
            "acceptedExceptions": classification_counts["accepted_exception"],
            "undeclaredDependencies": classification_counts["undeclared"],
            "forbiddenDependencies": classification_counts["forbidden"],
            "unownedShippingProjects": shipping_unowned_projects,
            "unownedShippingSymbols": shipping_unowned_symbols,
            "unresolvedTypeDependencies": len(unresolved),
            "registryMismatches": len(registry_mismatches),
        },
        "componentDependencies": component_dependencies,
        "unownedCode": {"projects": unowned_projects},
        "unresolvedTypeDependencyExamples": sorted(
            unresolved,
            key=lambda item: (item["sourceType"], item["targetType"]),
        )[:25],
        "registryMismatches": registry_mismatches,
        "observationBlindSpots": topology["blindSpots"],
    }


def conformance_contract_errors(report: dict) -> list[str]:
    summary = report["summary"]
    errors: list[str] = []
    if summary["forbiddenDependencies"]:
        errors.append(
            f"observed {summary['forbiddenDependencies']} forbidden component dependencies"
        )
    if summary["unownedShippingProjects"] or summary["unownedShippingSymbols"]:
        errors.append(
            "shipping topology is unowned: "
            f"{summary['unownedShippingProjects']} projects, "
            f"{summary['unownedShippingSymbols']} symbols"
        )
    if summary["registryMismatches"]:
        errors.append(f"observed {summary['registryMismatches']} registry mismatches")
    return errors


def check_or_write_conformance(
    report: dict, output: Path, write: bool, check: bool
) -> list[str]:
    errors = conformance_contract_errors(report)
    expected = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    try:
        output_label = repo_relative(output)
    except ValueError:
        output_label = output.as_posix()
    if write:
        write_atomic(output, expected)
        print(f"wrote {output_label}")
    if check:
        if not output.exists():
            errors.append(
                f"conformance: missing {output_label}; run "
                "scripts/check_architecture_registry.py --write-conformance"
            )
        elif output.read_text(encoding="utf-8") != expected:
            errors.append(
                f"conformance: stale {output_label}; run "
                "scripts/check_architecture_registry.py --write-conformance"
            )
    return errors


def dot_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")


def generate_observed_types_dot(
    design: dict, assessment: dict, topology: dict, conformance: dict, view: dict
) -> str:
    component_by_id = {component["id"]: component for component in design["components"]}
    assessment_by_id = {item["component"]: item for item in assessment["components"]}
    symbol_counts: dict[str, int] = defaultdict(int)
    for symbol in topology["symbols"]:
        if symbol["component"] is not None:
            symbol_counts[symbol["component"]] += 1
    selected = set(view["components"])
    lines = [
        "// Generated by scripts/check_architecture_registry.py. Do not edit by hand.",
        "digraph excise_architecture {",
        "  graph [rankdir=BT, fontname=\"Helvetica\", labelloc=t, label=\""
        + dot_escape(view["title"]) + "\"];",
        "  node [shape=box, style=\"rounded,filled\", fontname=\"Helvetica\"];",
        "  edge [fontname=\"Helvetica\"];",
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
            status = assessment_by_id[component["id"]]["implementationStatus"]
            label = (
                f"{component['name']}\n{status}\n"
                f"{symbol_counts.get(component['id'], 0)} retained symbols"
            )
            lines.append(
                f'    "{dot_escape(component["id"])}" '
                f'[label="{dot_escape(label)}", fillcolor="{STATUS_COLORS[status]}"];'
            )
        lines.append("  }")

    edge_styles = {
        "declared": ("#475569", "solid", "1"),
        "accepted_exception": ("#7e22ce", "dotted", "2"),
        "undeclared": ("#d97706", "dashed", "2"),
        "forbidden": ("#dc2626", "bold", "3"),
    }
    for dependency in conformance["componentDependencies"]:
        if dependency["source"] not in selected or dependency["target"] not in selected:
            continue
        color, style, width = edge_styles[dependency["classification"]]
        label = (
            f"{dependency['references']} refs / "
            f"{dependency['typeDependencyCount']} type pairs\n"
            f"{dependency['classification']}"
        )
        lines.append(
            f'  "{dot_escape(dependency["source"])}" -> '
            f'"{dot_escape(dependency["target"])}" '
            f'[label="{dot_escape(label)}", color="{color}", '
            f'style="{style}", penwidth={width}];'
        )
    lines.append("}")
    return "\n".join(lines) + "\n"


def generate_current_vs_target_dot(
    design: dict, assessment: dict, conformance: dict, view: dict
) -> str:
    component_by_id = {component["id"]: component for component in design["components"]}
    assessment_by_id = {item["component"]: item for item in assessment["components"]}
    selected = set(view["components"])
    observed = {
        (dependency["source"], dependency["target"]): dependency
        for dependency in conformance["componentDependencies"]
        if dependency["source"] in selected and dependency["target"] in selected
    }
    target = {
        (component_id, dependency)
        for component_id in selected
        for dependency in component_by_id[component_id].get("dependsOn", [])
        if dependency in selected
    }
    lines = [
        "// Generated by scripts/check_architecture_registry.py. Do not edit by hand.",
        "digraph excise_architecture {",
        "  graph [rankdir=BT, fontname=\"Helvetica\", labelloc=t, label=\""
        + dot_escape(view["title"]) + "\"];",
        "  node [shape=box, style=\"rounded,filled\", fontname=\"Helvetica\"];",
        "  edge [fontname=\"Helvetica\"];",
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
            status = assessment_by_id[component["id"]]["implementationStatus"]
            label = f"{component['name']}\n{status}"
            lines.append(
                f'    "{dot_escape(component["id"])}" '
                f'[label="{dot_escape(label)}", fillcolor="{STATUS_COLORS[status]}"];'
            )
        lines.append("  }")

    for source, target_component in sorted(target | set(observed)):
        dependency = observed.get((source, target_component))
        if (source, target_component) in target and dependency is not None:
            label = f"target + observed\n{dependency['references']} refs"
            color, style, width = "#15803d", "solid", "2"
        elif (source, target_component) in target:
            label = "target only"
            color, style, width = "#94a3b8", "dotted", "1"
        else:
            classification = dependency["classification"]
            labels = {
                "declared": "observed via parent boundary",
                "accepted_exception": "accepted exception",
                "undeclared": "observed, undeclared",
                "forbidden": "FORBIDDEN",
            }
            styles = {
                "declared": ("#2563eb", "dashed", "1"),
                "accepted_exception": ("#7e22ce", "dotted", "2"),
                "undeclared": ("#d97706", "dashed", "2"),
                "forbidden": ("#dc2626", "bold", "3"),
            }
            label = f"{labels[classification]}\n{dependency['references']} refs"
            color, style, width = styles[classification]
        lines.append(
            f'  "{dot_escape(source)}" -> "{dot_escape(target_component)}" '
            f'[label="{dot_escape(label)}", color="{color}", '
            f'style="{style}", penwidth={width}];'
        )
    lines.append("}")
    return "\n".join(lines) + "\n"


def generate_dot(
    design: dict,
    inventory: dict,
    assessment: dict,
    view: dict,
    topology: dict | None = None,
    conformance: dict | None = None,
) -> str:
    if view["mode"] == "observed-types":
        if topology is None or conformance is None:
            raise ValueError("observed-types diagram requires topology and conformance")
        return generate_observed_types_dot(
            design, assessment, topology, conformance, view
        )
    if view["mode"] == "comparison":
        if conformance is None:
            raise ValueError("comparison diagram requires conformance")
        return generate_current_vs_target_dot(design, assessment, conformance, view)

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
    design: dict,
    inventory: dict,
    assessment: dict,
    topology: dict,
    conformance: dict,
    write: bool,
    check: bool,
) -> list[str]:
    errors: list[str] = []
    for view in design["diagramViews"]:
        output = repo_path(view["output"])
        expected = generate_dot(
            design, inventory, assessment, view, topology, conformance
        )
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


def run_self_test(
    design: dict,
    inventory: dict,
    assessment: dict,
    decisions: dict,
    topology: dict,
    coupling: dict,
) -> int:
    baseline = validate_registry_set(design, inventory, assessment, decisions)
    baseline.extend(validate_topology_join(design, inventory, topology))
    baseline.extend(validate_change_coupling(inventory, coupling))
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
    first_report = generate_architecture_conformance(
        design, inventory, assessment, topology
    )
    second_report = generate_architecture_conformance(
        copy.deepcopy(design), copy.deepcopy(inventory),
        copy.deepcopy(assessment), copy.deepcopy(topology),
    )
    if first_report != second_report:
        print("FAIL: conformance generation is not deterministic", file=sys.stderr)
        return 1
    with tempfile.TemporaryDirectory(prefix="excise-architecture-selftest-") as directory:
        output = Path(directory) / "architecture-conformance.json"
        if check_or_write_conformance(first_report, output, write=True, check=True):
            print("FAIL: fresh conformance output did not pass", file=sys.stderr)
            return 1
        output.write_text("{}\n", encoding="utf-8")
        if not any(
            "stale" in error
            for error in check_or_write_conformance(
                first_report, output, write=False, check=True
            )
        ):
            print("FAIL: stale conformance mutation was not detected", file=sys.stderr)
            return 1
    for view in design["diagramViews"]:
        first_view = generate_dot(
            design, inventory, assessment, view, topology, first_report
        )
        second_view = generate_dot(
            copy.deepcopy(design), copy.deepcopy(inventory),
            copy.deepcopy(assessment), copy.deepcopy(view),
            copy.deepcopy(topology), copy.deepcopy(first_report),
        )
        if first_view != second_view:
            print(
                f"FAIL: diagram generation is not deterministic for {view['id']}",
                file=sys.stderr,
            )
            return 1
    mutated_topology = copy.deepcopy(topology)
    shipping_project = next(
        project for project in mutated_topology["projects"]
        if project["classification"] == "shipping"
    )
    shipping_project["component"] = None
    mutated_report = generate_architecture_conformance(
        design, inventory, assessment, mutated_topology
    )
    if not any("shipping topology is unowned" in error for error in conformance_contract_errors(mutated_report)):
        print("FAIL: unowned shipping project mutation was not detected", file=sys.stderr)
        return 1
    mutated_topology = copy.deepcopy(topology)
    owned_symbol = next(
        symbol for symbol in mutated_topology["symbols"]
        if symbol["component"] is not None
    )
    owned_symbol["workflows"] = ["not-a-workflow"]
    if not any(
        "workflows differ" in error
        for error in validate_topology_join(design, inventory, mutated_topology)
    ):
        print("FAIL: topology workflow mutation was not detected", file=sys.stderr)
        return 1
    mutated_topology = copy.deepcopy(topology)
    seeded_symbol = next(
        symbol for symbol in mutated_topology["symbols"] if symbol["seed"]
    )
    seeded_symbol["seedReasons"] = []
    if not any(
        "seed flag and seedReasons disagree" in error
        for error in validate_topology_join(design, inventory, mutated_topology)
    ):
        print("FAIL: topology seed provenance mutation was not detected", file=sys.stderr)
        return 1
    mutated_coupling = copy.deepcopy(coupling)
    mutated_coupling["scope"]["sourceRoots"].pop()
    if not any(
        "sourceRoots differ" in error
        for error in validate_change_coupling(inventory, mutated_coupling)
    ):
        print("FAIL: coupling scope mutation was not detected", file=sys.stderr)
        return 1
    print("PASS: normalized registries reject reference, inventory, and assessment drift")
    return 0


def main() -> int:
    args = parse_args()
    try:
        design, inventory, assessment, decisions, topology, coupling = [
            load_json(path.resolve())
            for path in (
                args.design, args.inventory, args.assessment, args.decisions,
                args.topology, args.coupling,
            )
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
        return run_self_test(
            design, inventory, assessment, decisions, topology, coupling
        )
    errors = validate_registry_set(design, inventory, assessment, decisions)
    errors.extend(validate_topology_join(design, inventory, topology))
    errors.extend(validate_change_coupling(inventory, coupling))
    conformance: dict = {}
    if not errors:
        conformance = generate_architecture_conformance(
            design, inventory, assessment, topology
        )
        errors.extend(schema_errors(
            conformance,
            "architecture-conformance.schema.json",
            "architecture conformance",
        ))
        errors.extend(
            check_or_write_conformance(
                conformance,
                args.conformance.resolve(),
                write=args.write_conformance,
                check=args.check_conformance,
            )
        )
    if not errors:
        errors.extend(
            check_or_write_diagrams(
                design, inventory, assessment, topology, conformance,
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
        f"{len(decisions['decisions'])} decisions; "
        f"{conformance['summary']['observedComponentDependencies']} observed component edges "
        f"({conformance['summary']['undeclaredDependencies']} undeclared, "
        f"{conformance['summary']['forbiddenDependencies']} forbidden)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
