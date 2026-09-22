# Excise.App.Tests

Test suite for the Avalonia GUI (`Excise.App`) and the viewer control it hosts.

⚠️ **Rewritten 2026-09-21 (#1769).** Every structural claim below was
re-derived with `find` / from the `.csproj`. The previous version described a
project that no longer exists: it named an `Integration/RedactionIntegrationTests.cs`
and a `Utilities/PdfTestHelpers.cs` that are **both gone**, listed the unit
tests as "to be added" when they are the second-largest directory here, and
credited **PdfSharpCore and PdfPig** as dependencies — those were removed in
v2.0, and PdfPig in particular was being cited as the suite's *verification*
oracle. Its worked example called `PdfReader.Open` and `PdfTestHelpers.ExtractAllText`,
neither of which compiles, and asserted removal with excise reading its own
output, which CLAUDE.md forbids.

## Test structure

```
Excise.App.Tests/
├── UI/            # headless Avalonia workflow tests — the bulk of the project
├── Unit/          # services, view-model orchestration, redaction service
├── Integration/   # search/index, thumbnail cache, real-document sweeps
├── Controls/      # control-level behaviour (typewriter, viewer chrome)
├── Automation/    # scripted perf scenarios
├── PublicApi/     # public-API surface baselines
├── Utilities/     # test PDF generator, headless view-model factory, attributes
├── Fixtures/ Resources/
└── Excise.App.Tests.csproj
```

`Utilities/` also carries the two attributes a UI test needs instead of
`[Fact]` / `[Theory]`: `[FixedAvaloniaFact]` and `[FixedAvaloniaTheory]`
(#337). A parameterized UI test must use the Theory one — a plain `[Theory]`
does not run on the headless dispatcher.

Two files are **linked in from `Excise.Core.Tests`, not copied** (see the
`<Compile Include="../Excise.Core.Tests/TestSupport/...">` items in the
`.csproj`):

- `TestSupport/SavedPdfLeakScanner.cs` — the carrier-agnostic saved-bytes leak
  scan, including inside `/FlateDecode` streams (#1049).
- `TestSupport/TestRepoLayout.cs` — the ONE fixture/corpus locator; it resolves
  the main checkout through git's worktree plumbing (#1527). Do not hand-roll
  an upward `..` walk; `scripts/check-fixture-locators.sh` fails the build if
  you do.

## Running tests

⚠️ **This project is SERIAL BY DESIGN** (`AssemblyInfo.cs`,
`DisableTestParallelization = true`): xunit parallelism races SkiaSharp's
process-wide native font manager and kills the test host (#363). It is also
long and sensitive to CPU contention — a concurrent run elsewhere on the
machine produces false reds (#619). Run it alone.

```bash
# Targeted, with a freshness check on the binary (preferred)
scripts/t.sh Excise.App.Tests --filter "FullyQualifiedName~RedactionServiceTests"

# The whole project — see CLAUDE.md's tier table before reaching for this
scripts/test-tier.sh t1
```

## Dependencies

From the `.csproj`: xUnit v3 3.2.2, `xunit.runner.visualstudio`,
AwesomeAssertions 9.6.0 (the OSS fork of FluentAssertions 7.x), Moq,
Avalonia.Headless(.XUnit) 12.1.2, SkiaSharp, SixLabors.ImageSharp, Serilog,
coverlet, PublicApiGenerator. The PDF stack under test is excise's own
(`Excise.App` → `Excise.Core` / `Excise.Rendering` / `Excise.Ocr`).

Reference oracles live in `Excise.Rendering/Differential/` and are used from
here directly — `MutoolTextExtractor`, `MutoolReferenceRenderer`,
`QpdfReferenceTool`. Their `IsAvailable` gates a declared
`Assert.SkipUnless` (#1172), never a silent `if`.

## Writing a redaction test

**A tool must not be its own oracle for the property it exists to guarantee.**
`page.Text` is excise reading excise; it is a necessary check and never a
sufficient one. Assert on the SAVED BYTES, and pair every absence claim with a
control that proves the check could have failed:

```csharp
[Fact]
public void RedactingASecret_LeavesItInNoCarrierOfTheSavedFile()
{
    var input = Path.Combine(_tempDir, "in.pdf");
    TestPdfGenerator.CreateMultiPagePdf(input, pageCount: 1);

    // INPUT-SIDE CONTROL: the scan can see this file's text at all.
    var sourceBytes = File.ReadAllBytes(input);
    SavedPdfLeakScanner.FindTerm(sourceBytes, "Secret on Page 1")
        .Should().NotBeEmpty();

    using var doc = PdfDocument.Open(sourceBytes);
    _service.RedactArea(doc.GetPage(1), new Rect(40, 165, 460, 60));

    var saved = doc.SaveToBytes();

    // THE CLAIM: gone from every carrier, compressed streams included.
    SavedPdfLeakScanner.FindTerm(saved, "Secret on Page 1").Should().BeEmpty();

    // CONSERVATION CONTROL: the redaction did not eat the rest of the page
    // (#942 destroyed 5-36% of a document per term while every removal
    // assertion stayed green).
    SavedPdfLeakScanner.AllCarriersText(saved).Should().Contain("Page 1 Content");
}
```

`SavedPdfLeakScanner.FindTerm` for absence — it names the carrier a leak
survived in — and `AllCarriersText` for presence. For a term that is never
contiguous in the bytes (scrambled glyph order, one glyph per `Tj`) the byte
scan is blind by construction and the input-side control is what makes that
visible; use `MutoolTextExtractor` there instead.

## Conventions

1. Each test is independent; temp files are cleaned in `Dispose()`.
2. A skip carries its reason IN CODE — `Assert.SkipWhen/SkipUnless/Skip` or
   `[Fact(Skip = "why")]` (#1172). There is no external allowlist.
3. Deleting or renaming a test that `UI/GuiWorkflowCoverageMatrixTests.cs`
   names breaks the build; re-point the capability at the test that really
   covers it rather than dropping the row.
4. Task tracking lives in GitHub Issues, not in this file — no TODO lists here
   (see CLAUDE.md, "Knowledge Management Strategy").
