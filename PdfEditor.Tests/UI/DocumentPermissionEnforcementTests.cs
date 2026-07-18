using System.Reactive.Linq;

using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfEditor.Services;
using PdfEditor.ViewModels;
using Xunit;

namespace PdfEditor.Tests.UI;

/// <summary>
/// #642: GUI/scripting enforcement of the document's /P permission flags.
/// Primary fixture: "Gday garçon - owner.pdf" (poppler corpus) — P = -3392,
/// qpdf-confirmed as "extract for any purpose: not allowed / extract for
/// accessibility: allowed / print + modify + annotate + forms: not
/// allowed", opening with an EMPTY user password. The user-visible
/// contract: copy/export/edit/annotate actions refuse with a toast (never
/// a silent no-op), while search and rendering — pdfe's internal,
/// accessibility-relevant extraction — keep working. Redaction is
/// deliberately not gated (see MainWindowViewModel.Permissions.cs).
/// </summary>
public class DocumentPermissionEnforcementTests : IDisposable
{
    private const string RestrictedFixtureRelativePath =
        "test-pdfs/poppler/unittestcases/Gday garçon - owner.pdf";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"pdfe-permission-enforcement-{Guid.NewGuid():N}");

    public DocumentPermissionEnforcementTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
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

    private static string? RestrictedFixturePathOrNull()
    {
        var path = Path.Combine(FindRepoRoot(), RestrictedFixtureRelativePath);
        return File.Exists(path) ? path : null;
    }

    private static (MainWindowViewModel vm, List<ToastService.ToastEventArgs> toasts) CreateViewModel()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var toastService = new ToastService();
        var toasts = new List<ToastService.ToastEventArgs>();
        toastService.ToastRequested += (_, args) => toasts.Add(args);
        var vm = new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            new PdfDocumentService(NullLogger<PdfDocumentService>.Instance),
            new PdfRenderService(NullLogger<PdfRenderService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            toastService);
        return (vm, toasts);
    }

    private static async Task<(MainWindowViewModel vm, List<ToastService.ToastEventArgs> toasts)>
        CreateViewModelWithRestrictedFixtureAsync(string fixturePath)
    {
        var (vm, toasts) = CreateViewModel();
        await vm.LoadDocumentCommand(fixturePath);
        return (vm, toasts);
    }

    // ---- copy ------------------------------------------------------------

    [FixedAvaloniaFact]
    public async Task CopyTextCommand_CopyForbiddenDocument_BlocksWithToast_AndKeepsClipboardEmpty()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);
        vm.SelectedText = "garçon";

        await vm.CopyTextCommand.Execute();

        vm.ClipboardHistory.Should().BeEmpty(
            "a copy-forbidden document's text must not reach the clipboard or its history");
        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"),
            "a blocked copy must give visible feedback, not silently no-op");
    }

    [FixedAvaloniaFact]
    public async Task SetSelectedTextAndCopyAsync_CopyForbidden_BlocksClipboard_ButKeepsSelection()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        await vm.SetSelectedTextAndCopyAsync("G'day garçon");

        vm.SelectedText.Should().Be("G'day garçon",
            "the in-app selection stays available (it powers highlights and search)");
        vm.ClipboardHistory.Should().BeEmpty();
        toasts.Should().Contain(t => t.Message.Contains("Blocked by document permissions"));
    }

    [FixedAvaloniaFact]
    public async Task SetSelectedTextAndCopyAsync_UnrestrictedDocument_StillCopies()
    {
        var blankPath = Path.Combine(_tempDir, "blank.pdf");
        using (var doc = Pdfe.Core.Document.PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.Save(blankPath);
        }

        var (vm, toasts) = CreateViewModel();
        await vm.LoadDocumentCommand(blankPath);

        await vm.SetSelectedTextAndCopyAsync("hello");

        vm.ClipboardHistory.Should().ContainSingle(e => e.Text == "hello",
            "permission enforcement must not affect unrestricted documents");
        toasts.Should().NotContain(t => t.Message.Contains("Blocked by document permissions"));
    }

    [FixedAvaloniaFact]
    public async Task SetSelectedTextAndCopyAsync_IgnoreDocumentPermissions_Overrides()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);
        vm.IgnoreDocumentPermissions = true;

        await vm.SetSelectedTextAndCopyAsync("garçon");

        vm.ClipboardHistory.Should().ContainSingle(e => e.Text == "garçon",
            "IgnoreDocumentPermissions is the scripting counterpart of --ignore-permissions");
        toasts.Should().BeEmpty();
    }

    // ---- export ----------------------------------------------------------

    [FixedAvaloniaFact]
    public async Task ExportCurrentPageToImage_CopyForbidden_BlocksWithToast_AndWritesNoFile()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);
        var outputPath = Path.Combine(_tempDir, "blocked-export.png");

        await vm.ExportCurrentPageToImageAsync(outputPath);

        File.Exists(outputPath).Should().BeFalse("a blocked export must not produce a file");
        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"));
    }

    // ---- edit / annotate -------------------------------------------------

    [FixedAvaloniaFact]
    public async Task ToggleTypewriterMode_ModifyForbidden_StaysOff_WithToast()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        await vm.ToggleTypewriterModeCommand.Execute();

        vm.IsTypewriterMode.Should().BeFalse("the fixture denies /P bit 4 (modify)");
        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"));
    }

    [FixedAvaloniaFact]
    public async Task ToggleFormAuthoringMode_ModifyForbidden_StaysOff_WithToast()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        await vm.ToggleFormAuthoringModeCommand.Execute();

        vm.IsFormAuthoringMode.Should().BeFalse("the fixture denies /P bit 4 (modify)");
        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"));
    }

    [FixedAvaloniaFact]
    public async Task AddStickyNoteAnnotation_AnnotateForbidden_BlocksWithToast()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        await vm.AddStickyNoteAnnotationAsync("note text");

        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"),
            "the fixture denies /P bit 6 (annotate)");
    }

    // ---- what must KEEP working -----------------------------------------

    [Fact]
    public void Search_CopyForbiddenDocument_StillFindsText()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        // Search is pdfe-internal extraction — the accessibility carve-out
        // and plain layering sense both say it must not be gated by bit 5.
        var searchService = new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        var matches = searchService.Search(fixturePath!, "garçon");

        matches.Should().NotBeEmpty("search must keep working on copy-forbidden documents");
    }

    [Fact]
    public async Task Rendering_CopyForbiddenDocument_StillRenders()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var renderService = new PdfRenderService(NullLogger<PdfRenderService>.Instance);
        var bitmap = await renderService.RenderPageAsync(fixturePath!, 0, 72);

        bitmap.Should().NotBeNull("on-screen rendering must keep working on copy-forbidden documents");
    }

    // ---- scripting surface ----------------------------------------------

    [FixedAvaloniaFact]
    public async Task ScriptExtractAllText_CopyForbidden_Throws_WithAccessibilityAndOverrideGuidance()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, _) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        var act = () => vm.ExtractAllText();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Blocked by document permissions*")
            .WithMessage("*forAccessibility*", "bit 10 is granted, so the carve-out must be advertised")
            .WithMessage("*IgnoreDocumentPermissions*");
    }

    [FixedAvaloniaFact]
    public async Task ScriptExtractAllText_ForAccessibility_HonoursBit10CarveOut()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, _) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        var text = vm.ExtractAllText(forAccessibility: true);

        text.Should().Contain("garçon",
            "the fixture grants /P bit 10 (extract for accessibility) while denying bit 5");
    }

    [FixedAvaloniaFact]
    public async Task ScriptExtractAllText_IgnoreDocumentPermissions_Overrides()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, $"Fixture not available: {RestrictedFixtureRelativePath}");

        var (vm, _) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);
        vm.IgnoreDocumentPermissions = true;

        vm.ExtractAllText().Should().Contain("garçon");
    }
}
