# Reader speed benchmark: excise vs Preview vs Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-aot/speed/r2r/r3`

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 659 / 4987 | 58 / 73 | 58 / 73 | 58 | 0/20 | 556 | 88 | 43 | 48 | 35 | 2 | 8 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 624 / 3396 | 43 / 83 | 43 / 83 | 43 | 1/16 | 514 | 44 | 54 | 49 | 35 | 0 | 33 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 629 / 3669 | 60 / 101 | 60 / 101 | 60 | 0/19 | 547 | 73 | 64 | 48 | 35 | 1 | 8 |

## Kept with a note (see the soft-check comment in summarize)

- excise altona r1: after turns: expected page 17, read 16

