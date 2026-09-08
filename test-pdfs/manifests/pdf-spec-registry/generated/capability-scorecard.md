# PDF capability scorecard

Implemented/Verified/Unknown (#1346/#1347) are graded from evidence: implemented requires a passing explicit test contract, verified additionally requires an independent-oracle (differential) check among those passing, unknown is everything else — a discovered testCandidate keyword match never earns credit on its own. Strict, evidence progress, and promotion readiness are the review-gated columns the redaction-security work still uses; they are not the headline.

Implementation evidence progress gives capped credit for reviewed state, contracts, passing runs, fixtures, independent evidence, and performance harnesses; it is never a conformance claim.

Workflow scores include only the processor roles that workflow needs; section and category scores retain every required/supported role.

Critical-path benchmark readiness: 100.0% ({'existing-harness': 6}).

| Area | Target modes | Implemented | Verified | Unknown | Strict | Evidence progress | Promotion readiness | Measured | Strict unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| overall | 928 | 70.7% | 1.2% | 272 | 0.5% | 51.9% | 23.4% | 57.4% | 395 |
| annotation-subtypes | 92 | 46.7% | 0.0% | 49 | 0.0% | 37.8% | 10.0% | 46.7% | 49 |
| content | 12 | 100.0% | 8.3% | 0 | 0.0% | 76.7% | 48.3% | 100.0% | 0 |
| document | 22 | 22.7% | 0.0% | 17 | 0.0% | 25.9% | 10.0% | 22.7% | 17 |
| graphics | 33 | 51.5% | 15.2% | 16 | 12.1% | 48.8% | 50.0% | 60.6% | 13 |
| image-requirements | 108 | 43.5% | 0.0% | 61 | 0.0% | 35.4% | 10.0% | 43.5% | 61 |
| interactive | 39 | 20.5% | 10.3% | 31 | 2.6% | 28.5% | 23.5% | 43.6% | 22 |
| interchange | 6 | 0.0% | 0.0% | 6 | 0.0% | 10.0% | 10.0% | 16.7% | 5 |
| multimedia | 0 | — | — | 0 | — | — | — | — | 0 |
| operators | 287 | 100.0% | 0.0% | 0 | 0.0% | 69.1% | 45.0% | 49.1% | 146 |
| optional-profiles | 0 | — | — | 0 | — | — | — | — | 0 |
| product-capabilities | 21 | 0.0% | 0.0% | 21 | 0.0% | 10.0% | 10.0% | 42.9% | 12 |
| renderer-requirements | 280 | 81.8% | 0.0% | 51 | 0.0% | 56.1% | 10.0% | 81.8% | 51 |
| rendering | 10 | 0.0% | 0.0% | 10 | 0.0% | 10.0% | 10.0% | 10.0% | 9 |
| syntax | 14 | 50.0% | 0.0% | 7 | 0.0% | 42.5% | 10.0% | 50.0% | 7 |
| transparency | 4 | 25.0% | 25.0% | 3 | 0.0% | 31.2% | 30.0% | 25.0% | 3 |

## Major categories

| Category | Target modes | Strict | Evidence progress | Promotion readiness | Measured | Planned verification | Executable verification | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| File model | 36 | 0.0% | 32.4% | 10.0% | 33.3% | 87.5% | 0.0% | 24 |
| Page content and rendering | 734 | 0.5% | 57.4% | 26.2% | 61.4% | 98.6% | 37.0% | 283 |
| Interaction and annotations | 131 | 0.8% | 35.0% | 14.0% | 45.8% | 86.2% | 6.9% | 71 |
| Interchange and profiles | 6 | 0.0% | 10.0% | 10.0% | 16.7% | 0.0% | 0.0% | 5 |
| PDFE product capabilities | 21 | 0.0% | 10.0% | 10.0% | 42.9% | 0.0% | 0.0% | 12 |

## Critical workflows

| Workflow | Target modes | Implemented | Promotion readiness | Modes at >=50 | Modes at >=90 | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| forms | 8 | 0.0% | 10.0% | 0 | 0 | 7 |
| redaction | 5 | 20.0% | 84.0% | 5 | 3 | 0 |
| redaction-annotations | 6 | 0.0% | 35.8% | 3 | 0 | 1 |
| rendering | 4 | 50.0% | 95.0% | 4 | 4 | 0 |
| safe-save | 5 | 0.0% | 10.0% | 0 | 0 | 5 |

## Evidence collection

Collected candidates are discovery material, not implementation credit.
All 282 capability leaves have a collection record: {'candidate-evidence': 22, 'registered-evidence': 260}.

## Test and benchmark attribution

Explicit test contracts: 656/928; passing recorded contracts: 656/928; candidate test coverage: 504/928. Benchmark harnesses: 6/6.
