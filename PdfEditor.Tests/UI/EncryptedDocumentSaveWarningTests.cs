using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pdfe.Core.Document;
using PdfEditor.Services;
using PdfEditor.ViewModels;
using Xunit;

namespace PdfEditor.Tests.UI;

/// <summary>
/// #643 (superseding the #638 warning these tests originally pinned): saving
/// a copy derived from an encrypted source document PRESERVES the
/// encryption — same algorithm/permissions, same password — silently,
/// because nothing is lost. The old "Encryption Will Be Removed"
/// confirmation must NOT appear on the normal save path anymore; dropping
/// protection is only reachable through the Security dialog's explicit
/// Remove Protection action (#641). Unencrypted sources must, as before,
/// never see any encryption dialog.
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
    public async Task SaveFileAsAsync_EncryptedSource_SavesEncrypted_WithoutAskingAnything()
    {
        var fixturePath = ExistingEncryptedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Encrypted PDF fixture not available: {EncryptedFixtureRelativePath}");

        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(fixturePath!, EncryptedFixturePassword);
        documentService.IsEncrypted.Should().BeTrue("fixture is a password-protected PDF");

        var (vm, dialog) = CreateViewModel(documentService);
        dialog.ConfirmResult = false; // if any dialog were shown and declined, the save must still happen

        var outputPath = Path.Combine(_tempDir, "preserved.pdf");
        await vm.SaveFileAsAsync(outputPath);

        dialog.ConfirmCallCount.Should().Be(0,
            "preserving the source's protection is the good path (#643) — there is no loss to confirm");
        File.Exists(outputPath).Should().BeTrue();

        // The output must still be protected by the SAME password.
        var withoutPassword = () => PdfDocument.Open(File.ReadAllBytes(outputPath));
        withoutPassword.Should().Throw<Pdfe.Core.Parsing.PdfEncryptionNotSupportedException>(
            "the saved copy must still require the source's password");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath), EncryptedFixturePassword);
        reopened.IsEncrypted.Should().BeTrue("saving an encrypted document must keep it encrypted (#643)");

        // The post-save reload must have reopened pdfe's own (now encrypted)
        // output with the remembered password — before that fix, the reload
        // threw and was silently swallowed by SaveFileAsAsync's catch.
        vm.PdfCoreDocument.Should().NotBeNull(
            "the app must be able to reload its own re-encrypted output with the remembered password");
        vm.PdfCoreDocument!.PageCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SaveDocument_EncryptedSource_PreservesPermissionsMask()
    {
        var fixturePath = ExistingEncryptedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Encrypted PDF fixture not available: {EncryptedFixtureRelativePath}");

        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(fixturePath!, EncryptedFixturePassword);
        var sourcePermissions = documentService.GetCurrentDocument()!.Permissions.RawValue;

        var outputPath = Path.Combine(_tempDir, "service-save.pdf");
        documentService.SaveDocument(outputPath);

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath), EncryptedFixturePassword);
        reopened.IsEncrypted.Should().BeTrue();
        reopened.Permissions.RawValue.Should().Be(sourcePermissions,
            "the source /P permission mask must survive the service-level save round-trip (#643)");

        // The service reloads from the saved file; the loaded document must
        // still be usable (i.e. it reopened its own encrypted output with
        // the remembered password).
        documentService.IsDocumentLoaded.Should().BeTrue();
        documentService.IsEncrypted.Should().BeTrue();
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
