#!/usr/bin/env python3
"""Collect deterministic evidence candidates for every PDF capability leaf.

The output is deliberately non-authoritative.  It indexes repository material
that a reviewer can promote into a leaf's explicit evidence contract; it never
alters a support claim based on a filename or word match.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REGISTRY = ROOT / "test-pdfs/manifests/pdf-spec-registry"
DEFAULT_OUTPUT = REGISTRY / "generated/evidence-collection.json"
WORD = re.compile(r"[A-Za-z][A-Za-z0-9-]{2,}")


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def kind(path: str) -> str:
    if ".Tests/" in path or path.startswith("tests/"):
        return "test"
    if path.startswith("docs/architecture/"):
        return "architecture"
    return "source"


def term_candidates(cap: dict, section: str, config: dict) -> tuple[list[str], list[str]]:
    ignored = {item.lower() for item in config["ignoredTerms"]}
    specific = {word.lower() for word in WORD.findall(cap["name"] + " " + cap["id"].replace(".", " ")) if word.lower() not in ignored}
    # Normalized British/American spellings are a discovery aid only.
    if "colour" in specific: specific.add("color")
    if "color" in specific: specific.add("colour")
    return (sorted(term for term in specific if len(term) >= 3), sorted(item.lower() for item in config["sectionTerms"].get(section, [])))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    registry = load(REGISTRY / "registry.json")
    config = load(REGISTRY / "evidence-collection.json")
    index: list[tuple[str, str, str]] = []
    digest = hashlib.sha256()
    for relative in config["sourceRoots"]:
        root = ROOT / relative
        if not root.exists():
            continue
        for path in sorted(candidate for candidate in root.rglob("*") if candidate.is_file() and candidate.suffix in config["extensions"] and "/bin/" not in str(candidate) and "/obj/" not in str(candidate)):
            text = path.read_text(encoding="utf-8", errors="ignore").lower()
            repo_path = str(path.relative_to(ROOT))
            digest.update(repo_path.encode()); digest.update(hashlib.sha256(text.encode()).digest())
            index.append((repo_path, kind(repo_path), text))
    rows = []
    by_section: dict[str, Counter] = defaultdict(Counter)
    for listing in registry["sections"]:
        section_doc = load(REGISTRY / listing["path"])
        for cap in section_doc["capabilities"]:
            specific_terms, context_terms = term_candidates(cap, listing["id"], config)
            terms = specific_terms + context_terms
            direct = cap.get("evidence", [])
            matches = []
            for path, material_kind, text in index:
                specific_hits = [term for term in specific_terms if term in text]
                context_hits = [term for term in context_terms if term in text]
                if len(specific_terms) >= config["minimumDistinctTermHits"]:
                    eligible = len(specific_hits) >= config["minimumDistinctTermHits"]
                else:
                    eligible = bool(specific_hits) and bool(context_hits)
                if eligible:
                    matches.append({"path": path, "kind": material_kind, "matchedTerms": (specific_hits + context_hits)[:8]})
            # A capability can be represented by a deliberately sparse policy
            # record. Preserve that fact instead of requiring a search hit.
            state = "registered-evidence" if direct else "candidate-evidence" if matches else "no-evidence-found"
            row = {"id": cap["id"], "section": listing["id"], "collectionState": state, "featureTerms": specific_terms, "contextTerms": context_terms, "registeredEvidence": direct, "registeredVerification": cap.get("verification"), "candidateReferences": matches[:80], "candidateReferenceCount": len(matches)}
            rows.append(row)
            by_section[listing["id"]][state] += 1
    result = {"schemaVersion": 1, "generatedBy": "scripts/collect-pdf-capability-evidence.py", "policy": config["policy"], "indexedFileCount": len(index), "indexedContentSha256": digest.hexdigest(), "capabilities": rows, "summary": {"capabilities": len(rows), "collectionStates": dict(sorted(Counter(row["collectionState"] for row in rows).items())), "bySection": {section: dict(sorted(counts.items())) for section, counts in sorted(by_section.items())}}}
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.output} with {len(rows)} capability evidence records from {len(index)} indexed files")


if __name__ == "__main__":
    main()
