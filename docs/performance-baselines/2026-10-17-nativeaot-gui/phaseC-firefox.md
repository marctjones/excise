==> 9 runs -> /Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseC-firefox
  firefox  irs     r1  OK
  firefox  altona  r1  OK
  firefox  scan    r1  OK
  firefox  irs     r2  OK
  firefox  altona  r2  OK
  firefox  scan    r2  OK
  firefox  irs     r3  OK
  firefox  altona  r3  OK
  firefox  scan    r3  OK
# Reader speed benchmark: excise vs Preview, Chrome, Firefox (pdf.js) and Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseC-firefox`

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| firefox | 3 | 840 / 9794 | 33 / 43 | 33 / 43 | 33 | 0/60 | — | 64 | 36 | 55 / 516 | 47 / 50 | 47 | 35 | 0 | 14 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| firefox | 3 | 839 / 9792 | 28 / 38 | 607 / 621 | 37 | 0/48 | — | 543 | 562 | 40 / 583 | 37 / 581 | 48 | 35 | 0 | 562 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| firefox | 3 | 858 / 10146 | 34 / 50 | 34 / 50 | 34 | 0/57 | — | 48 | 42 | 48 / 48 | 48 / 48 | 47 | 35 | 0 | 24 |

