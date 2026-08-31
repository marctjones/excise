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
   supplies type/method coupling and complexity signals for refactoring. Its
   project rows join to inventory classifications and project components; its
   symbol rows use the most-specific `ownership` root, fall back to the project
   container, and derive workflow IDs from `design.json`. A null component is
   explicit unregistered code, not an inferred directory owner. Seeded symbols
   carry category/reason provenance, and the seed summary records whether XAML,
   DI, reflection, source generation, native interop, and scripting use static
   edges, qualified seeds, explicit conservative fallbacks, or have no observed
   entry path. AXAML is parsed structurally; any untyped reflection-binding
   fallback is named in the topology blind-spot list.
   Type-dependency source ownership follows the declaration file of the member
   making the reference, including members of partial types. The target remains
   the owning component of its public containing type: for a partial type,
   `{TypeName}.cs` is its canonical contract declaration. A cross-component
   partial type without exactly one canonical declaration fails analysis rather
   than inheriting alphabetical file order. This distinguishes an engine-owned
   compatibility facade from document-model implementation without reclassifying
   callers that consume that facade.
   [`architecture-conformance.json`](../../architecture/generated/architecture-conformance.json)
   compares those observed type edges with target dependencies, explicit
   forbidden relationships, and accepted exceptions. The generated
   [`current-component-types.dot`](../../architecture/generated/current-component-types.dot)
   and [`current-vs-target.dot`](../../architecture/generated/current-vs-target.dot)
   provide compact reviewer views; undeclared edges are review candidates,
   while forbidden edges and unowned shipping code fail the registry gate.
   [`change-coupling.json`](../../architecture/generated/change-coupling.json)
   records fixed-window shipping files that repeatedly change together; its
   roots come from the generated inventory rather than a second source list.
   [`artifact-set.json`](../../architecture/generated/artifact-set.json) hashes
   the complete inventory/topology/coupling/conformance/diagram set so a mixed
   or partial regeneration fails as one unit.
   The large topology file is compact generated JSON; query it with `jq`
   instead of reviewing or editing it as prose.

`sourceRevision` records the commit used as the generation base. It is
provenance, not the freshness key: committing regenerated output necessarily
changes `HEAD`. Checks therefore preserve that field while comparing all other
normalized content, then verify exact artifact bytes through the set manifest.

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
scripts/check-architecture-artifacts.sh
scripts/check-architecture-artifacts.sh --self-test
scripts/check-architecture-docs.sh
scripts/check_architecture_docs.py --self-test
```

Regenerate all derived architecture artifacts transactionally only after
reviewing the source registry changes:

```bash
scripts/check-architecture-artifacts.sh --update
```
