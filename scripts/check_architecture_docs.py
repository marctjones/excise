#!/usr/bin/env python3
"""Validate the canonical architecture-document authority and local links."""

from __future__ import annotations

import argparse
import json
import re
import tempfile
from pathlib import Path
from urllib.parse import unquote


CANONICAL_DOCS = (
    "docs/architecture/README.md",
    "docs/architecture/system-design.md",
    "docs/architecture/decisions.md",
)

ENTRY_POINTS = ("README.md", "CLAUDE.md", "AGENTS.md")

LEGACY_ARCHITECTURE_DOCS = (
    "VISION.md",
    "docs/EXCISE_ARCHITECTURE.md",
    "docs/EXCISE_APP_RENDERER_IMPLEMENTATION_PLAN.md",
    "docs/ISSUE_173_FIX_PLAN.md",
    "docs/PDF_2.0_OPERATOR_TESTING_PLAN.md",
    "docs/FIX_ORDER.md",
    "docs/PRIORITY.md",
    "docs/RC17_RABBIT_HOLES.md",
    "docs/NATIVE_AOT_INVESTIGATION.md",
    "docs/archive/EXCISE_GAP_ANALYSIS.md",
    "docs/archive/EXCISE_UNIFIED_FRAMEWORK_PLAN.md",
    "docs/archive/README.md",
)

LINK_RE = re.compile(r"(?<!!)\[[^]]+\]\(([^)]+)\)")
COMPONENT_ID_RE = re.compile(r"`([a-z][a-z0-9-]+)`")
STATUS_HEADING_RE = re.compile(
    r"^#{1,6}\s+(?:current|implementation|migration)\s+status\s*$",
    re.IGNORECASE | re.MULTILINE,
)


def _read(path: Path, errors: list[str]) -> str:
    if not path.is_file():
        errors.append(f"missing required file: {path}")
        return ""
    return path.read_text(encoding="utf-8")


def _validate_links(path: Path, text: str, errors: list[str]) -> None:
    for raw_target in LINK_RE.findall(text):
        target = raw_target.strip().split()[0].strip("<>")
        if not target or target.startswith(("#", "http://", "https://", "mailto:")):
            continue
        target = unquote(target.split("#", 1)[0])
        if not target:
            continue
        resolved = (path.parent / target).resolve()
        if not resolved.exists():
            errors.append(f"dangling local link in {path}: {raw_target}")


def validate(root: Path) -> list[str]:
    errors: list[str] = []
    canonical_paths = [root / relative for relative in CANONICAL_DOCS]

    texts = {path: _read(path, errors) for path in canonical_paths}
    index_path, system_path, _ = canonical_paths
    index_text = texts[index_path]
    system_text = texts[system_path]

    for path, text in texts.items():
        if text:
            _validate_links(path, text, errors)

    for relative in CANONICAL_DOCS[1:]:
        name = Path(relative).name
        if name not in index_text:
            errors.append(f"architecture index does not link canonical document: {relative}")

    for relative in ENTRY_POINTS:
        path = root / relative
        text = _read(path, errors)
        if "docs/architecture/README.md" not in text:
            errors.append(f"entry point does not link canonical architecture index: {relative}")

    for relative in LEGACY_ARCHITECTURE_DOCS:
        if (root / relative).exists():
            errors.append(f"superseded architecture/status document still exists: {relative}")

    registry_path = root / "architecture/design.json"
    if not registry_path.is_file():
        errors.append(f"missing architecture design registry: {registry_path}")
    else:
        try:
            registry = json.loads(registry_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            errors.append(f"cannot read architecture design registry {registry_path}: {exc}")
        else:
            component_ids = {
                component.get("id")
                for component in registry.get("components", [])
                if isinstance(component, dict) and isinstance(component.get("id"), str)
            }
            prose_ids = set(COMPONENT_ID_RE.findall(system_text))
            missing_ids = sorted(component_ids - prose_ids)
            if missing_ids:
                errors.append(
                    "system design omits registered component IDs: " + ", ".join(missing_ids)
                )

    if STATUS_HEADING_RE.search(system_text):
        errors.append(
            "system design contains a status heading; current/target status belongs in registries"
        )

    return errors


def _write_fixture(root: Path) -> None:
    (root / "docs/architecture").mkdir(parents=True)
    (root / "architecture").mkdir()
    (root / "docs/architecture/README.md").write_text(
        "[System](system-design.md) [Decisions](decisions.md)\n",
        encoding="utf-8",
    )
    (root / "docs/architecture/system-design.md").write_text(
        "# Design\n\nComponent (`core`).\n",
        encoding="utf-8",
    )
    (root / "docs/architecture/decisions.md").write_text(
        "# Decisions\n",
        encoding="utf-8",
    )
    (root / "architecture/design.json").write_text(
        json.dumps({"components": [{"id": "core"}]}),
        encoding="utf-8",
    )
    for relative in ENTRY_POINTS:
        (root / relative).write_text(
            "[Architecture](docs/architecture/README.md)\n",
            encoding="utf-8",
        )


def self_test() -> None:
    with tempfile.TemporaryDirectory(prefix="excise-architecture-docs-") as temp:
        root = Path(temp)
        _write_fixture(root)
        assert not validate(root), "valid fixture must pass"

        legacy = root / LEGACY_ARCHITECTURE_DOCS[0]
        legacy.write_text("obsolete\n", encoding="utf-8")
        assert any("superseded" in error for error in validate(root))
        legacy.unlink()

        index = root / CANONICAL_DOCS[0]
        index.write_text("[System](system-design.md)\n", encoding="utf-8")
        assert any("does not link canonical" in error for error in validate(root))
        index.write_text(
            "[System](system-design.md) [Decisions](decisions.md) [Missing](missing.md)\n",
            encoding="utf-8",
        )
        assert any("dangling local link" in error for error in validate(root))

        registry = root / "architecture/design.json"
        registry.write_text(
            json.dumps({"components": [{"id": "core"}, {"id": "renderer"}]}),
            encoding="utf-8",
        )
        assert any("renderer" in error for error in validate(root))

    print("PASS: architecture-document validator self-test")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return 0

    root = Path(__file__).resolve().parents[1]
    errors = validate(root)
    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1

    print(f"PASS: {len(CANONICAL_DOCS)} canonical architecture documents are coherent")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
