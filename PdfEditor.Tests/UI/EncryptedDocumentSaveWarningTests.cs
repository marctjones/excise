using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pdfe.Core.Document;
using PdfEditor.Services;
using PdfEditor.ViewModels;
using Xunit;

namespace PdfEditor.Tests.UI;

/// <summary>
/// #638: saving a copy derived from an encrypted source document must not
/// silently drop the encryption. pdfe's writer cannot emit /Encrypt (#624),
/// so the GUI must ask for explicit acknowledgement before any such save,
/// and must never ask when the source wasn't encrypted.
/// </summary>
public class EncryptedDocumentSaveWarningTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pdfe-encrypted-save-warning-{Guid.NewGuid():N}");

    public EncryptedDocumentSaveWarningTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    /// <summary>Records whether/how <see cref="ShowConfirmAsync"/> was called; returns a settable result.</summary>
    private sealed class FakeUserDialogService : IUserDialogService
    {
        public int ConfirmCallCount { get; private set; }
        public bool ConfirmResult { get; set; }

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            return Task.FromResult(ConfirmResult);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test base directory.");
    }

    private const string EncryptedFixtureRelativePath = "test-pdfs/pdfjs/issue15893_reduced.pdf";
    private const string EncryptedFixturePassword = "test";

    private static string? ExistingEncryptedFixturePathOrNull()
    {
        var path = Path.Combine(FindRepoRoot(), EncryptedFixtureRelativePath);
        return File.Exists(path) ? path : null;
    }

    private static (MainWindowViewModel vm, FakeUserDialogService dialog) CreateViewModel(
        PdfDocumentService documentService)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dialog = new FakeUserDialogService();
        var vm = new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            documentService,
            new PdfRenderService(NullLogger<PdfRenderService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService(),
            dialogService: dialog);
        return (vm, dialog);
    }

    [Fact]
    public async Task SaveFileAsAsync_EncryptedSource_AsksForConfirmation_AndDoesNotSaveWhenDeclined()
    {
        var fixturePath = ExistingEncryptedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Encrypted PDF fixture not available: {EncryptedFixtureRelativePath}");

        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(fixturePath!, EncryptedFixturePassword);
        documentService.IsEncrypted.Should().BeTrue("fixture is a password-protected PDF");

        var (vm, dialog) = CreateViewModel(documentService);
        dialog.ConfirmResult = false;

        var outputPath = Path.Combine(_tempDir, "declined.pdf");
        await vm.SaveFileAsAsync(outputPath);

        dialog.ConfirmCallCount.Should().Be(1, "the user must be asked before an encrypted source is saved unencrypted");
        File.Exists(outputPath).Should().BeFalse("declining the confirmation must not produce a file");
    }

    [Fact]
    public async Task SaveFileAsAsync_EncryptedSource_SavesWhenConfirmed()
    {
        var fixturePath = ExistingEncryptedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Encrypted PDF fixture not available: {EncryptedFixtureRelativePath}");

        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(fixturePath!, EncryptedFixturePassword);

        var (vm, dialog) = CreateViewModel(documentService);
        dialog.ConfirmResult = true;

        var outputPath = Path.Combine(_tempDir, "confirmed.pdf");
        await vm.SaveFileAsAsync(outputPath);

        dialog.ConfirmCallCount.Should().Be(1);
        File.Exists(outputPath).Should().BeTrue("confirming the warning must proceed with the save");
    }

    [Fact]
    public async Task SaveFileAsAsync_UnencryptedSource_NeverAsksForConfirmation()
    {
        var blankPath = Path.Combine(_tempDir, "blank.pdf");
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.Save(blankPath);
        }

        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(blankPath);
        documentService.IsEncrypted.Should().BeFalse();

        var (vm, dialog) = CreateViewModel(documentService);
        dialog.ConfirmResult = false; // even if it were asked and declined, saving should never be blocked here

        var outputPath = Path.Combine(_tempDir, "blank-saved.pdf");
        await vm.SaveFileAsAsync(outputPath);

        dialog.ConfirmCallCount.Should().Be(0, "an unencrypted source must never trigger the encryption-loss warning");
        File.Exists(outputPath).Should().BeTrue();
    }
}
