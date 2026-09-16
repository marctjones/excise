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
from typing import Any, Callable

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

# #1506. change-coupling.json is derived from GIT HISTORY, not from the tree, so
# it cannot be hash-pinned in the same commit that changes it: the commit that
# carries a regeneration is itself a new commit in the `git log --no-merges
# -n200 -- <shipping roots>` window the generator reads, which changes `files`,
# `pairs` and `window`, which changes the sha256 recorded here. A merge is the
# loud case — it brings a branch's whole run of .cs commits into the window at
# once — and an artifacts-only commit is the quiet one, touching no .cs file and
# so shifting nothing. That asymmetry is why the staling looked intermittent,
# and intermittent reads as flaky, which is worse than reliably red.
#
# So a history-derived artifact is verified, but not by "regenerate and diff":
# it must exist, parse, satisfy its schema, and still describe the CURRENT
# shipping scope (see HISTORY_VALIDATORS). That is the contract the code can
# actually own. Coupling strength itself is evidence for a reviewer, not a
# contract the tree must reproduce byte for byte.
#
# ⚠️ Rejected alternatives, so they are not re-proposed: comparing against HEAD~
# (arbitrary the moment HEAD is not the commit that carried the artifacts, and
# wrong for the artifacts-only commit, which must compare against HEAD), and
# documenting the two-commit dance (it teaches a habit of running --update twice,
# which is exactly what hides a real staleness).
HISTORY_DERIVED = ("architecture/generated/change-coupling.json",)
HISTORY_VERIFICATION = (
    "schema-and-scope; derived from git history, deliberately not hash-pinned"
)
HISTORY_VALIDATORS = {
    "architecture/generated/change-coupling.json": (
        lambda documents, value: registry.validate_change_coupling(
            documents["inventory"], value
        )
    ),
}


def is_history_derived(relative: Path) -> bool:
    return relative.as_posix() in HISTORY_DERIVED


def hashed_artifacts() -> tuple[tuple[Path, str, str | None], ...]:
    """The artifacts whose exact bytes the manifest pins (structural)."""
    return tuple(item for item in ARTIFACTS if not is_history_derived(item[0]))


def history_artifacts() -> tuple[tuple[Path, str, str | None], ...]:
    """The artifacts verified by schema and scope instead of by hash."""
    return tuple(item for item in ARTIFACTS if is_history_derived(item[0]))


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
    # Path.write_text()'s newline= kwarg needs Python 3.10+; this machine's
    # default `python3` is Apple's system 3.9.6. open()'s newline= has been
    # supported since 3.0, so it works on any interpreter this ever runs on.
    with path.open("w", encoding="utf-8", newline="\n") as f:
        f.write(content)


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


def build_manifest(revision: str, sha_for: Callable[[Path], str]) -> dict[str, Any]:
    """The artifact-set manifest: hashes for structural artifacts, and a
    separate, deliberately unhashed record of the history-derived ones (#1506).

    Split out from generate_set so the self-test can build a manifest with
    stand-in hashes and validate its SHAPE without a whole-solution run — the
    t1 row is the only thing that exercised the real one.
    """
    return {
        "$schema": "../schemas/artifact-set.schema.json",
        "schemaVersion": 2,
        "generator": "scripts/architecture_artifacts.py",
        "sourceRevision": revision,
        "revisionSemantics": REVISION_SEMANTICS,
        "artifacts": [
            {
                "path": relative.as_posix(),
                "format": artifact_format,
                **({"schema": f"architecture/schemas/{schema}"} if schema else {}),
                "sha256": sha_for(relative),
            }
            for relative, artifact_format, schema in hashed_artifacts()
        ],
        "historyArtifacts": [
            {
                "path": relative.as_posix(),
                "format": artifact_format,
                **({"schema": f"architecture/schemas/{schema}"} if schema else {}),
                "verification": HISTORY_VERIFICATION,
            }
            for relative, artifact_format, schema in history_artifacts()
        ],
    }


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
    manifest = build_manifest(
        manifest_revision,
        lambda relative: hashlib.sha256(
            staged_path(stage, relative).read_bytes()
        ).hexdigest(),
    )
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


def history_artifact_errors(
    relative: Path, documents: dict[str, Any]
) -> list[str]:
    """Verify a checked-in history-derived artifact WITHOUT comparing content.

    Tolerance is not blindness: the file must exist, parse as an object, satisfy
    its schema, and agree with the CURRENT shipping scope. Adding a shipping
    project without regenerating still fails here — what no longer fails is a
    coupling window that moved because a commit was made.
    """
    path = ROOT / relative
    if not path.is_file():
        return [f"missing artifact: {relative}"]
    validator = HISTORY_VALIDATORS.get(relative.as_posix())
    if validator is None:
        # A history-derived artifact with no validator would be verified by
        # nothing at all, which is the failure mode this split must not create.
        return [
            f"{relative}: declared history-derived with no validator in "
            "HISTORY_VALIDATORS"
        ]
    try:
        checked_in = load_object(path)
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        return [f"{relative}: unreadable: {exc}"]
    return [f"{relative}: {error}" for error in validator(documents, checked_in)]


