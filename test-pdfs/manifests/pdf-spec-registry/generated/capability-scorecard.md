# PDF capability scorecard

Implemented/Verified/Unknown (#1346/#1347) are graded from evidence: implemented requires a passing explicit test contract, verified additionally requires an independent-oracle (differential) check among those passing, unknown is everything else — a discovered testCandidate keyword match never earns credit on its own. Strict, evidence progress, and promotion readiness are the review-gated columns the redaction-security work still uses; they are not the headline.

Implementation evidence progress gives capped credit for reviewed state, contracts, passing runs, fixtures, independent evidence, and performance harnesses; it is never a conformance claim.

Workflow scores include only the processor roles that workflow needs; section and category scores retain every required/supported role.

Critical-path benchmark readiness: 100.0% ({'existing-harness': 6}).

| Area | Target modes | Implemented | Verified | Unknown | Strict | Evidence progress | Promotion readiness | Measured | Strict unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| overall | 900 | 95.2% | 7.8% | 43 | 0.6% | 67.8% | 25.0% | 83.4% | 149 |
| annotation-subtypes | 92 | 76.1% | 47.8% | 22 | 0.0% | 62.6% | 10.0% | 100.0% | 0 |
| content | 12 | 100.0% | 8.3% | 0 | 0.0% | 76.7% | 48.3% | 100.0% | 0 |
| document | 21 | 100.0% | 9.5% | 0 | 0.0% | 73.6% | 10.0% | 100.0% | 0 |
| graphics | 33 | 93.9% | 15.2% | 2 | 12.1% | 72.4% | 79.1% | 100.0% | 0 |
| image-requirements | 106 | 100.0% | 3.8% | 0 | 0.0% | 70.4% | 10.0% | 100.0% | 0 |
| interactive | 39 | 69.2% | 15.4% | 12 | 2.6% | 58.8% | 25.1% | 97.4% | 1 |
| interchange | 6 | 66.7% | 33.3% | 2 | 0.0% | 55.0% | 10.0% | 66.7% | 2 |
| multimedia | 0 | — | — | 0 | — | — | — | — | 0 |
| operators | 287 | 100.0% | 0.0% | 0 | 0.0% | 69.1% | 45.0% | 49.1% | 146 |
| optional-profiles | 0 | — | — | 0 | — | — | — | — | 0 |
| product-capabilities | 20 | 80.0% | 0.0% | 4 | 0.0% | 58.0% | 10.0% | 100.0% | 0 |
| renderer-requirements | 259 | 100.0% | 1.2% | 0 | 0.0% | 67.6% | 10.0% | 100.0% | 0 |
| rendering | 10 | 90.0% | 0.0% | 1 | 0.0% | 64.0% | 10.0% | 100.0% | 0 |
| syntax | 14 | 100.0% | 14.3% | 0 | 0.0% | 74.6% | 10.0% | 100.0% | 0 |
| transparency | 1 | 100.0% | 100.0% | 0 | 0.0% | 95.0% | 90.0% | 100.0% | 0 |

## Major categories

| Category | Target modes | Strict | Evidence progress | Promotion readiness | Measured | Planned verification | Executable verification | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| File model | 35 | 0.0% | 74.0% | 10.0% | 100.0% | 100.0% | 0.0% | 0 |
| Page content and rendering | 708 | 0.6% | 69.0% | 28.2% | 79.4% | 100.0% | 37.4% | 146 |
| Interaction and annotations | 131 | 0.8% | 61.5% | 14.5% | 99.2% | 96.6% | 6.9% | 1 |
| Interchange and profiles | 6 | 0.0% | 55.0% | 10.0% | 66.7% | 100.0% | 0.0% | 2 |
| PDFE product capabilities | 20 | 0.0% | 58.0% | 10.0% | 100.0% | 100.0% | 0.0% | 0 |

## Critical workflows

| Workflow | Target modes | Implemented | Promotion readiness | Modes at >=50 | Modes at >=90 | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| forms | 8 | 0.0% | 10.0% | 0 | 0 | 0 |
| redaction | 5 | 20.0% | 84.0% | 5 | 3 | 0 |
| redaction-annotations | 6 | 0.0% | 46.7% | 4 | 0 | 0 |
| rendering | 4 | 50.0% | 95.0% | 4 | 4 | 0 |
| safe-save | 5 | 0.0% | 10.0% | 0 | 0 | 0 |

## Evidence collection

Collected candidates are discovery material, not implementation credit.
All 282 capability leaves have a collection record: {'candidate-evidence': 22, 'registered-evidence': 260}.

## Test and benchmark attribution

Explicit test contracts: 857/900; passing recorded contracts: 857/900; candidate test coverage: 622/900. Benchmark harnesses: 6/6.
