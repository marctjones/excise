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
# Each chunk exits after writing a slice JSON. All-page runs default to
# page-sharded manifests so very large PDFs are split into bounded page
# ranges instead of pinning one chunk on a single huge document. The slice
# JSONs (exploratory-chunk-NNN-of-MMM.json) are merged into one report at
# the end.
#
# Usage:
#   scripts/run-exploratory-corpus.sh                                      # pdf.js page 1, 14 chunks
#   scripts/run-exploratory-corpus.sh --page-mode sample                    # pages 1,2,5,20
#   scripts/run-exploratory-corpus.sh --page-mode all                       # every page
#   scripts/run-exploratory-corpus.sh --corpus test-pdfs/poppler --page-mode all
#   scripts/run-exploratory-corpus.sh --corpus test-pdfs --report-name exploratory-report-all-corpora-all.json --page-mode all
#   scripts/run-exploratory-corpus.sh --extra-oracles all                  # add Ghostscript/PDFBox/PDFium where available
#   scripts/run-exploratory-corpus.sh --password-manifest path/to/passwords.tsv
#   scripts/run-exploratory-corpus.sh --no-page-shards                     # keep legacy PDF-level chunking
#   scripts/run-exploratory-corpus.sh --resume-excise-render-cache           # reuse excise cache from a prior interrupted run
#   scripts/run-exploratory-corpus.sh --chunks 14 --tiny                    # 10-PDF smoke run
#   scripts/run-exploratory-corpus.sh --log-dir logs/run                    # keep chunk logs
#
# Output: Excise.Rendering.Tests/bin/Debug/net10.0/exploratory-report-<mode>.json

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

CHUNKS=14
CONFIG="Debug"
TINY=0
PER_CHUNK_PARALLEL="0"        # 0 = excise auto-picks (ProcessorCount/2)
PDF_TIMEOUT_MS="15000"        # mutool per-page timeout
PROCESS_TIMEOUT_SECONDS="600" # whole Excise.RenderTools corpus-scan process timeout
                              # (auto-scaled by page count for --page-mode all, see below)
INCREMENTAL_FLUSH_SECONDS="20" # how often a chunk makes its completed pages durable (#879)
SECONDS_PER_PAGE_BUDGET="3"    # per-page allowance when scaling a chunk's timeout
CHUNK_PARALLEL="4"            # how many chunks to run concurrently
PAGE_MODE="first"             # first | sample | all
EXTRA_ORACLES="ghostscript"   # none | ghostscript | pdfbox | pdfium | all
CHUNK_LOG_DIR=""              # defaults to a RUN-SCOPED dir under SLICE_DIR (#880)
CORPUS=""
REPORT_NAME=""
PASSWORD_MANIFEST=""
PASSWORD_MANIFEST_MODE="auto" # auto | explicit | off
EXPECTATION_MANIFEST=""
PAGE_SHARDS="auto"            # auto | off
LARGE_PDF_PAGE_THRESHOLD="100"  # shard any PDF larger than this into page ranges
PAGE_RANGE_SIZE="50"            # ...of this many pages (#879)
MAX_PAGES_PER_PDF="500"         # sample-cap for pathologically large documents (see below)
# WHY A CAP, AND WHY 500.
# One fixture dominates the entire corpus: Isartor
# "6.1 File structure/6.1.12 Implementation Limits/isartor-6-1-12-t01-fail-a.pdf"
# has 10,000 pages — 69% of every page in all four corpora, and 96 minutes of
# every full run. The next largest Isartor file has 20.
#
# Its PURPOSE is to violate a PDF/A-1b implementation limit so a VALIDATOR flags
# it. Rendering 10,000 near-identical pages tests nothing about rendering, and
# the robustness property it does test — that excise does not hang, crash or
# exhaust memory on it — is already covered by #648's conformance sweep, which
# opens every corpus file.
#
# 500 is chosen so it bites ONLY the pathological case: the largest genuine
# document in any corpus is freeculture.pdf at 352 pages, which stays whole.
# Set to 0 to disable.
# Both were 250/100. At those values a 240-page PDF was one indivisible work
# item, and a chunk could be handed 800 pages of a 10,000-page fixture — which
# cannot finish inside any fixed per-chunk timeout. Smaller ranges also mean a
# lost or retried unit costs less.
EXCISE_RENDER_CACHE="auto"      # auto | off
EXCISE_RENDER_CACHE_DIR=""
RESUME_EXCISE_RENDER_CACHE="0"

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
        --password-manifest) PASSWORD_MANIFEST="$2"; PASSWORD_MANIFEST_MODE="explicit"; shift 2 ;;
        --password-manifest=*) PASSWORD_MANIFEST="${1#*=}"; PASSWORD_MANIFEST_MODE="explicit"; shift ;;
        --no-password-manifest) PASSWORD_MANIFEST=""; PASSWORD_MANIFEST_MODE="off"; shift ;;
        --expectation-manifest) EXPECTATION_MANIFEST="$2"; shift 2 ;;
        --expectation-manifest=*) EXPECTATION_MANIFEST="${1#*=}"; shift ;;
        --page-shards)       PAGE_SHARDS="auto"; shift ;;
        --no-page-shards)    PAGE_SHARDS="off"; shift ;;
        --large-pdf-page-threshold) LARGE_PDF_PAGE_THRESHOLD="$2"; shift 2 ;;
        --large-pdf-page-threshold=*) LARGE_PDF_PAGE_THRESHOLD="${1#*=}"; shift ;;
        --max-pages-per-pdf) MAX_PAGES_PER_PDF="$2"; shift 2 ;;
        --max-pages-per-pdf=*) MAX_PAGES_PER_PDF="${1#*=}"; shift ;;
        --page-range-size)   PAGE_RANGE_SIZE="$2"; shift 2 ;;
        --page-range-size=*) PAGE_RANGE_SIZE="${1#*=}"; shift ;;
        --excise-render-cache-dir) EXCISE_RENDER_CACHE_DIR="$2"; shift 2 ;;
        --excise-render-cache-dir=*) EXCISE_RENDER_CACHE_DIR="${1#*=}"; shift ;;
        --no-excise-render-cache) EXCISE_RENDER_CACHE="off"; shift ;;
        --resume-excise-render-cache) RESUME_EXCISE_RENDER_CACHE="1"; shift ;;
        --help|-h)
            sed -n '2,28p' "$0"; exit 0 ;;
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

