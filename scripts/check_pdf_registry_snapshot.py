#!/usr/bin/env python3
"""Check derived PDF registry files without modifying the source repository (#1765).

Copy tracked working files (including staged additions) into a disposable local
clone. Generators still see the same HEAD provenance and tracked-file inventory,
but all writes, including partial failure writes, are confined to that clone.
Explicit refresh/adopt commands remain separate, intentional write operations.
"""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def clean_env() -> dict[str, str]:
    # Hooks export GIT_DIR/GIT_INDEX_FILE/etc. Never let those address the real
    # repository while constructing or operating a throwaway clone (#1673).
    env = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    env.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
    return env


def git(root: Path, *args: str) -> bytes:
    return subprocess.check_output(["git", "-C", str(root), *args], env=clean_env())


def check(root: Path) -> int:
    paths = git(root, "ls-files", "-z").decode().split("\0")
    with tempfile.TemporaryDirectory(prefix="excise-registry-check-") as temporary:
        snapshot = (Path(temporary) / "snapshot").resolve()
        subprocess.run(["git", "clone", "--quiet", "--shared", "--no-checkout", str(root), str(snapshot)],
                       env=clean_env(), check=True)
        git(snapshot, "read-tree", "HEAD")
        for relative in paths:
            if not relative:
                continue
            source = root / relative
            if not source.is_file():
                continue  # A tracked working-tree deletion must remain absent.
            target = snapshot / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
        # The comparison baseline is the caller's reviewed working bytes, not
        # HEAD or its index: never overwrite staged-only or unstaged changes.
        git(snapshot, "add", "--all")
        (snapshot / ".git/excise-registry-snapshot").touch()
        env = clean_env()
        env["EXCISE_REGISTRY_SNAPSHOT"] = str(snapshot)
        return subprocess.run(["bash", "scripts/check-pdf-capability-registry.sh", "--check-snapshot"],
                              cwd=snapshot, env=env).returncode


def fingerprint(root: Path) -> dict[str, str]:
    return {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in root.rglob("*") if path.is_file()}


def self_test() -> None:
    # All mutation injection happens in a throwaway repository, never ROOT.
    with tempfile.TemporaryDirectory(prefix="excise-registry-purity-test-") as temporary:
        root = Path(temporary)
        subprocess.run(["git", "init", "--quiet", str(root)], env=clean_env(), check=True)
        (root / "scripts").mkdir()
        script = root / "scripts/check-pdf-capability-registry.sh"
        script.write_text('echo original > generated.json\nexit 0\n')
        (root / "generated.json").write_text("original\n")
        git(root, "add", ".")
        git(root, "-c", "user.name=Purity test", "-c", "user.email=test@example.invalid",
            "commit", "--quiet", "-m", "fixture")
        # Preserve staged bytes separately from dirty working bytes and unknown
        # files, with hook-style variables deliberately pointing at the source.
        (root / "generated.json").write_text("staged\n")
        git(root, "add", "generated.json")
        (root / "generated.json").write_text("unstaged\n")
        (root / "unknown.txt").write_text("must survive\n")
        previous = dict(os.environ)
        try:
            os.environ.update(GIT_DIR=str(root / ".git"), GIT_WORK_TREE=str(root),
                              GIT_INDEX_FILE=str(root / ".git/index"))
            for rc in (0, 1, 2):
                script.write_text(f'echo planted-mutation > generated.json\nexit {rc}\n')
                before = fingerprint(root)
                assert check(root) == rc
                assert fingerprint(root) == before, "checker modified source repository"
        finally:
            os.environ.clear()
            os.environ.update(previous)
    print("PASS: registry snapshot preserves tree, index, config and refs on success/failure with dirty staging and inherited GIT_* variables")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    else:
        raise SystemExit(check(ROOT))
