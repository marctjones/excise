# Reader benchmark: excise vs Preview vs Chrome vs Adobe Acrobat (#1543)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/mem/r2r/r2`

Footprint = macOS physical footprint, summed over every process the app owns (main + children + per-app helper services that exit with it). Medians over successful repeats; ± is the min–max spread. MB.

## empty

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 204 | — | — | 200 | — | — | 278 | 269 | 0.6 | 0.27 |

## w9

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 327 | 396 | 392 | 390 | 491 | 295 | 587 | 578 | 4.2 | 0.13 |

## irs

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 375 | 602 | 598 | 582 | 583 | 577 | 685 | 686 | 7.2 | 0.20 |

## altona

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 3 | 564 | 785 | 781 | 335 | 320 | 320 | 907 | 958 | 7.1 | 0.17 |

## scan

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 323 | 541 | 537 | 354 | 346 | 252 | 628 | 622 | 3.7 | 0.13 |

