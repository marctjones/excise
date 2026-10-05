# Structural complexity and method-shape evidence (#1940)

`Excise.StructuralEvidence/2` records per-declaration structural evidence with
metric contract `excise-structural/2`. JSON schema 1 and the integer fields
`Cyclomatic`, `Cognitive`, `MaxNesting`, `ExecutableLines`, `ParameterCount`, and
`Accessibility` remain compatible with the review consumer. The tool has no
composite score, thresholds, or quality verdict. Any change to metric semantics
requires a new tool/metric contract and recapturing both sides of a comparison;
v1 prototype reports are not comparable to v2 reports.

`Cyclomatic` follows the [Sonar C# conditional-branch mapping](https://docs.sonarsource.com/sonarqube-server/user-guide/code-metrics/metrics-definition#cyclomatic-complexity):
one entry per body-bearing callable plus each `if`, loop, non-default switch
label, switch-expression arm (including `_`), ternary, conditional access,
`&&`, `||`, `??`, `??=`, and `and`/`or` pattern. Grouped case labels count
individually; a `default:` label does not. Catch clauses and case guards do not
add cyclomatic points themselves. Operators in their expressions still count.
This is a named syntax mapping, not a computed control-flow graph or a claim
about reachable independent paths.

`Cognitive` implements the [Sonar Cognitive Complexity whitepaper v1.7 (2023),
Appendix B](https://www.sonarsource.com/docs/CognitiveComplexity.pdf) for the
supported callable scope: `if`, loops, catch, ternary and either switch form add
one plus active nesting; `else`, `else if`, jumps with `goto`, runs of like
Boolean operators, and direct recursion add one without a nesting penalty.
A switch adds one structural point regardless of arm count. Parentheses preserve
Boolean sequences; negation breaks them. Null shorthand, ordinary calls,
`try`/`finally`, return, break and continue add no cognitive points. Pattern
`and`/`or` sequences use the same rule as Boolean operators.

The [C# metric implementation at commit d5737a1](https://github.com/SonarSource/sonar-dotnet/tree/d5737a1931aa051ee3eed7a310f994fba168662f/analyzers/src/SonarAnalyzer.CSharp/Metrics)
is the pinned language reference. This independently implemented collector is
an **isolated-callable supported subset**, not a SonarAnalyzer-equivalent
result. Local functions have individual rows with nesting reset to zero;
their declarations and bodies are excluded from enclosing rows. Sonar's
enclosing-method attribution for non-static locals differs. Lambdas and
anonymous methods remain inside their owner and add one nesting level without
an entry point. `goto` uses the whitepaper's fundamental increment, rather
than the C# analyzer's nesting penalty. Default and pattern-switch handling is
explicitly defined above rather than inherited from analyzer implementation
details.

Direct recursion adds one per callable, even with several self calls, only when
single-file Roslyn binding proves the target is that callable's original
definition. Same-name overloads are not guessed to be self calls. The core
library is the only metadata reference: unresolved calls, other files and
indirect recursion cycles are outside this supported subset. The whitepaper's
full recursion-cycle rule therefore cannot be claimed for repository totals.

`MaxNesting` is maximum active cognitive control/lambda depth; sibling branches
reset it and an `else if` chain stays at one branch level. `ExecutableLines`
counts distinct starting lines of owned non-block, non-empty statements,
excluding local-function declarations; an expression body contributes one
line. This is not logical or physical LOC. Shape records parameter count and
declared accessibility, not effective visibility through containing types.
Body-bearing methods, constructors, destructors, operators, conversions,
locals, accessors and expression-bodied properties/indexers are included.
Implicit constructors, field and constructor initializers, bodyless declarations
and top-level statements are outside scope. Type rows aggregate only callable
rows whose nearest type is that declaration, summing cyclomatic, cognitive and
executable lines and taking maximum nesting. Nested types and separate partial
declarations are not counted again inside their enclosing type.

The tool pins the `Microsoft.CodeAnalysis.CSharp` NuGet package to 5.3.0 and uses
C# 14 syntax parsing. It does not evaluate MSBuild or activate preprocessor
symbols; inactive branches are not measured. Inputs
are tracked C# files under the nearest tracked project, hashed after LF
normalization. Untracked files are ignored. Files covered by repository-scope
`excludedRoots` or `reviewOnlyRoots`, generated names/headers, and unowned files
are listed as exclusions. Test and helper projects remain in the report with
their separate scope classification because their complexity still matters for
maintenance; their measures are not comparable to shipping runtime code.

Capture twice from an unchanged checkout and compare bytes to verify a stable
snapshot:

```sh
dotnet run --project tools/Excise.StructuralEvidence -- self-test
dotnet run --project tools/Excise.StructuralEvidence -- capture . /tmp/structural.json
```

The self-test pins exact cyclomatic/cognitive/nesting goldens for switches,
Boolean sequences and patterns, recursion and overloads, nesting resets,
local functions, closures, expression-bodied roots and a ten-level nested
fixture (11 cyclomatic, 55 cognitive, depth 10). It also checks executable-line
ownership, local identity, contract mismatch rejection and changed-symbol
deltas. The report records tool, analyzer and SDK versions, parse contract,
input hashes, exclusions, supported metric definitions and reproduction command.
Compare reports with:

```sh
dotnet run --project tools/Excise.StructuralEvidence -- compare BEFORE.json AFTER.json DELTA.json
```

To restrict output to changed declarations from the #1939 comparison report,
pass that report as the fourth argument:

```sh
dotnet run --project tools/Excise.StructuralEvidence -- compare BEFORE.json AFTER.json DELTA.json SYMBOL-DELTA.json
```

The selector consumes its `Changed` declaration-key array and includes local
rows of selected owning callables. Local keys include the lexical owner,
signature and same-name declaration ordinal. Matching is by exact owning
project/declaration ID plus path; the start line is evidence, not match identity.
A line shift is a changed row, not a removed/added declaration. Separate partial
declarations retain their paths. Renames and file moves are removed plus added. Baseline
refresh should happen only against a reviewed, stable source snapshot; generated
counts describe structure and must not be used as merge or release gates.
