excise build shape: NativeAOT (single native Excise.App (43 MB), no managed Excise.Core.dll, bundle 63.7 MB) [/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot-head/excise.app]
==> 10 runs -> /Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseB-noenv
  excise   irs     r1  OK
  excise   scan    r1  OK
  excise   irs     r2  OK
  excise   scan    r2  OK
  excise   irs     r3  OK
  excise   scan    r3  OK
  excise   irs     r4  OK
  excise   scan    r4  OK
  excise   irs     r5  OK
  excise   scan    r5  OK
# Reader speed benchmark: excise vs Preview, Chrome, Firefox (pdf.js) and Adobe Acrobat (#1544)

Run: `/Users/marc/Documents/GitHub/pdfe/logs/gui-night/phaseB-noenv`

**Excise build shape: NativeAOT** (single native Excise.App (43 MB), no managed Excise.Core.dll, bundle 63.7 MB); bundle `/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot-head/excise.app`. Numbers from different build shapes are not comparable.

Times in ms from the input event, over the app's page area. Median across repeats (page turns: median and p95 across all turns). `drawn` = the first frame from which the page area stays final; speed index = area under (1 - visual completeness). Launch `drawn` covers the WHOLE window (sidebars and thumbnails filling in), because it is taken before the window is resized. Timing floor: one 60 Hz frame (~17 ms).

## irs

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| excise | 5 | 352 / 2579 | 41 / 50 | 41 / 50 | 41 | 0/100 | 528 | 64 | 56 | — / — | — / — | 50 | 31 | 0 | 15 |

## scan

| app | runs | launch→window / drawn | turn first change p50 / p95 | turn drawn p50 / p95 | turn speed index p50 | no-response turns | burst drawn | far jump (End) drawn | revisit (Home) drawn | zoom in first change / drawn | zoom out first change / drawn | scroll fps | scroll gap p95 | hitches | scroll settle |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|
| excise | 5 | 307 / 2062 | 40 / 54 | 40 / 54 | 40 | 0/95 | 527 | 57 | 52 | — / — | — / — | 50 | 31 | 0 | 10 |

