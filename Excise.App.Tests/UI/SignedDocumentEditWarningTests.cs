using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.Core.Document;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1415: saving edits to an already-signed document silently invalidated
/// the signature with no warning at all. The save path must now ask for
/// confirmation once per document-session before the first save, and must
/// never ask for an unsigned document.
/// </summary>
public class SignedDocumentEditWarningTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"excise-signed-edit-warning-{Guid.NewGuid():N}");

    public SignedDocumentEditWarningTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private sealed class FakeUserDialogService : IUserDialogService
    {
        public int ConfirmCallCount { get; private set; }
        public bool ConfirmResult { get; set; }
        public string? LastTitle { get; private set; }

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            LastTitle = title;
            return Task.FromResult(ConfirmResult);
        }
    }

    private static (MainWindowViewModel vm, FakeUserDialogService dialog) CreateViewModel(
        PdfDocumentService documentService)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dialog = new FakeUserDialogService();
        var vm = MainWindowViewModelTestFactory.Create(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            documentService,
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService(),
            dialogService: dialog);
        return (vm, dialog);
    }

    private string CreateSignedPdf()
    {
        var basePath = Path.Combine(_tempDir, "base.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(basePath, "Signed document warning test");

        using var certificate = SigningCertificateFactory.CreateSelfSigned("Signed Edit Warning Test");
        var signer = new SignatureApplicationService(NullLogger<SignatureApplicationService>.Instance);
        using var document = PdfDocument.Open(basePath);
        var signedBytes = signer.SignDocument(document, certificate);

        var signedPath = Path.Combine(_tempDir, "signed.pdf");
        File.WriteAllBytes(signedPath, signedBytes);
        return signedPath;
    }

    [Fact]
    public async Task SaveFileAsAsync_SignedSource_AsksForConfirmation_AndProceedsWhenConfirmed()
    {
        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(CreateSignedPdf());
        documentService.HasSignatures.Should().BeTrue("fixture was signed via SignatureApplicationService");

        var (vm, dialog) = CreateViewModel(documentService);
        dialog.ConfirmResult = true;

        var outputPath = Path.Combine(_tempDir, "saved.pdf");
        await vm.SaveFileAsAsync(outputPath);

        dialog.ConfirmCallCount.Should().Be(1, "a signed document must be confirmed once before saving");
        dialog.LastTitle.Should().Contain("Signed");
        File.Exists(outputPath).Should().BeTrue("confirming must let the save proceed");
    }

    [Fact]
    public async Task SaveFileAsAsync_SignedSource_DeclinedConfirmation_DoesNotSave()
    {
        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(CreateSignedPdf());

        var (vm, dialog) = CreateViewModel(documentService);
        dialog.ConfirmResult = false;

        var outputPath = Path.Combine(_tempDir, "declined.pdf");
        await vm.SaveFileAsAsync(outputPath);

        dialog.ConfirmCallCount.Should().Be(1);
        File.Exists(outputPath).Should().BeFalse("declining the warning must block the save");
    }

    [Fact]
    public async Task SaveFileAsAsync_SignedSource_OnlyAsksOnceAcrossMultipleSaves()
    {
        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(CreateSignedPdf());

        var (vm, dialog) = CreateViewModel(documentService);
        dialog.ConfirmResult = true;

        await vm.SaveFileAsAsync(Path.Combine(_tempDir, "first.pdf"));
        await vm.SaveFileAsAsync(Path.Combine(_tempDir, "second.pdf"));

        dialog.ConfirmCallCount.Should().Be(1,
            "the user shouldn't be asked again for the same document-session once they've confirmed");
    }

    [Fact]
    public async Task SaveFileAsAsync_UnsignedSource_NeverAsksForConfirmation()
    {
        var blankPath = Path.Combine(_tempDir, "blank.pdf");
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.Save(blankPath);
        }

        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(blankPath);
        documentService.HasSignatures.Should().BeFalse();

        var (vm, dialog) = CreateViewModel(documentService);
        dialog.ConfirmResult = false; // even if it were asked and declined, saving should never be blocked here

        var outputPath = Path.Combine(_tempDir, "blank-saved.pdf");
        await vm.SaveFileAsAsync(outputPath);

        dialog.ConfirmCallCount.Should().Be(0, "an unsigned document must never trigger the signature warning");
        File.Exists(outputPath).Should().BeTrue();
    }
}
