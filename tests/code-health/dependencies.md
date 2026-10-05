# Dependency and source API evidence (#1942)

Capture fresh semantic evidence and compare reviewed snapshots:

```sh
python3 scripts/code-health-dependencies.py self-test
dotnet run --project tools/Excise.Reachability -- --self-test
python3 scripts/code-health-dependencies.py capture /tmp/dependencies-before.json
python3 scripts/code-health-dependencies.py capture /tmp/dependencies-after.json
python3 scripts/code-health-dependencies.py compare /tmp/dependencies-before.json /tmp/dependencies-after.json /tmp/dependencies-delta.json
python3 scripts/code-health-dependencies.py graph /tmp/dependencies-after.json /tmp/dependencies-projects.dot --level project
python3 scripts/code-health-dependencies.py graph /tmp/dependencies-after.json /tmp/dependencies-components.dot --level component
python3 scripts/code-health-dependencies.py graph /tmp/dependencies-after.json /tmp/dependencies-symbols.dot --level symbol
python3 scripts/code-health-dependencies.py entrypoints /tmp/dependencies-after.json /tmp/dependencies-entrypoints.json
```

The optional `--review-evidence-output PATH` extends the authoritative
`Excise.Reachability` analyzer without changing its topology schema, seed rules,
or baseline gate. Capture restores the solution, runs that analyzer with a
temporary reachability baseline, and recomputes conformance with the existing
architecture-registry classifier. Repository architecture artifacts and
reachability/unwired ratchets are never overwritten. Explicit source declarations
and bound reference edges use project path plus Roslyn documentation ID; the full
source signature is the fallback for declarations lacking an ID. Type dependency
edges retain their authoritative partial-type component ownership; runtime
interface-dispatch edges remain available separately and are excluded from
compile-time conformance. Evaluated MSBuild project references form project edges.

`graph` and `entrypoints` derive their views from a captured snapshot and do not
rerun analysis or inspect a different source tree. Schema version 1 has additive
project/classification and reviewer fields; generator contracts are
`code-health-dependencies/2` and `Excise.Reachability/review-2`. Older captures
must be regenerated before using these views. Their snapshot SHA-256 identifies
the normalized capture bytes. Graph edges point from the source to its referenced
target. Symbol graphs retain isolated declarations and distinguish dashed runtime
interface dispatch from solid static references; these references include
contract and ownership relationships, not just method calls. Project graphs use
evaluated project references and label nodes outside the analyzed symbol scope.
Component graphs use the existing conformance classifier and authoritative type
ownership. Forbidden/undeclared edges are explicitly labeled review candidates;
they are not architectural verdicts or instructions to remove code. These DOT
views make no inference from directory names or semantic similarity.

The entrypoint inventory contains conservative seed roots, exported source API,
static-unreachable candidates and unregistered symbols. Each row includes project
classification, component, source spans, seed category/reason, reachability,
direct callers/callees with edge kind, production fan-in, test reference state
and review roles. A seed is a modeling rationale, not runtime-execution proof;
static fan-in excludes self-references and runtime dispatch, even when those
references appear in the caller list. No static/test references means exactly
that, rather than claiming an absence of observed runtime dispatch. In particular,
a reachable seeded public API with no production caller remains an unwired API
review candidate. The report retains the analyzer's dynamic-mechanism modeling
and blind spots for XAML, reflection, DI, source generation, scripting, native
entry paths and external consumers. Boundary review includes registry-classified
forbidden/undeclared edges, unregistered symbols, unowned project observations,
registry mismatches and unresolved-type examples with the full unresolved count.
These are candidate observations, never automatic refactor or deletion approval.

Public/protected source members are API rows only when their containing types
are also externally visible and their project is classified as shipping. The
source signature includes overload parameters, generic constraints, nullable
annotations, modifiers, defaults, declared attributes, base types/interfaces and
property accessor visibility. Additions/removals are keyed by symbol; stable-key
signature changes are explicit. Location-only changes are source-symbol changes,
not API changes. Renames are removed plus added. This is source-surface review,
not a binary compatibility oracle: synthesized/generated members, target-framework
variants and other host platforms require the existing approval/build validation.

Unwired API candidates have no incoming static production reference; test-project
evidence distinguishes nowhere from tests-only. Public-library seeds and runtime
interface dispatch do not hide that evidence. Test references follow the existing
semantic cross-compilation resolver and production-edge closure, so a listed
test project can be indirect. Unreachable rows likewise retain seed/reachability
evidence. Neither list authorizes deletion: external consumers, XAML, DI,
reflection, scripting, native callbacks and architecture ownership require
review. The separate existing approved-API identifier heuristic is included with
its generator, minimum identifier length and snapshot hashes; its name-only
matches must not be confused with bound overload-specific references.

Capture records the SDK/Roslyn versions, generator assembly hash, evaluated
project language/preprocessor/nullability/platform settings and metadata-reference
content hashes, HEAD, tree
state, all input hashes and explicitly included untracked source inputs. It
also hashes ignored/vendored source and approved snapshots consumed by the
existing lexical heuristic's own crawler and names non-Git heuristic inputs
separately. The semantic/configuration scan and the heuristic retain their
existing distinct scopes; no source affecting the included heuristic is implied
to be covered by Git-visible inputs alone. Capture
regenerates the semantic graph and rejects a capture if source/configuration
inputs or HEAD change during analysis. A dirty snapshot remains labeled dirty;
it is not a clean release baseline. Revision-only checked topology is never
assumed fresh. Comparison rejects differing schema/semantic contracts or generator
assemblies. Outputs contain no timestamp and use sorted UTF-8 JSON with final LF.
The same input bytes on the same evaluated SDK/platform reproduce the output.
NuGet/MSBuild evaluation remains host-specific; the snapshot does not claim
cross-platform package, generated-code or runtime reachability equivalence.

Review output reports changed symbols, symbol/project/type/component dependency
edges, forbidden edges, API signatures, declaration hashes, newly unwired/dead-code candidates, and
approved-snapshot changes. It is observational evidence, not a new score or gate.
Store a baseline only after reviewing its named inputs; use the changed-symbol
review/exception policy from #1945 when establishing a ratchet.

Focused self-tests plant a forbidden Core-to-App dependency, a public seed with
zero static callers and test-only references, an isolated unreachable symbol,
directed references and runtime interface dispatch. They verify API delta
identity, seed rationale, duplicate/dangling identity and inconsistent-flag rejection, DOT escaping,
byte-stable repeat output and ordering independent of graph insertion order.
Changing input order changes the recorded snapshot hash, as it identifies exact
capture bytes. Run the two self-tests above before capturing repository evidence;
their fixtures are tooling validation, not a clean-source baseline.
