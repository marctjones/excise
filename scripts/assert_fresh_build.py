#!/usr/bin/env python3
"""Refuse stale --no-build test/run executions.

The checker compares each project source tree against that project's build
output DLL. For a root test/app target, referenced project outputs copied into
the root output directory are checked as well. An unchanged test assembly can
legitimately be older than a referenced source file after a solution build; the
referenced DLL copy is the binary that must be fresh in that case.
"""

from __future__ import annotations

import argparse
import os
import re
import hashlib
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


SOURCE_SUFFIXES = {
    ".cs",
    ".csproj",
    ".props",
    ".targets",
    ".axaml",
    ".xaml",
    ".resx",
    ".json",
}
SKIP_DIRS = {"bin", "obj", "TestResults", ".git"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("targets", nargs="*", help="project, directory, or solution targets")
    parser.add_argument("-c", "--configuration", default="Debug")
    parser.add_argument("--repo-root", default=".")
    return parser.parse_args()


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def first_property(root: ET.Element, name: str) -> str | None:
    for element in root.iter():
        if local_name(element.tag) == name and element.text and element.text.strip():
            return element.text.strip()
    return None


def project_references(project: Path) -> list[Path]:
    try:
        root = ET.parse(project).getroot()
    except ET.ParseError:
        return []

    refs: list[Path] = []
    for element in root.iter():
        if local_name(element.tag) != "ProjectReference":
            continue
        include = element.attrib.get("Include")
        if include:
            include = include.replace("\\", os.sep)
            refs.append((project.parent / include).resolve())
    return refs


def project_target_frameworks(project: Path) -> list[str]:
    root = ET.parse(project).getroot()
    frameworks = first_property(root, "TargetFrameworks")
    if frameworks:
        return [f.strip() for f in frameworks.split(";") if f.strip()]
    framework = first_property(root, "TargetFramework")
    if framework:
        return [framework]
    return []


def assembly_name(project: Path) -> str:
    try:
        root = ET.parse(project).getroot()
        return first_property(root, "AssemblyName") or project.stem
    except ET.ParseError:
        return project.stem


def projects_from_solution(solution: Path) -> list[Path]:
    projects: list[Path] = []
    pattern = re.compile(r'"([^"]+\.csproj)"')
    for line in solution.read_text(encoding="utf-8", errors="replace").splitlines():
        match = pattern.search(line)
        if match:
            projects.append((solution.parent / match.group(1)).resolve())
    return projects


def resolve_target(target: Path, repo_root: Path) -> list[Path]:
    target = (repo_root / target).resolve() if not target.is_absolute() else target.resolve()
    if target.suffix == ".sln":
        return projects_from_solution(target)
    if target.suffix == ".csproj":
        return [target]
    if target.is_dir():
        direct = sorted(target.glob("*.csproj"))
        if direct:
            return [direct[0].resolve()]
    return []


def closure(project: Path, seen: set[Path] | None = None) -> list[Path]:
    if seen is None:
        seen = set()
    project = project.resolve()
    if project in seen:
        return []
    seen.add(project)
    projects = [project]
    for ref in project_references(project):
        projects.extend(closure(ref, seen))
    return projects


def source_files(projects: list[Path], repo_root: Path, include_shared: bool = True) -> list[Path]:
    files: list[Path] = []
    roots = {p.parent for p in projects}
    for root in roots:
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for filename in filenames:
                path = Path(dirpath) / filename
                if path.suffix in SOURCE_SUFFIXES:
                    files.append(path)

    if include_shared:
        for name in (
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Packages.props",
            "global.json",
            "NuGet.config",
        ):
            path = repo_root / name
            if path.exists():
                files.append(path)
    return files


def newest(paths: list[Path]) -> tuple[Path | None, float]:
    winner: Path | None = None
    mtime = 0.0
    for path in paths:
        try:
            current = path.stat().st_mtime
        except FileNotFoundError:
            continue
        if current > mtime:
            winner = path
            mtime = current
    return winner, mtime


def output_candidates(project: Path, configuration: str) -> list[Path]:
    name = assembly_name(project)
    candidates = [
        project.parent / "bin" / configuration / framework / f"{name}.dll"
        for framework in project_target_frameworks(project)
    ]
    if not candidates:
        candidates = sorted((project.parent / "bin" / configuration).glob(f"**/{name}.dll"))
    return candidates


def same_bytes(a, b):
    """Content check used only when mtimes SAY stale.

    A newer timestamp does not imply different code. run-full-suite.sh's
    corpus-scan steps build tools/Excise.RenderTools, which rebuilds
    Excise.Core and Excise.Rendering from unchanged sources; every later
    --no-build step then saw a reference output newer than its copy and
    refused, even though the bytes were identical. That is a false stale --
    it failed test-count-rendering and test-count-app on a run where nothing
    had been edited at all.

    Comparing content only on the mtime-says-stale path keeps the guard's
    real job intact (an actually-rebuilt dependency still differs and still
    fails) while removing the false positive, and costs one hash of a file
    we were about to reject anyway.
    """
    try:
        if a.stat().st_size != b.stat().st_size:
            return False
        return hashlib.sha256(a.read_bytes()).digest() == hashlib.sha256(b.read_bytes()).digest()
    except OSError:
        return False


def newest_output(project: Path, configuration: str) -> tuple[Path | None, float]:
    return newest(output_candidates(project, configuration))


def copied_reference_outputs(
    root_project: Path,
    reference_project: Path,
    configuration: str,
) -> list[Path]:
    reference_name = assembly_name(reference_project)
    copies: list[Path] = []
    for root_output in output_candidates(root_project, configuration):
        copies.append(root_output.parent / f"{reference_name}.dll")
    return copies


def main() -> int:
    args = parse_args()
    if os.environ.get("EXCISE_ALLOW_STALE_NO_BUILD") == "1":
        return 0

    repo_root = Path(args.repo_root).resolve()
    targets = [Path(t) for t in args.targets] or [repo_root / "excise.sln"]
    root_projects: list[Path] = []
    for target in targets:
        root_projects.extend(resolve_target(target, repo_root))

    if not root_projects:
        print(
            "FAIL: could not resolve a project or solution for --no-build freshness check.",
            file=sys.stderr,
        )
        print("      Set EXCISE_ALLOW_STALE_NO_BUILD=1 only for an intentional stale run.", file=sys.stderr)
        return 2

    failures: list[str] = []
    for root_project in root_projects:
        project_closure = closure(root_project)
        for project in project_closure:
            source, source_mtime = newest(source_files([project], repo_root))
            output, output_mtime = newest_output(project, args.configuration)

            if output is None:
                failures.append(
                    f"{project.relative_to(repo_root)} has no {args.configuration} output DLL. Build first."
                )
                continue
            if source is not None and source_mtime > output_mtime:
                failures.append(
                    f"{project.relative_to(repo_root)} output is older than "
                    f"{source.relative_to(repo_root)}. Build first."
                )

        for reference_project in project_closure[1:]:
            reference_output, reference_output_mtime = newest_output(reference_project, args.configuration)
            if reference_output is None:
                continue
            copy, copy_mtime = newest(copied_reference_outputs(root_project, reference_project, args.configuration))
            if copy is None:
                continue
            if reference_output_mtime > copy_mtime and not same_bytes(reference_output, copy):
                failures.append(
                    f"{root_project.relative_to(repo_root)} output copy {copy.relative_to(repo_root)} "
                    f"is older than {reference_output.relative_to(repo_root)}. Build first."
                )

    if failures:
        print("FAIL: refusing stale --no-build execution.", file=sys.stderr)
        for failure in failures[:20]:
            print(f"      - {failure}", file=sys.stderr)
        if len(failures) > 20:
            print(f"      ... {len(failures) - 20} more stale project(s)", file=sys.stderr)
        print("      Re-run without --no-build, or build the affected project(s) first.", file=sys.stderr)
        print("      Intentional escape hatch: EXCISE_ALLOW_STALE_NO_BUILD=1.", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
