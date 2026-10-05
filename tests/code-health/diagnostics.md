# Analyzer and formatting diagnostic baseline (#1944)

`diagnostics.json` records the legacy IDE0051/IDE0052 warnings and whitespace
formatting findings. The scoped rules are intentionally narrow: IDE0051 and
IDE0052 are the existing `.editorconfig` private-member warnings; whitespace
formatting is the SDK formatter's deterministic whitespace pass. No style
suggestion or aggregate score is a gate.

The toolchain is pinned to the .NET SDK 10.0.400 feature band in `global.json`
(patch roll-forward only). `dotnet format` and the SDK-bundled Roslyn analyzers
therefore use the pinned toolchain. Rule severities are declared in
`.editorconfig`. Diagnostics contain rule, project, symbol, file, and source
line provenance. Formatter locations are mapped to the nearest preceding
declaration from `symbols.json`; files with no declaration use a `file:` key.
Each report also carries the raw SHA-256 of the symbol inventory and an
order-independent SHA-256 over every tracked source path/hash pair, alongside
the inventory generator, Roslyn, and scope fingerprints. The collector rejects
an inventory whose tracked C# set, normalized source hashes, or scope hash has
changed, and repeats validation after formatting. Use `--inventory PATH` to
join a freshly captured inventory; its emitted path label omits machine paths.
#1945 can compare
these producer-attested snapshot fields before joining changed-symbol reports.

Run the ratchet with:

```sh
scripts/check-code-health-diagnostics.sh
python3 scripts/code-health-diagnostics.py self-test
# Capture against a fresh temporary inventory during review.
python3 scripts/code-health-diagnostics.py capture --inventory /tmp/symbols.json
```

The checker permits existing rows at their recorded counts and fails when a
rule/project/symbol/file count increases. Existing findings remain visible in
the JSON. Updating the baseline is an explicit reviewed operation:

```sh
python3 scripts/code-health-diagnostics.py capture --write-baseline
```

Review the JSON diff and resolve or explicitly accept each new finding before
refreshing. `dotnet format` runs only in verification mode; it never rewrites
repository files. The self-test plants an unused private field and malformed
whitespace in a temporary project and proves both are detected and rejected by
the baseline comparison. It also proves source/set/scope changes invalidate
inventory joins and duplicate finding rows add to the ratchet count.

The later #1945 changed-symbol report can consume these stable symbol keys to
show only findings relevant to the current diff. This issue establishes the
diagnostic evidence and legacy baseline; it does not gate on a code-health score.
