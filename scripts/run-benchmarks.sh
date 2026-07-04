#!/usr/bin/env bash
# Run pdfe performance/hotspot benchmarks without linking copyleft renderers.
#
# This wrapper intentionally uses Pdfe.RenderTools and existing JSON reports.
# External reference renderers remain external CLI tools; this script does not
# add reference libraries to the shippable dependency graph.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

CONFIG="${CONFIG:-Release}"
OUTPUT_DIR="${PDFE_BENCHMARK_OUTPUT_DIR:-logs/render-quality}"
RENDER_TOOLS_PROJECT="tools/Pdfe.RenderTools/Pdfe.RenderTools.csproj"

usage() {
    cat <<'EOF'
Run pdfe performance/hotspot benchmark reports.

Usage:
  scripts/run-benchmarks.sh
      Generate aggregate code-path hotspots from the newest available corpus
      scan and GUI display reports.

  scripts/run-benchmarks.sh corpus-hotspots <corpus-scan.json>... [--output out.json]
  scripts/run-benchmarks.sh gui-display-hotspots <gui-display.json>... [--output out.json]
      Pass explicit hotspot inputs through to Pdfe.RenderTools.

  scripts/run-benchmarks.sh render-tools <Pdfe.RenderTools args...>
      Run any Pdfe.RenderTools command directly.

Environment:
  CONFIG=Release|Debug
  PDFE_BENCHMARK_OUTPUT_DIR=logs/render-quality

Notes:
  - This replaces the old orphaned Pdfe.Benchmarks project call.
  - Use scripts/run-render-quality-scan.sh or scripts/run-local-real-world-gui-display.sh
    to produce fresh raw reports, then run this wrapper to aggregate hotspots.
EOF
}

run_render_tools() {
    dotnet run --project "$RENDER_TOOLS_PROJECT" -c "$CONFIG" -- "$@"
}

newest_file() {
    local pattern="$1"
    find . -path "$pattern" -type f -print0 2>/dev/null |
        xargs -0 ls -t 2>/dev/null |
        head -1 |
        sed 's#^\./##'
}

default_benchmarks() {
    mkdir -p "$OUTPUT_DIR"

    local ran=0
    local corpus_report
    corpus_report="$(newest_file "./logs/render-quality/*.raw-corpus-scan.json")"
    if [ -n "$corpus_report" ]; then
        echo "Corpus code-path hotspots from $corpus_report"
        run_render_tools corpus-hotspots "$corpus_report" \
            --output "$OUTPUT_DIR/latest-corpus-codepath-hotspots.json"
        ran=1
    else
        echo "No corpus raw scan report found under logs/render-quality/*.raw-corpus-scan.json" >&2
    fi

    mapfile -t gui_reports < <(
        find PdfEditor.Tests/bin -path "*/UI/test-output/gui-display-suite-*.json" -type f -print 2>/dev/null |
            sort
    )
    if [ "${#gui_reports[@]}" -gt 0 ]; then
        echo
        echo "GUI display code-path hotspots from ${#gui_reports[@]} report(s)"
        run_render_tools gui-display-hotspots "${gui_reports[@]}" \
            --output "$OUTPUT_DIR/latest-gui-codepath-hotspots.json"
        ran=1
    else
        echo "No GUI display reports found under PdfEditor.Tests/bin/*/UI/test-output/" >&2
    fi

    if [ "$ran" = "0" ]; then
        echo "No benchmark inputs were available. Generate reports first, then rerun." >&2
        return 2
    fi
}

if [ "$#" -eq 0 ]; then
    default_benchmarks
    exit $?
fi

case "$1" in
    -h|--help)
        usage
        ;;
    corpus-hotspots|gui-display-hotspots)
        run_render_tools "$@"
        ;;
    render-tools)
        shift
        run_render_tools "$@"
        ;;
    *)
        echo "Unknown benchmark command: $1" >&2
        usage >&2
        exit 2
        ;;
esac
