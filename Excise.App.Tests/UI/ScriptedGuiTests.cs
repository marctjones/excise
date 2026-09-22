using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
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