# "auto" was declared but never read, so a plain run supplied NO passwords and
# classified encrypted fixtures as unsupported — measuring nothing, since we
# know their passwords (#864). Auto now resolves the checked-in manifest when it
# exists; --no-password-manifest still opts out, and --password-manifest still
# wins.
DEFAULT_PASSWORD_MANIFEST="$ROOT/tests/corpus-passwords.tsv"
if [[ "$PASSWORD_MANIFEST_MODE" == "auto" && -f "$DEFAULT_PASSWORD_MANIFEST" ]]; then
    PASSWORD_MANIFEST="$DEFAULT_PASSWORD_MANIFEST"
fi
if [[ "$PASSWORD_MANIFEST_MODE" == "explicit" && "$PASSWORD_MANIFEST" != /* ]]; then
    PASSWORD_MANIFEST="$ROOT/$PASSWORD_MANIFEST"
fi
if [[ -n "$EXPECTATION_MANIFEST" && "$EXPECTATION_MANIFEST" != /* ]]; then
    EXPECTATION_MANIFEST="$ROOT/$EXPECTATION_MANIFEST"
fi

PASSWORD_ARGS=()
if [[ -n "$PASSWORD_MANIFEST" ]]; then
    if [[ ! -f "$PASSWORD_MANIFEST" ]]; then
        echo "✗ password manifest not found at $PASSWORD_MANIFEST" >&2
        exit 1
    fi
    PASSWORD_ARGS=(--password-manifest "$PASSWORD_MANIFEST")
fi
EXPECTATION_ARGS=()
if [[ -n "$EXPECTATION_MANIFEST" ]]; then
    if [[ ! -f "$EXPECTATION_MANIFEST" ]]; then
        echo "✗ expectation manifest not found at $EXPECTATION_MANIFEST" >&2
        exit 1
    fi
    EXPECTATION_ARGS=(--expectation-manifest "$EXPECTATION_MANIFEST")
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
    TINY_DIR="$(mktemp -d /tmp/excise-tiny-pdfjs.XXXXXX)"
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

echo "▶ Building Excise.RenderTools ($CONFIG)"
# Build once. Each chunk then runs the published binary directly,
# avoiding the ~3-min `dotnet test` VSTest startup tax per invocation.
# Net effect: 14 chunks now take ~2 min total instead of 65+.
dotnet build -c "$CONFIG" tools/Excise.RenderTools/Excise.RenderTools.csproj >/dev/null
EXCISE_RENDER_TOOLS_BIN="$ROOT/tools/Excise.RenderTools/bin/$CONFIG/net10.0/Excise.RenderTools"
if [[ ! -x "$EXCISE_RENDER_TOOLS_BIN" ]]; then
    echo "✗ Excise.RenderTools binary not found at $EXCISE_RENDER_TOOLS_BIN" >&2
    exit 1
fi

BIN_DIR="$ROOT/Excise.Rendering.Tests/bin/$CONFIG/net10.0"
REPORT_STEM="${REPORT_NAME%.json}"
SLICE_DIR="$BIN_DIR/exploratory-slices-$REPORT_STEM-$$"
mkdir -p "$BIN_DIR"
mkdir -p "$SLICE_DIR"

# Chunk logs and the excise render cache used to live at fixed /tmp paths
# (/tmp/exploratory-chunk-N.log, /tmp/excise-render-cache), so two scans running
# at once wrote to the same files (#880). Two ways that hurt:
#
#   - a failed chunk's "see .../exploratory-chunk-N.log" pointer could describe
#     a DIFFERENT run's corpus, which is how this was found;
#   - far worse, the render cache is keyed by content hash but was shared
#     across checkouts that may contain DIFFERENT renderer code, so a scan
#     validating a rendering fix could be served bitmaps from a build without
#     it. A differential harness exists precisely to rule that out.
#
# SLICE_DIR already carries the report name and PID, so scoping under it makes
# both per-run automatically. --log-dir still overrides for anyone who wants a
# stable location.
if [[ -z "$CHUNK_LOG_DIR" ]]; then
    CHUNK_LOG_DIR="$SLICE_DIR/logs"
fi
mkdir -p "$CHUNK_LOG_DIR"
rm -f "$BIN_DIR/$REPORT_NAME"
if [[ "$PAGE_MODE" == "first" && "$CORPUS_LABEL" == "test-pdfs/pdfjs" ]]; then
    rm -f "$BIN_DIR/exploratory-report.json"
fi

EXCISE_RENDER_CACHE_ENABLED="0"
EXCISE_CACHE_ARG=()
if [[ "$EXCISE_RENDER_CACHE" != "off" && "$PAGE_MODE" == "all" ]]; then
    if [[ -z "$EXCISE_RENDER_CACHE_DIR" ]]; then
        EXCISE_RENDER_CACHE_DIR="$CHUNK_LOG_DIR/excise-render-cache"
    elif [[ "$EXCISE_RENDER_CACHE_DIR" != /* ]]; then
        EXCISE_RENDER_CACHE_DIR="$ROOT/$EXCISE_RENDER_CACHE_DIR"
    fi

    if [[ "$RESUME_EXCISE_RENDER_CACHE" != "1" ]]; then
        rm -rf "$EXCISE_RENDER_CACHE_DIR"
    fi
    mkdir -p "$EXCISE_RENDER_CACHE_DIR"
    EXCISE_RENDER_CACHE_ENABLED="1"
    EXCISE_CACHE_ARG=(--excise-render-cache-dir "$EXCISE_RENDER_CACHE_DIR")
fi

PAGE_SHARD_DIR=""
PAGE_SHARD_SUMMARY=""
if [[ "$PAGE_MODE" == "all" && "$PAGE_SHARDS" != "off" ]]; then
    PAGE_SHARD_DIR="$SLICE_DIR/page-manifests"
    PAGE_SHARD_SUMMARY="$SLICE_DIR/page-shards-summary.tsv"
    mkdir -p "$PAGE_SHARD_DIR"
    echo "▶ Building page-shard manifests (threshold=$LARGE_PDF_PAGE_THRESHOLD pages, range=$PAGE_RANGE_SIZE pages)"
    python3 - "$CORPUS" "$CHUNKS" "$PAGE_SHARD_DIR" "$PAGE_SHARD_SUMMARY" "$LARGE_PDF_PAGE_THRESHOLD" "$PAGE_RANGE_SIZE" "$MAX_PAGES_PER_PDF" <<'PY'
import heapq
import os
import re
import subprocess
import sys
from pathlib import Path

corpus = Path(sys.argv[1])
chunks = int(sys.argv[2])
manifest_dir = Path(sys.argv[3])
summary_path = Path(sys.argv[4])
threshold = int(sys.argv[5])
range_size = int(sys.argv[6])
max_pages = int(sys.argv[7]) if len(sys.argv) > 7 else 0

if chunks < 1:
    raise SystemExit("chunks must be >= 1")
if threshold < 1:
    raise SystemExit("large PDF page threshold must be >= 1")
if range_size < 1:
    raise SystemExit("page range size must be >= 1")

pdfs = []
for pdf in corpus.rglob("*.pdf"):
    if ".git" in pdf.parts:
        continue
    rel = pdf.relative_to(corpus).as_posix()
    pdfs.append((rel, pdf))
pdfs.sort(key=lambda item: item[0])

def page_count(pdf: Path) -> int:
    try:
        result = subprocess.run(
            ["pdfinfo", str(pdf)],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=20,
            check=False,
        )
    except Exception:
        return 0
    if result.returncode != 0:
        return 0
    match = re.search(r"^Pages:\s+(\d+)\s*$", result.stdout, re.MULTILINE)
    return int(match.group(1)) if match else 0

units = []
large = []
unknown = []
sampled_pdfs = []
for rel, pdf in pdfs:
    count = page_count(pdf)
    if count <= 0:
        units.append((1, rel, 0, 0))
        unknown.append(rel)
        continue

    # Cap pathologically large documents to an EVENLY SPREAD sample rather than
    # the first N pages. An implementation-limits fixture is uniform, but a real
    # document is not, and page 9,999 is exactly where a late-revision or
    # deep-page-tree defect would hide — taking only the front would trade one
    # blind spot for another.
    if max_pages > 0 and count > max_pages:
        step = count / max_pages
        sampled = sorted({min(count, max(1, int(i * step) + 1)) for i in range(max_pages)})
        sampled_pdfs.append((rel, count, len(sampled)))
        for pg in sampled:
            units.append((1, rel, pg, pg))
        continue

    if count > threshold:
        large.append((rel, count))
        for start in range(1, count + 1, range_size):
            end = min(count, start + range_size - 1)
            units.append((end - start + 1, rel, start, end))
    else:
        units.append((count, rel, 1, count))

loads = [(0, i) for i in range(chunks)]
heapq.heapify(loads)
assigned = [[] for _ in range(chunks)]
for weight, rel, start, end in sorted(units, key=lambda item: (-item[0], item[1], item[2])):
    load, chunk = heapq.heappop(loads)
    assigned[chunk].append((rel, start, end))
    heapq.heappush(loads, (load + weight, chunk))

manifest_dir.mkdir(parents=True, exist_ok=True)
for chunk, rows in enumerate(assigned):
    rows.sort(key=lambda item: (item[0], item[1], item[2]))
    path = manifest_dir / f"chunk-{chunk:03d}.tsv"
    with path.open("w", encoding="utf-8") as f:
        f.write("path\tpageNumber\n")
        for rel, start, end in rows:
            if start == 0:
                f.write(f"{rel}\t0\n")
            else:
                for page in range(start, end + 1):
                    f.write(f"{rel}\t{page}\n")

with summary_path.open("w", encoding="utf-8") as f:
    f.write("chunk\tunits\tpages\tmanifest\n")
    for chunk, rows in enumerate(assigned):
        pages = sum(1 if start == 0 else end - start + 1 for _, start, end in rows)
        f.write(f"{chunk}\t{len(rows)}\t{pages}\t{manifest_dir / f'chunk-{chunk:03d}.tsv'}\n")
    f.write("\nlarge_pdf\tpages\n")
    for rel, count in large:
        f.write(f"{rel}\t{count}\n")
    f.write("\nunknown_page_count_pdf\n")
    for rel in unknown:
        f.write(f"{rel}\n")

total_pages = sum(weight for weight, *_ in units)
max_pages = max((sum(1 if start == 0 else end - start + 1 for _, start, end in rows) for rows in assigned), default=0)
min_pages = min((sum(1 if start == 0 else end - start + 1 for _, start, end in rows) for rows in assigned), default=0)
print(f"  PDFs: {len(pdfs)}")
print(f"  work units: {len(units)}")
print(f"  manifest page rows: {total_pages}")
print(f"  split large PDFs: {len(large)}")
# NEVER SILENT. A cap that is not announced reads as full coverage, which is the
# same failure shape as a partial report claiming completeness (#879).
for rel, total_pages_in_pdf, taken in sampled_pdfs:
    print(f"  ⚠ SAMPLED {rel}: {total_pages_in_pdf} pages -> {taken} evenly spread "
          f"(exceeds --max-pages-per-pdf)")
print(f"  unknown page-count PDFs: {len(unknown)}")
print(f"  pages per chunk: min={min_pages}, max={max_pages}")
print(f"  summary: {summary_path}")
PY
fi

echo "▶ Running $CHUNKS chunks ($CHUNK_PARALLEL chunks concurrent, each $PER_CHUNK_PARALLEL-way internally parallel, page-mode=$PAGE_MODE, extra-oracles=$EXTRA_ORACLES, process-timeout=${PROCESS_TIMEOUT_SECONDS}s)"
echo "  corpus: $CORPUS_LABEL"
echo "  chunk logs: $CHUNK_LOG_DIR/exploratory-chunk-N.log"
echo "  slice dir: $SLICE_DIR"
if [[ -n "$PAGE_SHARD_DIR" ]]; then
    echo "  page shards: $PAGE_SHARD_DIR"
fi
if [[ ${#PASSWORD_ARGS[@]} -gt 0 ]]; then
    echo "  password manifest: $PASSWORD_MANIFEST"
fi
if [[ ${#EXPECTATION_ARGS[@]} -gt 0 ]]; then
    echo "  expectation manifest: $EXPECTATION_MANIFEST"
fi
if [[ ${#EXCISE_CACHE_ARG[@]} -gt 0 ]]; then
    echo "  excise render cache: $EXCISE_RENDER_CACHE_DIR ($(if [[ "$RESUME_EXCISE_RENDER_CACHE" == "1" ]]; then echo resume; else echo fresh; fi))"
fi
chunk_failures=0
chunk_start=$(date +%s)

# Single-chunk runner — used by xargs -P below.
run_one_chunk() {
    local i="$1"
    local slice_path
    slice_path=$(printf '%s/exploratory-chunk-%03d-of-%03d.json' "$SLICE_DIR" "$i" "$CHUNKS")
    local scan_chunk="$i"
    local scan_total="$CHUNKS"
    local manifest_args=()
    local password_args=()
    local expectation_args=()
    local excise_cache_args=()
    if [[ -n "${PAGE_SHARD_DIR:-}" ]]; then
        manifest_args=(--page-manifest "$(printf '%s/chunk-%03d.tsv' "$PAGE_SHARD_DIR" "$i")")
        scan_chunk="0"
        scan_total="1"
    fi
    if [[ -n "${PASSWORD_MANIFEST:-}" ]]; then
        password_args=(--password-manifest "$PASSWORD_MANIFEST")
    fi
    if [[ -n "${EXPECTATION_MANIFEST:-}" ]]; then
        expectation_args=(--expectation-manifest "$EXPECTATION_MANIFEST")
    fi
    if [[ "${EXCISE_RENDER_CACHE_ENABLED:-0}" == "1" ]]; then
        excise_cache_args=(--excise-render-cache-dir "$EXCISE_RENDER_CACHE_DIR")
    fi
    # Scale the timeout to the work actually assigned (#879). A fixed 600s cap
    # is fine for a page-1 scan, where a chunk holds a few hundred single-page
    # PDFs, and impossible for an all-pages scan, where the shard summary can
    # hand one chunk 800 pages that each need an excise render plus up to five
    # oracle renders. The cap then fires by construction rather than because
    # anything is wrong.
    local chunk_timeout="$PROCESS_TIMEOUT_SECONDS"
    if [[ -n "${PAGE_SHARD_SUMMARY:-}" && -f "$PAGE_SHARD_SUMMARY" ]]; then
        local shard_pages
        shard_pages=$(awk -F'\t' -v c="$i" 'NR>1 && $1==c {print $3; exit}' "$PAGE_SHARD_SUMMARY")
        if [[ "$shard_pages" =~ ^[0-9]+$ ]] && (( shard_pages > 0 )); then
            # SECONDS_PER_PAGE is deliberately generous: the cost is one oracle
            # render per extra oracle, and the point of the cap is to catch a
            # hang, not to police throughput.
            local scaled=$(( shard_pages * ${SECONDS_PER_PAGE_BUDGET:-3} ))
            (( scaled > chunk_timeout )) && chunk_timeout="$scaled"
        fi
    fi

    local runner=()
    if command -v timeout >/dev/null 2>&1; then
        runner=(timeout --kill-after=10s "$chunk_timeout")
    elif command -v gtimeout >/dev/null 2>&1; then
        runner=(gtimeout --kill-after=10s "$chunk_timeout")
    fi

    # --incremental-output makes the slice file durable WHILE the chunk runs
    # (#879). Without it a chunk writes its slice only on completion, so a
    # SIGKILL from the timeout below discards every page it had already
    # rendered and compared — observed losing ~7,200 completed pages of a
    # 10,000-page fixture in a single chunk. The tool writes the partial report
    # to the same --output path via temp-file-plus-atomic-rename, so a partial
    # slice is never torn and the final write simply supersedes it.
    local command=("$EXCISE_RENDER_TOOLS_BIN" corpus-scan "$CORPUS"
        --output "$slice_path"
        --chunk "$scan_chunk"
        --total "$scan_total"
        --parallel "$PER_CHUNK_PARALLEL"
        --pdf-timeout-ms "$PDF_TIMEOUT_MS"
        --page-mode "$PAGE_MODE"
        --extra-oracles "$EXTRA_ORACLES"
        --incremental-output
        --progress-interval-seconds "${INCREMENTAL_FLUSH_SECONDS:-20}")
    if (( ${#manifest_args[@]} > 0 )); then command+=("${manifest_args[@]}"); fi
    if (( ${#password_args[@]} > 0 )); then command+=("${password_args[@]}"); fi
    if (( ${#expectation_args[@]} > 0 )); then command+=("${expectation_args[@]}"); fi
    if (( ${#excise_cache_args[@]} > 0 )); then command+=("${excise_cache_args[@]}"); fi

    if (( ${#runner[@]} > 0 )); then
        "${runner[@]}" "${command[@]}" > "$CHUNK_LOG_DIR/exploratory-chunk-$i.log" 2>&1
    else
        "${command[@]}" > "$CHUNK_LOG_DIR/exploratory-chunk-$i.log" 2>&1
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
# Parallel chunks run in a SEPARATE bash, so anything run_chunk reads must be
# exported here. A variable missing from this list is empty over there, and an
# empty value becomes an empty command-line argument rather than an error:
# `--progress-interval-seconds ""` is a parse failure that exits 1 while the
# chunk itself is perfectly fine. That is exactly what happened when
# INCREMENTAL_FLUSH_SECONDS and SECONDS_PER_PAGE_BUDGET were added and not
# exported — every parallel chunk failed rc=1 and the run fell back to a serial
# retry, which succeeded because the retry path runs in THIS shell where the
# variables are set. The scan looked merely slow rather than broken.
export EXCISE_RENDER_TOOLS_BIN CORPUS CORPUS_LABEL BIN_DIR SLICE_DIR CHUNKS PER_CHUNK_PARALLEL PDF_TIMEOUT_MS PROCESS_TIMEOUT_SECONDS PAGE_MODE EXTRA_ORACLES CHUNK_LOG_DIR
export INCREMENTAL_FLUSH_SECONDS SECONDS_PER_PAGE_BUDGET PAGE_SHARD_SUMMARY
export PAGE_SHARD_DIR PASSWORD_MANIFEST EXPECTATION_MANIFEST EXCISE_RENDER_CACHE_ENABLED EXCISE_RENDER_CACHE_DIR

recover_one_page_shard_chunk_isolated() {
    local i="$1"
    local slice_path="$2"
    local recovery_dir="$3"
    local source_manifest
    source_manifest=$(printf '%s/chunk-%03d.tsv' "$PAGE_SHARD_DIR" "$i")
    if [[ ! -f "$source_manifest" ]]; then
        echo "  missing page-shard manifest for chunk $((i + 1)): $source_manifest" >&2
        return 1
    fi

    local n=0
    local rel page rest
    while IFS=$'\t' read -r rel page rest; do
        if [[ "$rel" == "path" && "$page" == "pageNumber" ]]; then
            continue
        fi
        if [[ -z "$rel" || -z "$page" ]]; then
            continue
        fi

        local single_manifest single_json single_log
        single_manifest=$(printf '%s/single-%05d.tsv' "$recovery_dir" "$n")
        single_json=$(printf '%s/single-%05d.json' "$recovery_dir" "$n")
        single_log=$(printf '%s/single-%05d.log' "$recovery_dir" "$n")
        {
            printf 'path\tpageNumber\n'
            printf '%s\t%s\n' "$rel" "$page"
        } > "$single_manifest"

        local runner=()
        if command -v timeout >/dev/null 2>&1; then
            runner=(timeout --kill-after=10s "$PROCESS_TIMEOUT_SECONDS")
        elif command -v gtimeout >/dev/null 2>&1; then
            runner=(gtimeout --kill-after=10s "$PROCESS_TIMEOUT_SECONDS")
        fi

        local excise_cache_args=()
        local password_args=()
        local expectation_args=()
        if [[ -n "${PASSWORD_MANIFEST:-}" ]]; then
            password_args=(--password-manifest "$PASSWORD_MANIFEST")
        fi
        if [[ -n "${EXPECTATION_MANIFEST:-}" ]]; then
            expectation_args=(--expectation-manifest "$EXPECTATION_MANIFEST")
        fi
        if [[ "${EXCISE_RENDER_CACHE_ENABLED:-0}" == "1" ]]; then
            excise_cache_args=(--excise-render-cache-dir "$EXCISE_RENDER_CACHE_DIR")
        fi

        local command=("$EXCISE_RENDER_TOOLS_BIN" corpus-scan "$CORPUS"
            --output "$single_json"
            --chunk 0
            --total 1
            --parallel 1
            --pdf-timeout-ms "$PDF_TIMEOUT_MS"
            --page-mode "$PAGE_MODE"
            --extra-oracles "$EXTRA_ORACLES"
            --page-manifest "$single_manifest")
        if (( ${#password_args[@]} > 0 )); then command+=("${password_args[@]}"); fi
        if (( ${#expectation_args[@]} > 0 )); then command+=("${expectation_args[@]}"); fi
        if (( ${#excise_cache_args[@]} > 0 )); then command+=("${excise_cache_args[@]}"); fi

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
            python3 - "$single_json" "$rel" "$page" "$rc" "$PAGE_MODE" <<'PY'
import json, sys, datetime
out, rel, page, rc, page_mode = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), sys.argv[5]
status = "TIMEOUT" if rc == 124 else "SCANNER_CRASH"
error_type = "ProcessTimeout" if rc == 124 else "ProcessExit"
entry = {
    "path": rel,
    "pageNumber": page,
    "status": status,
    "errorPhase": "scan",
    "errorType": error_type,
    "errorMessage": f"Excise.RenderTools corpus-scan exited {rc} before writing a single-page report",
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
    done < "$source_manifest"

    python3 - "$recovery_dir" "$slice_path" "$i" "$CHUNKS" "$PAGE_MODE" "$CORPUS_LABEL" "$EXTRA_ORACLES" <<'PY'
import glob, json, os, sys, datetime
recovery_dir, out_path, chunk, total, page_mode, corpus_label, extra_oracles = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), sys.argv[5], sys.argv[6], sys.argv[7]
entries = []
counts = {}
pdfs = set()
peak = 0
generated = []
for path in sorted(glob.glob(os.path.join(recovery_dir, "single-*.json"))):
    with open(path, encoding="utf-8") as f:
        d = json.load(f)
    for entry in d.get("entries", []):
        entries.append(entry)
        p = entry.get("path", entry.get("Path"))
        if p:
            pdfs.add(p)
    for k, v in d.get("counts", {}).items():
        counts[k] = counts.get(k, 0) + v
    peak = max(peak, d.get("peakRssBytes", 0))
    if d.get("generatedUtc"):
        generated.append(d["generatedUtc"])
report = {
    "generatedUtc": max(generated) if generated else datetime.datetime.utcnow().isoformat() + "Z",
    "corpus": corpus_label,
    "chunkIndex": chunk,
    "chunkTotal": total,
    "pageMode": page_mode,
    "extraOracles": extra_oracles,
    "recovery": {
        "mode": "isolated-page-shard",
        "sourceChunkCrashed": True,
        "sourceChunkIndex": chunk,
        "recoveredReports": len(generated),
    },
    "counts": counts,
    "total": len(entries),
    "pdfs": len(pdfs),
    "peakRssBytes": peak,
    "entries": sorted(entries, key=lambda e: (e.get("path", e.get("Path", "")), e.get("pageNumber", e.get("PageNumber", 0)))),
}
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(report, f, indent=2)
print(f"  isolated page-shard recovery wrote {out_path} ({len(entries)} page results)")
PY
}

recover_one_chunk_isolated() {
    local i="$1"
    local slice_path
    slice_path=$(printf '%s/exploratory-chunk-%03d-of-%03d.json' "$SLICE_DIR" "$i" "$CHUNKS")
    local recovery_dir
    recovery_dir=$(printf '%s/exploratory-recovery-chunk-%03d-of-%03d' "$SLICE_DIR" "$i" "$CHUNKS")
    mkdir -p "$recovery_dir"
    rm -f "$recovery_dir"/single-*.json "$recovery_dir"/single-*.log "$recovery_dir"/files.txt

    if [[ -n "${PAGE_SHARD_DIR:-}" ]]; then
        recover_one_page_shard_chunk_isolated "$i" "$slice_path" "$recovery_dir"
        return
    fi

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

        local excise_cache_args=()
        local password_args=()
        local expectation_args=()
        if [[ -n "${PASSWORD_MANIFEST:-}" ]]; then
            password_args=(--password-manifest "$PASSWORD_MANIFEST")
        fi
        if [[ -n "${EXPECTATION_MANIFEST:-}" ]]; then
            expectation_args=(--expectation-manifest "$EXPECTATION_MANIFEST")
        fi
        if [[ "${EXCISE_RENDER_CACHE_ENABLED:-0}" == "1" ]]; then
            excise_cache_args=(--excise-render-cache-dir "$EXCISE_RENDER_CACHE_DIR")
        fi

        local command=("$EXCISE_RENDER_TOOLS_BIN" corpus-scan "$item_dir"
            --output "$single_json"
            --chunk 0
            --total 1
            --parallel 1
            --pdf-timeout-ms "$PDF_TIMEOUT_MS"
            --page-mode "$PAGE_MODE"
            --extra-oracles "$EXTRA_ORACLES")
        if (( ${#password_args[@]} > 0 )); then command+=("${password_args[@]}"); fi
        if (( ${#expectation_args[@]} > 0 )); then command+=("${expectation_args[@]}"); fi
        if (( ${#excise_cache_args[@]} > 0 )); then command+=("${excise_cache_args[@]}"); fi

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
    "errorMessage": f"Excise.RenderTools corpus-scan exited {rc} before writing a single-PDF report",
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
    "recovery": {
        "mode": "isolated-pdf",
        "sourceChunkCrashed": True,
        "sourceChunkIndex": chunk,
        "recoveredReports": len(generated),
    },
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
python3 - "$BIN_DIR" "$SLICE_DIR" "$CHUNKS" "$PAGE_MODE" "$REPORT_NAME" "$CORPUS_LABEL" "$EXTRA_ORACLES" "$PAGE_SHARD_SUMMARY" "$EXCISE_RENDER_CACHE_DIR" "$EXCISE_RENDER_CACHE_ENABLED" <<'PY'
import json, os, sys, glob
bin_dir, slice_dir, expected, page_mode, report_name, corpus_label, extra_oracles, page_shard_summary, excise_cache_dir, excise_cache_enabled = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4], sys.argv[5], sys.argv[6], sys.argv[7], sys.argv[8], sys.argv[9], sys.argv[10]
slices = sorted(glob.glob(os.path.join(slice_dir, "exploratory-chunk-*.json")))
print(f"  found {len(slices)} slice file(s) (expected {expected})")

merged_entries = []
counts = {}
result_counts = {}
result_category_counts = {}
peak = 0
chunk_pdf_visits = 0
generated_utcs = []
recoveries = []
expectation_manifest_entries = 0
partial_slices = []
for path in slices:
    with open(path) as f:
        d = json.load(f)
    # A slice written by the incremental writer carries partial=true until the
    # chunk finishes and overwrites it (#879). Its entries are real, completed
    # pages and are worth keeping — that is the entire point of making them
    # durable — but the merged report must NOT claim full coverage, or a run
    # that lost a chunk to a timeout reads exactly like a clean one.
    if d.get("partial") is True:
        partial_slices.append((os.path.basename(path), len(d.get("entries", []))))
    merged_entries.extend(d.get("entries", []))
    for k, v in d.get("counts", {}).items():
        counts[k] = counts.get(k, 0) + v
    for k, v in d.get("resultCounts", {}).items():
        result_counts[k] = result_counts.get(k, 0) + v
    for k, v in d.get("resultCategoryCounts", {}).items():
        result_category_counts[k] = result_category_counts.get(k, 0) + v
    peak = max(peak, d.get("peakRssBytes", 0))
    chunk_pdf_visits += d.get("pdfs", 0)
    if d.get("generatedUtc"):
        generated_utcs.append(d["generatedUtc"])
    if d.get("recovery"):
        recoveries.append(d["recovery"])
    manifest = d.get("expectationManifest")
    if isinstance(manifest, dict):
        expectation_manifest_entries = max(expectation_manifest_entries, manifest.get("entries", 0) or 0)

def get(e, key, default=None):
    return e.get(key, e.get(key[:1].upper() + key[1:], default))

unique_pdfs = {
    get(entry, "path")
    for entry in merged_entries
    if get(entry, "path")
}

out = {
    "generatedUtc": max(generated_utcs) if generated_utcs else None,
    "corpus": corpus_label,
    "pageMode": page_mode,
    "extraOracles": extra_oracles,
    "pageShardSummary": page_shard_summary or None,
    "exciseRenderCache": {
        "enabled": excise_cache_enabled == "1",
        "directory": excise_cache_dir or None,
    },
    "expectationManifest": {"entries": expectation_manifest_entries} if expectation_manifest_entries else None,
    "chunksMerged": len(slices),
    "expectedChunks": expected,
    # Coverage honesty (#879): true only when every chunk ran to completion.
    # A merged report built from an incremental slice still contains real
    # results, but it does not cover every page it was asked to.
    "partial": bool(partial_slices) or len(slices) < expected,
    "partialSlices": [{"slice": n, "entries": c} for n, c in partial_slices] or None,
    "recoverySummary": {
        "recoveredChunks": len(recoveries),
        "recoveries": recoveries,
    },
    "counts": counts,
    "resultCounts": result_counts or None,
    "resultCategoryCounts": result_category_counts or None,
    "total": len(merged_entries),
    "pdfs": len(unique_pdfs),
    "chunkPdfVisits": chunk_pdf_visits,
    "perChunkPeakRssBytes": peak,
    # JSON keys come through as PascalCase from the .NET serializer
    # (Path, Status, …); use case-insensitive lookup defensively.
    "entries": sorted(merged_entries, key=lambda e: (get(e, "path", ""), get(e, "pageNumber", 0))),
}
out_path = os.path.join(bin_dir, report_name)
with open(out_path, "w") as f:
    json.dump(out, f, indent=2)

print(f"  wrote {out_path}")
print(f"  total page results: {out['total']}")
print(f"  unique PDFs scanned: {out['pdfs']}")
if partial_slices:
    kept = sum(c for _, c in partial_slices)
    print(f"  ⚠ PARTIAL: {len(partial_slices)} chunk(s) did not finish; kept {kept} "
          f"page result(s) they had already completed")
    for n, c in partial_slices:
        print(f"      {n}: {c} entries")
    print("    Before #879 these pages were discarded entirely. They are kept now,")
    print("    but the report is marked partial=true — do NOT pin a manifest from it.")
if chunk_pdf_visits != out["pdfs"]:
    print(f"  chunk PDF visits: {chunk_pdf_visits}")
for k in sorted(counts, key=counts.get, reverse=True):
    print(f"    {counts[k]:4d}  {k}")

# ---- agreement classification (#877) -------------------------------------
# `status` answers "what happened to this page". It does NOT answer "was excise
# right", and reading it as though it did understates the result badly: a file
# that mutool, pdftocairo, Ghostscript, PDFBox and PDFium all refuse is scored
# identically to a crash. On the PDFium corpus that turned ~96% correct into a
# reported 76% PASS.
#
# This is deliberately ADDITIVE — no status is rewritten, so every
# tests/corpus-expectations*.tsv stays valid. It only reports a second, honest
# number alongside the first.
ORACLE_STATUS_KEYS = ("mutoolStatus", "cairoStatus", "ghostscriptStatus",
                      "pdfboxStatus", "pdfiumStatus")
CREDENTIAL_STATUSES = {"PASSWORD_REQUIRED", "UNSUPPORTED_ENCRYPTED"}

def agreement_class(e):
    st = get(e, "status")
    if st == "PASS":
        return "PASS"
    if st == "PASS_ONE":
        return "ORACLE_SPLIT"
    if st == "DIFF":
        return "DIFF"
    if st == "MISSING_CONTENT":
        # excise drew nothing where the reference drew content (#883).
        return "MISSING_CONTENT"
    if st == "RECOVERED_MALFORMED_CONTENT":
        # excise met damaged content and RECOVERED instead of crashing, hanging
        # or exhausting memory. For a decompression bomb (bomb_giant.pdf: 123 KB
        # on disk, one page, expands enormously) that is precisely the property
        # the fixture exists to test — surviving it is the fixture PASSING.
        # Counting it as non-PASS made a success look like a defect every run.
        return "RECOVERED"
    oracle_states = [get(e, k) for k in ORACLE_STATUS_KEYS]
    any_oracle_ok = any(s == "OK" for s in oracle_states)
    # A status of None means the oracle was never INVOKED, which is different
    # from having been invoked and failed (#882). Distinguishing them matters:
    # when excise's own render hits the wall budget the comparison never runs,
    # so all five come back None — and treating that as "nobody could render
    # it" filed a page pdftocairo and Ghostscript both render fine into the
    # bucket meaning "refusing is correct". That page was the #881 DoS: the
    # classification meant to surface defects concealed one.
    any_oracle_attempted = any(s is not None for s in oracle_states)
    excise_rendered = get(e, "renderMs") is not None
    if excise_rendered:
        return "EXCISE_ONLY" if not any_oracle_ok else "RENDERED"
    if st in CREDENTIAL_STATUSES:
        # NOT corroboration: everyone was locked out equally, which says
        # nothing about whether the page is renderable.
        return "CREDENTIAL_BLOCKED"
    if any_oracle_ok:
        # An oracle rendered it and excise did not. The only class here that
        # is unambiguously an excise defect.
        return "EXCISE_SIDE_GAP"
    if not any_oracle_attempted:
        # The harness formed no opinion. Counts as neither correct nor
        # incorrect, and is reported loudly rather than folded into a
        # correct-looking bucket.
        return "UNCORROBORATED"
    return "AGREED_REFUSAL"

agreement = {}
for e in merged_entries:
    a = agreement_class(e)
    agreement[a] = agreement.get(a, 0) + 1
out["agreementCounts"] = agreement

correct_classes = ("PASS", "AGREED_REFUSAL", "EXCISE_ONLY", "RECOVERED")
correct = sum(agreement.get(c, 0) for c in correct_classes)
total_entries = len(merged_entries) or 1
print()
print("  agreement (excise vs the reference renderers):")
for k in ("PASS", "AGREED_REFUSAL", "EXCISE_ONLY", "RECOVERED", "ORACLE_SPLIT",
          "CREDENTIAL_BLOCKED", "UNCORROBORATED", "MISSING_CONTENT",
          "DIFF", "EXCISE_SIDE_GAP", "RENDERED"):
    if agreement.get(k):
        note = {
            "AGREED_REFUSAL": "no renderer managed it — refusing is correct",
            "EXCISE_ONLY": "excise rendered where no oracle could",
            "RECOVERED": "survived damaged/bomb content — the fixture's point",
            "ORACLE_SPLIT": "oracles disagree among themselves",
            "CREDENTIAL_BLOCKED": "nobody had the password — proves nothing",
            "UNCORROBORATED": "oracles never ran — no opinion formed, NOT corroboration",
            "MISSING_CONTENT": "excise blank where the reference has content — DEFECT",
            "EXCISE_SIDE_GAP": "an oracle rendered and excise did not — DEFECT",
        }.get(k, "")
        print(f"    {agreement[k]:4d}  {k:20} {note}")
print(f"  excise behaves correctly on {correct}/{total_entries} "
      f"({100.0 * correct / total_entries:.1f}%) — PASS + agreed refusal + excise-only")
if agreement.get("EXCISE_SIDE_GAP"):
    print(f"  ⚠ {agreement['EXCISE_SIDE_GAP']} page(s) an oracle rendered and excise did not")
if agreement.get("MISSING_CONTENT"):
    print(f"  ⚠ {agreement['MISSING_CONTENT']} page(s) excise rendered blank where the reference has content")
if agreement.get("UNCORROBORATED"):
    print(f"  ⚠ {agreement['UNCORROBORATED']} page(s) had NO oracle invoked — not corroboration, no opinion")

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
        "INVALID_PAGE_GEOMETRY", "INVALID_PAGE_NUMBER", "PARSE_ERROR", "RENDER_ERROR",
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

# Expectation gate (#862). ApplyCorpusExpectations already compares each page
# against the manifest and records expectationResult=FAIL, but nothing acted on
# it: a page could regress PASS -> DIFF and the run still exited 0. Verified by
# flipping an expectation and watching a --tiny run pass regardless.
if [[ -n "$EXPECTATION_MANIFEST" && -f "$BIN_DIR/$REPORT_NAME" ]]; then
    EXP_FAILS=$(python3 - "$BIN_DIR/$REPORT_NAME" <<'PYEXP'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    print(-1); raise SystemExit
rows = d if isinstance(d, list) else (d.get("results") or d.get("entries") or [])
bad = [r for r in rows if r.get("expectationResult") == "FAIL"]
for r in bad[:20]:
    f = (r.get("file") or r.get("pdf") or r.get("path") or "?").split("/")[-1]
    print(f"    {f}#p{r.get('page', r.get('pageNumber', 1))}  {r.get('expectationFailure','')}",
          file=sys.stderr)
print(len(bad))
PYEXP
)
    if [[ "$EXP_FAILS" == "-1" ]]; then
        echo "⚠ could not read $BIN_DIR/$REPORT_NAME to check expectations" >&2
    elif (( EXP_FAILS > 0 )); then
        echo
        echo "✗ $EXP_FAILS page(s) did not match $EXPECTATION_MANIFEST"
        echo "  A status change is not automatically bad — a page moving DIFF -> PASS is"
        echo "  an improvement. Review, then regenerate the manifest to accept it."
        exit 1
    fi
    echo "  expectations: all pages matched"
fi

echo "✓ done"
