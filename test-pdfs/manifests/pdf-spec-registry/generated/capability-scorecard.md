# PDF capability scorecard

Implemented/Verified/Unknown (#1346/#1347) are graded from evidence: implemented requires a passing explicit test contract, verified additionally requires an independent-oracle (differential) check among those passing, unknown is everything else — a discovered testCandidate keyword match never earns credit on its own. Strict, evidence progress, and promotion readiness are the review-gated columns the redaction-security work still uses; they are not the headline.

Implementation evidence progress gives capped credit for reviewed state, contracts, passing runs, fixtures, independent evidence, and performance harnesses; it is never a conformance claim.

Workflow scores include only the processor roles that workflow needs; section and category scores retain every required/supported role.

Critical-path benchmark readiness: 100.0% ({'existing-harness': 6}).

| Area | Target modes | Implemented | Verified | Unknown | Strict | Evidence progress | Promotion readiness | Measured | Strict unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| overall | 944 | 3.8% | 1.2% | 908 | 0.5% | 14.6% | 16.6% | 7.4% | 874 |
| annotation-subtypes | 92 | 25.0% | 0.0% | 69 | 0.0% | 24.7% | 10.0% | 25.0% | 69 |
| content | 18 | 5.6% | 5.6% | 17 | 0.0% | 17.8% | 14.4% | 5.6% | 17 |
| document | 22 | 0.0% | 0.0% | 22 | 0.0% | 12.3% | 10.0% | 0.0% | 22 |
| graphics | 33 | 15.2% | 15.2% | 28 | 12.1% | 32.4% | 23.3% | 51.5% | 16 |
| image-requirements | 108 | 0.0% | 0.0% | 108 | 0.0% | 9.2% | 10.0% | 0.0% | 108 |
| interactive | 39 | 15.4% | 10.3% | 33 | 2.6% | 27.4% | 23.5% | 43.6% | 22 |
| interchange | 6 | 0.0% | 0.0% | 6 | 0.0% | 10.0% | 10.0% | 16.7% | 5 |
| multimedia | 0 | — | — | 0 | — | — | — | — | 0 |
| operators | 292 | 0.0% | 0.0% | 292 | 0.0% | 22.8% | 27.5% | 0.0% | 292 |
| optional-profiles | 0 | — | — | 0 | — | — | — | — | 0 |
| product-capabilities | 21 | 0.0% | 0.0% | 21 | 0.0% | 10.0% | 10.0% | 42.9% | 12 |
| renderer-requirements | 285 | 0.0% | 0.0% | 285 | 0.0% | 1.6% | 10.0% | 0.0% | 285 |
| rendering | 10 | 0.0% | 0.0% | 10 | 0.0% | 10.0% | 10.0% | 10.0% | 9 |
| syntax | 14 | 0.0% | 0.0% | 14 | 0.0% | 12.5% | 10.0% | 0.0% | 14 |
| transparency | 4 | 25.0% | 25.0% | 3 | 0.0% | 31.2% | 30.0% | 25.0% | 3 |

## Major categories

| Category | Target modes | Strict | Evidence progress | Promotion readiness | Measured | Planned verification | Executable verification | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| File model | 36 | 0.0% | 12.4% | 10.0% | 0.0% | 0.0% | 0.0% | 36 |
| Page content and rendering | 750 | 0.5% | 13.0% | 17.6% | 2.7% | 97.2% | 36.9% | 730 |
| Interaction and annotations | 131 | 0.8% | 25.5% | 14.0% | 30.5% | 86.2% | 6.9% | 91 |
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

Explicit test contracts: 184/944; passing recorded contracts: 36/944; candidate test coverage: 236/944. Benchmark harnesses: 6/6.
