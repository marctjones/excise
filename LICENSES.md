# Third-Party Licenses

excise ships several third-party libraries. The complete list — with each
package's name, version, license, copyright, and verbatim license text —
is available three ways:

1. **In-app**: open the GUI and choose **Help → About PDF Editor**.
   The About dialog reads the same manifest used to generate this file.
2. **As JSON**: see [`Excise.App/Assets/third-party-licenses.json`](Excise.App/Assets/third-party-licenses.json).
   Regenerate via `scripts/generate-license-manifest.sh [--scancode]`.
3. **In a published `.deb`**: `/usr/share/doc/excise/copyright` lists the
   primary `excise` license; the manifest is embedded in the binary.

## License summary

All runtime dependencies use **permissive** licenses (MIT, Apache-2.0,
BSD-3-Clause, OFL-1.1). No copyleft (GPL/LGPL/AGPL).

The release **verifies** this manifest rather than regenerating it
(`scripts/verify-license-manifest.sh`, #1082): it regenerates into a temp path,
diffs against the checked-in file, and fails on drift. Before that, the release
ran the generator and shipped whatever came out — so the attribution users saw
was not the attribution that had been reviewed and gated. That is only possible
because regeneration is byte-identical (#1081); with the old `generatedAt`
timestamp inside, every release drifted by construction.

Completeness and **policy** are enforced by `scripts/check-license-compliance.sh`,
which runs in **t0** — on every push. It reads the actual restored package closure from
NuGet's own `project.assets.json` (an independent source, so the manifest cannot
vouch for its own completeness) and fails if any shipped package is missing an
attribution, carries an unresolved license, or shows only a link instead of
verbatim license text. A new dependency therefore cannot ship without appearing
in the About dialog.

Build-time tooling and reference-only packages that carry no runtime DLL
(`Microsoft.NETCore.Platforms`, `NETStandard.Library`, `Avalonia.Diagnostics`,
`Microsoft.CodeAnalysis.Analyzers`, `Avalonia.BuildServices`, `Fody`) are
excluded as non-redistributed. The check RE-DERIVES that list from
`scripts/generate-license-manifest.sh` rather than restating it, so the two
cannot drift.

> ⚠️ This was three xunit tests in `Excise.App.Tests` until 2026-08-19. It
> shelled out to `dotnet list package`, MSBuild node-reuse workers inherited the
> redirected pipe, the read never returned, and three consecutive full-suite runs
> aborted — leaving 1,310 correctness tests with no verdict because a compliance
> check was blocked on a pipe. It never gated them logically; xunit runs an
> assembly in one process, so any test that can hang takes every other test's
> RESULT with it. **A compliance check must not be able to stop correctness
> tests from reporting**, which is why a static file check now lives in
> `scripts/` beside the other t0 gates (#1068).

The script `scripts/generate-license-manifest.sh --scancode` cross-checks
each package against [scancode-toolkit](https://scancode-toolkit.readthedocs.io/)
to verify that the license declared in the package metadata matches the
license texts actually shipped in the package files. Discrepancies are
flagged in the JSON manifest as `scancodeMismatch: true` and surfaced
in the About dialog with a warning banner.

## Vendored data files

Beyond NuGet packages, excise embeds a bounded set of **Adobe CMap resource
files** (registered CJK CMaps: `UniGB-UCS2-H/V`, `UniCNS-UCS2-H/V`,
`UniJIS-UCS2-H/V`, `UniKS-UCS2-H/V`, `90ms-RKSJ-H/V`, and the
`Adobe-{GB1,CNS1,Japan1,Korea1,KR}-UCS2` CID→Unicode maps) into
`Excise.Core` for Type0 font text extraction (#515). They are unmodified
copies from
[adobe-type-tools/cmap-resources](https://github.com/adobe-type-tools/cmap-resources)
and
[adobe-type-tools/mapping-resources-pdf](https://github.com/adobe-type-tools/mapping-resources-pdf),
both **BSD-3-Clause**. The verbatim license text and per-file provenance are
in [`Excise.Core/Resources/CMaps/LICENSE.md`](Excise.Core/Resources/CMaps/LICENSE.md).

## Why permissive only

You can:

- Use commercially
- Modify the code
- Distribute modified versions
- Embed in proprietary software
- Ship without source disclosure

## Regenerating the manifest

```bash
# Quick: pulls SPDX/copyright/URL from each package's .nuspec metadata.
scripts/generate-license-manifest.sh

# Verified: also runs scancode-toolkit to cross-check the declared
# license against the actual license text in each package. Slower
# (~10 minutes for the full package set) but produces evidence-backed results.
scripts/generate-license-manifest.sh --scancode

# Both write to:
#   Excise.App/Assets/third-party-licenses.json  (embedded into the GUI)
#   artifacts/scancode/<package>.json           (per-package scancode runs)
```

## excise itself

The excise source is MIT-licensed. See [LICENSE](LICENSE) for the full
text.
