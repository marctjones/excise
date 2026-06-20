#!/usr/bin/env bash
# Run the exploratory differential test against a PDF corpus,
# chunked across separate dotnet test processes so SkiaSharp's native
# memory doesn't accumulate to OOM.
#
# Why chunked: 684 PDFs × ~60 MB peak per iteration = 40+ GB of memory
# churn. SkiaSharp uses native allocations that .NET's GC reclaims
# lazily; in a tight loop they pile up faster than finalizers run.
# A single test process previously consumed 26.7 GB RSS before the
# kernel OOM-killed it (and Claude's session along with it) on
# 2026-05-01. Process exit is the only reliable native-memory release.
#
# Each chunk processes ~50 PDFs, peaks at ~600 MB, and exits. The
# slice JSONs (exploratory-chunk-NNN-of-MMM.json) are merged into
# a single exploratory-report.json at the end.
#
# Usage:
#   scripts/run-exploratory-corpus.sh                                      # pdf.js page 1, 14 chunks
#   scripts/run-exploratory-corpus.sh --page-mode sample                    # pages 1,2,5,20
#   scripts/run-exploratory-corpus.sh --page-mode all                       # every page
#   scripts/run-exploratory-corpus.sh --corpus test-pdfs/poppler --page-mode all
#   scripts/run-exploratory-corpus.sh --corpus test-pdfs --report-name exploratory-report-all-corpora-all.json --page-mode all
#   scripts/run-exploratory-corpus.sh --extra-oracles all                  # add Ghostscript/PDFBox/PDFium where available
#   scripts/run-exploratory-corpus.sh --chunks 14 --tiny                    # 10-PDF smoke run
#   scripts/run-exploratory-corpus.sh --log-dir logs/run                    # keep chunk logs
#
# Output: Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report-<mode>.json

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

CHUNKS=14
CONFIG="Debug"
TINY=0
PER_CHUNK_PARALLEL="0"        # 0 = pdfe auto-picks (ProcessorCount/2)
PDF_TIMEOUT_MS="15000"        # mutool per-page timeout
PROCESS_TIMEOUT_SECONDS="600" # whole pdfe corpus-scan process timeout
CHUNK_PARALLEL="4"            # how many chunks to run concurrently
PAGE_MODE="first"             # first | sample | all
EXTRA_ORACLES="ghostscript"   # none | ghostscript | pdfbox | pdfium | all
CHUNK_LOG_DIR="/tmp"
CORPUS=""
REPORT_NAME=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --chunks)            CHUNKS="$2"; shift 2 ;;
        --release)           CONFIG="Release"; shift ;;
        --tiny)              TINY=1; shift ;;
        --per-chunk-parallel) PER_CHUNK_PARALLEL="$2"; shift 2 ;;
        --pdf-timeout-ms)    PDF_TIMEOUT_MS="$2"; shift 2 ;;
        --process-timeout-seconds) PROCESS_TIMEOUT_SECONDS="$2"; shift 2 ;;
        --chunk-parallel)    CHUNK_PARALLEL="$2"; shift 2 ;;
        --page-mode)         PAGE_MODE="$2"; shift 2 ;;
        --extra-oracles)     EXTRA_ORACLES="$2"; shift 2 ;;
        --extra-oracles=*)   EXTRA_ORACLES="${1#*=}"; shift ;;
        --log-dir)           CHUNK_LOG_DIR="$2"; shift 2 ;;
        --corpus)            CORPUS="$2"; shift 2 ;;
        --corpus=*)          CORPUS="${1#*=}"; shift ;;
        --report-name)       REPORT_NAME="$2"; shift 2 ;;
        --report-name=*)     REPORT_NAME="${1#*=}"; shift ;;
        --help|-h)
            sed -n '2,24p' "$0"; exit 0 ;;
        *)
            echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

DEFAULT_CORPUS="$ROOT/test-pdfs/pdfjs"
if [[ -z "$CORPUS" ]]; then
    CORPUS="$DEFAULT_CORPUS"
