#!/usr/bin/env python3
"""Audit PDF 2.0 renderer requirements against tests, fixtures, corpus, and render reports."""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path
from typing import Any


DEFAULT_TEXT_EVIDENCE_ROOTS = [
    "Excise.Core.Tests",
    "Excise.Rendering.Tests",
    "Excise.Cli.Tests",
    "scripts",
]
DEFAULT_CONTRACT_ROOTS = ["test-pdfs/rendering-contracts"]
HARD_GATE_PROFILES = {"RendererCore", "ParserSupport", "SecurityPolicy"}
NON_BLOCKING_PROFILES = {"PreserveOnly", "Interactive", "TaggedPdf", "Signatures", "Multimedia3D", "Geospatial"}


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def load_operator_evidence(path: Path | None) -> dict[str, dict[str, Any]]:
    if not path:
        return {}
    if not path.exists():
        raise SystemExit(f"operator evidence manifest not found: {path}")
    manifest = load_json(path)
    return {
        row["id"]: row
        for row in manifest.get("operatorEvidence", [])
        if isinstance(row, dict) and "id" in row
    }


def iter_files(roots: list[Path]) -> list[Path]:
    files: list[Path] = []
    skipped = {".git", "bin", "obj", "logs", "TestResults"}
    for root in roots:
        if not root.exists():
            continue
        if root.is_file():
            files.append(root)
            continue
        for pattern in ("*.cs", "*.json", "*.py", "*.sh", "*.md"):
            for path in root.rglob(pattern):
                if path.is_file() and not (set(path.parts) & skipped):
                    files.append(path)
    return sorted(files)


def evidence_tokens(row: dict[str, Any]) -> list[str]:
    tokens = [row["id"]]
    tokens.extend(row.get("detectFeatures", []))
    if row.get("operator"):
        tokens.extend([f"operator:{row['operator']}", row["operator"]])
    if row.get("area"):
        tokens.append(row["area"])
    unique: list[str] = []
    seen: set[str] = set()
    for token in tokens:
        if token and token not in seen:
            seen.add(token)
            unique.append(token)
    return unique


def find_text_evidence(row: dict[str, Any], files: list[Path], root: Path, max_items: int) -> list[str]:
    patterns = [re.compile(re.escape(token), re.IGNORECASE) for token in evidence_tokens(row)]
    matches: list[str] = []
    for file in files:
        try:
            text = file.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        if any(pattern.search(text) for pattern in patterns):
            matches.append(file.relative_to(root).as_posix() if file.is_relative_to(root) else file.as_posix())
            if len(matches) >= max_items:
                break
    return matches


def iter_contract_files(roots: list[Path]) -> list[Path]:
    files: list[Path] = []
    for root in roots:
        if not root.exists():
            continue
        if root.is_file() and root.suffix == ".json":
            files.append(root)
            continue
        files.extend(path for path in root.rglob("*.json") if path.is_file())
    return sorted(files)


def contract_requirement_ids(contract: dict[str, Any]) -> set[str]:
    ids: set[str] = set()
    for key in ("Pdf20Requirements", "pdf20Requirements"):
        values = contract.get(key, [])
        if isinstance(values, list):
            ids.update(value for value in values if isinstance(value, str))

    pages = contract.get("Pages", {})
    if isinstance(pages, dict):
        for page in pages.values():
            if not isinstance(page, dict):
                continue
            for key in ("Pdf20Requirements", "pdf20Requirements"):
                values = page.get(key, [])
                if isinstance(values, list):
                    ids.update(value for value in values if isinstance(value, str))
    return ids


def find_corpus_evidence(requirement_id: str, files: list[Path], root: Path, max_items: int) -> list[str]:
    evidence: list[str] = []
    for file in files:
        try:
            contract = load_json(file)
        except (OSError, json.JSONDecodeError):
            continue
        if requirement_id not in contract_requirement_ids(contract):
            continue
        evidence.append(file.relative_to(root).as_posix() if file.is_relative_to(root) else file.as_posix())
        if len(evidence) >= max_items:
            break
    return evidence


def load_render_report(path: Path | None) -> dict[str, Any] | None:
    if not path:
        return None
    if not path.exists():
        raise SystemExit(f"render report not found: {path}")
    return load_json(path)


def render_failures_for(row: dict[str, Any], report: dict[str, Any] | None, max_items: int) -> list[str]:
    if not report:
        return []
    failures: list[str] = []
    tokens = [token.lower() for token in evidence_tokens(row)]
    for entry in report.get("results", report.get("entries", [])):
        status = str(entry.get("status", entry.get("resultStatus", ""))).upper()
        if status in {"PASS", "COVERED", "OK", "EXPECTED_REFUSAL"}:
            continue
        haystack = json.dumps(entry, sort_keys=True).lower()
        if any(token.lower() in haystack for token in tokens):
            failures.append(entry.get("pdf", entry.get("path", "<unknown>")))
            if len(failures) >= max_items:
                break
    return failures


def classify(
    row: dict[str, Any],
    text_evidence: list[str],
    corpus_evidence: list[str],
    unit_evidence: list[dict[str, Any]],
    atomic_evidence: list[dict[str, Any]],
    render_failures: list[str],
) -> str:
    if row["profile"] in NON_BLOCKING_PROFILES:
        return "UNSUPPORTED_TRACKED" if row.get("status") == "UNSUPPORTED_TRACKED" else "COVERED"
    if render_failures:
        return "FAILING_RENDER"

    required = set(row.get("requiredEvidence", []))
    needs_text = bool(required & {"unit", "atomic-pdf"})
    unit_source = unit_evidence if row.get("kind") == "content-operator" else unit_evidence or text_evidence
    atomic_source = atomic_evidence if row.get("kind") == "content-operator" else atomic_evidence or text_evidence
    if "corpus" in required and not corpus_evidence:
        return "MISSING_CORPUS"
    if "unit" in required and not unit_source:
        return "MISSING_UNIT"
    if "atomic-pdf" in required and not atomic_source:
        return "MISSING_ATOMIC_PDF"
    if needs_text and not (unit_source or atomic_source):
        return "MISSING_UNIT"
    return "COVERED"


