#!/usr/bin/env python3
"""Migrate the original combined architecture registry into normalized files."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


def write_json(path: Path, value: dict) -> None:
    content = json.dumps(value, indent=2, ensure_ascii=False) + "\n"
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


def project_references(project: Path) -> list[str]:
    root = ET.parse(project).getroot()
    references: list[str] = []
    for node in root.iter():
        if node.tag.rsplit("}", 1)[-1] != "ProjectReference":
            continue
        include = node.attrib.get("Include")
        if include:
            target = (project.parent / include.replace("\\", "/")).resolve()
            references.append(target.relative_to(ROOT).as_posix())
    return sorted(set(references))


def classification(path: Path) -> str:
    relative = path.relative_to(ROOT).as_posix()
    if ".Tests/" in relative or relative.endswith(".Tests.csproj"):
        return "test"
    if relative.startswith("tools/"):
        return "tool"
    if "Benchmarks" in relative:
        return "benchmark"
    if "Sample" in relative or "Demo" in relative:
        return "sample"
    return "shipping"


def inventory() -> dict:
    projects = sorted(
        path
        for path in ROOT.rglob("*.csproj")
        if not any(part in {"bin", "obj", ".claude"} for part in path.parts)
    )
    revision = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    return {
        "$schema": "./schemas/inventory.schema.json",
        "schemaVersion": 1,
        "generator": {
            "name": "scripts/check_architecture_registry.py",
            "version": 1,
            "sourceRevision": revision,
        },
        "projects": [
            {
                "path": project.relative_to(ROOT).as_posix(),
                "classification": classification(project),
                "sourceRoot": project.parent.relative_to(ROOT).as_posix(),
                "projectReferences": project_references(project),
            }
            for project in projects
        ],
    }


def migrate(source: Path) -> None:
    combined = json.loads(source.read_text(encoding="utf-8"))

    design_components = []
    assessment_components = []
    for component in combined["components"]:
        design = {
            "id": component["id"],
            "name": component["name"],
            "kind": component["kind"],
            "layer": component["layer"],
            "pathRole": (
                "container"
                if "projectFile" in component
                else "evidence"
                if component["id"] in {"core-redaction", "verification"}
                else "ownership"
            ),
            "sourceRoots": component["paths"],
            "targetState": component["targetState"],
            "workflows": component["primaryWorkflows"],
            "dependsOn": component.get("desiredDependsOn", component["dependsOn"]),
        }
        if component["id"].startswith("core-"):
            design["parent"] = "core"
        elif component["id"] == "app-main-window":
            design["parent"] = "app"
        if "projectFile" in component:
            design["projectFile"] = component["projectFile"]
            design["allowedProjectDependencies"] = component[
                "allowedProjectDependencies"
            ]
        design_components.append(design)

        assessment = {
            "component": component["id"],
            "implementationStatus": component["currentStatus"],
            "evidence": component["verification"],
            "gaps": component["knownGaps"],
            "issueIds": component.get("issueIds", []),
        }
        assessment_components.append(assessment)

    design = {
        "$schema": "./schemas/design.schema.json",
        "schemaVersion": 1,
        "designVersion": 1,
        "repositoryScope": "architecture/repository-scope.json",
        "components": design_components,
        "relationships": [
            {
                "source": relationship["source"],
                "target": relationship["target"],
                "type": relationship["type"],
                "rationale": relationship["rationale"],
            }
            for relationship in combined["relationships"]
            if relationship["targetStatus"] != "deprecated"
        ],
        "workflows": combined["workflows"],
        "rules": combined["architectureRules"],
        "diagramViews": combined["diagramViews"],
    }

    assessment = {
        "$schema": "./schemas/assessment.schema.json",
        "schemaVersion": 1,
        "reviewedAt": combined["reviewedAt"],
        "statusDefinitions": combined["statusDefinitions"],
        "components": assessment_components,
        "relationships": [
            {
                "source": relationship["source"],
                "target": relationship["target"],
                "type": relationship["type"],
                "implementationStatus": relationship["currentStatus"],
            }
            for relationship in combined["relationships"]
        ],
        "concerns": combined["concerns"],
    }

    decisions = {
        "$schema": "./schemas/decisions.schema.json",
        "schemaVersion": 1,
        "decisions": [
            {
                "id": f"AD-{number:03d}",
                "status": "accepted",
                "prose": f"../docs/architecture/decisions.md#ad-{number:03d}",
                "supersedes": [],
            }
            for number in range(1, 9)
        ],
    }

    write_json(ROOT / "architecture/design.json", design)
    write_json(ROOT / "architecture/assessment.json", assessment)
    write_json(ROOT / "architecture/inventory.generated.json", inventory())
    write_json(ROOT / "architecture/decisions.json", decisions)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "source",
        nargs="?",
        type=Path,
        default=ROOT / "architecture/registry.json",
    )
    args = parser.parse_args()
    migrate(args.source.resolve())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