elif [[ "$CORPUS" != /* ]]; then
    CORPUS="$ROOT/$CORPUS"
fi
CORPUS="${CORPUS%/}"
if [[ ! -d "$CORPUS" ]]; then
    echo "✗ corpus not found at $CORPUS" >&2
    exit 1
fi

CORPUS_LABEL="$(python3 - "$ROOT" "$CORPUS" <<'PY'
import os, sys
root = os.path.realpath(sys.argv[1])
corpus = os.path.realpath(sys.argv[2])
try:
    rel = os.path.relpath(corpus, root)
    if rel == ".":
        print(".")
    elif not rel.startswith(".."):
        print(rel.replace(os.sep, "/"))
    else:
        print(corpus)
except ValueError:
    print(corpus)
PY
)"

REPORT_SLUG="$(python3 - "$CORPUS_LABEL" <<'PY'
import re, sys
slug = re.sub(r"[^A-Za-z0-9._-]+", "-", sys.argv[1]).strip("-")
print(slug or "corpus")
PY
)"

if [[ -z "$REPORT_NAME" ]]; then
    if [[ "$CORPUS" == "$DEFAULT_CORPUS" ]]; then
        REPORT_NAME="exploratory-report-${PAGE_MODE}.json"
    else
        REPORT_NAME="exploratory-report-${REPORT_SLUG}-${PAGE_MODE}.json"
    fi
fi

# When --tiny, run against a 10-PDF subset in an out-of-tree temp directory so
# the real corpus is never swapped, moved, or modified.
TINY_DIR=""
if [[ "$TINY" == "1" ]]; then
    TINY_DIR="$(mktemp -d /tmp/pdfe-tiny-pdfjs.XXXXXX)"
    trap 'rm -rf "$TINY_DIR"' EXIT
    python3 - "$CORPUS" "$TINY_DIR" <<'PY'
import os, shutil, sys
from pathlib import Path
corpus = Path(sys.argv[1])
tiny = Path(sys.argv[2])
pdfs = []
for pdf in corpus.rglob("*.pdf"):
    if ".git" in pdf.parts:
        continue
    rel = pdf.relative_to(corpus)
    pdfs.append((rel.as_posix(), pdf))
for rel, pdf in sorted(pdfs, key=lambda item: item[0])[:10]:
    dest = tiny / rel
    dest.parent.mkdir(parents=True, exist_ok=True)
    try:
        os.symlink(pdf, dest)
    except OSError:
        shutil.copy2(pdf, dest)
PY
    tiny_count="$(find "$TINY_DIR" -name '*.pdf' | wc -l | tr -d ' ')"
    CORPUS="$TINY_DIR"
    CORPUS_LABEL="${CORPUS_LABEL} (tiny)"
    echo "▶ tiny mode: corpus symlinked to $TINY_DIR ($tiny_count PDFs)"
fi

echo "▶ Building Pdfe.Cli ($CONFIG)"
# Build once. Each chunk then runs the published binary directly,
# avoiding the ~3-min `dotnet test` VSTest startup tax per invocation.
# Net effect: 14 chunks now take ~2 min total instead of 65+.
dotnet build -c "$CONFIG" Pdfe.Cli/Pdfe.Cli.csproj >/dev/null
PDFE_BIN="$ROOT/Pdfe.Cli/bin/$CONFIG/net10.0/pdfe"
if [[ ! -x "$PDFE_BIN" ]]; then
    echo "✗ pdfe binary not found at $PDFE_BIN" >&2
    exit 1
fi

BIN_DIR="$ROOT/Pdfe.Rendering.Tests/bin/$CONFIG/net10.0"
REPORT_STEM="${REPORT_NAME%.json}"
SLICE_DIR="$BIN_DIR/exploratory-slices-$REPORT_STEM-$$"
mkdir -p "$BIN_DIR"
mkdir -p "$SLICE_DIR"
mkdir -p "$CHUNK_LOG_DIR"
rm -f "$BIN_DIR/$REPORT_NAME"
if [[ "$PAGE_MODE" == "first" && "$CORPUS_LABEL" == "test-pdfs/pdfjs" ]]; then
    rm -f "$BIN_DIR/exploratory-report.json"
fi

echo "▶ Running $CHUNKS chunks ($CHUNK_PARALLEL chunks concurrent, each $PER_CHUNK_PARALLEL-way internally parallel, page-mode=$PAGE_MODE, extra-oracles=$EXTRA_ORACLES, process-timeout=${PROCESS_TIMEOUT_SECONDS}s)"
echo "  corpus: $CORPUS_LABEL"
echo "  chunk logs: $CHUNK_LOG_DIR/exploratory-chunk-N.log"
echo "  slice dir: $SLICE_DIR"
chunk_failures=0
chunk_start=$(date +%s)

# Single-chunk runner — used by xargs -P below.
run_one_chunk() {
    local i="$1"
    local slice_path
    slice_path=$(printf '%s/exploratory-chunk-%03d-of-%03d.json' "$SLICE_DIR" "$i" "$CHUNKS")
    local runner=()
    if command -v timeout >/dev/null 2>&1; then
        runner=(timeout --kill-after=10s "$PROCESS_TIMEOUT_SECONDS")
    elif command -v gtimeout >/dev/null 2>&1; then
        runner=(gtimeout --kill-after=10s "$PROCESS_TIMEOUT_SECONDS")
    fi

    if (( ${#runner[@]} > 0 )); then
        "${runner[@]}" "$PDFE_BIN" corpus-scan "$CORPUS" \
            --output "$slice_path" \
            --chunk "$i" \
            --total "$CHUNKS" \
            --parallel "$PER_CHUNK_PARALLEL" \
            --pdf-timeout-ms "$PDF_TIMEOUT_MS" \
            --page-mode "$PAGE_MODE" \
            --extra-oracles "$EXTRA_ORACLES" \
            > "$CHUNK_LOG_DIR/exploratory-chunk-$i.log" 2>&1
    else
        "$PDFE_BIN" corpus-scan "$CORPUS" \
            --output "$slice_path" \
            --chunk "$i" \
            --total "$CHUNKS" \
            --parallel "$PER_CHUNK_PARALLEL" \
            --pdf-timeout-ms "$PDF_TIMEOUT_MS" \
            --page-mode "$PAGE_MODE" \
            --extra-oracles "$EXTRA_ORACLES" \
            > "$CHUNK_LOG_DIR/exploratory-chunk-$i.log" 2>&1
    fi
    local rc=$?
    if [[ "$rc" == "0" && -f "$slice_path" ]]; then
        local stats
        stats=$(python3 -c "
import json
d=json.load(open('$slice_path'))
print(f'{d[\"total\"]} page results, peak {d[\"peakRssBytes\"]//1024//1024} MB')
" 2>/dev/null || echo "ok")
        printf '  ✓ chunk %d/%d  %s\n' "$((i + 1))" "$CHUNKS" "$stats"
        return 0
    else
        printf '  ✗ chunk %d/%d failed (rc=%d) — see %s/exploratory-chunk-%d.log\n' "$((i + 1))" "$CHUNKS" "$rc" "$CHUNK_LOG_DIR" "$i"
        return "$rc"
    fi
}
export -f run_one_chunk
export PDFE_BIN CORPUS CORPUS_LABEL BIN_DIR SLICE_DIR CHUNKS PER_CHUNK_PARALLEL PDF_TIMEOUT_MS PROCESS_TIMEOUT_SECONDS PAGE_MODE EXTRA_ORACLES CHUNK_LOG_DIR

recover_one_chunk_isolated() {
    local i="$1"
    local slice_path
    slice_path=$(printf '%s/exploratory-chunk-%03d-of-%03d.json' "$SLICE_DIR" "$i" "$CHUNKS")
    local recovery_dir
    recovery_dir=$(printf '%s/exploratory-recovery-chunk-%03d-of-%03d' "$SLICE_DIR" "$i" "$CHUNKS")
    mkdir -p "$recovery_dir"
    rm -f "$recovery_dir"/single-*.json "$recovery_dir"/single-*.log "$recovery_dir"/files.txt

    python3 - "$CORPUS" "$i" "$CHUNKS" "$recovery_dir/files.txt" <<'PY'
import os, sys
from pathlib import Path
corpus, chunk, total, out_path = Path(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]
pdfs = []
for path in corpus.rglob("*.pdf"):
    if ".git" in path.parts:
        continue
    rel = path.relative_to(corpus).as_posix()
    pdfs.append((rel, path))
with open(out_path, "w", encoding="utf-8") as f:
    for idx, (rel, path) in enumerate(sorted(pdfs, key=lambda item: item[0])):
        if idx % total == chunk:
            f.write(f"{rel}\t{path}\n")
PY

    local n=0
    while IFS=$'\t' read -r rel pdf; do
        local item_dir single_json single_log dest
        item_dir=$(printf '%s/pdf-%03d' "$recovery_dir" "$n")
        single_json=$(printf '%s/single-%03d.json' "$recovery_dir" "$n")
        single_log=$(printf '%s/single-%03d.log' "$recovery_dir" "$n")
        dest="$item_dir/$rel"
        mkdir -p "$(dirname "$dest")"
        ln -sf "$pdf" "$dest"

        local runner=()
        if command -v timeout >/dev/null 2>&1; then
            runner=(timeout --kill-after=10s "$PROCESS_TIMEOUT_SECONDS")
        elif command -v gtimeout >/dev/null 2>&1; then
            runner=(gtimeout --kill-after=10s "$PROCESS_TIMEOUT_SECONDS")
        fi

        local command=("$PDFE_BIN" corpus-scan "$item_dir" \
            --output "$single_json" \
            --chunk 0 \
            --total 1 \
            --parallel 1 \
            --pdf-timeout-ms "$PDF_TIMEOUT_MS" \
            --page-mode "$PAGE_MODE" \
            --extra-oracles "$EXTRA_ORACLES")

        local rc=0
        if (( ${#runner[@]} > 0 )); then
            if "${runner[@]}" "${command[@]}" > "$single_log" 2>&1; then
                rc=0
            else
                rc=$?
            fi
        elif "${command[@]}" > "$single_log" 2>&1; then
            rc=0
        else
            rc=$?
        fi
        if [[ "$rc" != "0" || ! -f "$single_json" ]]; then
            python3 - "$single_json" "$rel" "$rc" "$PAGE_MODE" <<'PY'
import json, sys, datetime
out, rel, rc, page_mode = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4]
status = "TIMEOUT" if rc == 124 else "SCANNER_CRASH"
error_type = "ProcessTimeout" if rc == 124 else "ProcessExit"
entry = {
    "path": rel,
    "pageNumber": 0,
    "status": status,
    "errorPhase": "scan",
    "errorType": error_type,
    "errorMessage": f"pdfe corpus-scan exited {rc} before writing a single-PDF report",
}
report = {
    "generatedUtc": datetime.datetime.utcnow().isoformat() + "Z",
    "corpus": rel,
    "chunkIndex": 0,
    "chunkTotal": 1,
    "pageMode": page_mode,
    "counts": {status: 1},
    "total": 1,
    "pdfs": 1,
    "peakRssBytes": 0,
    "entries": [entry],
}
with open(out, "w", encoding="utf-8") as f:
    json.dump(report, f, indent=2)
PY
        fi
        n=$((n + 1))
    done < "$recovery_dir/files.txt"

    python3 - "$recovery_dir" "$slice_path" "$i" "$CHUNKS" "$PAGE_MODE" "$CORPUS_LABEL" <<'PY'
import glob, json, os, sys, datetime
recovery_dir, out_path, chunk, total, page_mode, corpus_label = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), sys.argv[5], sys.argv[6]
entries = []
counts = {}
pdfs = 0
peak = 0
generated = []
for path in sorted(glob.glob(os.path.join(recovery_dir, "single-*.json"))):
    with open(path, encoding="utf-8") as f:
        d = json.load(f)
    entries.extend(d.get("entries", []))
    for k, v in d.get("counts", {}).items():
        counts[k] = counts.get(k, 0) + v
    pdfs += d.get("pdfs", 0)
    peak = max(peak, d.get("peakRssBytes", 0))
    if d.get("generatedUtc"):
        generated.append(d["generatedUtc"])
report = {
    "generatedUtc": max(generated) if generated else datetime.datetime.utcnow().isoformat() + "Z",
    "corpus": corpus_label,
    "chunkIndex": chunk,
    "chunkTotal": total,
    "pageMode": page_mode,
    "counts": counts,
    "total": len(entries),
    "pdfs": pdfs,
    "peakRssBytes": peak,
    "entries": sorted(entries, key=lambda e: e.get("path", e.get("Path", ""))),
}
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(report, f, indent=2)
print(f"  isolated recovery wrote {out_path} ({len(entries)} page results)")
PY
}

# xargs -P runs CHUNK_PARALLEL chunks concurrently. -I {} substitutes
# the chunk index. The bash -c wrapper is needed because run_one_chunk
# is a bash function (which we exported) — xargs can't call it directly.
seq 0 $((CHUNKS - 1)) | xargs -n1 -P"$CHUNK_PARALLEL" -I{} bash -c 'run_one_chunk "$@"' _ {} || true

# Native image codecs and reference-renderer subprocesses can occasionally
# destabilize a high-parallelism chunk before it writes JSON. Retry only the
# missing slices serially so release scans still produce a complete baseline
# without hiding a deterministic per-PDF failure inside the JSON report.
missing_chunks=()
for ((i=0; i<CHUNKS; i++)); do
    sp=$(printf '%s/exploratory-chunk-%03d-of-%03d.json' "$SLICE_DIR" "$i" "$CHUNKS")
    [[ -f "$sp" ]] || missing_chunks+=("$i")
done
if (( ${#missing_chunks[@]} > 0 )); then
    echo "  retrying ${#missing_chunks[@]} missing chunk(s) serially with per-chunk parallelism=1"
    original_per_chunk_parallel="$PER_CHUNK_PARALLEL"
    PER_CHUNK_PARALLEL=1
    for i in "${missing_chunks[@]}"; do
        recovered=0
        for attempt in 1 2 3; do
            if (( attempt > 1 )); then
                printf '  retrying chunk %d/%d again (serial attempt %d/3)\n' "$((i + 1))" "$CHUNKS" "$attempt"
            fi
            if run_one_chunk "$i"; then
                recovered=1
                break
            else
                rc=$?
                if [[ "$rc" == "124" || "$rc" == "137" ]]; then
                    printf '  chunk %d/%d hit process timeout during serial recovery; switching to isolated per-PDF recovery\n' "$((i + 1))" "$CHUNKS"
                    break
                fi
            fi
        done
        if (( recovered == 0 )); then
            printf '  chunk %d/%d still missing after serial recovery attempts\n' "$((i + 1))" "$CHUNKS"
            recover_one_chunk_isolated "$i"
        fi
    done
    PER_CHUNK_PARALLEL="$original_per_chunk_parallel"
fi

# Count chunks where the slice JSON wasn't produced after recovery — that's the
# real failure signal (a chunk could log "✓" then segfault later).
chunk_failures=0
for ((i=0; i<CHUNKS; i++)); do
    sp=$(printf '%s/exploratory-chunk-%03d-of-%03d.json' "$SLICE_DIR" "$i" "$CHUNKS")
    [[ -f "$sp" ]] || chunk_failures=$((chunk_failures + 1))
done

chunk_elapsed=$(( $(date +%s) - chunk_start ))
echo "  total chunk runtime: ${chunk_elapsed}s"

echo
echo "▶ Merging $CHUNKS chunk reports → $REPORT_NAME"
python3 - "$BIN_DIR" "$SLICE_DIR" "$CHUNKS" "$PAGE_MODE" "$REPORT_NAME" "$CORPUS_LABEL" "$EXTRA_ORACLES" <<'PY'
import json, os, sys, glob
bin_dir, slice_dir, expected, page_mode, report_name, corpus_label, extra_oracles = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4], sys.argv[5], sys.argv[6], sys.argv[7]
slices = sorted(glob.glob(os.path.join(slice_dir, "exploratory-chunk-*.json")))
print(f"  found {len(slices)} slice file(s) (expected {expected})")

merged_entries = []
counts = {}
peak = 0
pdfs = 0
generated_utcs = []
for path in slices:
    with open(path) as f:
        d = json.load(f)
    merged_entries.extend(d.get("entries", []))
    for k, v in d.get("counts", {}).items():
        counts[k] = counts.get(k, 0) + v
    peak = max(peak, d.get("peakRssBytes", 0))
    pdfs += d.get("pdfs", 0)
    if d.get("generatedUtc"):
        generated_utcs.append(d["generatedUtc"])

out = {
    "generatedUtc": max(generated_utcs) if generated_utcs else None,
    "corpus": corpus_label,
    "pageMode": page_mode,
    "extraOracles": extra_oracles,
    "chunksMerged": len(slices),
    "expectedChunks": expected,
    "counts": counts,
    "total": len(merged_entries),
    "pdfs": pdfs,
    "perChunkPeakRssBytes": peak,
    # JSON keys come through as PascalCase from the .NET serializer
    # (Path, Status, …); use case-insensitive lookup defensively.
    "entries": sorted(merged_entries, key=lambda e: e.get("path", e.get("Path", ""))),
}
out_path = os.path.join(bin_dir, report_name)
with open(out_path, "w") as f:
    json.dump(out, f, indent=2)

print(f"  wrote {out_path}")
print(f"  total page results: {out['total']}")
print(f"  pdfs scanned: {out['pdfs']}")
for k in sorted(counts, key=counts.get, reverse=True):
    print(f"    {counts[k]:4d}  {k}")

def get(e, key, default=None):
    return e.get(key, e.get(key[:1].upper() + key[1:], default))

def elapsed(e):
    v = get(e, "elapsedMs")
    try:
        return int(v or 0)
    except Exception:
        return 0

slow = [e for e in merged_entries if elapsed(e) > 0]
slow.sort(key=elapsed, reverse=True)
if slow:
    print("  slowest page entries:")
    for e in slow[:10]:
        print(
            "    "
            f"{elapsed(e):7d}ms  "
            f"{get(e, 'status', 'UNKNOWN'):18s} "
            f"{get(e, 'path', '')}#p{get(e, 'pageNumber', 0)} "
            f"render={get(e, 'renderMs', '-') or '-'}ms "
            f"mutool={get(e, 'mutoolMs', '-') or '-'}ms/{get(e, 'mutoolStatus', '-') or '-'} "
            f"cairo={get(e, 'cairoMs', '-') or '-'}ms/{get(e, 'cairoStatus', '-') or '-'} "
            f"gs={get(e, 'ghostscriptMs', '-') or '-'}ms/{get(e, 'ghostscriptStatus', '-') or '-'} "
            f"pdfbox={get(e, 'pdfboxMs', '-') or '-'}ms/{get(e, 'pdfboxStatus', '-') or '-'} "
            f"pdfium={get(e, 'pdfiumMs', '-') or '-'}ms/{get(e, 'pdfiumStatus', '-') or '-'}"
        )

failures = [
    e for e in merged_entries
    if get(e, "status") in {
        "TIMEOUT", "MALFORMED_PDF", "UNSUPPORTED_ENCRYPTED",
        "UNSUPPORTED_COMPRESSION", "DECODE_ERROR", "RESOURCE_LIMIT",
        "INVALID_PAGE_GEOMETRY", "PARSE_ERROR", "RENDER_ERROR",
        "COMPARE_ERROR", "ALL_ORACLES_REFUSED", "EMPTY_DOC", "RENDER_NULL",
        "SCANNER_CRASH"
    }
]
if failures:
    print("  failure diagnostics:")
    for e in failures[:20]:
        phase = get(e, "errorPhase", "-") or "-"
        etype = get(e, "errorType", "-") or "-"
        msg = get(e, "diagnostic") or get(e, "errorMessage") or ""
        if not msg and get(e, "status") == "ALL_ORACLES_REFUSED":
            msg = (
                f"mutool={get(e, 'mutoolStatus', '-') or '-'}"
                f" ({get(e, 'mutoolError', '') or ''}); "
                f"pdftocairo={get(e, 'cairoStatus', '-') or '-'}"
                f" ({get(e, 'cairoError', '') or ''}); "
                f"ghostscript={get(e, 'ghostscriptStatus', '-') or '-'}"
                f" ({get(e, 'ghostscriptError', '') or ''}); "
                f"pdfbox={get(e, 'pdfboxStatus', '-') or '-'}"
                f" ({get(e, 'pdfboxError', '') or ''}); "
                f"pdfium={get(e, 'pdfiumStatus', '-') or '-'}"
                f" ({get(e, 'pdfiumError', '') or ''})"
            )
        if len(msg) > 140:
            msg = msg[:137] + "..."
        print(
            "    "
            f"{get(e, 'status', 'UNKNOWN'):18s} "
            f"phase={phase:10s} "
            f"{get(e, 'path', '')}#p{get(e, 'pageNumber', 0)} "
            f"{etype}: {msg}"
        )

if page_mode == "first" and corpus_label == "test-pdfs/pdfjs":
    compat_path = os.path.join(bin_dir, "exploratory-report.json")
    with open(compat_path, "w") as f:
        json.dump(out, f, indent=2)
    print(f"  wrote compatibility copy {compat_path}")
PY

if (( chunk_failures > 0 )); then
    echo
    echo "⚠ $chunk_failures chunk(s) failed — merged report may be partial"
    exit 1
fi
echo "✓ done"
