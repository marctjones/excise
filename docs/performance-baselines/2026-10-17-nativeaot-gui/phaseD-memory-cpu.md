# Reader benchmark: excise vs Preview vs Chrome vs Adobe Acrobat (#1543)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseD`

**Excise build shape: NativeAOT** (single native Excise.App (43 MB), no managed Excise.Core.dll, bundle 63.6 MB); bundle `/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot-fix/excise.app`. Numbers from different build shapes are not comparable.

Footprint = macOS physical footprint, summed over every process the app owns (main + children + per-app helper services that exit with it). Medians over successful repeats; ± is the min–max spread. MB.

## irs

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 3.0 | 245 ±2 | 311 ±3 | 307 ±3 | 200 ±1 | 197 ±1 | 180 ±0 | 523 | 514 | 3.8 | 0.15 |
| preview | 2/2 | 7.0 | 295 ±0 | 320 ±2 | 331 ±0 | 267 ±24 | 171 ±2 | 171 ±2 | 523 | 535 | 24.1 | 0.07 |
| chrome | 2/2 | 28.5 | 853 ±48 | 872 ±17 | 608 ±8 | 617 ±7 | 228 ±5 | 205 ±5 | 1134 | 117 | 7.3 | 1.94 |
| acrobat | 0/2 | — | — | — | — | — | — | — | — | — | — | — |

## altona

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 3.0 | 447 ±36 | 493 ±9 | 489 ±9 | 186 ±1 | 169 ±8 | 165 ±0 | 610 | 643 | 3.8 | 0.10 |
| preview | 2/2 | 7.0 | 243 ±4 | 313 ±2 | 316 ±2 | 241 ±20 | 179 ±17 | 179 ±17 | 443 | 487 | 14.7 | 0.07 |
| chrome | 2/2 | 29.0 | 1236 ±24 | 1128 ±25 | 860 ±1 | 831 ±1 | 233 ±1 | 209 ±1 | 1498 | 135 | 13.0 | 2.70 |
| acrobat | 0/2 | — | — | — | — | — | — | — | — | — | — | — |

## scan

| app | runs ok | procs | opened | paged-30 | idle-20s-after-paging | idle-30s | closed-idle-20s | closed-idle-45s | peak (all) | peak (main) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2/2 | 3.5 | 202 ±14 | 371 ±1 | 366 ±1 | 159 ±1 | 147 ±14 | 147 ±14 | 451 | 470 | 2.0 | 0.08 |
| preview | 2/2 | 7.0 | 210 ±1 | 310 ±2 | 319 ±3 | 244 ±4 | 210 ±4 | 210 ±4 | 471 | 529 | 13.6 | 0.07 |
| chrome | 2/2 | 29.0 | 833 ±18 | 876 ±19 | 602 ±27 | 572 ±5 | 232 ±5 | 208 ±5 | 1140 | 119 | 8.7 | 3.30 |
| acrobat | 0/2 | — | — | — | — | — | — | — | — | — | — | — |

## Failed runs (excluded from the medians)

- acrobat altona r1: did not quit within 20 s; killed
- acrobat altona r2: did not quit within 20 s; killed
- acrobat irs r1: did not quit within 20 s; killed
- acrobat irs r2: did not quit within 20 s; killed
- acrobat scan r1: did not quit within 20 s; killed
- acrobat scan r2: did not quit within 20 s; killed

