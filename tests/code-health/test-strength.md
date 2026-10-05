# Test evidence (#1943)

`Excise.TestEvidence` joins production declarations to direct xUnit test
references using a Roslyn MSBuild workspace. It uses the exact symbol keys from
the #1939 inventory, including overloads. Regenerate the inventory after source
changes; stale source hashes fail capture.
Tracked source membership and scope hashes are also validated before and after
capture, including excluded sources retained in the inventory.

```sh
dotnet run --project tools/Excise.CodeHealth -- inventory . /tmp/symbols.json
dotnet run --project tools/Excise.TestEvidence -- self-test
dotnet run --project tools/Excise.TestEvidence -- capture . /tmp/symbols.json /tmp/test-evidence.json
dotnet run --project tools/Excise.TestEvidence -- capture . /tmp/symbols.json /tmp/changed-test-evidence.json --changed /tmp/delta.json
```

The optional delta is the #1939 comparison output; its `Added`, `Removed`, and
`Changed` keys select rows. Unbound current production declarations remain
explicitly unknown in `UnmatchedInventorySymbols`. Selected keys without a
production row (including removed and non-production keys) are retained in
`UnmatchedSelectedKeys`. Only tracked inventory
source enters attribution; generated/untracked workspace documents are listed
as omitted. The solution and projects are evaluated with MSBuild, and each
project records its compilation-error count. Unresolved invocations are counted
and never matched by their spelling. A partial compilation can therefore
produce observations, but its unresolved calls and unbound symbols cannot be
treated as absence of test coverage.

Each declaration records:

- `AttributionStatus`: `direct-attributed-tests` or
  `no-direct-attributed-tests`. The latter means no resolved direct reference,
  not proof that no test reaches it.
- `Tests`: exact test identity, source location, project, unresolved invocation
  count, static oracle candidates, and any supplied execution outcomes.
- `ExecutedAttributedTestCount`: attributed tests with matching TRX
  `Passed`/`Failed` outcomes. This proves that the test ran, not that this
  production declaration or oracle call executed on its chosen branch.
- `StaticOracleCandidateTestCount`: attributed tests containing resolved direct
  invocations of types in `oracles.json`. A call does not prove its result was
  asserted, the tool was available, or an independent comparison succeeded.
  Review the assertion and execution evidence before claiming oracle coverage.
- `ObservedCoverageLines`: line/hit observations from supplied Cobertura files.
  Missing input remains `not-supplied`; an empty line array is not zero coverage.
  Type spans can overlap member spans; these are not independent populations.

Helper calls, inherited/custom test attributes, dynamic calls, reflection,
transitive call paths, generated code, and test frameworks other than exact
xUnit `Fact`/`Theory` attributes are explicit attribution limits. Source-level
references include constructors, properties, and type uses; a type reference
does not establish execution of every member. Oracle policy distinguishes
external renderer/extractor tools from the independent saved-byte scanner;
Excise's own extractor is not registered as an independent oracle.

## Execution and coverage observations

Use `--execution MANIFEST.json` and/or `--coverage MANIFEST.json`. Each manifest
has this schema; execution artifacts are TRX and coverage artifacts are
Cobertura XML:

```json
{
  "SchemaVersion": 1,
  "InventorySha256": "SHA-256 of exact inventory file bytes",
  "Runs": [
    {
      "Project": "Excise.Core.Tests/Excise.Core.Tests.csproj",
      "Artifact": "results.trx"
    }
  ]
}
```

Artifact paths resolve relative to the manifest directory. The producer must
attest the exact inventory used for its run; a mismatched fingerprint fails.
This is an explicit producer attestation, not an automatic freshness proof for
a previously generated TRX or coverage file. Use `scripts/t.sh` to build fresh
tests, retain results, and record the inventory fingerprint for that run.
Theory cases join through TRX `TestMethod` class/method identity, preserving all
outcomes rather than inferring success from a display name. Skipped outcomes
remain recorded and do not increment the executed count.
An execution manifest with no test results fails rather than becoming supplied
execution evidence. Coverage is mapped
by repository-relative filename and declaration span; filenames that cannot
join remain outside symbol observations. It is not per-test tracing.

Reports contain generator/source hashes, Roslyn and SDK versions, inventory
and oracle-policy hashes, source inputs, build-input hashes, evaluated project
properties, reference-assembly hashes, and imported artifact hashes. Arrays are
sorted and omit timestamps and checkout paths. Two captures of unchanged inputs
with the same evaluated build environment produce identical report bytes.

## Bounded mutation pilot

The runner copies `PdfPermissions.cs`, `DocumentAction.cs`, and the existing
raw-bitmask test slice into a temporary net10.0 project. It applies six named
first-order bit/boolean substitutions only in that temporary source. It excludes
the original document/corpus integration section explicitly. Project package
versions come from the Core test project; each mutation's replacement must
match exactly once. It never changes a checkout production file.

```sh
python3 scripts/code-health-mutation-pilot.py --self-test
python3 scripts/code-health-mutation-pilot.py --root . --output /tmp/mutation-pilot.json --logs-directory /tmp/mutation-logs --repeat 2 --timeout 120
```

The pilot report records the measured population: 32 baseline tests passed in
each trial; all six selected mutants were killed in both trials, with zero
survivors, timeouts, or invalid runs. Each result records its runtime, command,
exit code, TRX/log fingerprint, counters, and mutated-source hash. Total runtime
and repeated outcomes are recorded; runtimes and TRX hashes are observations
and are not expected to reproduce byte for byte.

A historical report is not current evidence: verify its input hashes or rerun
the pilot against the current source. The combined capture leaves execution
and coverage unknown unless separate fingerprinted observations are supplied.

The population is six deliberately selected candidates, not all mutations in
Core. These results say nothing about unselected operators, equivalent mutants,
document integration, or repository-wide test strength. The baseline must pass
without zero/skipped tests. Assertion failures kill a mutant; compilation
errors, missing results, and infrastructure failures are invalid rather than
killed. Timeouts are separate observations. Runs use independent temporary
source copies and check input hashes again at completion.
The runner requires complete counters and the same executed test population
as the passing baseline. It recognizes xUnit/AwesomeAssertions assertion
failures in failed TRX rows; other exception failures remain invalid. The
self-test plants zero, skipped, changed-population and infrastructure outcomes
so they cannot inflate the killed count.

No coverage threshold is lowered, no aggregate strength score is calculated,
and mutation results are not a merge or release gate. Any broader mutation
policy needs a reviewed baseline, population/exception rules, and an anti-gaming
policy under #1938/#1945. Correctness, security, performance, and user workflows
remain primary release evidence.
