# Reader speed benchmark: excise vs Preview vs Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/speed/r2r/r1`

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 1768 / 8378 | 60 / 75 | 60 / 75 | 60 | 0/20 | 533 | 76 | 63 | 48 | 35 | 3 | 6 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 623 / 3473 | 46 / 100 | 46 / 100 | 46 | 1/16 | 533 | 77 | 43 | 48 | 35 | 0 | 17 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 643 / 3519 | 66 / 77 | 66 / 77 | 66 | 0/19 | 528 | 70 | 66 | 49 | 35 | 1 | 12 |

## Kept with a note (see the soft-check comment in summarize)

- excise altona r1: after turns: expected page 17, read 16

