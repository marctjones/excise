#!/usr/bin/env python3
"""Generate or check the coherent Excise architecture artifact set."""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from typing import Any

import check_architecture_registry as registry
import generate_change_coupling as coupling
from validate_json_schema import load_object, self_test as schema_self_test
from validate_json_schema import validate_json_schema


ROOT = Path(__file__).resolve().parents[1]
MANIFEST_PATH = Path("architecture/generated/artifact-set.json")
ARTIFACTS = (
    (Path("architecture/inventory.generated.json"), "json", "inventory.schema.json"),
    (Path("architecture/generated/code-topology.json"), "json", "topology.schema.json"),
    (Path("architecture/generated/change-coupling.json"), "json", "change-coupling.schema.json"),
    (
        Path("architecture/generated/architecture-conformance.json"),
        "json",
        "architecture-conformance.schema.json",
    ),
    (Path("architecture/generated/current-projects.dot"), "dot", None),
    (Path("architecture/generated/target-components.dot"), "dot", None),
    (Path("architecture/generated/current-component-types.dot"), "dot", None),
    (Path("architecture/generated/current-vs-target.dot"), "dot", None),
)
REVISION_SEMANTICS = (
    "provenance-only; normalized content and hashes determine freshness"
)


def git_revision() -> str:
    return subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def json_text(value: dict[str, Any]) -> str:
    return json.dumps(value, indent=2, ensure_ascii=False) + "\n"


