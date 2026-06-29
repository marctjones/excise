#!/usr/bin/env python3
"""Audit image/filter corpus coverage against the normative matrix.

The inventory answers "what does this corpus contain?" This audit answers
"which spec-derived obligations have enough atomic and corpus evidence?"
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path
from typing import Any


DEFAULT_ATOMIC_ROOTS = [
    "Pdfe.Core.Tests",
    "Pdfe.Rendering.Tests",
    "Pdfe.Cli.Tests",
    "test-pdfs/rendering-contracts",
]


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def iter_source_files(roots: list[Path]) -> list[Path]:
    files: list[Path] = []
    skipped_dirs = {".git", "bin", "obj", "logs"}
    for root in roots:
        if not root.exists():
            continue
        if root.is_file():
            files.append(root)
            continue
        for pattern in ("*.cs", "*.json", "*.py", "*.sh", "*.md"):
            for path in root.rglob(pattern):
                if not path.is_file():
                    continue
                parts = set(path.parts)
                if parts & skipped_dirs:
                    continue
                if any(part.startswith("exploratory-") for part in path.parts):
                    continue
                files.append(path)
    return sorted(files)


def token_regex(token: str) -> re.Pattern[str]:
    escaped = re.escape(token)
    return re.compile(escaped, re.IGNORECASE)


def evidence_tokens(requirement: dict[str, Any]) -> list[str]:
    tokens = [requirement["id"]]
    tokens.extend(requirement.get("detectFeatures", []))
    for item in requirement.get("mustCover", []):
        if len(item) >= 3:
            tokens.append(item)

    # Add shorter filter/color names so existing unit tests count before they
    # are migrated to explicit requirement IDs.
    for token in list(tokens):
        for prefix in (
            "filter:",
            "stream-filter:",
            "image-filter:",
            "image-color:",
            "colorspace:",
            "decodeparms:",
            "ccitt:",
            "dct:",
            "jbig2:",
            "jpx:",
            "image:",
            "policy:",
            "graphics:",
            "placement:",
            "bpc:",
        ):
            if token.startswith(prefix):
                suffix = token.removeprefix(prefix).split(".", 1)[0].split(":", 1)[0]
                if len(suffix) >= 3:
                    tokens.append(suffix)

    seen: set[str] = set()
    unique: list[str] = []
    for token in tokens:
        if token not in seen:
            seen.add(token)
            unique.append(token)
    return unique


def atomic_evidence(requirement: dict[str, Any], files: list[Path], root: Path, max_items: int) -> list[str]:
    regexes = [token_regex(token) for token in evidence_tokens(requirement)]
    evidence: list[str] = []
    for file in files:
        try:
            text = file.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        if any(regex.search(text) for regex in regexes):
            evidence.append(file.relative_to(root).as_posix() if file.is_relative_to(root) else file.as_posix())
            if len(evidence) >= max_items:
                break
    return evidence


def corpus_evidence(requirement: dict[str, Any], entries: list[dict[str, Any]], max_items: int) -> tuple[int, list[str]]:
    required = requirement.get("detectFeatures", [])
    mode = requirement.get("detectMode", "any")
    examples: list[str] = []
    count = 0

    for entry in entries:
        if entry.get("status") != "OK":
            continue
        features = set(entry.get("features", []))
        if not required:
            matched = False
        elif mode == "all":
            matched = all(feature in features for feature in required)
        else:
            matched = any(feature in features for feature in required)
        if not matched:
            continue
        count += 1
        if len(examples) < max_items:
            examples.append(entry.get("path", "<unknown>"))

    return count, examples


def minimums(matrix: dict[str, Any], requirement: dict[str, Any]) -> dict[str, int]:
    defaults = matrix.get("coveragePolicy", {}).get("defaultMinimum", {})
    explicit = requirement.get("minimumCoverage", {})
    return {
        "atomic": int(explicit.get("atomic", defaults.get("atomic", 1))),
        "corpus": int(explicit.get("corpus", defaults.get("corpus", 1))),
    }


def requirements_from_matrix(matrix: dict[str, Any]) -> list[dict[str, Any]]:
    requirements = matrix.get("requirements")
    if requirements:
        return requirements

    # Backward-compatible path for the earlier coarse schema.
    return [
        {
            "id": item["id"],
            "category": item.get("category", "unknown"),
            "requiredBy": item.get("requiredBy", ""),
            "detectFeatures": [item["id"]],
            "mustCover": item.get("mustCover", []),
        }
        for item in matrix.get("features", [])
        if isinstance(item, dict) and "id" in item
    ]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--matrix", default="test-pdfs/manifests/pdf-image-feature-matrix.json")
    parser.add_argument("--inventory", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--atomic-root", action="append", dest="atomic_roots", help="Root containing atomic tests/fixtures. Repeatable.")
    parser.add_argument("--max-examples", type=int, default=8)
    parser.add_argument("--fail-on-missing", action="store_true", help="Exit non-zero when any required coverage is missing.")
    args = parser.parse_args()

    root = Path.cwd()
    matrix_path = Path(args.matrix)
    inventory_path = Path(args.inventory)
    matrix = load_json(matrix_path)
    inventory = load_json(inventory_path)
    requirements = requirements_from_matrix(matrix)

    atomic_roots = [Path(item) for item in (args.atomic_roots or DEFAULT_ATOMIC_ROOTS)]
    source_files = iter_source_files(atomic_roots)
    entries = inventory.get("entries", [])

    results: list[dict[str, Any]] = []
    status_counts: Counter[str] = Counter()
    missing_atomic = 0
    missing_corpus = 0

    for requirement in requirements:
        mins = minimums(matrix, requirement)
        atomic = atomic_evidence(requirement, source_files, root, args.max_examples)
        corpus_count, corpus = corpus_evidence(requirement, entries, args.max_examples)
        has_atomic = len(atomic) >= mins["atomic"]
        has_corpus = corpus_count >= mins["corpus"]

        if has_atomic and has_corpus:
            status = "COVERED"
        elif has_atomic:
            status = "MISSING_CORPUS"
        elif has_corpus:
            status = "MISSING_ATOMIC"
        else:
            status = "MISSING_BOTH"

        if not has_atomic:
            missing_atomic += 1
        if not has_corpus:
            missing_corpus += 1
        status_counts[status] += 1

        results.append({
            "id": requirement["id"],
            "category": requirement.get("category", "unknown"),
            "requiredBy": requirement.get("requiredBy", ""),
            "mustCover": requirement.get("mustCover", []),
            "detectFeatures": requirement.get("detectFeatures", []),
            "minimumCoverage": mins,
            "atomicCount": len(atomic),
            "atomicEvidence": atomic,
            "corpusCount": corpus_count,
            "corpusExamples": corpus,
            "status": status,
        })

    report = {
        "schemaVersion": 1,
        "matrix": str(matrix_path),
        "inventory": str(inventory_path),
        "requirements": len(results),
        "statusCounts": dict(sorted(status_counts.items())),
        "missingAtomicRequirements": missing_atomic,
        "missingCorpusRequirements": missing_corpus,
        "results": results,
    }

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")

    print(f"wrote coverage audit: {output}")
    print(f"requirements: {len(results)}")
    for status, count in sorted(status_counts.items()):
        print(f"  {status}: {count}")
    print(f"missing atomic requirements: {missing_atomic}")
    print(f"missing corpus requirements: {missing_corpus}")

    if args.fail_on_missing and (missing_atomic or missing_corpus):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
