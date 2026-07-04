#!/usr/bin/env bash
# Populate the optional local real-world book corpus from PDFs that already
# exist on the current machine. The PDFs are intentionally ignored by git.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
MANIFEST="$ROOT/test-pdfs/manifests/local-real-world-books.json"
TARGET=""
DRY_RUN=0

usage() {
    sed -n '2,8p' "$0"
    cat <<'EOF'

Usage:
  scripts/setup-local-real-world-corpus.sh [options]

Options:
  --manifest <path>  Manifest JSON. Defaults to test-pdfs/manifests/local-real-world-books.json.
  --target <dir>     Output directory. Defaults to manifest defaultTarget.
  --dry-run          Print planned copies without copying.
  --help             Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --manifest) MANIFEST="$2"; shift 2 ;;
        --manifest=*) MANIFEST="${1#*=}"; shift ;;
        --target) TARGET="$2"; shift 2 ;;
        --target=*) TARGET="${1#*=}"; shift ;;
        --dry-run) DRY_RUN=1; shift ;;
        --help|-h) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [[ "$MANIFEST" != /* ]]; then
    MANIFEST="$ROOT/$MANIFEST"
fi
if [[ -n "$TARGET" && "$TARGET" != /* ]]; then
    TARGET="$ROOT/$TARGET"
fi

python3 - "$ROOT" "$MANIFEST" "$TARGET" "$DRY_RUN" <<'PY'
import hashlib
import json
import shutil
import sys
from pathlib import Path

root = Path(sys.argv[1])
manifest_path = Path(sys.argv[2])
target_arg = sys.argv[3]
dry_run = sys.argv[4] == "1"

manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
target = Path(target_arg) if target_arg else root / manifest["defaultTarget"]
if not target.is_absolute():
    target = root / target

def expand(path: str) -> Path:
    return Path(path).expanduser()

def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()

copied = 0
missing = []
for doc in manifest["documents"]:
    expected = doc["sha256"]
    source = None
    for candidate_raw in doc.get("localCandidates", []):
        candidate = expand(candidate_raw)
        if not candidate.exists():
            continue
        actual = sha256(candidate)
        if actual != expected:
            print(f"checksum mismatch: {candidate}", file=sys.stderr)
            print(f"  expected {expected}", file=sys.stderr)
            print(f"  actual   {actual}", file=sys.stderr)
            continue
        source = candidate
        break

    if source is None:
        missing.append(doc["filename"])
        print(f"missing: {doc['filename']}")
        continue

    dest = target / doc["filename"]
    print(f"{'would copy' if dry_run else 'copy'}: {source} -> {dest}")
    if not dry_run:
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, dest)
        copied += 1

print(f"target: {target}")
print(f"documents copied: {copied}")
if missing:
    print("documents missing:")
    for name in missing:
        print(f"  {name}")

if missing:
    sys.exit(1 if not dry_run else 0)
PY