def check_set(stage: Path, documents: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    for relative, _, _ in history_artifacts():
        errors.extend(history_artifact_errors(relative, documents))
    for relative, artifact_format, _ in (
        *hashed_artifacts(), (MANIFEST_PATH, "json", None)
    ):
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

    if history_split_self_test() != 0:
        return 1

    print(
        "PASS: unified architecture artifact gate rejects cycle, fan, order, "
        "scope, broad-commit, schema, and stale-hash mutations, hash-pins "
        f"{len(hashed_artifacts())} structural artifacts, and verifies "
        f"{len(history_artifacts())} history-derived artifact(s) by schema and "
        f"scope; stale diagnostic: {stale_difference}"
    )
    return 0


def history_split_self_test() -> int:
    """#1506. Prove the split does the two things it must do.

    1. The manifest hash-pins every STRUCTURAL artifact and hash-pins the
       history-derived one nowhere — otherwise the self-staling loop is back.
    2. Verifying the history-derived artifact by schema and scope instead of by
       content is still a real check: a content shift (the thing a commit
       causes) passes, while a scope drift or a schema break FAILS.
    """
    manifest = build_manifest("0" * 40, lambda relative: "a" * 64)
    schema = load_object(ROOT / "architecture/schemas/artifact-set.schema.json")
    schema_failures = validate_json_schema(manifest, schema)
    if schema_failures:
        print(
            "FAIL: generated manifest does not satisfy artifact-set.schema.json: "
            f"{schema_failures}",
            file=sys.stderr,
        )
        return 1

    hashed_paths = {item["path"] for item in manifest["artifacts"]}
    history_paths = {item["path"] for item in manifest["historyArtifacts"]}
    if hashed_paths & history_paths:
        print("FAIL: an artifact is both hash-pinned and history-derived", file=sys.stderr)
        return 1
    if hashed_paths | history_paths != {
        relative.as_posix() for relative, _, _ in ARTIFACTS
    }:
        print("FAIL: the manifest does not account for every artifact", file=sys.stderr)
        return 1
    if history_paths != set(HISTORY_DERIVED):
        print(
            f"FAIL: history artifacts {sorted(history_paths)} != "
            f"{sorted(HISTORY_DERIVED)}",
            file=sys.stderr,
        )
        return 1
    if any("sha256" in item for item in manifest["historyArtifacts"]):
        print(
            "FAIL: a history-derived artifact is hash-pinned, which is the "
            "#1506 self-staling loop",
            file=sys.stderr,
        )
        return 1
    if any(relative.as_posix() not in HISTORY_VALIDATORS for relative in
           (Path(path) for path in HISTORY_DERIVED)):
        print("FAIL: a history-derived artifact has no validator", file=sys.stderr)
        return 1

    roots = ("Excise.App", "Excise.Core")
    inventory = {
        "projects": [
            {"sourceRoot": root, "classification": "shipping"} for root in roots
        ] + [{"sourceRoot": "Excise.App.Tests", "classification": "test"}],
    }
    documents = {"inventory": inventory}
    report = coupling.analyze(
        [
            ("a" * 40, ["Excise.App/A.cs", "Excise.App/B.cs"]),
            ("b" * 40, ["Excise.App/A.cs", "Excise.App/B.cs", "Excise.Core/C.cs"]),
        ],
        200,
        "d" * 40,
        roots,
    )
    validator = HISTORY_VALIDATORS[HISTORY_DERIVED[0]]
    if validator(documents, report):
        print(
            "FAIL: a well-formed in-scope coupling report was rejected: "
            f"{validator(documents, report)}",
            file=sys.stderr,
        )
        return 1

    # The #1506 property: the window moving is NOT staleness.
    moved_window = copy.deepcopy(report)
    moved_window["window"]["commitsObserved"] += 1
    moved_window["files"][0]["commits"] += 1
    moved_window["pairs"][0]["cochanges"] += 1
    moved_window["window"]["newestProductionCommit"] = "e" * 40
    moved_window["sourceRevision"] = "f" * 40
    if validator(documents, moved_window):
        print(
            "FAIL: a shifted history window was reported as an error, so the "
            "self-staling loop is not actually fixed",
            file=sys.stderr,
        )
        return 1

    # ...but drifting off the current shipping scope, or off the schema, is.
    tolerance_mutations = {
        "scope-roots": lambda value: value["scope"]["sourceRoots"].append("Excise.Gone"),
        "out-of-scope-file": lambda value: value["files"].append(
            {"path": "Excise.App.Tests/T.cs", "commits": 3}
        ),
        "schema-break": lambda value: value.update({"schemaVersion": 1}),
    }
    for name, mutate in tolerance_mutations.items():
        mutated = copy.deepcopy(report)
        mutate(mutated)
        if not validator(documents, mutated):
            print(
                f"FAIL: {name} mutation of the history-derived artifact was "
                "not detected; schema-and-scope verification is blind",
                file=sys.stderr,
            )
            return 1
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
                errors.extend(check_set(stage, documents))
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as exc:
        print(f"FAIL: architecture artifact generation failed: {exc}", file=sys.stderr)
        return 1

    if errors:
        print("FAIL: architecture artifact set is invalid", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1
    action = "updated" if args.update else "current"
    print(
        f"PASS: coherent architecture artifact set is {action} "
        f"({len(hashed_artifacts())} hash-pinned, "
        f"{len(history_artifacts())} verified by schema and scope)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
