# Reader benchmark: excise vs Preview vs Chrome vs Adobe Acrobat (#1543)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/mem/aot-b/r2`

Footprint = macOS physical footprint, summed over every process the app owns (main + children + per-app helper services that exit with it). Medians over successful repeats; ± is the min–max spread. MB.

## w9

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 233 | 292 | 287 | 278 | 380 | 249 | 461 | 466 | 1.8 | 0.10 |

## irs

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 305 | 496 | 492 | 473 | 488 | 378 | 578 | 581 | 3.5 | 0.10 |

## altona

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 448 | 511 | 507 | 221 | 200 | 200 | 620 | 656 | 3.7 | 0.10 |

## scan

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 243 | 447 | 443 | 437 | 428 | 428 | 521 | 534 | 1.8 | 0.10 |

