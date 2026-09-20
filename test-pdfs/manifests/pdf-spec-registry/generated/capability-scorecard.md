# PDF capability scorecard

Implemented/Verified/Unknown (#1346/#1347) are graded from evidence: implemented requires a passing explicit test contract, verified additionally requires an independent-oracle (differential) check among those passing, unknown is everything else — a discovered testCandidate keyword match never earns credit on its own. Strict, evidence progress, and promotion readiness are the review-gated columns the redaction-security work still uses; they are not the headline.

Implementation evidence progress gives capped credit for reviewed state, contracts, passing runs, fixtures, independent evidence, and performance harnesses; it is never a conformance claim.

Workflow scores include only the processor roles that workflow needs; section and category scores retain every required/supported role.

Critical-path benchmark readiness: 100.0% ({'existing-harness': 6}).

| Area | Target modes | Implemented | Verified | Unknown | Strict | Evidence progress | Promotion readiness | Measured | Strict unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| overall | 899 | 97.8% | 84.6% | 20 | 0.6% | 81.1% | 33.2% | 83.4% | 149 |
| annotation-subtypes | 90 | 100.0% | 97.8% | 0 | 0.0% | 84.6% | 10.0% | 100.0% | 0 |
| content | 12 | 100.0% | 100.0% | 0 | 0.0% | 90.4% | 48.3% | 100.0% | 0 |
| document | 21 | 100.0% | 95.2% | 0 | 0.0% | 86.4% | 10.0% | 100.0% | 0 |
| graphics | 33 | 93.9% | 72.7% | 2 | 12.1% | 81.1% | 79.1% | 100.0% | 0 |
| image-requirements | 106 | 99.1% | 63.2% | 1 | 0.0% | 79.2% | 10.0% | 100.0% | 0 |
| interactive | 39 | 74.4% | 56.4% | 10 | 2.6% | 65.6% | 29.0% | 97.4% | 1 |
| interchange | 6 | 66.7% | 33.3% | 2 | 0.0% | 55.0% | 10.0% | 66.7% | 2 |
| multimedia | 0 | — | — | 0 | — | — | — | — | 0 |
| operators | 287 | 100.0% | 100.0% | 0 | 0.0% | 84.2% | 69.9% | 49.1% | 146 |
| optional-profiles | 0 | — | — | 0 | — | — | — | — | 0 |
| product-capabilities | 21 | 81.0% | 0.0% | 4 | 0.0% | 62.4% | 17.6% | 100.0% | 0 |
| renderer-requirements | 259 | 100.0% | 86.1% | 0 | 0.0% | 81.1% | 10.0% | 100.0% | 0 |
| rendering | 10 | 90.0% | 10.0% | 1 | 0.0% | 65.5% | 10.0% | 100.0% | 0 |
| syntax | 14 | 100.0% | 100.0% | 0 | 0.0% | 86.1% | 10.0% | 100.0% | 0 |
| transparency | 1 | 100.0% | 100.0% | 0 | 0.0% | 95.0% | 90.0% | 100.0% | 0 |

## Major categories

| Category | Target modes | Strict | Evidence progress | Promotion readiness | Measured | Planned verification | Executable verification | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| File model | 35 | 0.0% | 86.3% | 10.0% | 100.0% | 100.0% | 0.0% | 0 |
| Page content and rendering | 708 | 0.6% | 82.0% | 38.2% | 79.4% | 100.0% | 37.4% | 146 |
| Interaction and annotations | 129 | 0.8% | 78.8% | 15.7% | 99.2% | 100.0% | 6.9% | 1 |
| Interchange and profiles | 6 | 0.0% | 55.0% | 10.0% | 66.7% | 100.0% | 0.0% | 2 |
| PDFE product capabilities | 21 | 0.0% | 62.4% | 17.6% | 100.0% | 100.0% | 14.3% | 0 |

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

Explicit test contracts: 882/899; passing recorded contracts: 876/899; candidate test coverage: 647/899. Benchmark harnesses: 6/6.
