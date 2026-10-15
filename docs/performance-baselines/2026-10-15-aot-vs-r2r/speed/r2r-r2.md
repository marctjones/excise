# Reader speed benchmark: excise vs Preview vs Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/speed/r2r/r2`

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 727 / 4924 | 65 / 107 | 65 / 107 | 65 | 0/20 | 544 | 88 | 69 | 48 | 35 | 1 | 16 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 614 / 3401 | 50 / 79 | 50 / 79 | 50 | 1/16 | 529 | 63 | 62 | 49 | 35 | 0 | 14 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 625 / 3378 | 63 / 83 | 63 / 83 | 63 | 0/19 | 526 | 69 | 64 | 48 | 35 | 2 | 7 |

## Kept with a note (see the soft-check comment in summarize)

- excise altona r1: after turns: expected page 17, read 16

