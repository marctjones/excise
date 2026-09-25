# Versioning and API stability

What the published libraries promise between releases.

The publishable libraries — **`Excise.Core`**, **`Excise.Rendering`**, **`Excise.Avalonia`** — follow [Semantic Versioning](https://semver.org/) on their **public** API:

- **MAJOR** — a breaking change to a public type/member.
- **MINOR** — backward-compatible additions (new types/members/overloads).
- **PATCH** — backward-compatible fixes with no public-API change.

What counts as the supported public contract:

- Public types and members of the three libraries are the contract. Anything marked `internal` (or excluded from the public surface) may change in any release.
- The high-level authoring surface — `Excise.Core.Authoring.*` (`PdfDocumentBuilder`, `TextStyle`, `PageSize`, `PageMargins`, `FontFamily`, `LayoutContext`) — is the recommended, stable entry point for *writing* PDFs. The low-level `PdfGraphics` / `AcroFormAuthoring` API remains available as an escape hatch.
- The public API is **gated in CI**: `PublicApiApprovalTests` snapshots the full public surface of `Excise.Core` against a committed baseline (`Excise.Core.Tests/PublicApi/Excise.Core.approved.txt`). Any addition, removal, or signature change fails the build until the baseline is intentionally regenerated (`APPROVE_PUBLIC_API=1`) and committed — so every public-API change is a deliberate, reviewable SemVer decision.

**Distribution:** packages ship as `.nupkg` + `.snupkg` (symbols) with [SourceLink](https://github.com/dotnet/sourcelink) for step-into debugging, attached to each [GitHub Release](https://github.com/marctjones/excise/releases). They are **not published to nuget.org** — consume them via a local/private feed or a project reference. See issues #383 (writer DX) and #384 (viewer/render DX).
