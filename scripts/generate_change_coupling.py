#!/usr/bin/env python3
"""Generate deterministic production-file change coupling from Git history."""

from __future__ import annotations

import argparse
from collections import Counter
from itertools import combinations
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT = ROOT / "architecture/generated/change-coupling.json"
PRODUCTION_ROOTS = (
    "Excise.Core",
    "Excise.Rendering",
    "Excise.Avalonia",
    "Excise.App",
    "Excise.Cli",
    "Excise.Ocr",
    "Excise.Ocr.Native",
)
SOURCE_SUFFIXES = (".cs", ".axaml", ".csproj")
MAX_FILES_PER_COMMIT = 60


def git(*args: str) -> str:
    return subprocess.run(
        ["git", *args], cwd=ROOT, check=True, capture_output=True, text=True
    ).stdout


def load_commits(limit: int) -> list[tuple[str, list[str]]]:
    output = git(
        "log",
        "--no-merges",
        f"-n{limit}",
        "--format=commit:%H",
        "--name-only",
        "--",
        *PRODUCTION_ROOTS,
    )
    commits: list[tuple[str, list[str]]] = []
    revision: str | None = None
    files: list[str] = []
    for raw in output.splitlines():
        line = raw.strip()
        if line.startswith("commit:"):
            if revision is not None:
                commits.append((revision, sorted(set(files))))
            revision = line.removeprefix("commit:")
            files = []
        elif line.endswith(SOURCE_SUFFIXES):
            files.append(line)
    if revision is not None:
        commits.append((revision, sorted(set(files))))
    return commits


def analyze(
    commits: list[tuple[str, list[str]]], requested: int, source_revision: str
) -> dict:
    file_counts: Counter[str] = Counter()
    pair_counts: Counter[tuple[str, str]] = Counter()
    broad_excluded = 0
    accepted: list[tuple[str, list[str]]] = []
    for revision, files in commits:
        if not files:
            continue
        if len(files) > MAX_FILES_PER_COMMIT:
            broad_excluded += 1
            continue
        accepted.append((revision, files))
        file_counts.update(files)
        pair_counts.update(combinations(files, 2))

    pairs = []
    for (source, target), cochanges in pair_counts.items():
        if cochanges < 2:
            continue
        union = file_counts[source] + file_counts[target] - cochanges
        pairs.append(
            {
                "source": source,
                "target": target,
                "cochanges": cochanges,
                "jaccard": round(cochanges / union, 4),
            }
        )
    pairs.sort(key=lambda item: (-item["cochanges"], -item["jaccard"], item["source"], item["target"]))

    return {
        "schemaVersion": 1,
        "generator": "scripts/generate_change_coupling.py",
        "sourceRevision": source_revision,
        "window": {
            "commitsRequested": requested,
            "commitsObserved": len(accepted),
            "broadCommitsExcluded": broad_excluded,
            "newestProductionCommit": accepted[0][0] if accepted else None,
            "oldestProductionCommit": accepted[-1][0] if accepted else None,
        },
        "files": [
            {"path": path, "commits": count}
            for path, count in sorted(file_counts.items(), key=lambda item: (-item[1], item[0]))
        ],
        "pairs": pairs[:1000],
    }


def serialize(report: dict) -> str:
    return json.dumps(report, indent=2, ensure_ascii=False) + "\n"


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


def self_test() -> None:
    report = analyze(
        [
            ("a" * 40, ["Excise.App/A.cs", "Excise.App/B.cs"]),
            ("b" * 40, ["Excise.App/A.cs", "Excise.App/B.cs", "Excise.Core/C.cs"]),
            ("c" * 40, [f"Excise.Core/F{index}.cs" for index in range(61)]),
        ],
        3,
        "d" * 40,
    )
    assert report["window"]["commitsObserved"] == 2
    assert report["window"]["broadCommitsExcluded"] == 1
    assert report["pairs"][0]["source"] == "Excise.App/A.cs"
    assert report["pairs"][0]["target"] == "Excise.App/B.cs"
    assert report["pairs"][0]["cochanges"] == 2
    print("PASS: change-coupling generator self-test")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--commits", type=int, default=200)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if args.commits < 1:
        parser.error("--commits must be positive")

    output = args.output.resolve()
    report = analyze(load_commits(args.commits), args.commits, git("rev-parse", "HEAD").strip())
    if args.check:
        if not output.is_file():
            print(f"FAIL: change-coupling output is missing: {output}", file=sys.stderr)
            return 1
        actual = json.loads(output.read_text(encoding="utf-8"))
        report["sourceRevision"] = actual.get("sourceRevision")
        if actual != report:
            print(f"FAIL: change-coupling output is stale: {output}", file=sys.stderr)
            print(f"      regenerate with {Path(__file__).name}", file=sys.stderr)
            return 1
        print(f"PASS: change coupling is current ({len(report['pairs'])} retained pairs)")
        return 0

    write_atomic(output, serialize(report))
    print(f"wrote {output.relative_to(ROOT)} ({len(report['pairs'])} retained pairs)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
