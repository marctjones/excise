#!/usr/bin/env bash
# Run the exploratory differential test against the full pdf.js corpus,
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
#   scripts/run-exploratory-corpus.sh                 # default: 14 chunks
#   scripts/run-exploratory-corpus.sh --chunks 7      # custom chunk count
#   scripts/run-exploratory-corpus.sh --chunks 14 --tiny  # 10-PDF smoke run
#
# Output: Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report.json

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

CHUNKS=14
CONFIG="Debug"
TINY=0
PER_CHUNK_PARALLEL="0"        # 0 = pdfe auto-picks (ProcessorCount/2)
PDF_TIMEOUT_MS="15000"        # mutool per-page timeout
CHUNK_PARALLEL="4"            # how many chunks to run concurrently

while [[ $# -gt 0 ]]; do
    case "$1" in
        --chunks)            CHUNKS="$2"; shift 2 ;;
        --release)           CONFIG="Release"; shift ;;
        --tiny)              TINY=1; shift ;;
        --per-chunk-parallel) PER_CHUNK_PARALLEL="$2"; shift 2 ;;
        --pdf-timeout-ms)    PDF_TIMEOUT_MS="$2"; shift 2 ;;
        --chunk-parallel)    CHUNK_PARALLEL="$2"; shift 2 ;;
        --help|-h)
            sed -n '2,18p' "$0"; exit 0 ;;
        *)
            echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

CORPUS="$ROOT/test-pdfs/pdfjs"
if [[ ! -d "$CORPUS" ]]; then
    echo "✗ pdf.js corpus not found at $CORPUS" >&2
    echo "  run scripts/download-pdfjs-corpus.sh first" >&2
    exit 1
fi

