# Aggregate hot-path profile baseline — issue #597 (epic #596)

- **Date:** 2026-07-25
- **Commit:** `01e8348` (develop tip; branch `evidence/597-hotpath-baseline`)
- **Machine:** Apple M5, 24 GB RAM, macOS (Darwin 25.5), .NET SDK 10.0.300, Release build
- **Corpus:** `test-pdfs/smoke` — 10 real-world government PDFs (CDC, IRS 1040 + instructions,
  Pub 509, W-4, W-9, two SCOTUS opinions, DS-11/DS-82 passport forms), downloaded via
  `scripts/download-test-pdfs.sh`. This is the aggregate smoke corpus, not one-off PDFs.
- **Caps:** page-limit 8 per document (workflow profiler) / 8 total (benchmark-suite,
  harness default `EXCISE_BENCHMARK_PAGE_LIMIT=8`), DPI 96, zoom-rerender DPI 192.
  Save/redaction-save operate on the **full document** regardless of page limit.

## Exact commands (reproducibility)

```bash
# 1. Workflow-level elapsed + managed-allocation profile (new profile-workflows command,
#    added by this issue; writes incremental NDJSON so long PDFs cannot hide status)
dotnet tools/Excise.RenderTools/bin/Release/net10.0/Excise.RenderTools.dll \
  profile-workflows --corpus test-pdfs/smoke \
  --output-dir logs/benchmarks/597-baseline/workflows \
  --page-limit 8 --dpi 96 --zoom-dpi 192 --search-term the

# 2. Harness benchmark suite (reference oracles: mutool, pdftocairo, ghostscript available)
EXCISE_BENCHMARK_OUTPUT_DIR=logs/benchmarks/597-baseline \
EXCISE_BENCHMARK_CORPUS_DIR=test-pdfs/smoke \
EXCISE_BENCHMARK_PAGE_LIMIT=8 EXCISE_BENCHMARK_DPI=96 scripts/run-benchmarks.sh

# 3. Function-level CPU attribution, one trace per isolated workflow
#    (dotnet-trace 9.0.661903, profile dotnet-sampled-thread-time, ~100 Hz)
dotnet-trace collect --profile dotnet-sampled-thread-time -o trace-<w>.nettrace -- \
  dotnet .../Excise.RenderTools.dll profile-workflows --corpus test-pdfs/smoke \
  --page-limit 8 --steps <workflow>       # save-roundtrip | first-page-render,... | text-extract,search | redaction-save
dotnet-trace report trace-<w>.nettrace topN -n 30 [--inclusive]
```

Raw outputs are checked in next to this file: `workflow-profile.{json,ndjson}`,
`benchmark-report.{json,md}`, `benchmark-hotpaths.json`, `benchmark-pages.csv`,
`latest-performance-baseline.{json,md}`, and `cpu-attribution/trace-*-top*.txt`.

## 1. Workflow ranking — time and managed allocation (10 PDFs, smoke corpus)

From `workflow-profile.json` (single pass, cold per document, ordered by cumulative time):

