# Reader benchmark: excise vs Preview vs Chrome vs Adobe Acrobat (#1543)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/mem/r2r/r3`

Footprint = macOS physical footprint, summed over every process the app owns (main + children + per-app helper services that exit with it). Medians over successful repeats; ± is the min–max spread. MB.

## empty

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 204 | — | — | 200 | — | — | 278 | 269 | 0.6 | 0.27 |

## w9

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 328 | 387 | 383 | 390 | 492 | 300 | 570 | 578 | 4.2 | 0.13 |

## irs

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 375 | 601 | 597 | 582 | 591 | 484 | 685 | 690 | 7.2 | 0.20 |

## altona

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 562 | 843 | 839 | 336 | 321 | 321 | 928 | 958 | 7.3 | 0.17 |

## scan

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 324 | 526 | 522 | 353 | 344 | 250 | 612 | 621 | 3.7 | 0.13 |

