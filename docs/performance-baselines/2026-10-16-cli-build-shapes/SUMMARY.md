# excise CLI by build shape: JIT, ReadyToRun, NativeAOT (2026-10-16)

Fresh process per command (`scripts/cli-shape-bench.py`, median of 5 after a discarded warm-up, shapes interleaved), Apple M5,
`nice`, no window or input. Shapes: **JIT** = `dotnet build -c Release` output (framework-dependent, what the test tiers and
`reference-performance` measure today); **ReadyToRun** = `dotnet publish -r osx-arm64 --self-contained -p:PublishReadyToRun=true`;
**NativeAOT** = `scripts/build-aot-cli.sh`. Commit 019a4b6d. Machine load was 7 (1-minute) when the run started and 2-3 at the end
(other work after a restart), so absolute times are inflated about 2x against an earlier quiet smoke pass; the ratios between shapes are
the result, since the shapes alternate within every cell. Full table: `table.md`.

## Result

- Output files are the same size under all three shapes in every cell (including `redact` and `optimize`).
- NativeAOT is fastest and smallest in every cell. Short commands (`info`, `optimize`, `text` on small files): 3-8x faster than JIT
  and 2-5x faster than ReadyToRun, peak RSS 25-55 MB against 55-80 (JIT) and 145 (ReadyToRun; the self-contained runtime maps more).
- Long commands converge: `redact` on the 6-page AcroForm (ds11) 6.9 / 6.3 / 5.9 s and on the 126-page instructions 12.5 / 12.0 / 11.7 s
  (JIT / ReadyToRun / NativeAOT). The work is compute-bound after start-up, so shape matters little there. `redact` of ds11 peaks at
  366-444 MB in every shape, which is an engine allocation matter, not a shape one.
- `excise redact` exits 3 when the redaction is not clean (here: "Social" split across a hyphenated line break on page 82 and an
  unscrubbable Type1 font); the bench counts 3 as a timed result.

## Not covered

Managed allocation per workflow (the `workflow-budgets.json` rows run in `Excise.RenderTools`, which is JIT-only), the CLI against
mutool/pdftocairo on the NativeAOT binary (`reference-performance` accepts `EXCISE_BENCHMARK_CLI_COMMAND`), Linux and Windows.
