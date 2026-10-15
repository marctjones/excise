# Reader benchmark: excise vs Preview vs Chrome vs Adobe Acrobat (#1543)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-after/06-memory/run`

Footprint = macOS physical footprint, summed over every process the app owns (main + children + per-app helper services that exit with it). Medians over successful repeats; ± is the min–max spread. MB.

## empty

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 2.0 | 203 ±0 | — | — | 200 ±1 | — | — | 278 | 269 | 0.6 | 0.25 |
| preview | 2/2 | 4.0 | 54 ±1 | — | — | 54 ±1 | — | — | 55 | 21 | 0.8 | 0.02 |

## w9

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 2.0 | 326 ±0 | 396 ±1 | 392 ±1 | 388 ±0 | 486 ±5 | 348 ±1 | 573 | 573 | 4.2 | 0.15 |
| preview | 2/2 | 5.0 | 243 ±1 | 190 ±5 | 205 ±5 | 151 ±0 | 150 ±0 | 150 ±0 | 349 | 360 | 11.2 | 0.07 |

## irs

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 2.0 | 376 ±1 | 601 ±1 | 597 ±1 | 582 ±2 | 588 ±6 | 414 ±130 | 683 | 688 | 7.3 | 0.22 |
| preview | 2/2 | 5.0 | 211 ±7 | 330 ±7 | 330 ±7 | 242 ±3 | 179 ±18 | 179 ±18 | 448 | 447 | 48.6 | 0.10 |

## altona

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 2.0 | 534 ±92 | 702 ±115 | 698 ±115 | 493 ±4 | 320 ±4 | 320 ±3 | 801 | 825 | 6.9 | 0.18 |
| preview | 2/2 | 5.0 | 221 ±5 | 297 ±9 | 264 ±4 | 248 ±5 | 178 ±8 | 178 ±8 | 369 | 425 | 14.2 | 0.07 |

## scan

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 2.0 | 323 ±0 | 534 ±14 | 530 ±14 | 349 ±8 | 341 ±8 | 249 ±6 | 620 | 620 | 3.7 | 0.13 |
| preview | 2/2 | 5.0 | 212 ±0 | 270 ±8 | 251 ±6 | 180 ±3 | 174 ±3 | 174 ±3 | 394 | 409 | 27.8 | 0.08 |

