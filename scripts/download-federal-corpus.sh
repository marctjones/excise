#!/usr/bin/env bash
# Download the manifest-driven federal everyday PDF corpus.
#
# The PDFs themselves are not checked into git. The manifest records official
# source URLs, categories, license basis, page counts, and SHA-256 hashes so
# release-quality corpus scans are repeatable and legally clean.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
MANIFEST="$ROOT/test-pdfs/manifests/federal-everyday-corpus.json"
TARGET=""
DRY_RUN=0
FORCE=0

usage() {
    cat <<'EOF'
Download the manifest-driven federal everyday PDF corpus.

Usage:
  scripts/download-federal-corpus.sh [options]

Options:
  --manifest <path>  Manifest JSON. Defaults to test-pdfs/manifests/federal-everyday-corpus.json.
  --target <dir>     Output directory. Defaults to the manifest defaultTarget.
  --dry-run          Print planned downloads and metadata without downloading.
  --force            Re-download files even when the existing SHA-256 matches.
  -h, --help         Show this help.
EOF
}

require_value() {
    local option="$1"
    local value="${2:-}"
    if [ -z "$value" ]; then
        echo "Missing value for $option" >&2
        usage >&2
        exit 2
    fi
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --manifest) require_value "$1" "${2:-}"; MANIFEST="$2"; shift 2 ;;
        --manifest=*) MANIFEST="${1#*=}"; shift ;;
        --target) require_value "$1" "${2:-}"; TARGET="$2"; shift 2 ;;
        --target=*) TARGET="${1#*=}"; shift ;;
        --dry-run) DRY_RUN=1; shift ;;
        --force) FORCE=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

python3 - "$ROOT" "$MANIFEST" "$TARGET" "$DRY_RUN" "$FORCE" <<'PY'
import hashlib
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

root = Path(sys.argv[1])
manifest_path = Path(sys.argv[2])
target_arg = sys.argv[3]
dry_run = sys.argv[4] == "1"
force = sys.argv[5] == "1"

with manifest_path.open("r", encoding="utf-8") as f:
    manifest = json.load(f)

target = Path(target_arg) if target_arg else root / manifest["defaultTarget"]
if not target.is_absolute():
    target = root / target

documents = manifest.get("documents", [])
if not documents:
    print(f"No documents in manifest: {manifest_path}", file=sys.stderr)
    sys.exit(2)

print("=================================================")
print("Federal Corpus Downloader")
print("=================================================")
print(f"Manifest: {manifest_path}")
print(f"Target:   {target}")
print(f"Docs:     {len(documents)}")
print(f"License:  {manifest.get('licenseBasis', 'see manifest entries')}")
if dry_run:
    print("Mode:     dry-run")
print("")

def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()

def page_count(path: Path) -> int | None:
    if shutil.which("pdfinfo") is None:
        return None
    proc = subprocess.run(
        ["pdfinfo", str(path)],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        check=False,
    )
    if proc.returncode != 0:
        return None
    for line in proc.stdout.splitlines():
        if line.startswith("Pages:"):
            try:
                return int(line.split(":", 1)[1].strip())
            except ValueError:
                return None
    return None

def download(url: str, dest: Path) -> int:
    tmp = dest.with_suffix(dest.suffix + ".tmp")
    tmp.unlink(missing_ok=True)
    cmd = [
        "curl",
        "-L",
        "--fail",
        "--silent",
        "--show-error",
        "--connect-timeout",
        "15",
        "--max-time",
        "180",
        "-o",
        str(tmp),
        url,
    ]
    rc = subprocess.run(cmd, check=False).returncode
    if rc == 0:
        tmp.replace(dest)
    else:
        tmp.unlink(missing_ok=True)
    return rc

ok = skip = fail = 0
target.mkdir(parents=True, exist_ok=True)

for doc in documents:
    filename = doc["filename"]
    url = doc["url"]
    expected_sha = doc.get("sha256", "").lower()
    expected_pages = doc.get("pageCount")
    category = doc.get("category", "uncategorized")
    dest = target / filename

    print(f"-> {filename} [{category}]")
    print(f"   {url}")

    if dry_run:
        print(f"   pages={expected_pages} sha256={expected_sha}")
        skip += 1
        continue

    if dest.exists() and not force:
        actual_sha = sha256(dest)
        if expected_sha and actual_sha == expected_sha:
            pages = page_count(dest)
            if expected_pages is None or pages is None or pages == expected_pages:
                print(f"   OK existing sha256={actual_sha} pages={pages if pages is not None else 'unchecked'}")
                skip += 1
                continue
        print(f"   existing file metadata mismatch; re-downloading")

    rc = download(url, dest)
    if rc != 0:
        print(f"   FAIL curl rc={rc}")
        fail += 1
        continue

    actual_sha = sha256(dest)
    if expected_sha and actual_sha != expected_sha:
        print(f"   FAIL sha256 mismatch expected={expected_sha} actual={actual_sha}")
        dest.unlink(missing_ok=True)
        fail += 1
        continue

    pages = page_count(dest)
    if expected_pages is not None and pages is not None and pages != expected_pages:
        print(f"   FAIL page count mismatch expected={expected_pages} actual={pages}")
        dest.unlink(missing_ok=True)
        fail += 1
        continue

    print(f"   OK sha256={actual_sha} pages={pages if pages is not None else 'unchecked'}")
    ok += 1

print("")
print("=================================================")
print(f"Downloaded: {ok}   Skipped: {skip}   Failed: {fail}")
print("=================================================")
sys.exit(1 if fail else 0)
PY
