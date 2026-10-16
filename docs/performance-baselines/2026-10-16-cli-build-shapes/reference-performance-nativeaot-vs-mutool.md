# Reference Renderer Performance

- Each result launches a fresh excise CLI or external renderer process. No timed render result is cached; PNGs exist only transiently for same-run fidelity comparison. CPU/RSS are OS-reported process figures and may be null on platforms that do not expose them after exit.
- .NET: `.NET 10.0.12` from `(unset; resolved from PATH)`
- excise CLI codegen: `aot` — Native AOT, built by `scripts/build-aot-cli.sh`. No tiered JIT and no dynamic PGO.
- excise CLI binary shape: `override:EXCISE_BENCHMARK_CLI_COMMAND`
- Gate: `FAIL` — MODE MISMATCH — this run used the 'aot' CLI, the baseline was recorded with 'jit'. Native AOT and JIT generate different code, so these render times are not comparable and no ratio below would mean anything. Re-record the baseline with the binary you intend to measure (scripts/build-aot-cli.sh builds the AOT one), or point the bench back at the other binary.

| Fixture | Run | Renderer | Status | Wall ms | Render ms | Startup ms | CPU ms | Peak RSS MiB | Fidelity |
|---|---:|---|---|---:|---:|---:|---:|---:|---|
| `plans-soft-masks-36dpi` | 1 | excise-cli | OK | 186 | 118 | 68 | 113 | 49.0 | True |
| `plans-soft-masks-36dpi` | 1 | mutool | OK | 160 |  |  | 134 | 15.2 | True |
| `plans-soft-masks-36dpi` | 2 | excise-cli | OK | 152 | 114 | 38 | 115 | 49.1 | True |
| `plans-soft-masks-36dpi` | 2 | mutool | OK | 158 |  |  | 125 | 15.2 | True |
| `plans-soft-masks-150dpi` | 1 | excise-cli | OK | 429 | 389 | 39 | 392 | 100.6 | True |
| `plans-soft-masks-150dpi` | 1 | mutool | OK | 460 |  |  | 382 | 79.4 | True |
| `plans-soft-masks-150dpi` | 2 | excise-cli | OK | 434 | 394 | 39 | 400 | 102.8 | True |
| `plans-soft-masks-150dpi` | 2 | mutool | OK | 440 |  |  | 396 | 79.4 | True |
| `acc-compensation-cover` | 1 | excise-cli | OK | 490 | 448 | 41 | 514 | 152.9 | True |
| `acc-compensation-cover` | 1 | mutool | OK | 435 |  |  | 354 | 70.5 | True |
| `acc-compensation-cover` | 2 | excise-cli | OK | 472 | 431 | 40 | 509 | 152.8 | True |
| `acc-compensation-cover` | 2 | mutool | OK | 404 |  |  | 357 | 70.5 | True |
| `multilingual-cjk` | 1 | excise-cli | OK | 106 | 67 | 38 | 61 | 44.3 | True |
| `multilingual-cjk` | 1 | mutool | OK | 101 |  |  | 35 | 19.2 | True |
| `multilingual-cjk` | 2 | excise-cli | OK | 105 | 67 | 37 | 71 | 45.7 | True |
| `multilingual-cjk` | 2 | mutool | OK | 74 |  |  | 36 | 19.2 | True |
| `irs-w9-form` | 1 | excise-cli | OK | 153 | 115 | 37 | 115 | 45.9 | True |
| `irs-w9-form` | 1 | mutool | OK | 112 |  |  | 68 | 19.9 | True |
| `irs-w9-form` | 2 | excise-cli | OK | 152 | 114 | 37 | 112 | 46.0 | True |
| `irs-w9-form` | 2 | mutool | OK | 105 |  |  | 75 | 20.3 | True |
| `altona-technical2-p1` | 1 | excise-cli | OK | 2688 | 2598 | 89 | 2498 | 137.7 | True |
| `altona-technical2-p1` | 1 | mutool | OK | 3871 |  |  | 3759 | 450.1 | True |
| `altona-technical2-p1` | 2 | excise-cli | OK | 2663 | 2574 | 87 | 2486 | 138.1 | True |
| `altona-technical2-p1` | 2 | mutool | OK | 3863 |  |  | 3770 | 450.9 | True |
| `altona-technical2-p5` | 1 | excise-cli | OK | 641 | 584 | 55 | 590 | 83.9 | True |
| `altona-technical2-p5` | 1 | mutool | OK | 720 |  |  | 679 | 54.0 | True |
| `altona-technical2-p5` | 2 | excise-cli | OK | 631 | 580 | 49 | 584 | 83.8 | True |
| `altona-technical2-p5` | 2 | mutool | OK | 719 |  |  | 677 | 54.0 | True |
| `altona-technical2-p7` | 1 | excise-cli | OK | 311 | 255 | 54 | 263 | 57.5 | True |
| `altona-technical2-p7` | 1 | mutool | OK | 440 |  |  | 357 | 23.8 | False |
| `altona-technical2-p7` | 2 | excise-cli | OK | 308 | 256 | 49 | 260 | 57.4 | True |
| `altona-technical2-p7` | 2 | mutool | OK | 436 |  |  | 354 | 23.7 | False |
| `scotus-opinion-p1` | 1 | excise-cli | OK | 128 | 83 | 44 | 85 | 43.8 | True |
| `scotus-opinion-p1` | 1 | mutool | OK | 88 |  |  | 50 | 18.6 | True |
| `scotus-opinion-p1` | 2 | excise-cli | OK | 137 | 82 | 54 | 85 | 43.4 | True |
| `scotus-opinion-p1` | 2 | mutool | OK | 98 |  |  | 51 | 18.6 | True |
| `irs-1040-p1` | 1 | excise-cli | OK | 155 | 110 | 44 | 114 | 52.7 | True |
| `irs-1040-p1` | 1 | mutool | OK | 174 |  |  | 87 | 21.2 | True |
| `irs-1040-p1` | 2 | excise-cli | OK | 154 | 110 | 44 | 113 | 52.3 | True |
| `irs-1040-p1` | 2 | mutool | OK | 170 |  |  | 88 | 21.2 | True |
| `indexed-4bit-33mp-image` | 1 | excise-cli | OK | 157 | 114 | 43 | 147 | 47.8 | True |
| `indexed-4bit-33mp-image` | 1 | mutool | OK | 395 |  |  | 359 | 141.4 | True |
| `indexed-4bit-33mp-image` | 2 | excise-cli | OK | 156 | 113 | 43 | 145 | 47.7 | True |
| `indexed-4bit-33mp-image` | 2 | mutool | OK | 406 |  |  | 359 | 141.4 | True |
| `fifty-transparency-groups` | 1 | excise-cli | OK | 425 | 348 | 76 | 364 | 158.5 | True |
| `fifty-transparency-groups` | 1 | mutool | OK | 314 |  |  | 240 | 60.1 | True |
| `fifty-transparency-groups` | 2 | excise-cli | OK | 392 | 344 | 47 | 372 | 158.6 | True |
| `fifty-transparency-groups` | 2 | mutool | OK | 312 |  |  | 236 | 60.1 | True |
| `mesh-shading-type7` | 1 | excise-cli | OK | 339 | 255 | 83 | 253 | 90.0 | True |
| `mesh-shading-type7` | 1 | mutool | OK | 219 |  |  | 182 | 35.6 | True |
| `mesh-shading-type7` | 2 | excise-cli | OK | 336 | 254 | 81 | 245 | 90.0 | True |
| `mesh-shading-type7` | 2 | mutool | OK | 225 |  |  | 185 | 35.5 | True |
| `nested-forms-dct` | 1 | excise-cli | OK | 138 | 92 | 45 | 86 | 56.9 | True |
| `nested-forms-dct` | 1 | mutool | OK | 250 |  |  | 167 | 54.3 | True |
| `nested-forms-dct` | 2 | excise-cli | OK | 137 | 92 | 44 | 97 | 57.0 | True |
| `nested-forms-dct` | 2 | mutool | OK | 214 |  |  | 173 | 54.2 | True |

## By cohort

`render ms` is excise's in-process raster time, reported by the CLI itself (#1387).
`wall ms` additionally includes .NET startup and JIT, which a native reference does not
pay. Ratios are against the MEDIAN oracle, never the fastest.

| Cohort | Fixtures | excise render ms | excise wall ms | oracle wall ms | render/oracle | wall/oracle |
|---|---:|---:|---:|---:|---:|---:|
| tail-colour | 3 | 582 | 636 | 720 | 0.8x | 0.9x |
| tail-flat | 4 | 231 | 289 | 355 | 0.7x | 0.8x |
| tail-scaling | 2 | 173 | 237 | 222 | 0.8x | 1.1x |
| typical | 5 | 110 | 153 | 109 | 1.0x | 1.4x |

## Regression checks

| Check | Actual | Threshold | Result |
|---|---:|---:|---|
| plans-soft-masks-36dpi.excise-cli.rss-ceiling | 51527680 | 100000000 | PASS |
