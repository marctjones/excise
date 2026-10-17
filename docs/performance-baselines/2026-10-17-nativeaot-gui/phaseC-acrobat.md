==> 9 runs -> /Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseC-acrobat
  acrobat  irs     r1  OK
  acrobat  altona  r1  OK
  acrobat  scan    r1  OK
  acrobat  irs     r2  OK
  acrobat  altona  r2  OK
  acrobat  scan    r2  OK
  acrobat  irs     r3  OK
  acrobat  altona  r3  OK
  acrobat  scan    r3  OK
# Reader speed benchmark: excise vs Preview, Chrome, Firefox (pdf.js) and Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseC-acrobat`

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| acrobat | 3 | 738 / 8616 | 39 / 50 | 39 / 50 | 39 | 0/60 | 531 | 76 | 65 | 64 / 457 | 89 / 475 | 44 | 35 | 6 | 29 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| acrobat | 3 | 817 / 8766 | 41 / 190 | 41 / 543 | 41 | 0/48 | 520 | 56 | 69 | 143 / 143 | 66 / 69 | 4 | 568 | 1 | 2656 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| acrobat | 3 | 667 / 19793 | 37 / 46 | 37 / 46 | 37 | 0/57 | 521 | 66 | 76 | 100 / 100 | 101 / 101 | 29 | 33 | 1 | 2550 |

