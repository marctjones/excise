# Repeatable code-health evidence (D17, #1938)

This is tooling, not a one-time model audit. Capture once for a reviewed
baseline, then compare a new capture to review changed symbols. Collection and
comparison use ordinary programs; no model tokens are required. Models review
the small candidate report and design any subsequent issue-linked refactor.

```sh
python3 scripts/code-health.py capture --output /tmp/d17-before --reviewer REVIEWER --as-of YYYY-MM-DD
python3 scripts/code-health.py capture --output /tmp/d17-after --reviewer REVIEWER --as-of YYYY-MM-DD
python3 scripts/code-health.py review --before /tmp/d17-before/snapshot.json --after /tmp/d17-after/snapshot.json --output /tmp/d17-review --as-of YYYY-MM-DD
python3 scripts/code-health.py self-test
python3 scripts/code-health.py self-test --collectors
```

Output directories must be empty and outside the checkout. Finish concurrent
edits and stage intended new/deleted source before capture: the syntax inventory
is tracked-only. The driver rejects source/configuration drift, fingerprints
all six evidence families and labels dirty captures diagnostic-only. It never
promotes a baseline, edits source, or accepts a review increase automatically.

`summary.json` gives separate top metric rankings and bounded duplicate and
entry-point candidates. Full evidence remains in the adjacent JSON reports.
`project-dependencies.dot` and `component-dependencies.dot` are directed graphs;
`entrypoints.json` identifies seeds, reasons, callers and boundary-review
candidates. For the larger symbol graph use the dependency collector's `graph`
command. Graphs and entry-point candidates guide review, not deletion or an
automatic judgment that an architecture is good.

See [structural.md](structural.md), [duplication.md](duplication.md),
[dependencies.md](dependencies.md), [test-strength.md](test-strength.md), and
[diagnostics.md](diagnostics.md) for definitions and explicit blind spots.

## Symbol inventory (#1939)

The combined changed-symbol report, exception registry and stable-snapshot
review procedure are documented in [review-policy.md](review-policy.md) (#1945).

`symbols.json` is declaration evidence, not a quality score or release verdict.
Generate it from a clean checkout after staging any new source files:

```sh
dotnet run --project tools/Excise.CodeHealth -- inventory . tests/code-health/symbols.json
dotnet run --project tools/Excise.CodeHealth -- self-test .
dotnet run --project tools/Excise.CodeHealth -- compare . BEFORE.json AFTER.json DELTA.json
```

Schema 1 records the generator version/source hash, Roslyn version, parse
contract, scope registry hash, reproducible command, sorted tracked input files
and their SHA-256 hashes, explicit exclusions, and sorted declarations. JSON is
UTF-8 without BOM with a final LF; paths are Git-relative with `/` separators;
input text is LF-normalized. There are no timestamps, machine paths, HEAD IDs,
or locale-dependent orderings. The same tracked input bytes and generator
produce identical output across clean checkouts.

The key is the owning tracked project plus Roslyn's namespace-qualified
documentation declaration ID (including overload signature). Partial type and
method declarations share a key and retain separate source rows. Each row has
a declaration hash, file hash, kind, source path, and one-based start line.
The nearest tracked ancestor project supplies ownership; no project means an
explicit exclusion. Source classifications use `architecture/repository-scope.json`.
Generated filenames/headers are recorded as excluded inputs. Test, tool,
benchmark, sample, and shipping declarations remain separately classified.
Untracked files and compiler-generated output are never enumerated.

This is a syntax inventory, not an MSBuild workspace or bound API analysis:
default preprocessor symbols, C# 14, no assembly references. Inactive `#if`
branches, synthesized members, accessors, locals, and top-level entry points
are not declaration rows. Tracked source is considered even if a project's
MSBuild `Compile` rules would remove it. Unresolved signature types use
Roslyn's syntax-derived documentation IDs; binding/reachability evidence
belongs to #1942. Syntax errors and missing tracked files fail generation
instead of silently producing partial evidence.

Comparison reports sorted added, removed, and changed keys. A symbol rename
is explicitly removed plus added; it does not guess identity from similar
bodies. Moving a declaration retains the key and reports it changed. Deleted
files must be removed from Git's index before regeneration. Changed file
hashes and line shifts also report unchanged declarations in that file as
changed, conservatively preserving evidence for the later #1945 ratchet.
Baseline freshness enforcement and reviewed exceptions belong to #1945;
this issue introduces no complexity or behavior gate.
