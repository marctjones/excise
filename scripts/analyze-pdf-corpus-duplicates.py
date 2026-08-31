#!/usr/bin/env python3
"""Report exact duplicate PDFs from a hash-enabled corpus-governance inventory."""
from __future__ import annotations
import argparse
import json
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT = ROOT / "test-pdfs/manifests/pdf-spec-registry/generated/corpus-governance.json"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", type=Path, default=DEFAULT)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    inventory = json.loads(args.inventory.read_text())
    by_hash: dict[str, list[dict]] = defaultdict(list)
    by_name: dict[str, list[dict]] = defaultdict(list)
    for asset in inventory.get("assets", []):
        for item in asset.get("files", []):
            record={"corpus": asset["id"], "path": item["path"], "bytes": item["bytes"], "sha256": item["sha256"]}
            by_hash[item["sha256"]].append(record)
            by_name[Path(item["path"]).name].append(record)
    groups = [{"sha256": digest, "files": files} for digest, files in sorted(by_hash.items()) if len(files) > 1]
    near = [{"basename": name, "files": files} for name, files in sorted(by_name.items()) if len({item["sha256"] for item in files}) > 1]
    result = {"schemaVersion": 1, "generatedBy": "scripts/analyze-pdf-corpus-duplicates.py", "inventory": str(args.inventory), "exactDuplicateGroups": groups, "exactDuplicateFileCount": sum(len(group["files"]) for group in groups), "obviousNearDuplicateCandidates": near, "nearDuplicateRule": "same basename with different SHA-256; these are review candidates, not duplicates"}
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(f"wrote {args.output} with {len(groups)} exact duplicate groups")


if __name__ == "__main__":
    main()
