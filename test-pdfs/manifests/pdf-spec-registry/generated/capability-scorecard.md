# PDF capability scorecard

Raw counts only: no overall or per-category percentage is generated (#1739, #1758). Implemented/Verified/Unknown (#1346/#1347) are graded from evidence: implemented requires a passing explicit test contract, verified additionally requires an independent-oracle (differential) check among those passing, unknown is everything else (unmeasured is a first-class state) — a discovered testCandidate keyword match never earns credit on its own. Strict counts modes a reviewer has set to implemented.

Section and category rows retain every required/supported role; workflow rows include only the processor roles that workflow needs.

Critical-path benchmark scenarios: 6 ({'existing-harness': 6}).

| Area | Target modes | Implemented | Verified | Unknown | Strict implemented | Strict unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| annotation-subtypes | 90 | 90 | 88 | 0 | 0 | 0 |
| content | 12 | 12 | 11 | 0 | 0 | 0 |
| document | 21 | 21 | 20 | 0 | 0 | 0 |
| graphics | 33 | 31 | 16 | 2 | 4 | 0 |
| image-requirements | 106 | 105 | 67 | 1 | 0 | 0 |
| interactive | 39 | 29 | 22 | 10 | 1 | 1 |
| interchange | 6 | 4 | 2 | 2 | 0 | 2 |
| multimedia | 0 | 0 | 0 | 0 | 0 | 0 |
| operators | 287 | 287 | 211 | 0 | 0 | 146 |
| optional-profiles | 0 | 0 | 0 | 0 | 0 | 0 |
| product-capabilities | 21 | 17 | 1 | 4 | 0 | 0 |
| renderer-requirements | 259 | 259 | 212 | 0 | 0 | 0 |
| rendering | 10 | 9 | 1 | 1 | 0 | 0 |
| syntax | 14 | 14 | 14 | 0 | 0 | 0 |
| transparency | 1 | 1 | 1 | 0 | 0 | 0 |

## Major categories

| Category | Target modes | Strict implemented | Measured | Target capabilities | Planned verification | Executable verification | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| File model | 35 | 0 | 35 | 8 | 8 | 0 | 0 |
| Page content and rendering | 708 | 4 | 562 | 214 | 214 | 80 | 146 |
| Interaction and annotations | 129 | 1 | 128 | 29 | 29 | 2 | 1 |
| Interchange and profiles | 6 | 0 | 4 | 1 | 1 | 0 | 2 |
| PDFE product capabilities | 21 | 0 | 21 | 7 | 7 | 1 | 0 |

## Critical workflows

| Workflow | Target modes | Strict implemented | Modes at >=50 | Modes at >=90 | Unknown |
| --- | ---: | ---: | ---: | ---: | ---: |
| forms | 8 | 0 | 0 | 0 | 0 |
| redaction | 5 | 1 | 5 | 3 | 0 |
| redaction-annotations | 6 | 0 | 4 | 0 | 0 |
| rendering | 4 | 2 | 4 | 4 | 0 |
| safe-save | 5 | 0 | 0 | 0 | 0 |

## Evidence collection

Collected candidates are discovery material, not implementation credit.
All 282 capability leaves have a collection record: {'candidate-evidence': 22, 'registered-evidence': 260}.

## Test and benchmark attribution

Explicit test contracts: 882/899; passing recorded contracts: 875/899; candidate test coverage: 642/899. Benchmark harnesses: 6/6.
