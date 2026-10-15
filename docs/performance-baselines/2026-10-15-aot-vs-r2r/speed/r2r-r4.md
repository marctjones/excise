# Reader speed benchmark: excise vs Preview vs Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/speed/r2r/r4`

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 676 / 4892 | 60 / 74 | 60 / 74 | 60 | 0/20 | 542 | 94 | 62 | 48 | 35 | 3 | 17 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 611 / 3434 | 49 / 84 | 49 / 84 | 49 | 1/16 | 537 | 67 | 52 | 50 | 35 | 0 | 0 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 639 / 3573 | 64 / 86 | 64 / 86 | 64 | 0/19 | 515 | 68 | 61 | 48 | 36 | 3 | 8 |

## Kept with a note (see the soft-check comment in summarize)

- excise altona r1: after turns: expected page 17, read 16

