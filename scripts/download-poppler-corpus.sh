#!/usr/bin/env bash
# Mirror Poppler's public regression test corpus.
#
# Poppler keeps its test data in a separate public GitLab repository instead of
# the main source tree. We keep the checkout under test-pdfs/ so it stays
# ignored by git but can be scanned by the differential rendering harness.
#
# Output:
#   test-pdfs/poppler/...
#   test-pdfs/poppler/.excise-manifest.tsv  (sha256<TAB>relative-path<TAB>size)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

REPO_URL="${POPPLER_TEST_REPO:-https://gitlab.freedesktop.org/poppler/test.git}"
REF="${POPPLER_TEST_REF:-master}"
TARGET="$ROOT/test-pdfs/poppler"

usage() {
    cat <<'EOF'
Download or refresh Poppler's public test corpus.

Usage:
  scripts/download-poppler-corpus.sh [options]

Options:
  --ref <ref>       Git ref to fetch. Defaults to POPPLER_TEST_REF or master.
  --target <dir>    Output directory. Defaults to test-pdfs/poppler.
  -h, --help        Show this help.
EOF
}

require_value() {
    local option="$1"
    local value="${2:-}"
    if [[ -z "$value" ]]; then
        echo "Missing value for $option" >&2
        usage >&2
        exit 2
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --ref) require_value "$1" "${2:-}"; REF="$2"; shift 2 ;;
        --ref=*) REF="${1#*=}"; shift ;;
        --target) require_value "$1" "${2:-}"; TARGET="$2"; shift 2 ;;
        --target=*) TARGET="${1#*=}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [[ "$TARGET" != /* ]]; then
    TARGET="$ROOT/$TARGET"
fi

if ! command -v git >/dev/null 2>&1; then
    echo "git is required to download the Poppler corpus" >&2
    exit 2
fi

mkdir -p "$(dirname "$TARGET")"

echo "================================================="
echo "Poppler Corpus Downloader"
echo "================================================="
echo "Repo:   $REPO_URL"
echo "Ref:    $REF"
echo "Target: $TARGET"
echo ""

if [[ -d "$TARGET/.git" ]]; then
    local_changes="$(git -C "$TARGET" status --porcelain | grep -v -E '^\?\? \.excise-manifest\.tsv$' || true)"
    if [[ -n "$local_changes" ]]; then
        echo "Existing Poppler corpus checkout has local changes:" >&2
        printf '%s\n' "$local_changes" >&2
        echo "Refusing to overwrite it. Clean the checkout or use a different --target." >&2
        exit 1
    fi

    git -C "$TARGET" fetch --depth 1 origin "$REF"
    git -C "$TARGET" switch --detach FETCH_HEAD
elif [[ -e "$TARGET" && -n "$(find "$TARGET" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]; then
    echo "Target exists and is not an empty git checkout: $TARGET" >&2
    echo "Use a different --target or move the existing directory aside." >&2
    exit 1
else
    git clone --depth 1 --branch "$REF" "$REPO_URL" "$TARGET"
fi

MANIFEST="$TARGET/.excise-manifest.tsv"
python3 - "$TARGET" "$MANIFEST" <<'PY'
import hashlib
import os
import sys
from pathlib import Path

root = Path(sys.argv[1])
manifest = Path(sys.argv[2])

def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()

pdfs = []
with manifest.open("w", encoding="utf-8") as out:
    for pdf in sorted(root.rglob("*.pdf"), key=lambda p: p.relative_to(root).as_posix()):
        if ".git" in pdf.parts:
            continue
        rel = pdf.relative_to(root).as_posix()
        size = pdf.stat().st_size
        out.write(f"{sha256(pdf)}\t{rel}\t{size}\n")
        pdfs.append(rel)

print(f"PDFs:     {len(pdfs)}")
print(f"Manifest: {manifest}")
PY

echo ""
echo "Run this corpus with:"
echo "  scripts/run-exploratory-corpus.sh --corpus test-pdfs/poppler --page-mode all"
