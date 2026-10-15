# Reader speed benchmark: excise vs Preview vs Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-20261013/05-speed/run`

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2 | 839 / 3052 | 56 / 91 | 56 / 91 | 56 | 0/40 | 535 | 97 | 57 | 48 | 35 | 0 | 24 |
| preview | 2 | 647 / 1539 | 25 / 55 | 36 / 55 | 26 | 12/40 | 604 | 35 | 667 | 24 | 35 | 0 | 12 |
| firefox | 2 | 1036 / 9631 | 31 / 50 | 33 / 214 | 32 | 5/40 | — | 56 | 33 | 24 | 35 | 1 | 26 |
| acrobat | 1 | 747 / 11538 | 28 / 43 | 28 / 43 | 30 | 0/20 | 522 | 65 | 66 | 47 | 36 | 3 | 19 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2 | 706 / 2500 | 56 / 85 | 56 / 85 | 56 | 2/32 | 524 | 66 | 38 | 24 | 35 | 0 | 7 |
| preview | 2 | 731 / 1627 | 30 / 55 | 30 / 271 | 32 | 12/32 | 984 | 604 | 260 | 47 | 35 | 0 | 42 |
| firefox | 2 | 887 / 9536 | 22 / 46 | 607 / 1504 | 31 | 14/32 | 2152 | 562 | 563 | 49 | 35 | 0 | 566 |
| acrobat | 2 | 1023 / 9602 | 45 / 187 | 45 / 446 | 45 | 0/32 | 519 | 51 | 54 | 8 | 278 | 2 | 201 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2 | 704 / 2438 | 56 / 79 | 56 / 79 | 56 | 0/38 | 522 | 44 | 55 | 48 | 36 | 0 | 8 |
| preview | 2 | 557 / 3212 | 38 / 61 | 78 / 99 | 41 | 0/38 | 556 | 120 | 69 | 49 | 35 | 1 | 13 |
| firefox | 2 | 929 / 9985 | 26 / 38 | 27 / 380 | 26 | 0/38 | — | 28 | 24 | 24 | 35 | 0 | 10 |
| acrobat | 2 | 765 / 18769 | 30 / 46 | 30 / 46 | 30 | 0/38 | 513 | 52 | 44 | 33 | 34 | 1 | 193 |

## Kept with a note (see the soft-check comment in summarize)

- excise altona r1: after turns: expected page 17, read 16
- excise altona r2: after turns: expected page 17, read 16
- firefox altona r1: after turns: expected page 17, read 3
- firefox irs r2: after turns: expected page 21, read 16
- preview irs r2: after turns: expected page 21, read 9

## Failed runs (excluded)

- acrobat irs r1: after turns: expected page 21, read 1; did not quit; killed
