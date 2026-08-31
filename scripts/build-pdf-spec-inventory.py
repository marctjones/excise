#!/usr/bin/env python3
"""Deterministically audit PDF-registry citations against local ISO PDF sources.

The script deliberately extracts only text and headings; it does not decide a
feature's product policy or infer implementation.  Reviewers decide those
facts from named code and test evidence.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path


REPO = Path(__file__).resolve().parents[1]
REGISTRY = REPO / "test-pdfs/manifests/pdf-spec-registry"


def extracted_text(pdf: Path) -> str:
    result = subprocess.run(["pdftotext", "-layout", str(pdf), "-"], check=True, text=True, capture_output=True)
    return result.stdout


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pdf17", type=Path, required=True)
    parser.add_argument("--pdf20", type=Path, required=True)
    parser.add_argument("--registry", type=Path, default=REGISTRY)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    documents = {"iso-32000-1": args.pdf17, "iso-32000-2": args.pdf20}
    texts = {source: extracted_text(path) for source, path in documents.items()}
    registry = json.loads((args.registry / "registry.json").read_text())
    results = []
    for section in registry["sections"]:
        data = json.loads((args.registry / section["path"]).read_text())
        for capability in data["capabilities"]:
            for reference in capability["spec"]:
                source = reference["source"]
                if source not in texts:
                    continue
                for clause in reference["clauses"]:
                    if clause.startswith("Annex"):
                        pattern = re.escape(clause.split()[1])
                    else:
                        pattern = rf"(?m)^\s*{re.escape(clause)}(?:\s|\.|$)"
                    results.append({"capability": capability["id"], "source": source, "clause": clause, "found": bool(re.search(pattern, texts[source]))})
    report = {
        "schemaVersion": 1,
        "generatedBy": "scripts/build-pdf-spec-inventory.py",
        "sources": [{"id": name, "path": str(path), "sha256": digest(path), "bytes": path.stat().st_size} for name, path in documents.items()],
        "citations": results,
        "resolved": sum(row["found"] for row in results),
        "unresolved": sum(not row["found"] for row in results)
    }
    output = args.output or args.registry / registry["generatedViews"]["specSourceInventory"]
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n")
    try:
        display_path = output.relative_to(REPO)
    except ValueError:
        display_path = output
    print(f"wrote {display_path}: {report['resolved']} resolved, {report['unresolved']} unresolved citations")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