def write_text(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8", newline="\n")


def staged_path(stage: Path, relative: Path) -> Path:
    return stage / relative


def preserved_revision(relative: Path, keys: tuple[str, ...], fallback: str) -> str:
    path = ROOT / relative
    if not path.is_file():
        return fallback
    try:
        value: Any = json.loads(path.read_text(encoding="utf-8"))
        for key in keys:
            value = value[key]
        return value if isinstance(value, str) else fallback
    except (OSError, json.JSONDecodeError, KeyError, TypeError):
        return fallback


def generate_set(stage: Path, *, preserve_revisions: bool) -> dict[str, Any]:
    revision = git_revision()
    inventory_revision = (
        preserved_revision(
            ARTIFACTS[0][0], ("generator", "sourceRevision"), revision
        )
        if preserve_revisions
        else revision
    )
    inventory = registry.generate_inventory(inventory_revision)
    inventory_output = staged_path(stage, ARTIFACTS[0][0])
    write_text(inventory_output, json_text(inventory))

    topology_output = staged_path(stage, ARTIFACTS[1][0])
    topology_output.parent.mkdir(parents=True, exist_ok=True)
    command = [
        str(ROOT / "scripts/check-reachability.sh"),
        "--quiet",
        "--architecture-inventory",
        str(inventory_output),
        "--topology-output",
        str(topology_output),
    ]
    completed = subprocess.run(
        command,
        cwd=ROOT,
        text=True,
        capture_output=True,
    )
    if completed.returncode != 0:
        raise RuntimeError(
            "topology generation failed:\n"
            + completed.stdout
            + completed.stderr
        )
    topology = load_object(topology_output)
    if preserve_revisions:
        generated_revision = topology["sourceRevision"]
        recorded_revision = preserved_revision(
            ARTIFACTS[1][0], ("sourceRevision",), revision
        )
        topology_content = topology_output.read_text(encoding="utf-8")
        topology_content = topology_content.replace(
            f'"sourceRevision":"{generated_revision}"',
            f'"sourceRevision":"{recorded_revision}"',
            1,
        )
        write_text(topology_output, topology_content)
        topology["sourceRevision"] = recorded_revision

    roots = coupling.load_source_roots(inventory_output)
    coupling_report = coupling.analyze(
        coupling.load_commits(200, roots),
        200,
        preserved_revision(
            ARTIFACTS[2][0], ("sourceRevision",), revision
        ) if preserve_revisions else revision,
        roots,
    )
    write_text(staged_path(stage, ARTIFACTS[2][0]), coupling.serialize(coupling_report))

    design = registry.load_json(registry.DEFAULT_DESIGN)
    assessment = registry.load_json(registry.DEFAULT_ASSESSMENT)
    decisions = registry.load_json(registry.DEFAULT_DECISIONS)
    conformance = registry.generate_architecture_conformance(
        design, inventory, assessment, topology
    )
    write_text(staged_path(stage, ARTIFACTS[3][0]), json_text(conformance))

    views_by_output = {
        Path(view["output"]): view for view in design["diagramViews"]
    }
    for relative, artifact_format, _ in ARTIFACTS:
        if artifact_format != "dot":
            continue
        view = views_by_output[relative]
        content = registry.generate_dot(
            design,
            inventory,
            assessment,
            view,
            topology,
            conformance,
        )
        write_text(staged_path(stage, relative), content)

    manifest_revision = preserved_revision(
        MANIFEST_PATH, ("sourceRevision",), revision
    ) if preserve_revisions else revision
    manifest = {
        "$schema": "../schemas/artifact-set.schema.json",
        "schemaVersion": 1,
        "generator": "scripts/architecture_artifacts.py",
        "sourceRevision": manifest_revision,
        "revisionSemantics": REVISION_SEMANTICS,
        "artifacts": [
            {
                "path": relative.as_posix(),
                "format": artifact_format,
                **({"schema": f"architecture/schemas/{schema}"} if schema else {}),
                "sha256": hashlib.sha256(
                    staged_path(stage, relative).read_bytes()
                ).hexdigest(),
            }
            for relative, artifact_format, schema in ARTIFACTS
        ],
    }
    write_text(staged_path(stage, MANIFEST_PATH), json_text(manifest))
    return {
        "design": design,
        "inventory": inventory,
        "assessment": assessment,
        "decisions": decisions,
        "topology": topology,
        "coupling": coupling_report,
        "conformance": conformance,
        "manifest": manifest,
    }


def validate_set(stage: Path, documents: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    for relative, artifact_format, schema_name in ARTIFACTS:
        path = staged_path(stage, relative)
        if not path.is_file():
            errors.append(f"missing staged artifact {relative}")
            continue
        if artifact_format == "json" and schema_name is not None:
            schema = load_object(ROOT / "architecture/schemas" / schema_name)
            value = load_object(path)
            errors.extend(
                f"{relative}: {error}"
                for error in validate_json_schema(value, schema)
            )

    manifest_schema = load_object(
        ROOT / "architecture/schemas/artifact-set.schema.json"
    )
    errors.extend(
        f"{MANIFEST_PATH}: {error}"
        for error in validate_json_schema(documents["manifest"], manifest_schema)
    )
    errors.extend(registry.validate_registry_set(
        documents["design"],
        documents["inventory"],
        documents["assessment"],
        documents["decisions"],
    ))
    errors.extend(registry.validate_topology_join(
        documents["design"], documents["inventory"], documents["topology"]
    ))
    errors.extend(registry.validate_change_coupling(
        documents["inventory"], documents["coupling"]
    ))
    errors.extend(registry.schema_errors(
        documents["conformance"],
        "architecture-conformance.schema.json",
        "architecture conformance",
    ))
    errors.extend(registry.conformance_contract_errors(documents["conformance"]))
    return errors


def first_difference(expected: Any, actual: Any, path: str = "$") -> str | None:
    if type(expected) is not type(actual):
        return f"{path}: type {type(actual).__name__} != {type(expected).__name__}"
    if isinstance(expected, dict):
        expected_keys = set(expected)
        actual_keys = set(actual)
        if expected_keys != actual_keys:
            missing = sorted(expected_keys - actual_keys)
            extra = sorted(actual_keys - expected_keys)
            return f"{path}: missing keys {missing}; extra keys {extra}"
        for key in sorted(expected):
            difference = first_difference(expected[key], actual[key], f"{path}.{key}")
            if difference:
                return difference
        return None
    if isinstance(expected, list):
        if len(expected) != len(actual):
            return f"{path}: length {len(actual)} != {len(expected)}"
        for index, (expected_item, actual_item) in enumerate(zip(expected, actual)):
            difference = first_difference(
                expected_item, actual_item, f"{path}[{index}]"
            )
            if difference:
                return difference
        return None
    if expected != actual:
        return f"{path}: {actual!r} != {expected!r}"
    return None


def check_set(stage: Path) -> list[str]:
    errors: list[str] = []
    for relative, artifact_format, _ in (*ARTIFACTS, (MANIFEST_PATH, "json", None)):
        expected_path = staged_path(stage, relative)
        actual_path = ROOT / relative
        if not actual_path.is_file():
            errors.append(f"missing artifact: {relative}")
            continue
        expected_bytes = expected_path.read_bytes()
        actual_bytes = actual_path.read_bytes()
        if expected_bytes == actual_bytes:
            continue
        detail = "content differs"
        if artifact_format == "json":
            try:
                detail = first_difference(
                    json.loads(expected_bytes), json.loads(actual_bytes)
                ) or detail
            except json.JSONDecodeError as exc:
                detail = f"invalid JSON: {exc}"
        errors.append(
            f"stale artifact {relative}: {detail}; "
            "run scripts/check-architecture-artifacts.sh --update"
        )
    debris = sorted(
        path.relative_to(ROOT).as_posix()
        for path in ROOT.rglob("*.architecture-artifacts.tmp")
    )
    if debris:
        errors.append(f"temporary architecture outputs remain: {debris}")
    return errors


def transactional_replace(stage: Path) -> None:
    targets = [relative for relative, _, _ in ARTIFACTS] + [MANIFEST_PATH]
    prepared: list[tuple[Path, Path]] = []
    backups: dict[Path, tuple[bytes, int] | None] = {}
    try:
        for relative in targets:
            target = ROOT / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            temporary = target.with_name(
                f".{target.name}.{os.getpid()}.architecture-artifacts.tmp"
            )
            temporary.write_bytes(staged_path(stage, relative).read_bytes())
            backups[target] = (
                (target.read_bytes(), target.stat().st_mode)
                if target.exists()
                else None
            )
            prepared.append((temporary, target))
        replaced: list[Path] = []
        try:
            for temporary, target in prepared:
                os.replace(temporary, target)
                replaced.append(target)
        except OSError:
            for target in reversed(replaced):
                backup = backups[target]
                if backup is None:
                    target.unlink(missing_ok=True)
                else:
                    content, mode = backup
                    target.write_bytes(content)
                    target.chmod(mode)
            raise
    finally:
        for temporary, _ in prepared:
            temporary.unlink(missing_ok=True)


def self_test() -> int:
    if schema_self_test() != 0:
        return 1
    coupling.self_test()
    topology = {
        "symbols": [
            {"symbol": "A", "fanIn": 0, "fanOut": 1},
            {"symbol": "B", "fanIn": 1, "fanOut": 0},
        ],
        "methodCycles": [{"members": ["A", "B"]}],
    }
    mutations = {
        "cycle": {**topology, "methodCycles": []},
        "fan-in": copy.deepcopy(topology),
        "fan-out": copy.deepcopy(topology),
        "deterministic-order": {
            **topology,
            "symbols": list(reversed(topology["symbols"])),
        },
    }
    mutations["fan-in"]["symbols"][1]["fanIn"] = 0
    mutations["fan-out"]["symbols"][0]["fanOut"] = 0
    for name, mutation in mutations.items():
        if first_difference(topology, mutation) is None:
            print(f"FAIL: {name} mutation was not detected", file=sys.stderr)
            return 1
    stale = {"artifacts": [{"path": "a", "sha256": "0" * 64}]}
    current = copy.deepcopy(stale)
    current["artifacts"][0]["sha256"] = "1" * 64
    stale_difference = first_difference(stale, current)
    if stale_difference is None or "sha256" not in stale_difference:
        print("FAIL: stale artifact hash mutation was not detected", file=sys.stderr)
        return 1
    print(
        "PASS: unified architecture artifact gate rejects cycle, fan, order, "
        "scope, broad-commit, schema, and stale-hash mutations; "
        f"stale diagnostic: {stale_difference}"
    )
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true")
    mode.add_argument("--update", action="store_true")
    mode.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()

    preserve_revisions = not args.update
    try:
        with tempfile.TemporaryDirectory(
            prefix="excise-architecture-artifacts-"
        ) as directory:
            stage = Path(directory)
            documents = generate_set(
                stage, preserve_revisions=preserve_revisions
            )
            errors = validate_set(stage, documents)
            if not errors and args.update:
                transactional_replace(stage)
            elif not errors:
                errors.extend(check_set(stage))
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as exc:
        print(f"FAIL: architecture artifact generation failed: {exc}", file=sys.stderr)
        return 1

    if errors:
        print("FAIL: architecture artifact set is invalid", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1
    action = "updated" if args.update else "current"
    print(f"PASS: coherent architecture artifact set is {action} ({len(ARTIFACTS)} artifacts)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