| Workflow step | Owner area | Child issue | Total ms | Avg ms | P95 ms | Total alloc | Avg alloc/doc |
|---|---|---|---:|---:|---:|---:|---:|
| save-roundtrip (open + `SaveToBytes`, no edit) | Excise.Core writer + object store | *(unowned — see §5)* | 1875 | 188 | 1576 | **3953 MB** | 395 MB |
| redaction-save (`RedactArea` word + save) | Excise.Core redaction + writer | *(unowned — see §5)* | 1663 | 166 | 822 | **4378 MB** | 438 MB |
| all-page-render (≤8 pages @96dpi) | Excise.Rendering | #598/#599 | 1595 | 160 | 511 | 1085 MB | 108 MB |
| first-page-render | Excise.Rendering | #598/#599 | 846 | 85 | 474 | 249 MB | 25 MB |
| navigation-rerender (2 pages, no cache) | Excise.Rendering (+GUI cache #601) | #598/#601 | 465 | 47 | 149 | 303 MB | 30 MB |
| zoom-rerender (page 1 @192dpi) | Excise.Rendering | #598/#599/#601 | 321 | 32 | 125 | 181 MB | 18 MB |
| text-extract (≤8 pages) | Excise.Core text | #600 | 247 | 25 | 81 | 637 MB | 64 MB |
| open (parse to first structural access) | Excise.Core parser | — | 41 | 4 | 24 | 37 MB | 4 MB |
| search (`GetWords` + term scan, warm letters) | Excise.Core text | #600 | 38 | 4 | 10 | 21 MB | 2 MB |

Notes:
- Allocation is managed (GC) bytes on the driver thread (`GC.GetAllocatedBytesForCurrentThread`);
  native SkiaSharp bitmap memory is *not* included, so render allocation is understated.
- Worst single document: `irs-1040-instructions.pdf` (4.4 MB file, 785 object streams,
  76,233 compressed objects) — save-roundtrip **1576 ms / 3178 MB allocated** for one save.
- Render-heaviest documents: `state-ds11-passport.pdf` / `state-ds82-passport-renewal.pdf`
  (CMYK transparency-group content): 511/497 ms for 6 pages @96dpi, 424/383 MB alloc;
  also the text-extract outliers (81/66 ms, 297/196 MB alloc).
- `redaction-save` ≈ `save-roundtrip` + redaction: glyph removal itself is cheap; the cost
  is the save path plus the structure-tree scrub walk (see §3.4).

## 2. Benchmark-suite hot-path buckets (harness convention, reference oracles on)

From `benchmark-hotpaths.json` (8 pages: cdc-vis-covid-19 ×2, irs-1040-instructions ×6):

| Bucket | Scope | n | Total ms | Avg ms | P95 ms |
|---|---|---:|---:|---:|---:|
| renderer.page-render | excise-owned | 8 | 191 | 23.9 | 82 |
| text.extract-search-input | excise-owned | 8 | 92 | 11.5 | 60 |
| parser.document-open | excise-owned | 2 | 32 | 16.0 | 25 |
| redaction.synthetic-save | excise-owned-security-critical | 1 | 11 | 11.0 | 11 |
| **reference.external-render** | **external-reference (compare-only)** | 24 | **2934** | **122.2** | **217** |

**Reference-renderer timing is reported separately and is NOT a pdfe hot path** (mutool,
pdftocairo, ghostscript external subprocesses; avg 122 ms/page vs excise in-process 23.9 ms/page).
Regression gate: PASS (render avg 23.9 ms ≤ 2500; parse avg 20.5 ms ≤ 750; synthetic
redaction completeness PASS; reference fidelity pass rate 100 %).

## 3. Function-area CPU attribution (dotnet-trace, per-workflow isolated)

Percentages are of total sampled thread time in each trace; the EventPipe `PollGC`
diagnostics thread (41–67 % of raw samples — itself a signal of allocation pressure)
runs on its own thread, so read the main-thread structure relatively. Full topN tables
in `cpu-attribution/`.

### 3.1 Save workflow (`trace-save`, steps=save-roundtrip)

`PdfDocument.SaveToBytes` → `PdfDocumentWriter.WriteObjects` 90 % →
`PdfDocument.GetObject` 80 % → `ComputeReachableObjectsFrom` 79 % →
**`GetObjectFromStream` 67 %** → `PdfParser.ParseDictionaryContents` 65 %.
Actual byte serialization (`PdfObjectWriter.Serialize*`) is only ~5 %.

Speedscope caller analysis (1687 ms sampled): `GetObjectFromStream` on-stack 1136 ms,
all under `GetObject`; `Array.Copy` on-stack 501 ms, of which 312 ms is
`Dictionary<string,PdfObject>.Resize` (default-capacity dictionary growth per parsed
object) and 124 ms `List<T>` growth. Direct index-loop lexing under
`GetObjectFromStream` is only ~35 ms — the CPU cost is the one-time parse of all 76k
objects; the **allocation** cost is per-fetch machinery.

Code-verified root cause (`Excise.Core/Document/PdfDocument.cs:899-940`): every
`GetObjectFromStream(streamNumber, index)` call allocates a fresh `PdfParser` +
`PdfLexer` (8 KB buffer) + `MemoryStream`, re-lexes the stream's full N-pair index
(~100 entries → ~200 token strings + an N-tuple array), then parses one object.
For irs-1040-instructions that is **76,233 parser instantiations + index re-parses**
(≈7.6 M redundant index tokens, ≈122 MB of redundant offset arrays alone) to
materialize 785 object streams — 3.2 GB total churn and 1.6 s for one save.

### 3.2 Render workflows (`trace-render`, steps=first-page/all-page/navigation/zoom)

`SkiaRenderer.RenderPage` 82 % of trace. Breakdown by function area:

| Function area | Incl. % | Key frames | Child issue |
|---|---:|---|---|
| Form XObject execution (nesting, state save/restore) | 44 % | `RenderFormXObject*` | #598 |
| Text showing / glyph paths | 25 % | `ShowTextBytes`, `FillTextUsingGlyphPath` 14 %, `SKFont.GetTextPath`, `SKTypeface.FromData` | #600 (+#598 dispatch) |
| **Path fill via DeviceCMYK blend** | **16.6 %** | `FillPath` → `TryPaintDeviceCmykBlendPath` 16.4 % incl / **6.0 % excl (managed per-pixel loop)** | **#599** |
| Soft-mask compositing | 14.4 % | `RenderWithCurrentSoftMask` | #599 |
| Content-stream parse (operator tokenization) | 12.8 % | `ContentStreamParser.Parse` | #598 |
| Skia native raster | 13.4 % excl | `SKCanvas.DrawPath` | (native; driven by #598/#599 call patterns) |
| Lock/GC overhead from object churn | 17.2 % excl | `Monitor.Enter_Slowpath` (SkiaSharp handle registry + GC), `Gen2GcCallback` 15 % | #598/#599 allocation discipline |

Code-verified (`Excise.Rendering/SkiaRenderer.Paths.cs:306-420`):
`TryPaintDeviceCmykBlendPath` allocates a **full-page RGBA SKBitmap mask per path
fill** and then runs a managed per-pixel loop using `SKBitmap.GetPixel(x,y)` — a
P/Invoke per pixel — over the path bounds. On the CMYK transparency-group passport
forms this executes per filled path and is the single largest tractable render leaf.

### 3.3 Text extraction/search (`trace-extract`, steps=text-extract,search)

`PdfPage.Text` → `TextExtractor.ExtractLetters` 80 %; `ParseContentBytes` 62 %
(7.1 % exclusive — string-based operator parsing); `ExtractFormXObjectText` 31 %;
`PdfDocument.GetObject` 39 % (font/XObject resource materialization → the same
`GetObjectFromStream` path as §3.1). `Array.Copy` on-stack 345 ms of 816 ms sampled:
`List<T>`/`List<byte>` growth (un-presized letter/byte accumulators,
`TextExtractor.cs:614`), `ToUnicodeCMapParser` token lists, `BuildWords`. → #600.

### 3.4 Redaction save (`trace-redact`, steps=redaction-save)

`RedactArea` 73 % incl, but inside it `StructureTreeRedactionScrubber.ScrubArea` 46 %
and `PdfDocument.GetObject`/`GetObjectFromStream` 59 %/49 % — the scrubber's
whole-tree walk force-materializes the object graph through the same per-fetch parser
churn as §3.1. Glyph filtering/reconstruction itself is not a significant CPU factor.
The redaction *pipeline* is security-critical and must not be short-cut; the cost
here is the shared object-store hot path, not the glyph logic.

## 4. Top pdfe-owned hot paths, ranked (aggregate across workflows)

By cumulative time **and** allocation, grouped by function area:

| # | Hot path (function area) | Where | Evidence | Child issue |
|---|---|---|---|---|
| 1 | **Object materialization through per-fetch `GetObjectFromStream`** (per-call parser/lexer/index re-parse; hit by save, redaction-save, extraction resources, render resources) | `Excise.Core/Document/PdfDocument.cs:899` | 67 % of save trace, 49 % of redaction trace, 39 % of extract trace; 3.2 GB alloc on one save | **none of #598–601** — epic-level gap (see §5) |
| 2 | **DeviceCMYK blend path painting** (full-page mask alloc + managed `GetPixel` per-pixel loop) | `Excise.Rendering/SkiaRenderer.Paths.cs:306` | 16.4 % incl / 6.0 % excl of render trace; passport forms 2× slower than everything else | **#599** |
| 3 | Parsed-object dictionary/list growth churn (default-capacity `Dictionary`/`List` per parsed object) | `Excise.Core/Parsing/PdfParser` + `PdfDictionary` | 312 ms Dictionary.Resize + 124 ms List growth of 1687 ms save trace | #598 (resource lookups) / same fix wave as #1 |
| 4 | Form XObject execution overhead | `Excise.Rendering/SkiaRenderer` (`RenderFormXObject*`) | 44 % incl of render trace | #598 |
| 5 | Glyph path text rendering (`GetTextPath` per run, typeface re-materialization) | `Excise.Rendering` text path | 25 % incl of render trace | #600 |
| 6 | Soft-mask compositing | `Excise.Rendering` (`RenderWithCurrentSoftMask`) | 14.4 % incl of render trace | #599 |
| 7 | Text extraction operator parse + un-presized accumulators | `Excise.Core/Text/TextExtractor` | 62 % incl of extract trace; 345 ms List/Array.Copy churn | #600 |
| 8 | Skia object churn → handle-registry lock + GC pressure | Excise.Rendering call patterns | `Monitor.Enter_Slowpath` 17 % excl, `Gen2GcCallback` 15 % of render trace | #598/#599 |

GUI-layer latency (#601) was **not** measured in this pass — no live GUI hotspot
reports were available in this workspace (see Limitations). Engine-side navigation and
zoom re-render costs (rows above) bound what #601's caching/scheduling can save.

## 5. Recommendation

**Highest-value, most-tractable target: the shared object-store hot path (#4 table
row 1) — cache object-stream indexes and batch-materialize object streams in
`PdfDocument.GetObjectFromStream`.** Concretely: on first touch of an object stream,
parse the index once and either (a) cache `(offsets, first)` per stream number, or
(b) parse all N contained objects with the single parser pass and populate
`_objectCache` for the whole stream. This replaces 76k parser/lexer/index
instantiations with 785 on the worst smoke document, and directly attacks:
save 1.9 s/4.0 GB, redaction-save 1.7 s/4.4 GB (a *daily-driver redaction workflow*),
plus the `GetObject` shares of extraction (39 %) and rendering. Expected order-of-magnitude
allocation reduction on save; likely >2× wall-clock on object-stream-heavy documents.

**However, this path is owned by none of #598–601** (it is Excise.Core object
store/writer, not renderer/image/font/GUI). Recommended sequencing:

1. Slot the `GetObjectFromStream` fix as its own slice under epic #596 (or stretch
   #598's "resource lookups" bullet) — it is the largest measured cost in the corpus
   and the redaction-save path makes it user-visible on every save.
2. **Among #598–601 as written, start with #599**: the concrete first optimization is
   `SkiaRenderer.TryPaintDeviceCmykBlendPath` (`Excise.Rendering/SkiaRenderer.Paths.cs:306`)
   — allocate the mask only for the clipped path bounds instead of the full page, read
   pixels via `SKBitmap.GetPixels()`/`Span` instead of per-pixel `GetPixel` P/Invoke,
   and reuse the mask bitmap across fills. Measured justification: 16.4 % of render
   trace with 6.0 % exclusive managed time, concentrated on exactly the documents
   (CMYK transparency forms) that are 3–6× slower to render than the rest of the
   corpus (511 ms vs ~80 ms median for 6–8 pages). Guardrail: keep the Altona/Ghent
   CMYK visual comparison gates green (#599's acceptance criteria already require this).
3. #600 next (extraction accumulator presizing + `GetTextPath` caching), then #598
   (form-XObject execution), then #601 once GUI traces exist.

All of the above are measurement-directed; nothing was optimized in this pass (#597 is
evidence-only).

## Limitations / caveats

- **Corpus:** smoke corpus (10 real-world PDFs) + the harness's 8-page benchmark cap.
  A fuller corpus (verapdf/altona/ghent) would raise the weight of image-decode and
  CMYK/transparency paths (#599) — the smoke corpus contains only two heavy-CMYK
  documents — but is unlikely to demote hot path #1, which scales with object-stream
  count in ordinary office/government PDFs.
- **Allocation scope:** managed bytes on the driver thread only; native Skia memory
  excluded, so render-side allocation is understated relative to Core.
- **GUI (#601):** no GUI display/workflow hotspot reports existed in this workspace
  (`Excise.App.Tests` UI suite not run here — it requires an exclusive ~17-minute
  serial run, #619). `latest-performance-baseline.json` marks those artifacts
  `exists:false`. Engine-level navigation/zoom numbers are included instead.
- **Sampling:** dotnet-trace `dotnet-sampled-thread-time` at ~100 Hz; the PollGC
  diagnostics thread inflates whole-trace percentages; per-thread structure was used.
  Single pass per workflow (not statistical); ordering effects visible (e.g.
  redaction-save ran after save-roundtrip with a warm file cache).
- **Timer granularity:** benchmark-suite per-page timings are Stopwatch milliseconds;
  the workflow profiler uses `Elapsed.TotalMilliseconds` (sub-ms precision).
