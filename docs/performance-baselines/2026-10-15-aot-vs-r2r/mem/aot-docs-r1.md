# Reader benchmark: excise vs Preview vs Chrome vs Adobe Acrobat (#1543)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/mem/aot-b/r1`

Footprint = macOS physical footprint, summed over every process the app owns (main + children + per-app helper services that exit with it). Medians over successful repeats; ± is the min–max spread. MB.

## w9

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 234 | 292 | 287 | 278 | 380 | 252 | 459 | 466 | 1.8 | 0.10 |

## irs

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 290 | 493 | 489 | 471 | 479 | 376 | 579 | 578 | 3.6 | 0.10 |

## altona

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 397 | 501 | 496 | 222 | 201 | 201 | 655 | 663 | 3.8 | 0.10 |

## scan

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 244 | 445 | 441 | 429 | 421 | 421 | 531 | 522 | 1.9 | 0.10 |

