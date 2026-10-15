# Reader benchmark: excise vs Preview vs Chrome vs Adobe Acrobat (#1543)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/mem/aot-b/r3`

Footprint = macOS physical footprint, summed over every process the app owns (main + children + per-app helper services that exit with it). Medians over successful repeats; ± is the min–max spread. MB.

## w9

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 230 | 279 | 274 | 278 | 377 | 252 | 456 | 466 | 1.8 | 0.10 |

## irs

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 285 | 496 | 492 | 473 | 482 | 379 | 581 | 582 | 3.6 | 0.13 |

## altona

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 401 | 507 | 503 | 223 | 202 | 201 | 655 | 662 | 3.8 | 0.10 |

## scan

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1/1 | 2 | 242 | 444 | 440 | 431 | 422 | 422 | 516 | 523 | 1.8 | 0.07 |

