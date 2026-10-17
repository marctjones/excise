excise build shape: NativeAOT (single native Excise.App (43 MB), no managed Excise.Core.dll, bundle 63.6 MB) [/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot-fix/excise.app]
==> 45 runs -> /Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseC
  excise   irs     r1  OK
  preview  irs     r1  OK
  chrome   irs     r1  OK
  firefox  irs     r1  FAIL after turns: expected page 21, read None
  acrobat  irs     r1  FAIL did not quit; killed
  excise   altona  r1  FAIL after turns: expected page 17, read 16
  preview  altona  r1  OK
  chrome   altona  r1  OK
  firefox  altona  r1  FAIL after turns: expected page 17, read None
  acrobat  altona  r1  FAIL did not quit; killed
  excise   scan    r1  OK
  preview  scan    r1  OK
  chrome   scan    r1  OK
  firefox  scan    r1  FAIL after turns: expected page 20, read None
  acrobat  scan    r1  FAIL did not quit; killed
  excise   irs     r2  OK
  preview  irs     r2  OK
  chrome   irs     r2  OK
  firefox  irs     r2  FAIL after turns: expected page 21, read None
  acrobat  irs     r2  FAIL did not quit; killed
  excise   altona  r2  FAIL after turns: expected page 17, read 16
  preview  altona  r2  OK
  chrome   altona  r2  OK
  firefox  altona  r2  FAIL after turns: expected page 17, read None
  acrobat  altona  r2  FAIL did not quit; killed
  excise   scan    r2  OK
  preview  scan    r2  OK
  chrome   scan    r2  OK
  firefox  scan    r2  FAIL after turns: expected page 20, read None
  acrobat  scan    r2  FAIL did not quit; killed
  excise   irs     r3  OK
  preview  irs     r3  OK
  chrome   irs     r3  OK
  firefox  irs     r3  FAIL after turns: expected page 21, read None
  acrobat  irs     r3  FAIL did not quit; killed
  excise   altona  r3  FAIL after turns: expected page 17, read 16
  preview  altona  r3  OK
  chrome   altona  r3  OK
  firefox  altona  r3  FAIL after turns: expected page 17, read None
  acrobat  altona  r3  FAIL did not quit; killed
  excise   scan    r3  OK
  preview  scan    r3  OK
  chrome   scan    r3  OK
  firefox  scan    r3  FAIL after turns: expected page 20, read None
  acrobat  scan    r3  FAIL did not quit; killed
# Reader speed benchmark: excise vs Preview, Chrome, Firefox (pdf.js) and Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseC`

**Excise build shape: NativeAOT** (single native Excise.App (43 MB), no managed Excise.Core.dll, bundle 63.6 MB); bundle `/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot-fix/excise.app`. Numbers from different build shapes are not comparable.

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| excise | 3 | 409 / 2644 | 72 / 80 | 72 / 80 | 72 | 0/60 | 534 | 102 | 79 | 67 / 100 | 68 / 68 | 50 | 33 | 0 | 20 |
| preview | 3 | 581 / 1447 | 42 / 72 | 44 / 623 | 43 | 0/60 | 582 | 41 | 718 | 60 / 232 | 53 / 231 | 41 | 49 | 25 | 1 |
| chrome | 3 | 506 / 940 | 61 / 80 | 61 / 80 | 61 | 0/60 | 526 | 222 | 224 | 76 / 76 | 69 / 69 | 49 | 32 | 3 | 53 |
| firefox | 3 | 821 / 10027 | — / — | — / — | — | 60/60 | — | — | — | — / — | 50 / 265 | 0 | — | — | — |
| acrobat | 3 | 861 / 8756 | 35 / 53 | 35 / 53 | 35 | 0/60 | 517 | 69 | 67 | 148 / 150 | — / — | 44 | 35 | 5 | 19 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| excise | 3 | 397 / 2148 | 53 / 70 | 53 / 70 | 53 | 3/48 | 525 | 68 | 67 | 58 / 400 | 71 / 71 | 50 | 32 | 0 | 10 |
| preview | 3 | 736 / 1891 | 39 / 66 | 39 / 66 | 40 | 6/48 | 929 | 471 | 476 | 60 / 260 | 50 / 731 | 42 | 49 | 23 | 34 |
| chrome | 3 | 514 / 5153 | 280 / 639 | 280 / 639 | 280 | 18/48 | 1593 | 774 | 877 | 172 / 172 | 174 / 174 | 51 | 32 | 1 | 253 |
| firefox | 3 | 820 / 9841 | — / — | — / — | — | 48/48 | — | — | — | — / — | — / — | 0 | — | — | — |
| acrobat | 3 | 811 / 8742 | 37 / 188 | 37 / 437 | 37 | 0/48 | 514 | 73 | 70 | 149 / 149 | 60 / 60 | 4 | 553 | 1 | 210 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| excise | 3 | 389 / 2139 | 72 / 80 | 72 / 80 | 72 | 0/57 | 531 | 72 | 76 | 65 / 100 | 66 / 66 | 49 | 33 | 2 | 26 |
| preview | 3 | 517 / 1418 | 40 / 59 | 94 / 109 | 44 | 0/57 | 579 | 131 | 77 | 54 / 231 | 50 / 225 | 41 | 49 | 22 | 13 |
| chrome | 3 | 492 / 1019 | 74 / 84 | 74 / 84 | 74 | 0/57 | 543 | 226 | 224 | 100 / 100 | 92 / 92 | 55 | 20 | 2 | 46 |
| firefox | 3 | 824 / 9908 | — / — | — / — | — | 57/57 | — | — | — | — / — | — / — | 0 | — | — | — |
| acrobat | 3 | 652 / 19725 | 35 / 59 | 35 / 59 | 35 | 0/57 | 522 | 62 | 60 | 119 / 119 | — / — | 29 | 34 | 1 | 196 |

## Kept with a note (see the soft-check comment in summarize)

- acrobat altona r1: did not quit; killed
- acrobat altona r2: did not quit; killed
- acrobat altona r3: did not quit; killed
- acrobat irs r1: did not quit; killed
- acrobat irs r2: did not quit; killed
- acrobat irs r3: did not quit; killed
- acrobat scan r1: did not quit; killed
- acrobat scan r2: did not quit; killed
- acrobat scan r3: did not quit; killed
- excise altona r1: after turns: expected page 17, read 16
- excise altona r2: after turns: expected page 17, read 16
- excise altona r3: after turns: expected page 17, read 16
- firefox altona r1: after turns: expected page 17, read None
- firefox altona r2: after turns: expected page 17, read None
- firefox altona r3: after turns: expected page 17, read None
- firefox irs r1: after turns: expected page 21, read None
- firefox irs r2: after turns: expected page 21, read None
- firefox irs r3: after turns: expected page 21, read None
- firefox scan r1: after turns: expected page 20, read None
- firefox scan r2: after turns: expected page 20, read None
- firefox scan r3: after turns: expected page 20, read None