# When --tiny, run against a 10-PDF subset in an OUT-OF-TREE temp
# directory so we can never accidentally orphan or clobber the real
# corpus. We point the dotnet test at the temp dir via env var.
TINY_DIR=""
if [[ "$TINY" == "1" ]]; then
    TINY_DIR="$(mktemp -d /tmp/pdfe-tiny-pdfjs.XXXXXX)"
    trap 'rm -rf "$TINY_DIR"' EXIT
    ls "$CORPUS"/*.pdf 2>/dev/null | head -10 | while read -r pdf; do
        cp "$pdf" "$TINY_DIR/$(basename "$pdf")"
    done
    # Hijack the corpus path the test uses by overlaying a symlink.
    # The .NET test reads `test-pdfs/pdfjs` relative to repo root; a
    # symlink keeps the real corpus untouched at its real path.
    REAL_CORPUS_BACKUP="$ROOT/test-pdfs/pdfjs"
    SYMLINKED_CORPUS="$ROOT/test-pdfs/.pdfjs-tiny-link-$$"
    ln -s "$TINY_DIR" "$SYMLINKED_CORPUS"
    # Move real corpus out of the way and replace with the symlink, so
    # the test reads the tiny set. Restore in trap.
    mv "$REAL_CORPUS_BACKUP" "$REAL_CORPUS_BACKUP.swapped-$$"
    mv "$SYMLINKED_CORPUS" "$REAL_CORPUS_BACKUP"
    # Atomic restore that survives any crash mid-run.
    trap 'rm -f "$REAL_CORPUS_BACKUP";
          [[ -d "$REAL_CORPUS_BACKUP.swapped-$$" ]] && mv "$REAL_CORPUS_BACKUP.swapped-$$" "$REAL_CORPUS_BACKUP";
          rm -rf "$TINY_DIR"' EXIT
    echo "▶ tiny mode: corpus symlinked to $TINY_DIR ($(ls "$TINY_DIR" | wc -l) PDFs)"
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

# All slice JSONs land in a stable directory beside the test bin so
# the merge step finds them.
BIN_DIR="$ROOT/Pdfe.Rendering.Tests/bin/$CONFIG/net10.0"
mkdir -p "$BIN_DIR"
rm -f "$BIN_DIR"/exploratory-chunk-*.json "$BIN_DIR"/exploratory-report.json

echo "▶ Running $CHUNKS chunks ($CHUNK_PARALLEL chunks concurrent, each $PER_CHUNK_PARALLEL-way internally parallel)"
chunk_failures=0
chunk_start=$(date +%s)

# Single-chunk runner — used by xargs -P below.
run_one_chunk() {
    local i="$1"
    local slice_path
    slice_path=$(printf '%s/exploratory-chunk-%03d-of-%03d.json' "$BIN_DIR" "$i" "$CHUNKS")
    local runner=()
    if command -v timeout >/dev/null 2>&1; then
        runner=(timeout 600)
    elif command -v gtimeout >/dev/null 2>&1; then
        runner=(gtimeout 600)
    fi

    "${runner[@]}" "$PDFE_BIN" corpus-scan "$CORPUS" \
        --output "$slice_path" \
        --chunk "$i" \
        --total "$CHUNKS" \
        --parallel "$PER_CHUNK_PARALLEL" \
        --pdf-timeout-ms "$PDF_TIMEOUT_MS" \
        > "/tmp/exploratory-chunk-$i.log" 2>&1
    local rc=$?
    if [[ "$rc" == "0" && -f "$slice_path" ]]; then
        local stats
        stats=$(python3 -c "
import json
d=json.load(open('$slice_path'))
print(f'{d[\"total\"]} pdfs, peak {d[\"peakRssBytes\"]//1024//1024} MB')
" 2>/dev/null || echo "ok")
        printf '  ✓ chunk %d/%d  %s\n' "$((i + 1))" "$CHUNKS" "$stats"
        return 0
    else
        printf '  ✗ chunk %d/%d failed (rc=%d) — see /tmp/exploratory-chunk-%d.log\n' "$((i + 1))" "$CHUNKS" "$rc" "$i"
        return 1
    fi
}
export -f run_one_chunk
export PDFE_BIN CORPUS BIN_DIR CHUNKS PER_CHUNK_PARALLEL PDF_TIMEOUT_MS

# xargs -P runs CHUNK_PARALLEL chunks concurrently. -I {} substitutes
# the chunk index. The bash -c wrapper is needed because run_one_chunk
# is a bash function (which we exported) — xargs can't call it directly.
seq 0 $((CHUNKS - 1)) | xargs -n1 -P"$CHUNK_PARALLEL" -I{} bash -c 'run_one_chunk "$@"' _ {}

# Count chunks where the slice JSON wasn't produced — that's the
# real failure signal (a chunk could log "✓" then segfault later).
chunk_failures=0
for ((i=0; i<CHUNKS; i++)); do
    sp=$(printf '%s/exploratory-chunk-%03d-of-%03d.json' "$BIN_DIR" "$i" "$CHUNKS")
    [[ -f "$sp" ]] || chunk_failures=$((chunk_failures + 1))
done

chunk_elapsed=$(( $(date +%s) - chunk_start ))
echo "  total chunk runtime: ${chunk_elapsed}s"

echo
echo "▶ Merging $CHUNKS chunk reports → exploratory-report.json"
python3 - "$BIN_DIR" "$CHUNKS" <<'PY'
import json, os, sys, glob
bin_dir, expected = sys.argv[1], int(sys.argv[2])
slices = sorted(glob.glob(os.path.join(bin_dir, "exploratory-chunk-*.json")))
print(f"  found {len(slices)} slice file(s) (expected {expected})")

merged_entries = []
counts = {}
peak = 0
generated_utcs = []
for path in slices:
    with open(path) as f:
        d = json.load(f)
    merged_entries.extend(d.get("entries", []))
    for k, v in d.get("counts", {}).items():
        counts[k] = counts.get(k, 0) + v
    peak = max(peak, d.get("peakRssBytes", 0))
    if d.get("generatedUtc"):
        generated_utcs.append(d["generatedUtc"])

out = {
    "generatedUtc": max(generated_utcs) if generated_utcs else None,
    "corpus": "test-pdfs/pdfjs",
    "chunksMerged": len(slices),
    "expectedChunks": expected,
    "counts": counts,
    "total": len(merged_entries),
    "perChunkPeakRssBytes": peak,
    # JSON keys come through as PascalCase from the .NET serializer
    # (Path, Status, …); use case-insensitive lookup defensively.
    "entries": sorted(merged_entries, key=lambda e: e.get("path", e.get("Path", ""))),
}
out_path = os.path.join(bin_dir, "exploratory-report.json")
with open(out_path, "w") as f:
    json.dump(out, f, indent=2)

print(f"  wrote {out_path}")
print(f"  total: {out['total']}")
for k in sorted(counts, key=counts.get, reverse=True):
    print(f"    {counts[k]:4d}  {k}")
PY

if (( chunk_failures > 0 )); then
    echo
    echo "⚠ $chunk_failures chunk(s) failed — merged report may be partial"
    exit 1
fi
echo "✓ done"
