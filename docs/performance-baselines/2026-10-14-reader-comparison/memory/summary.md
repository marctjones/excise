# Reader benchmark: excise vs Preview vs Chrome vs Adobe Acrobat (#1543)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-20261013/06-memory/run`

Footprint = macOS physical footprint, summed over every process the app owns (main + children + per-app helper services that exit with it). Medians over successful repeats; ± is the min–max spread. MB.

## empty

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 2.0 | 204 ±0 | — | — | 200 ±0 | — | — | 278 | 269 | 0.6 | 0.25 |
| preview | 2/2 | 4.0 | 54 ±0 | — | — | 54 ±0 | — | — | 55 | 21 | 0.8 | 0.03 |
| firefox | 2/2 | 13.0 | 803 ±5 | — | — | 984 ±24 | — | — | 1030 | 522 | 10.2 | 19.29 |
| acrobat | 2/2 | 24.0 | 1014 ±16 | — | — | 979 ±16 | — | — | 1156 | 283 | 6.5 | 1.24 |

## w9

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 2.0 | 326 ±3 | 388 ±13 | 382 ±13 | 388 ±1 | 492 ±1 | 322 ±47 | 579 | 578 | 4.4 | 0.15 |
| preview | 2/2 | 5.0 | 243 ±5 | 196 ±7 | 206 ±7 | 150 ±1 | 150 ±1 | 150 ±1 | 344 | 362 | 11.1 | 0.07 |
| firefox | 2/2 | 14.0 | 984 ±63 | 1118 ±3 | 1221 ±47 | 1231 ±17 | 872 ±17 | 871 ±17 | 1379 | 547 | 11.4 | 0.65 |
| acrobat | 2/2 | 22.5 | 1183 ±0 | 1161 ±3 | 1156 ±4 | 1157 ±3 | 1050 ±4 | 1050 ±4 | 1495 | 547 | 9.5 | 1.36 |

## irs

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 2.0 | 377 ±0 | 609 ±3 | 605 ±3 | 588 ±3 | 597 ±3 | 484 ±1 | 691 | 696 | 7.3 | 0.20 |
| preview | 2/2 | 5.0 | 207 ±1 | 321 ±8 | 293 ±48 | 249 ±10 | 200 ±30 | 200 ±30 | 445 | 441 | 49.1 | 0.10 |
| firefox | 2/2 | 14.0 | 1246 ±11 | 1712 ±154 | 1733 ±1 | 1800 ±23 | 1007 ±21 | 1007 ±21 | 2149 | 520 | 15.2 | 0.86 |
| acrobat | 2/2 | 24.5 | 1230 ±22 | 1281 ±6 | 1272 ±6 | 1381 ±35 | 1239 ±44 | 1236 ±37 | 1497 | 739 | 10.3 | 0.93 |

## altona

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 3.0 | 551 ±100 | 676 ±13 | 672 ±13 | 497 ±0 | 323 ±0 | 323 ±0 | 798 | 825 | 8.0 | 0.18 |
| preview | 2/2 | 5.0 | 221 ±2 | 293 ±2 | 263 ±2 | 227 ±30 | 180 ±8 | 180 ±8 | 359 | 424 | 14.3 | 0.07 |
| firefox | 2/2 | 14.0 | 1657 ±40 | 2423 ±10 | 2597 ±22 | 1733 ±146 | 867 ±48 | 867 ±48 | 2828 | 547 | 15.6 | 0.48 |
| acrobat | 2/2 | 23.5 | 1902 ±105 | 1981 ±15 | 1972 ±15 | 1781 ±52 | 1545 ±81 | 1542 ±87 | 2098 | 1326 | 15.2 | 1.06 |

## scan

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 2.0 | 323 ±0 | 542 ±15 | 538 ±15 | 462 ±1 | 454 ±1 | 252 ±0 | 622 | 629 | 3.7 | 0.12 |
| preview | 2/2 | 5.0 | 212 ±1 | 278 ±4 | 257 ±2 | 176 ±3 | 170 ±3 | 170 ±3 | 389 | 401 | 28.1 | 0.07 |
| firefox | 2/2 | 14.0 | 1003 ±35 | 1835 ±2 | 1974 ±8 | 1649 ±36 | 867 ±16 | 867 ±16 | 2136 | 551 | 11.9 | 0.89 |
| acrobat | 2/2 | 23.5 | 1568 ±1 | 1558 ±4 | 1548 ±4 | 1549 ±4 | 1455 ±10 | 1446 ±15 | 1824 | 943 | 9.5 | 0.98 |

