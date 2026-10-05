# Duplicate implementation evidence (#1941)

`Excise.DuplicateEvidence` emits deterministic review candidates for complete
method bodies and nested C# blocks. Candidates have at least 60 syntax tokens
and three non-block statements. Method root blocks are not emitted twice.
It is deliberately conservative: token kinds, literal spelling, operators,
types, calls and member names must match. Bound local names are replaced by
first-occurrence indices; bound parameters retain their parameter positions
and declaring scopes (including nested lambda parameters).
Trivia is ignored. Exact matches and parameter/local rename matches are
distinguished. There is no fuzzy similarity score, aggregate rating, merge
gate, automatic refactoring or deletion.

```sh
dotnet run --project tools/Excise.DuplicateEvidence -- self-test .
dotnet run --project tools/Excise.DuplicateEvidence -- capture . /tmp/duplicates.json
# Optional explicit date evaluates intentional-duplicate review deadlines.
dotnet run --project tools/Excise.DuplicateEvidence -- capture . /tmp/duplicates-reviewed.json 2026-10-04
```

The report records generator version/source hash, Roslyn version, scope and
registry hashes, sorted inputs and normalized source hashes, explicit excluded
files, token evidence, source spans (one-based lines/columns, end exclusive),
and source declaration keys compatible with the #1939 inventory. Candidate IDs
hash the candidate kind and normalized token sequence, so whitespace changes,
line shifts and local renames preserve candidate identity. Adding/removing an
occurrence changes the occurrence list, not that identity. Block occurrence
ordinals are declaration-local traversal positions and can change when an
earlier block is inserted. Identical candidates remain reviewable by their
source spans and owning declarations.

Inputs are Git-tracked C# files under the nearest tracked project, normalized
to LF. `architecture/repository-scope.json` controls classifications and
exclusions. Generated names/headers, `bin`/`obj` components, vendored/review-only
roots, unowned and untracked files are deliberately excluded. Shipping, test,
tool, sample and benchmark classifications stay separate in occurrences.
The tool parses C# 14 without MSBuild evaluation, referenced assemblies or
preprocessor symbols. Syntax errors and missing tracked source fail capture.
Inactive branches, synthesized source, arbitrary repeated fragments,
approximate clones and methods below the minimum are outside its coverage.
Properties/accessors/local functions/lambdas are not separate owners; their
tokens can appear inside the enclosing method or nested-block evidence.
Equal tokens do not prove equivalent contracts, thread safety, state ownership,
or safe consolidation; inspect signatures, captured state and caller behavior.

Each occurrence links to conservative, name-only invocation caller leads;
overloads and unrelated same-name calls can appear. These are explicitly not
bound call edges. Existing `architecture/generated/code-topology.json` rows
join only by exact path, declaration start line and display signature. Unique
matches retain semantic fan-in, reachability, seed reasons, workflows and test
projects. Missing/ambiguous joins report unknown reachability. The topology
hash, source revision and freshness assessment accompany the report. A Git
input diff proves staleness; no diff leaves generator freshness unverified.
Unresolved syntax-only signature types may prevent a topology join. Static
analysis blind spots and conservative seeds still apply. Before refactoring,
refresh/check reachability using `scripts/check-reachability.sh` and review its
evidence, including framework/dynamic entry paths. A name-only lead or an
unreachable result never authorizes deletion.

Known intentional candidates can be registered in
`intentional-duplicates.json` with `CandidateId`, an Excise GitHub issue URL,
`Rationale`, and ISO `ReviewBy` date. The registry never suppresses evidence.
Invalid entries/duplicate IDs fail; unmatched entries remain visible. With an
explicit review date, past deadlines become `expired-registration;
review-required`; without one, deadline evaluation is explicitly absent.
Register only after reviewing all current occurrences, and review membership
changes even while the same candidate ID remains registered.

Baseline refresh requires a reviewed stable snapshot: stage intended new
sources/projects so Git enumeration includes them, resolve missing tracked
files, refresh the topology, capture twice with the same explicit date (or
without a date), and compare bytes. Record the exact source/scope/tool/registry
hashes as review evidence. During concurrent work in a dirty checkout, capture
to temporary output only: it represents those tracked working-tree bytes,
omits untracked implementations, and is not an accepted release baseline.
No repository-wide duplicate baseline is pinned while stabilization changes
are still in progress. Changed-symbol reporting and ratchets belong to #1945.

The self-test plants method and block clones, parameter/local renames, changed
literals/operators/callees/parameter positions/captured parameters, caller and
topology links with unavailable-revision provenance, tiny wrappers, generated and
untracked files, shifted source lines, registry validation and expired entries.
It compares repeated captures across independent checkout paths byte for byte.
