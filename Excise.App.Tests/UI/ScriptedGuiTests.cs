using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Tests.Utilities.Fakes;
using Excise.Ocr;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// GUI tests using Roslyn C# scripting to automate user interactions.
/// These tests validate the complete GUI workflow by executing scripts
/// that interact with MainWindowViewModel exactly as a user would.
/// </summary>
public class ScriptedGuiTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDataDir;

    public ScriptedGuiTests(ITestOutputHelper output)
    {
        _output = output;
        _testDataDir = Path.Combine(Path.GetTempPath(), "excise_scripted_tests");
        Directory.CreateDirectory(_testDataDir);
    }

    /// <summary>
    /// Helper to execute a script and return the result.
    /// </summary>
    private async Task<ScriptExecutionResult> ExecuteScriptAsync(
        MainWindowViewModel viewModel,
        string scriptCode)
    {
        var scriptingService = new ScriptingService(viewModel);
        var result = await scriptingService.ExecuteAsync(scriptCode);

        _output.WriteLine($"Script execution: {(result.Success ? "SUCCESS" : "FAILED")}");
        if (!result.Success)
        {
            _output.WriteLine($"Error: {result.ErrorMessage}");
        }
        else if (result.ReturnValue != null)
        {
            _output.WriteLine($"Return value: {result.ReturnValue}");
        }

        return result;
    }

    [Fact]
    public async Task Script_CanAccessViewModel_ReturnsExpectedValue()
    {
        // Arrange
        var viewModel = MainWindowViewModelTestFactory.Create();

        // Act
        var result = await ExecuteScriptAsync(viewModel, @"
            // Simple script that returns a value
            return 42;
        ");

        // Assert
        result.Success.Should().BeTrue();
        result.ReturnValue.Should().Be(42);
    }

    [Fact]
    public async Task Script_InvalidSyntax_ReturnsCompilationError()
    {
        // Arrange
        var viewModel = MainWindowViewModelTestFactory.Create();

        // Act
        var result = await ExecuteScriptAsync(viewModel, @"
            // Invalid syntax
            this is not valid C# code {
        ");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty("Should have an error message for invalid syntax");
    }

    [Fact]
    public async Task Script_AccessViewModelProperties_Works()
    {
        // Arrange
        var viewModel = MainWindowViewModelTestFactory.Create();

        // Act
        var result = await ExecuteScriptAsync(viewModel, @"
            // Access ViewModel properties
            var hasPendingRedactions = PendingRedactions.Count > 0;
            var hasDocument = CurrentDocument != null;

            return new {
                PendingRedactions = hasPendingRedactions,
                HasDocument = hasDocument
            };
        ");

        // Assert
        result.Success.Should().BeTrue();
        result.ReturnValue.Should().NotBeNull();
    }

    [Fact]
    public async Task Script_LoadDocument_UpdatesViewModel()
    {
        // Arrange
        var viewModel = MainWindowViewModelTestFactory.Create();
        var testPdf = CreateTestPdf();

        // Act — deliberately calls the OLD name. #1540 renamed this to
        // LoadDocumentHeadlessAsync and kept LoadDocumentCommand as an
        // [Obsolete] alias so users' existing .csx scripts keep running; this
        // is the only remaining caller, and it exists to prove the alias still
        // resolves through the scripting engine. Every other script here uses
        // the new name.
        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentCommand(@""{testPdf}"");
            var isLoaded = CurrentDocument != null;
            var filePath = CurrentDocument?.FilePath;
            return new {{ IsLoaded = isLoaded, FilePath = filePath }};
        ");

        // Assert
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ReturnValue.Should().NotBeNull();
    }

    [Fact]
    public async Task Script_RedactText_CreatesRedactionArea()
    {
        // Arrange
        var viewModel = MainWindowViewModelTestFactory.Create();
        var testPdf = CreateTestPdf();

        // Act
        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentHeadlessAsync(@""{testPdf}"");
            await RedactTextCommand(""SECRET"");
            return PendingRedactions.Count;
        ");

        // Assert
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ReturnValue.Should().Be(1,
            "queueing one text-redaction adds one pending-redaction marker");
    }

    [Fact]
    public async Task Script_CompleteRedactionWorkflow_EndToEnd()
    {
        // Arrange
        var viewModel = MainWindowViewModelTestFactory.Create();
        var sourcePdf = CreateTestPdf();
        var outputPdf = Path.Combine(_testDataDir, $"redacted_output_{Guid.NewGuid():N}.pdf");

        // Act — complete load → redact → apply → save workflow through scripting
        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentHeadlessAsync(@""{sourcePdf}"");
            await RedactTextCommand(""SECRET"");
            await RedactTextCommand(""CONFIDENTIAL"");
            await ApplyRedactionsCommand();
            await SaveDocumentCommand(@""{outputPdf}"");
            return System.IO.File.Exists(@""{outputPdf}"");
        ");

        // Assert
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ReturnValue.Should().Be(true);
        File.Exists(outputPdf).Should().BeTrue("SaveDocumentCommand must produce a file");

        // The security guarantee for scripted redaction, read from the SAVED
        // BYTES in every carrier rather than from excise's own extractor
        // (#1769) — with the source as the control that the scan can see these
        // terms at all, and the untouched remainder of the sentence as the
        // control that it can still see text in the output.
        var sourceBytes = File.ReadAllBytes(sourcePdf);
        var savedBytes = File.ReadAllBytes(outputPdf);
        foreach (var term in new[] { "SECRET", "CONFIDENTIAL" })
        {
            SavedPdfLeakScanner.FindTerm(sourceBytes, term).Should().NotBeEmpty(
                $"input-side control: '{term}' must be findable before the redaction");
            SavedPdfLeakScanner.FindTerm(savedBytes, term).Should().BeEmpty(
                $"'{term}' must be gone from every carrier of the scripted redaction's output");
        }

        SavedPdfLeakScanner.AllCarriersText(savedBytes).Should().Contain("for good measure",
            "output-side control: text the script never asked to redact must survive");
    }

    [Fact]
    public async Task Script_BirthCertificateRedaction_Success()
    {
        // Arrange
        var viewModel = MainWindowViewModelTestFactory.Create();

        // Use synthetic birth certificate PDF (not the original personal file)
        // #1706 — the shared locator, not a hand-rolled walk to .git (which used
        // Directory.GetParent rather than .Parent, so a scan for the latter
        // missed it). And a real SKIP with a checkable reason: this used to
        // `return` — i.e. REPORT A PASS — when it could not find the fixture,
        // which is the silent-green shape #1706 is about.
        var birthCertPath = TestRepoLayout.FindFile(
            "test-pdfs", "sample-pdfs", "birth-certificate-request-scrambled.pdf");
        Assert.SkipWhen(birthCertPath == null, TestRepoLayout.AbsenceReason(
            "synthetic birth certificate", "test-pdfs/sample-pdfs/birth-certificate-request-scrambled.pdf"));
        var outputPdf = Path.Combine(_testDataDir, "birth-cert-redacted.pdf");

        // Act — birth certificate redaction workflow
        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentHeadlessAsync(@""{birthCertPath}"");
            var terms = new[] {{ ""TORRINGTON"", ""CERTIFICATE"", ""BIRTH"", ""CITY CLERK"" }};
            foreach (var term in terms)
                await RedactTextCommand(term);
            await ApplyRedactionsCommand();
            await SaveDocumentCommand(@""{outputPdf}"");
            return System.IO.File.Exists(@""{outputPdf}"");
        ");

        // Assert
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ReturnValue.Should().Be(true);
        File.Exists(outputPdf).Should().BeTrue("redacted PDF should be created");

        // Carrier-agnostic scan of the SAVED BYTES (#1769). This fixture has
        // SCRAMBLED GLYPH ORDER, so only some of the redacted terms are
        // contiguous in the file at all — "TORRINGTON" and "CITY CLERK" are
        // split across TJ array elements and the byte scan cannot see them
        // even before the redaction. Asserting their absence here would be a
        // vacuous pass; the input-side control below is what makes that
        // distinction visible instead of silently swallowing it, and mutool
        // (which reassembles the glyph runs) covers the rest.
        var sourceBytes = File.ReadAllBytes(birthCertPath!);
        var savedBytes = File.ReadAllBytes(outputPdf);
        foreach (var term in new[] { "CERTIFICATE", "BIRTH" })
        {
            SavedPdfLeakScanner.FindTerm(sourceBytes, term).Should().NotBeEmpty(
                $"input-side control: '{term}' is contiguous in the fixture and must be findable before redaction");
            SavedPdfLeakScanner.FindTerm(savedBytes, term).Should().BeEmpty(
                $"'{term}' must be gone from every carrier of the redacted birth certificate");
        }
    }

    /// <summary>
    /// The same scripted birth-certificate workflow, graded by an extractor
    /// that is not excise. This is the only assertion that can speak for the
    /// scrambled-glyph terms ("TORRINGTON", "CITY CLERK"): they are never
    /// contiguous in the bytes, so the carrier scan above is blind to them,
    /// and excise reading its own output would be no oracle at all.
    /// </summary>
    [Fact]
    public async Task Script_BirthCertificateRedaction_NotReadableByIndependentExtractor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var viewModel = MainWindowViewModelTestFactory.Create();
        var birthCertPath = TestRepoLayout.FindFile(
            "test-pdfs", "sample-pdfs", "birth-certificate-request-scrambled.pdf");
        Assert.SkipWhen(birthCertPath == null, TestRepoLayout.AbsenceReason(
            "synthetic birth certificate", "test-pdfs/sample-pdfs/birth-certificate-request-scrambled.pdf"));
        var outputPdf = Path.Combine(_testDataDir, $"birth-cert-mutool-{Guid.NewGuid():N}.pdf");

        var before = MutoolTextExtractor.ExtractAllPages(birthCertPath!, PageCount(birthCertPath!));
        before.Should().NotBeNull("mutool must be able to read the fixture");
        var beforeText = string.Concat(before!);
        beforeText.Should().Contain("TORRINGTON",
            "input-side control: the independent extractor must read the term before the redaction");

        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentHeadlessAsync(@""{birthCertPath}"");
            var terms = new[] {{ ""TORRINGTON"", ""CERTIFICATE"", ""BIRTH"", ""CITY CLERK"" }};
            foreach (var term in terms)
                await RedactTextCommand(term);
            await ApplyRedactionsCommand();
            await SaveDocumentCommand(@""{outputPdf}"");
            return System.IO.File.Exists(@""{outputPdf}"");
        ");

        result.Success.Should().BeTrue(result.ErrorMessage);
        var after = MutoolTextExtractor.ExtractAllPages(outputPdf, PageCount(outputPdf));
        after.Should().NotBeNull("mutool must be able to read the redacted copy");
        var afterText = string.Concat(after!);
        foreach (var term in new[] { "TORRINGTON", "CERTIFICATE", "BIRTH", "CITY CLERK" })
        {
            afterText.Should().NotContain(term,
                $"an extractor that is not excise must not read '{term}' out of the redacted file");
        }
    }

    private static int PageCount(string pdfPath)
    {
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(pdfPath));
        return doc.PageCount;
    }

    [Fact]
    public async Task Script_LoadNonexistentFile_HandlesError()
    {
        // Arrange
        var viewModel = MainWindowViewModelTestFactory.Create();
        var nonexistentFile = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.pdf");

        // Act — the script should observe FileNotFoundException from the command
        var result = await ExecuteScriptAsync(viewModel, $@"
            try
            {{
                await LoadDocumentHeadlessAsync(@""{nonexistentFile}"");
                return false; // Should not reach here
            }}
            catch (FileNotFoundException)
            {{
                return true;
            }}
        ");

        // Assert
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ReturnValue.Should().Be(true, "script should catch FileNotFoundException");
    }

    [Fact]
    public async Task Script_RedactMultiplePages_AllPagesProcessed()
    {
        // Arrange
        var viewModel = MainWindowViewModelTestFactory.Create();
        var multiPagePdf = CreateMultiPageTestPdf();
        var outputPdf = Path.Combine(_testDataDir, $"multipage-redacted-{Guid.NewGuid():N}.pdf");

        // Act — CreateMultiPageTestPdf seeds "Secret on Page N" on each page;
        // we ask scripting to redact that common substring.
        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentHeadlessAsync(@""{multiPagePdf}"");
            var pageCount = CurrentDocument.PageCount;
            await RedactTextCommand(""Secret"");
            await ApplyRedactionsCommand();
            await SaveDocumentCommand(@""{outputPdf}"");
            return pageCount;
        ");

        // Assert
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ReturnValue.Should().Be(3, "CreateMultiPageTestPdf returns 3 pages");
        File.Exists(outputPdf).Should().BeTrue();

        // "Secret" must be absent from every carrier of the whole saved file —
        // not merely from what excise's own letter model reports per page
        // (#1769). Each page seeds its own "Secret on Page N", so the scan
        // covers all three without iterating pages.
        var sourceBytes = File.ReadAllBytes(multiPagePdf);
        var savedBytes = File.ReadAllBytes(outputPdf);
        SavedPdfLeakScanner.FindTerm(sourceBytes, "Secret").Should().NotBeEmpty(
            "input-side control: the seeded term must be findable before the redaction");
        SavedPdfLeakScanner.FindTerm(savedBytes, "Secret").Should().BeEmpty(
            "scripted multi-page redaction must leave 'Secret' in no carrier of the saved file");

        // Conservation control, per page: the OTHER line each page carries must
        // survive. Without it, a build that empties every content stream passes.
        var savedCarriers = SavedPdfLeakScanner.AllCarriersText(savedBytes);
        for (int p = 1; p <= 3; p++)
        {
            savedCarriers.Should().Contain($"Page {p} Content",
                $"page {p}'s unredacted line must survive the multi-page redaction");
        }
    }

    /// <summary>
    /// Regression for #1501 item 2: applying pending redactions in memory
    /// through the scripting harness used to clear
    /// <c>FileState.PendingRedactionsCount</c> without recording that the
    /// document itself was changed, so <c>HasUnsavedChanges</c> went back to
    /// false with the loaded document already differing from the file on
    /// disk.
    /// </summary>
    [Fact]
    public async Task ApplyRedactionsCommand_KeepsDocumentDirtyUntilSave()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var sourcePdf = CreateTestPdf();
        var outputPdf = Path.Combine(_testDataDir, $"applied-dirty-{Guid.NewGuid():N}.pdf");

        await viewModel.LoadDocumentHeadlessAsync(sourcePdf);
        await viewModel.RedactTextCommand("SECRET");
        viewModel.FileState.HasUnsavedChanges.Should().BeTrue("a pending redaction is itself unsaved");

        await viewModel.ApplyRedactionsCommand();

        viewModel.FileState.HasUnsavedChanges.Should().BeTrue(
            "applying in memory with no save yet must still count as unsaved changes");

        await viewModel.SaveDocumentCommand(outputPdf);

        viewModel.FileState.HasUnsavedChanges.Should().BeFalse(
            "SaveDocumentCommand must clear the dirty state that ApplyRedactionsCommand set");
    }

    /// <summary>
    /// Regression for #1501 item 3: with two or more queued text
    /// redactions, the file-based loop deleted only the PREVIOUS
    /// intermediate on each non-final pass, so the last intermediate
    /// (excise_script_redact_&lt;N-2&gt;_*.pdf) always survived — a
    /// partially-redacted copy of the document left in the temp directory.
    /// </summary>
    [Fact]
    public async Task SaveDocumentCommand_MultipleTextRedactions_LeavesNoIntermediateTempFiles()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var sourcePdf = CreateTestPdf();
        var outputPdf = Path.Combine(_testDataDir, $"cleanup-{Guid.NewGuid():N}.pdf");
        var tempDir = Path.GetTempPath();
        var before = Directory.GetFiles(tempDir, "excise_script_redact_*");

        await viewModel.LoadDocumentHeadlessAsync(sourcePdf);
        await viewModel.RedactTextCommand("SECRET");
        await viewModel.RedactTextCommand("CONFIDENTIAL");
        await viewModel.ApplyRedactionsCommand();
        await viewModel.SaveDocumentCommand(outputPdf);

        var after = Directory.GetFiles(tempDir, "excise_script_redact_*");
        after.Should().BeEquivalentTo(before,
            "every intermediate the multi-term redaction loop creates, including the last one, must be deleted");
    }

    /// <summary>
    /// Regression for #1501 item 4: a <see cref="System.Threading.CancellationTokenSource"/>
    /// passed to <c>Task.Run</c> only cancels the task if it fires BEFORE
    /// the delegate starts running on the pool thread. Once the (fake, slow)
    /// load is actually running, the old implementation had no way to bound
    /// the wait and simply awaited however long the delegate took.
    /// </summary>
    [Fact]
    public async Task LoadDocumentCommand_TimeoutBoundsAnAlreadyRunningLoad()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var testPdf = CreateTestPdf();
        viewModel.LoadDocumentTimeoutSeconds = 1;
        viewModel.LoadDocumentOverrideForTests = _ => Task.Delay(TimeSpan.FromSeconds(10));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Func<Task> act = () => viewModel.LoadDocumentHeadlessAsync(testPdf);

        await act.Should().ThrowAsync<TimeoutException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the wait must be bounded by LoadDocumentTimeoutSeconds, not by how long the (fake) load actually takes");
    }

    /// <summary>
    /// Regression for #1878: the scripted load never pointed
    /// <c>PdfCoreDocument</c> at the document service's document, so the view
    /// model kept the PREVIOUS document — which the service had just
    /// disposed — until something else happened to resync it.
    /// </summary>
    [Fact]
    public async Task LoadDocumentCommand_SetsPdfCoreDocumentToTheServicesDocument()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var testPdf = CreateTestPdf();

        await viewModel.LoadDocumentHeadlessAsync(testPdf);

        viewModel.PdfCoreDocument.Should().NotBeNull();
        viewModel.PdfCoreDocument.Should().BeSameAs(viewModel.SaveDocumentForTests,
            "the scripted load must point PdfCoreDocument at the document service's own document");
    }

    /// <summary>
    /// #1501: a scripted text redaction reports exactly what <c>excise
    /// redact</c> reports, because both run <see cref="TermRedactionRunner"/>.
    /// The old scripted path returned a count and two warnings: it said nothing
    /// about a hyphen-wrapped occurrence still readable in the output, and it
    /// re-saved the file through a second, area-shaped safety pass the CLI
    /// does not run. The fixture carries the term in the page text, /Info and
    /// an outline title, and a second term wrapped across a line by a hyphen.
    /// </summary>
    [Fact]
    public async Task SaveDocumentCommand_TextRedaction_ReportsWhatTheCliReports()
    {
        var sourcePdf = Path.Combine(_testDataDir, $"parity-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(sourcePdf, ParityFixture());
        var scriptedPdf = Path.Combine(_testDataDir, $"parity-scripted-{Guid.NewGuid():N}.pdf");
        var cliMiddlePdf = Path.Combine(_testDataDir, $"parity-cli-1-{Guid.NewGuid():N}.pdf");
        var cliPdf = Path.Combine(_testDataDir, $"parity-cli-2-{Guid.NewGuid():N}.pdf");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(sourcePdf), "PARITYSECRET").Should().HaveCountGreaterThan(1,
            "input-side control: the term must be findable in the page and its other carriers");

        try
        {
            var viewModel = MainWindowViewModelTestFactory.Create(settingsStore: new InMemorySettingsStore());
            await viewModel.LoadDocumentHeadlessAsync(sourcePdf);
            await viewModel.RedactTextCommand("PARITYSECRET");
            await viewModel.RedactTextCommand("Anderson");
            await viewModel.SaveDocumentCommand(scriptedPdf);

            // What `excise redact` runs for each term, with the same options.
            var options = viewModel.RedactionPreferences.ToOptions() with { CaseSensitive = false };
            var cli = new[]
            {
                TermRedactionRunner.Execute(new TermRedactionRequest(sourcePdf, cliMiddlePdf, "PARITYSECRET", options)),
                TermRedactionRunner.Execute(new TermRedactionRequest(cliMiddlePdf, cliPdf, "Anderson", options)),
            };

            static object Report(TermRedactionResult r) => new
            {
                r.Text, r.Count, r.Flattened, r.WholeWord, r.HasUnremovedWrappedOccurrence,
                r.AccessibilityRemoved, Notes = string.Join("\n", r.CarrierNotes),
                Diagnostics = string.Join("\n", r.Diagnostics),
                Removals = string.Join("\n", r.Removals.Select(x => x.ToString())),
            };
            viewModel.LastTextRedactionResults.Select(Report).Should().Equal(cli.Select(Report));

            var wrapped = viewModel.LastTextRedactionResults.Single(r => r.Text == "Anderson");
            wrapped.HasUnremovedWrappedOccurrence.Should().BeTrue();
            wrapped.CarrierNotes.Should().Contain(n => n.Contains("NOT REMOVED (hyphen-wrapped)"),
                "a wrapped occurrence is still readable in the output and the scripted result must say so");

            var scriptedBytes = File.ReadAllBytes(scriptedPdf);
            SavedPdfLeakScanner.FindTerm(scriptedBytes, "PARITYSECRET").Should().BeEmpty(
                "page text, /Info and the outline title must all lose the term");
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(cliPdf), "PARITYSECRET").Should().BeEmpty();
            var mutool = MutoolTextExtractor.ExtractPage(scriptedPdf, 1);
            if (mutool != null)
                mutool.Should().NotContain("PARITYSECRET");
        }
        finally
        {
            foreach (var path in new[] { sourcePdf, scriptedPdf, cliMiddlePdf, cliPdf })
                File.Delete(path);
        }
    }

    /// <summary>
    /// One page: the term on a line by itself, "Ander-" / "son" wrapped across
    /// two lines (#1372), plus the term in /Info /Title and an outline title.
    /// </summary>
    private static byte[] ParityFixture()
    {
        const string content =
            "BT /F1 12 Tf 72 720 Td (Filed by PARITYSECRET today) Tj ET " +
            "BT /F1 12 Tf 72 700 Td (Reported by Ander-) Tj ET " +
            "BT /F1 12 Tf 72 686 Td (son on the record) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            "<< /Title (PARITYSECRET chapter) /Parent 6 0 R /Dest [3 0 R /XYZ 0 792 0] >>",
            "<< /Title (PARITYSECRET report) /Producer (test) >>",
        };
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Root 1 0 R /Info 8 0 R /Size {objects.Length + 1} >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>
    /// Create a single-page PDF containing the sentinel strings "SECRET"
    /// and "CONFIDENTIAL" that the scripting tests redact.
    /// </summary>
    private string CreateTestPdf()
    {
        var pdfPath = Path.Combine(_testDataDir, $"test_simple_{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(pdfPath, new[]
        {
            "This document contains SECRET information.",
            "Also marked CONFIDENTIAL for good measure.",
        });
        return pdfPath;
    }

    /// <summary>
    /// Create a 3-page PDF where every page carries a "Secret on Page N"
    /// line — lets multi-page tests verify every page was touched.
    /// </summary>
    private string CreateMultiPageTestPdf()
    {
        var pdfPath = Path.Combine(_testDataDir, $"test_multipage_{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 3);
        return pdfPath;
    }
}
