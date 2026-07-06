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
      gates, then aggregate any available corpus/GUI hotspot reports and
      write latest-performance-baseline.{json,md}.

  scripts/run-benchmarks.sh suite <benchmark-suite args...>
      Run Pdfe.RenderTools benchmark-suite directly.

  scripts/run-benchmarks.sh corpus-hotspots <corpus-scan.json>... [--output out.json]
  scripts/run-benchmarks.sh gui-display-hotspots <gui-display.json>... [--output out.json]
  scripts/run-benchmarks.sh gui-display-hotspots <gui-workflow.json>... [--output out.json]
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
  PDFE_BENCHMARK_CLI_RENDER=0

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

json_escape() {
    local s="${1:-}"
    s="${s//\\/\\\\}"
    s="${s//\"/\\\"}"
    s="${s//$'\n'/\\n}"
    s="${s//$'\r'/}"
    printf '%s' "$s"
}

artifact_json() {
    local name="$1"
    local path="$2"
    local issue_refs="$3"
    local exists=false
    [ -f "$path" ] && exists=true
    printf '    {"name":"%s","path":"%s","exists":%s,"issueRefs":"%s"}' \
        "$(json_escape "$name")" \
        "$(json_escape "$path")" \
        "$exists" \
        "$(json_escape "$issue_refs")"
}

write_performance_baseline_summary() {
    local suite_output="$1"
    local corpus_output="$2"
    local gui_output="$3"
    local gui_workflow_output="$4"
    local baseline_json="$OUTPUT_DIR/latest-performance-baseline.json"
    local baseline_md="$OUTPUT_DIR/latest-performance-baseline.md"

    {
        printf '{\n'
        printf '  "schemaVersion": 1,\n'
        printf '  "issues": ["#596", "#597", "#601", "#602"],\n'
        printf '  "generatedUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf '  "policy": "Benchmark and hotspot evidence is grouped by pdfe-owned workload/code-path area; reference renderer timing is evidence only and is not an optimization target.",\n'
        printf '  "artifacts": [\n'
        artifact_json "benchmark-report" "$suite_output/benchmark-report.json" "#596 #597 #602"; printf ',\n'
        artifact_json "benchmark-pages" "$suite_output/benchmark-pages.csv" "#596 #597 #602"; printf ',\n'
        artifact_json "benchmark-hotpaths" "$suite_output/benchmark-hotpaths.json" "#596 #597 #598 #599 #600"; printf ',\n'
        artifact_json "benchmark-markdown" "$suite_output/benchmark-report.md" "#596 #597"; printf ',\n'
        artifact_json "corpus-codepath-hotspots" "$corpus_output" "#597 #598 #599"; printf ',\n'
        artifact_json "gui-codepath-hotspots" "$gui_output" "#597 #601"; printf ',\n'
        artifact_json "gui-workflow-hotspots" "$gui_workflow_output" "#601"; printf '\n'
        printf '  ]\n'
        printf '}\n'
    } > "$baseline_json"

    {
        printf '# pdfe Performance Baseline\n\n'
        printf -- '- Issues: #596, #597, #601, #602\n'
        printf -- '- Policy: benchmark and hotspot evidence is grouped by pdfe-owned workload/code-path area; reference renderer timing is evidence only.\n\n'
        printf '| Artifact | Exists | Path | Issues |\n'
        printf '| --- | --- | --- | --- |\n'
        for row in \
            "benchmark-report|$suite_output/benchmark-report.json|#596 #597 #602" \
            "benchmark-pages|$suite_output/benchmark-pages.csv|#596 #597 #602" \
            "benchmark-hotpaths|$suite_output/benchmark-hotpaths.json|#596 #597 #598 #599 #600" \
            "benchmark-markdown|$suite_output/benchmark-report.md|#596 #597" \
            "corpus-codepath-hotspots|$corpus_output|#597 #598 #599" \
            "gui-codepath-hotspots|$gui_output|#597 #601" \
            "gui-workflow-hotspots|$gui_workflow_output|#601"; do
            IFS='|' read -r name path issues <<< "$row"
            local exists="no"
            [ -f "$path" ] && exists="yes"
            printf '| %s | %s | `%s` | %s |\n' "$name" "$exists" "$path" "$issues"
        done
    } > "$baseline_md"

    echo
    echo "Performance baseline summary: $baseline_md"
}

default_benchmarks() {
    mkdir -p "$OUTPUT_DIR"

    local suite_output="$OUTPUT_DIR/latest-suite"
    local page_limit="${PDFE_BENCHMARK_PAGE_LIMIT:-8}"
    local dpi="${PDFE_BENCHMARK_DPI:-96}"
    local timeout_ms="${PDFE_BENCHMARK_TIMEOUT_MS:-20000}"
    local oracles="${PDFE_BENCHMARK_ORACLES:-all}"
    local cli_render="${PDFE_BENCHMARK_CLI_RENDER:-0}"
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
    if [ "$cli_render" = "1" ] || [ "$cli_render" = "true" ]; then
        suite_args+=(--include-cli-render)
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

    local gui_workflow_reports=()
    while IFS= read -r gui_report; do
        gui_workflow_reports+=("$gui_report")
    done < <(
        find PdfEditor.Tests/bin -path "*/UI/test-output/gui-workflow-suite-*.json" -type f -print 2>/dev/null |
            sort
    )
    if [ "${#gui_workflow_reports[@]}" -gt 0 ]; then
        echo
        echo "GUI workflow code-path hotspots from ${#gui_workflow_reports[@]} report(s)"
        run_render_tools gui-display-hotspots "${gui_workflow_reports[@]}" \
            --output "$OUTPUT_DIR/latest-gui-workflow-hotspots.json"
        ran=1
    else
        echo "No GUI workflow reports found under PdfEditor.Tests/bin/*/UI/test-output/" >&2
    fi

    if [ "$ran" = "0" ]; then
        echo
        echo "No hotspot inputs were available. The speed + quality benchmark report was still written." >&2
    fi

    write_performance_baseline_summary \
        "$suite_output" \
        "$OUTPUT_DIR/latest-corpus-codepath-hotspots.json" \
        "$OUTPUT_DIR/latest-gui-codepath-hotspots.json" \
        "$OUTPUT_DIR/latest-gui-workflow-hotspots.json"
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
