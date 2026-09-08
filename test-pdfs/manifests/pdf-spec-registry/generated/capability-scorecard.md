# PDF capability scorecard

Implemented/Verified/Unknown (#1346/#1347) are graded from evidence: implemented requires a passing explicit test contract, verified additionally requires an independent-oracle (differential) check among those passing, unknown is everything else — a discovered testCandidate keyword match never earns credit on its own. Strict, evidence progress, and promotion readiness are the review-gated columns the redaction-security work still uses; they are not the headline.

Implementation evidence progress gives capped credit for reviewed state, contracts, passing runs, fixtures, independent evidence, and performance harnesses; it is never a conformance claim.

Workflow scores include only the processor roles that workflow needs; section and category scores retain every required/supported role.

Critical-path benchmark readiness: 100.0% ({'existing-harness': 6}).

| Area | Target modes | Implemented | Verified | Unknown | Strict | Evidence progress | Promotion readiness | Measured | Strict unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| overall | 907 | 76.7% | 1.7% | 211 | 0.6% | 55.8% | 23.9% | 63.2% | 334 |
| annotation-subtypes | 92 | 46.7% | 0.0% | 49 | 0.0% | 37.8% | 10.0% | 46.7% | 49 |
| content | 12 | 100.0% | 8.3% | 0 | 0.0% | 76.7% | 48.3% | 100.0% | 0 |
| document | 22 | 22.7% | 0.0% | 17 | 0.0% | 25.9% | 10.0% | 22.7% | 17 |
| graphics | 33 | 63.6% | 15.2% | 12 | 12.1% | 56.1% | 54.8% | 72.7% | 9 |
| image-requirements | 108 | 43.5% | 0.0% | 61 | 0.0% | 35.4% | 10.0% | 43.5% | 61 |
| interactive | 39 | 33.3% | 10.3% | 26 | 2.6% | 36.5% | 23.5% | 56.4% | 17 |
| interchange | 6 | 33.3% | 16.7% | 4 | 0.0% | 32.5% | 10.0% | 50.0% | 3 |
| multimedia | 0 | — | — | 0 | — | — | — | — | 0 |
| operators | 287 | 100.0% | 0.0% | 0 | 0.0% | 69.1% | 45.0% | 49.1% | 146 |
| optional-profiles | 0 | — | — | 0 | — | — | — | — | 0 |
| product-capabilities | 21 | 0.0% | 0.0% | 21 | 0.0% | 10.0% | 10.0% | 42.9% | 12 |
| renderer-requirements | 261 | 98.1% | 1.1% | 5 | 0.0% | 66.4% | 10.0% | 98.1% | 5 |
| rendering | 10 | 0.0% | 0.0% | 10 | 0.0% | 10.0% | 10.0% | 10.0% | 9 |
| syntax | 14 | 64.3% | 0.0% | 5 | 0.0% | 51.1% | 10.0% | 64.3% | 5 |
| transparency | 2 | 50.0% | 50.0% | 1 | 0.0% | 52.5% | 50.0% | 50.0% | 1 |

## Major categories

| Category | Target modes | Strict | Evidence progress | Promotion readiness | Measured | Planned verification | Executable verification | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| File model | 36 | 0.0% | 35.7% | 10.0% | 38.9% | 100.0% | 0.0% | 22 |
| Page content and rendering | 713 | 0.6% | 61.7% | 26.9% | 67.6% | 98.6% | 37.4% | 231 |
| Interaction and annotations | 131 | 0.8% | 37.4% | 14.0% | 49.6% | 93.1% | 6.9% | 66 |
| Interchange and profiles | 6 | 0.0% | 32.5% | 10.0% | 50.0% | 100.0% | 0.0% | 3 |
| PDFE product capabilities | 21 | 0.0% | 10.0% | 10.0% | 42.9% | 0.0% | 0.0% | 12 |

## Critical workflows

| Workflow | Target modes | Implemented | Promotion readiness | Modes at >=50 | Modes at >=90 | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| forms | 8 | 0.0% | 10.0% | 0 | 0 | 5 |
| redaction | 5 | 20.0% | 84.0% | 5 | 3 | 0 |
| redaction-annotations | 6 | 0.0% | 35.8% | 3 | 0 | 1 |
| rendering | 4 | 50.0% | 95.0% | 4 | 4 | 0 |
| safe-save | 5 | 0.0% | 10.0% | 0 | 0 | 4 |

## Evidence collection

Collected candidates are discovery material, not implementation credit.
All 282 capability leaves have a collection record: {'candidate-evidence': 22, 'registered-evidence': 260}.

## Test and benchmark attribution

Explicit test contracts: 696/907; passing recorded contracts: 696/907; candidate test coverage: 530/907. Benchmark harnesses: 6/6.
