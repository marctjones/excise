WARNING: no caffeinate running; the screen may lock and block keystrokes
excise build shape: NativeAOT (single native Excise.App (43 MB), no managed Excise.Core.dll, bundle 63.6 MB) [/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot-fix/excise.app]
==> 15 runs -> /Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseB
  excise   irs     r1  OK
  excise   altona  r1  FAIL after turns: expected page 17, read 16
  excise   scan    r1  OK
  excise   irs     r2  OK
  excise   altona  r2  FAIL after turns: expected page 17, read 16
  excise   scan    r2  OK
  excise   irs     r3  OK
  excise   altona  r3  FAIL after turns: expected page 17, read 16
  excise   scan    r3  OK
  excise   irs     r4  OK
  excise   altona  r4  FAIL after turns: expected page 17, read 16
  excise   scan    r4  OK
  excise   irs     r5  OK
  excise   altona  r5  FAIL after turns: expected page 17, read 16
  excise   scan    r5  OK
# Reader speed benchmark: excise vs Preview, Chrome, Firefox (pdf.js) and Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseB`

**Excise build shape: NativeAOT** (single native Excise.App (43 MB), no managed Excise.Core.dll, bundle 63.6 MB); bundle `/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot-fix/excise.app`. Numbers from different build shapes are not comparable.

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| excise | 5 | 338 / 2577 | 41 / 53 | 41 / 53 | 41 | 0/100 | 519 | 67 | 61 | — / — | — / — | 50 | 31 | 0 | 5 |

## altona

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| excise | 5 | 325 / 2072 | 39 / 47 | 39 / 47 | 39 | 5/80 | 523 | 37 | 35 | — / — | — / — | 50 | 31 | 0 | 7 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| excise | 5 | 306 / 2062 | 42 / 53 | 42 / 53 | 42 | 0/95 | 521 | 55 | 53 | — / — | — / — | 50 | 31 | 0 | 5 |

## Kept with a note (see the soft-check comment in summarize)

- excise altona r1: after turns: expected page 17, read 16
- excise altona r2: after turns: expected page 17, read 16
- excise altona r3: after turns: expected page 17, read 16
- excise altona r4: after turns: expected page 17, read 16
- excise altona r5: after turns: expected page 17, read 16

