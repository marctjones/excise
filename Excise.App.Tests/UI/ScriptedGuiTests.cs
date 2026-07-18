using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
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
        var viewModel = new MainWindowViewModel();

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
        var viewModel = new MainWindowViewModel();

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
        var viewModel = new MainWindowViewModel();

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
        var viewModel = new MainWindowViewModel();
        var testPdf = CreateTestPdf();

        // Act — escape path separators for the script string literal
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
        var viewModel = new MainWindowViewModel();
        var testPdf = CreateTestPdf();

        // Act
        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentCommand(@""{testPdf}"");
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
        var viewModel = new MainWindowViewModel();
        var sourcePdf = CreateTestPdf();
        var outputPdf = Path.Combine(_testDataDir, $"redacted_output_{Guid.NewGuid():N}.pdf");

        // Act — complete load → redact → apply → save workflow through scripting
        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentCommand(@""{sourcePdf}"");
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

        // Verify the text was actually removed — this is the security
        // guarantee for scripted redaction. Open the saved file with
        // Excise.Core and confirm neither phrase appears.
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(outputPdf));
        var text = string.Concat(doc.GetPage(1).Letters.Select(l => l.Value));
        text.Should().NotContain("SECRET");
        text.Should().NotContain("CONFIDENTIAL");
    }

    [Fact]
    public async Task Script_BirthCertificateRedaction_Success()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();

        // Use synthetic birth certificate PDF (not the original personal file)
        // Find repository root by walking up from current directory
        var currentDir = Directory.GetCurrentDirectory();
        var repoRoot = currentDir;
        while (repoRoot != null && !Directory.Exists(Path.Combine(repoRoot, ".git")))
        {
            var parent = Directory.GetParent(repoRoot);
            repoRoot = parent?.FullName;
        }

        if (repoRoot == null)
        {
            _output.WriteLine("Skipping: Could not find repository root");
            return;
        }

        var birthCertPath = Path.Combine(repoRoot, "test-pdfs", "sample-pdfs", "birth-certificate-request-scrambled.pdf");
        var outputPdf = Path.Combine(_testDataDir, "birth-cert-redacted.pdf");

        // Skip if synthetic birth certificate not available
        if (!File.Exists(birthCertPath))
        {
            _output.WriteLine($"Skipping: Synthetic birth certificate not found at {birthCertPath}");
            return;
        }

        // Act — birth certificate redaction workflow
        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentCommand(@""{birthCertPath}"");
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

        // Verify the sensitive terms are actually gone from the saved file.
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(outputPdf));
        var allText = string.Concat(
            Enumerable.Range(1, doc.PageCount)
                .SelectMany(p => doc.GetPage(p).Letters.Select(l => l.Value)));
        allText.Should().NotContain("TORRINGTON");
        allText.Should().NotContain("CERTIFICATE");
    }

    [Fact]
    public async Task Script_LoadNonexistentFile_HandlesError()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var nonexistentFile = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.pdf");

        // Act — the script should observe FileNotFoundException from the command
        var result = await ExecuteScriptAsync(viewModel, $@"
            try
            {{
                await LoadDocumentCommand(@""{nonexistentFile}"");
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
        var viewModel = new MainWindowViewModel();
        var multiPagePdf = CreateMultiPageTestPdf();
        var outputPdf = Path.Combine(_testDataDir, $"multipage-redacted-{Guid.NewGuid():N}.pdf");

        // Act — CreateMultiPageTestPdf seeds "Secret on Page N" on each page;
        // we ask scripting to redact that common substring.
        var result = await ExecuteScriptAsync(viewModel, $@"
            await LoadDocumentCommand(@""{multiPagePdf}"");
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

        // "Secret" must be absent from every page of the redacted file.
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(outputPdf));
        for (int p = 1; p <= doc.PageCount; p++)
        {
            var text = string.Concat(doc.GetPage(p).Letters.Select(l => l.Value));
            text.Should().NotContain("Secret",
                $"page {p} must not leak 'Secret' after scripted multi-page redaction");
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
