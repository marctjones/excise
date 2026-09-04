# PDF capability scorecard

Unknown modes receive no credit. Security gates are non-compensating.

Implementation evidence progress gives capped credit for reviewed state, contracts, passing runs, fixtures, independent evidence, and performance harnesses; it is never a conformance claim.

Workflow scores include only the processor roles that workflow needs; section and category scores retain every required/supported role.

Critical-path benchmark readiness: 100.0% ({'existing-harness': 6}).

| Area | Target modes | Strict | Evidence progress | Promotion readiness | Measured | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| overall | 964 | 0.5% | 23.2% | 16.5% | 3.6% | 929 |
| annotation-subtypes | 112 | 0.0% | 29.3% | 10.0% | 0.0% | 112 |
| content | 18 | 0.0% | 17.8% | 14.4% | 5.6% | 17 |
| document | 22 | 0.0% | 12.3% | 10.0% | 0.0% | 22 |
| graphics | 33 | 12.1% | 25.2% | 23.3% | 15.2% | 28 |
| image-requirements | 108 | 0.0% | 29.2% | 10.0% | 0.0% | 108 |
| interactive | 39 | 2.6% | 27.4% | 23.5% | 43.6% | 22 |
| interchange | 6 | 0.0% | 10.0% | 10.0% | 16.7% | 5 |
| multimedia | 0 | — | — | — | — | 0 |
| operators | 292 | 0.0% | 22.8% | 27.5% | 0.0% | 292 |
| optional-profiles | 0 | — | — | — | — | 0 |
| product-capabilities | 21 | 0.0% | 10.0% | 10.0% | 42.9% | 12 |
| renderer-requirements | 285 | 0.0% | 21.5% | 10.0% | 0.0% | 285 |
| rendering | 10 | 0.0% | 10.0% | 10.0% | 10.0% | 9 |
| syntax | 14 | 0.0% | 12.5% | 10.0% | 0.0% | 14 |
| transparency | 4 | 0.0% | 31.2% | 30.0% | 25.0% | 3 |

## Major categories

| Category | Target modes | Strict | Evidence progress | Promotion readiness | Measured | Planned verification | Executable verification | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| File model | 36 | 0.0% | 12.4% | 10.0% | 0.0% | 0.0% | 0.0% | 36 |
| Page content and rendering | 750 | 0.5% | 23.1% | 17.6% | 1.1% | 97.2% | 36.9% | 742 |
| Interaction and annotations | 151 | 0.7% | 28.8% | 13.5% | 11.3% | 88.2% | 5.9% | 134 |
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

Explicit test contracts: 666/964; passing recorded contracts: 13/964; candidate test coverage: 213/964. Benchmark harnesses: 6/6.
