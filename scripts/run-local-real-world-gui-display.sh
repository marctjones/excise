#!/usr/bin/env bash
# Run the optional local-real-world book GUI display suite in bounded chunks.
# This avoids the per-test xUnit timeout that a single 700+ page GUI sweep hits.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

CONFIG="${CONFIG:-Debug}"
CHUNK_SIZE="${EXCISE_LOCAL_REAL_WORLD_GUI_CHUNK_SIZE:-150}"
MANIFEST="${EXCISE_LOCAL_REAL_WORLD_MANIFEST:-test-pdfs/manifests/local-real-world-books.json}"
NO_BUILD="${EXCISE_LOCAL_REAL_WORLD_GUI_NO_BUILD:-1}"
GENERATE_HOTSPOTS="${EXCISE_LOCAL_REAL_WORLD_GUI_HOTSPOTS:-1}"
HOTSPOT_OUTPUT="${EXCISE_LOCAL_REAL_WORLD_GUI_HOTSPOT_OUTPUT:-logs/render-quality/local-real-world-gui-codepath-hotspots.json}"
REPORT_DIR="Excise.App.Tests/bin/$CONFIG/net10.0/UI/test-output"

if ! command -v jq >/dev/null 2>&1; then
    echo "jq is required to read $MANIFEST" >&2
    exit 2
fi

if [ ! -f "$MANIFEST" ]; then
    echo "Missing manifest: $MANIFEST" >&2
    echo "Run scripts/setup-local-real-world-corpus.sh first." >&2
    exit 2
fi

TOTAL="$(jq '[.documents[].pageCount] | add' "$MANIFEST")"
if [ -z "$TOTAL" ] || [ "$TOTAL" = "null" ] || [ "$TOTAL" -le 0 ]; then
    echo "Could not determine page total from $MANIFEST" >&2
    exit 2
fi

if [ "$CHUNK_SIZE" -le 0 ]; then
    echo "EXCISE_LOCAL_REAL_WORLD_GUI_CHUNK_SIZE must be positive" >&2
    exit 2
fi

echo "Local real-world GUI display sweep"
echo "  config     : $CONFIG"
echo "  total pages: $TOTAL"
echo "  chunk size : $CHUNK_SIZE"

reports=()
for ((offset = 0; offset < TOTAL; offset += CHUNK_SIZE)); do
    remaining=$((TOTAL - offset))
    limit=$CHUNK_SIZE
    if [ "$remaining" -lt "$limit" ]; then
        limit=$remaining
    fi

    echo
    echo "Chunk offset=$offset limit=$limit"
    args=(test Excise.App.Tests -c "$CONFIG")
    if [ "$NO_BUILD" = "1" ]; then
        args+=(--no-build)
    fi
    args+=(--filter "FullyQualifiedName~PdfViewerHeadlessRenderTests.PdfViewer_RenderingQualitySuite_DisplayBitmapsMatchRenderer")
    args+=(--logger "console;verbosity=normal")

    EXCISE_GUI_DISPLAY_CONTRACT_GROUPS=local-real-world \
    EXCISE_GUI_DISPLAY_FULL_CONTRACTS=1 \
    EXCISE_GUI_DISPLAY_PAGE_OFFSET="$offset" \
    EXCISE_GUI_DISPLAY_PAGE_LIMIT="$limit" \
        dotnet "${args[@]}"

    reports+=("$REPORT_DIR/gui-display-suite-local-real-world-full-pages-offset-$offset-limit-$limit.json")
done

if [ "$GENERATE_HOTSPOTS" = "1" ]; then
    echo
    echo "Aggregating GUI display code-path hotspots"
    dotnet run --project tools/Excise.RenderTools/Excise.RenderTools.csproj -c "$CONFIG" -- \
        gui-display-hotspots "${reports[@]}" --output "$HOTSPOT_OUTPUT"
fi
