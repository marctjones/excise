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
OUTPUT_DIR="${PDFE_BENCHMARK_OUTPUT_DIR:-logs/benchmarks}"
RENDER_TOOLS_PROJECT="tools/Pdfe.RenderTools/Pdfe.RenderTools.csproj"
BENCHMARK_DOTNET_PROJECT="Pdfe.Benchmarks/Pdfe.Benchmarks.csproj"

usage() {
    cat <<'EOF'
Run pdfe performance/hotspot benchmark reports.

Usage:
  scripts/run-benchmarks.sh
      Run the speed/quality benchmark suite with deterministic regression
      gates, then aggregate any available corpus/GUI hotspot reports.

  scripts/run-benchmarks.sh suite <benchmark-suite args...>
      Run Pdfe.RenderTools benchmark-suite directly.

  scripts/run-benchmarks.sh corpus-hotspots <corpus-scan.json>... [--output out.json]
  scripts/run-benchmarks.sh gui-display-hotspots <gui-display.json>... [--output out.json]
      Pass explicit hotspot inputs through to Pdfe.RenderTools.

  scripts/run-benchmarks.sh benchmarkdotnet <BenchmarkDotNet args...>
      Run the isolated Pdfe.Benchmarks project for pdfe-vs-pdfe microbenchmarks.

  scripts/run-benchmarks.sh render-tools <Pdfe.RenderTools args...>
      Run any Pdfe.RenderTools command directly.

Environment:
  CONFIG=Release|Debug
  PDFE_BENCHMARK_OUTPUT_DIR=logs/benchmarks
  PDFE_BENCHMARK_CORPUS_DIR=<optional corpus dir>
  PDFE_BENCHMARK_PAGE_LIMIT=8
  PDFE_BENCHMARK_DPI=96
  PDFE_BENCHMARK_TIMEOUT_MS=20000
  PDFE_BENCHMARK_ORACLES=all

Notes:
  - Copyleft/AGPL renderers remain external subprocesses; no reference
    renderer libraries are added to the shippable dependency graph.
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

    local suite_output="$OUTPUT_DIR/latest-suite"
    local page_limit="${PDFE_BENCHMARK_PAGE_LIMIT:-8}"
    local dpi="${PDFE_BENCHMARK_DPI:-96}"
    local timeout_ms="${PDFE_BENCHMARK_TIMEOUT_MS:-20000}"
    local oracles="${PDFE_BENCHMARK_ORACLES:-all}"
    local suite_args=(
        benchmark-suite
        --output-dir "$suite_output"
        --page-limit "$page_limit"
        --dpi "$dpi"
        --timeout-ms "$timeout_ms"
        --oracles "$oracles"
        --fail-on-regression
    )

    if [ -n "${PDFE_BENCHMARK_CORPUS_DIR:-}" ]; then
        suite_args+=(--corpus "$PDFE_BENCHMARK_CORPUS_DIR")
    fi

    echo "Speed + quality benchmark suite"
    run_render_tools "${suite_args[@]}"

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

    local gui_reports=()
    local gui_report
    while IFS= read -r gui_report; do
        gui_reports+=("$gui_report")
    done < <(
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
        echo
        echo "No hotspot inputs were available. The speed + quality benchmark report was still written." >&2
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
    suite|benchmark-suite)
        shift
        run_render_tools benchmark-suite "$@"
        ;;
    corpus-hotspots|gui-display-hotspots)
        run_render_tools "$@"
        ;;
    benchmarkdotnet|benchmark-dotnet)
        shift
        dotnet run --project "$BENCHMARK_DOTNET_PROJECT" -c "$CONFIG" -- "$@"
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