def image_matrix_summary(path: Path | None) -> dict[str, Any] | None:
    if not path or not path.exists():
        return None
    matrix = load_json(path)
    requirements = matrix.get("requirements", matrix.get("features", []))
    return {
        "path": str(path),
        "requirements": len(requirements),
        "status": "REPRESENTED",
    }


def image_coverage_summary(path: Path | None) -> dict[str, Any] | None:
    if not path:
        return None
    if not path.exists():
        raise SystemExit(f"image coverage report not found: {path}")
    report = load_json(path)
    missing_atomic = int(report.get("missingAtomicRequirements", 0))
    missing_corpus = int(report.get("missingCorpusRequirements", 0))
    return {
        "path": str(path),
        "requirements": report.get("requirements", 0),
        "statusCounts": report.get("statusCounts", {}),
        "missingAtomicRequirements": missing_atomic,
        "missingCorpusRequirements": missing_corpus,
        "status": "COVERED" if missing_atomic == 0 and missing_corpus == 0 else "MISSING",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--matrix", default="test-pdfs/manifests/pdf20-renderer-requirements.json")
    parser.add_argument("--image-matrix", default="test-pdfs/manifests/pdf-image-feature-matrix.json")
    parser.add_argument("--image-coverage-report", default="logs/image-conformance/normative/coverage-audit.json")
    parser.add_argument("--operator-evidence", default="test-pdfs/manifests/pdf20-operator-evidence.json")
    parser.add_argument("--render-report", help="Optional render-quality report JSON.")
    parser.add_argument("--output", required=True)
    parser.add_argument("--evidence-root", action="append", dest="evidence_roots")
    parser.add_argument("--contract-root", action="append", dest="contract_roots")
    parser.add_argument("--max-examples", type=int, default=8)
    parser.add_argument("--fail-on-hard-gate", action="store_true")
    args = parser.parse_args()

    root = Path.cwd()
    matrix_path = Path(args.matrix)
    matrix = load_json(matrix_path)
    operator_evidence = load_operator_evidence(Path(args.operator_evidence) if args.operator_evidence else None)
    files = iter_files([Path(item) for item in (args.evidence_roots or DEFAULT_TEXT_EVIDENCE_ROOTS)])
    contract_files = iter_contract_files([Path(item) for item in (args.contract_roots or DEFAULT_CONTRACT_ROOTS)])
    render_report = load_render_report(Path(args.render_report) if args.render_report else None)
    image_coverage = image_coverage_summary(Path(args.image_coverage_report) if args.image_coverage_report else None)

    results: list[dict[str, Any]] = []
    status_counts: Counter[str] = Counter()
    hard_gate_failures = 0
    for row in matrix.get("requirements", []):
        text_evidence = find_text_evidence(row, files, root, args.max_examples)
        corpus_evidence = find_corpus_evidence(row["id"], contract_files, root, args.max_examples)
        explicit_operator_evidence = operator_evidence.get(row["id"], {}) if row.get("kind") == "content-operator" else {}
        unit_evidence = explicit_operator_evidence.get("unitEvidence", [])
        atomic_evidence = explicit_operator_evidence.get("atomicEvidence", [])
        failures = render_failures_for(row, render_report, args.max_examples)
        status = classify(row, text_evidence, corpus_evidence, unit_evidence, atomic_evidence, failures)
        status_counts[status] += 1
        if row["profile"] in HARD_GATE_PROFILES and status != "COVERED":
            hard_gate_failures += 1
        results.append({
            "id": row["id"],
            "clause": row["clause"],
            "area": row["area"],
            "profile": row["profile"],
            "obligation": row["obligation"],
            "requiredEvidence": row["requiredEvidence"],
            "oracle": row["oracle"],
            "matrixStatus": row["status"],
            "auditStatus": status,
            "textEvidence": text_evidence,
            "corpusEvidence": corpus_evidence,
            "unitEvidence": unit_evidence,
            "atomicEvidence": atomic_evidence,
            "renderFailures": failures,
        })

    area_counts = Counter(row["area"] for row in matrix.get("requirements", []))
    profile_counts = Counter(row["profile"] for row in matrix.get("requirements", []))
    report = {
        "schemaVersion": 1,
        "matrix": str(matrix_path),
        "imageFilterMatrix": image_matrix_summary(Path(args.image_matrix) if args.image_matrix else None),
        "imageFilterCoverage": image_coverage,
        "operatorEvidence": args.operator_evidence,
        "renderReport": args.render_report,
        "requirements": len(results),
        "hardGateFailures": hard_gate_failures,
        "statusCounts": dict(sorted(status_counts.items())),
        "areaCounts": dict(sorted(area_counts.items())),
        "profileCounts": dict(sorted(profile_counts.items())),
        "results": results,
    }

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"wrote PDF 2.0 renderer coverage audit: {output}")
    print(f"requirements: {len(results)}")
    print(f"hard gate failures: {hard_gate_failures}")
    for status, count in sorted(status_counts.items()):
        print(f"  {status}: {count}")

    image_coverage_missing = bool(image_coverage and image_coverage.get("status") != "COVERED")
    if args.fail_on_hard_gate and (hard_gate_failures or image_coverage_missing):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
