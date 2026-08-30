# Excise architecture

This directory is the single prose authority for Excise's intended software
architecture. It explains boundaries and rationale; it does not maintain a
second implementation-status list.

## Read in this order

1. [System design](system-design.md) — components, dependency direction,
   workflows, and cross-cutting invariants.
2. [Architecture decisions](decisions.md) — durable choices and the conditions
   under which they may be superseded.
3. The normalized registries:
   [`design.json`](../../architecture/design.json) owns intended boundaries and
   workflows; [`inventory.generated.json`](../../architecture/inventory.generated.json)
   owns observed projects/references; [`assessment.json`](../../architecture/assessment.json)
   owns status, evidence, gaps, and issue links; and
   [`decisions.json`](../../architecture/decisions.json) indexes durable choices.
   [`repository-scope.json`](../../architecture/repository-scope.json) classifies
   shipping, test, sample, benchmark, tool, generated, vendored, corpus, and
   nested-worktree roots used by deterministic inventory.
4. [`current-projects.dot`](../../architecture/generated/current-projects.dot)
   and [`target-components.dot`](../../architecture/generated/target-components.dot)
   — generated design views; never edit them directly. The Roslyn-derived
   [`code-topology.json`](../../architecture/generated/code-topology.json)
   supplies type/method coupling and complexity signals for refactoring;
   [`change-coupling.json`](../../architecture/generated/change-coupling.json)
   records fixed-window production files that repeatedly change together.
   The large topology file is compact generated JSON; query it with `jq`
   instead of reviewing or editing it as prose.

## Authority matrix

| Question | Authority |
|---|---|
| What should the system be? | `system-design.md` and accepted decisions |
| Why was a consequential boundary chosen? | `decisions.md` |
| What exists today? | Architecture inventory and assessment registries |
| What is implemented, partial, planned, or unknown? | Architecture assessment data, backed by named evidence |
| What work remains? | GitHub Issues and the current milestone |
| What shipped? | `CHANGELOG.md` and release artifacts |
| How is a property verified? | Test/gate contracts such as `CI_GATES.md`, corpus manifests, and registry evidence |
| What did an old plan or investigation say? | Git history and its linked issue; historical plans are not kept as current docs |

## What does not belong here

- Priority lists, fix orders, roadmaps, and implementation plans belong in
  milestones and issues.
- PDF-format theory and general algorithms belong in the Wiki.
- Experimental notes belong in Discussions or issue comments.
- Release, corpus, benchmark, accessibility, automation, and redaction-safety
  contracts remain beside the code they govern.
- Generated observations belong under `architecture/generated/` and must name
  their generator, schema, and source revision.

## Maintenance rules

- Prose states intent and invariants, not unsupported completion claims.
- Every component and workflow named here uses the stable ID from
  `architecture/design.json`.
- `unknown` is preferable to an inference from a directory name or old plan.
- A new architecture document requires a distinct authority not already served
  by these files. Otherwise update an existing file or record the work in an
  issue.
- Consequential changes update the decision record, registry, generated views,
  and enforcement in the same reviewed change.

Validate the architecture surface with:

```bash
scripts/check-architecture-registry.sh
scripts/check_architecture_registry.py --self-test
scripts/check-architecture-docs.sh
scripts/check_architecture_docs.py --self-test
```
