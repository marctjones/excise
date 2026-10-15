# Reader speed benchmark: excise vs Preview vs Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/perf-after/05-speed/run`

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 1 | 687 / 2885 | 65 / 82 | 65 / 82 | 65 | 0/20 | 509 | 73 | 75 | 49 | 33 | 1 | 20 |
| preview | 2 | 611 / 1483 | 41 / 70 | 43 / 70 | 41 | 0/40 | 598 | 43 | 633 | 42 | 48 | 18 | 21 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2 | 617 / 3190 | 40 / 79 | 40 / 79 | 40 | 2/32 | 513 | 59 | 63 | 49 | 33 | 0 | 3 |
| preview | 2 | 705 / 1587 | 39 / 75 | 39 / 274 | 41 | 4/32 | 959 | 612 | 262 | 42 | 48 | 22 | 1 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| excise | 2 | 623 / 1363 | 63 / 77 | 63 / 77 | 63 | 0/38 | 520 | 72 | 68 | 49 | 33 | 0 | 22 |
| preview | 2 | 528 / 1434 | 41 / 69 | 83 / 101 | 43 | 0/38 | 577 | 123 | 73 | 42 | 49 | 22 | 16 |

## Kept with a note (see the soft-check comment in summarize)

- excise altona r1: after turns: expected page 17, read 16
- excise altona r2: after turns: expected page 17, read 16

## Failed runs (excluded)

- excise irs r1: aborted: osascript failed: 701:863: execution error: System Events got an error: Can’t get item 1 of every window of process 1 whose unix id = 10469. Invalid index. (-1719); did not quit; killed
