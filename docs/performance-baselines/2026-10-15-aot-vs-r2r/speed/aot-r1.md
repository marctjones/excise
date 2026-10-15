# Reader speed benchmark: excise vs Preview vs Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/speed/aot/r1`

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 2073 / 5404 | 61 / 80 | 61 / 80 | 61 | 0/20 | 552 | 76 | 70 | 48 | 36 | 4 | 6 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 424 / 3192 | 49 / 64 | 49 / 64 | 49 | 1/16 | 537 | 69 | 57 | 48 | 35 | 0 | 21 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 413 / 3196 | 66 / 76 | 66 / 76 | 66 | 0/19 | 515 | 56 | 67 | 48 | 35 | 2 | 28 |

## Kept with a note (see the soft-check comment in summarize)

- excise altona r1: after turns: expected page 17, read 16

