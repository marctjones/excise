using Excise.Core.Signatures;
using System.Reactive.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Rendering;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #642: GUI/scripting enforcement of the document's /P permission flags.
/// Primary fixture: "Gday garçon - owner.pdf" (poppler corpus) — P = -3392,
/// qpdf-confirmed as "extract for any purpose: not allowed / extract for
/// accessibility: allowed / print + modify + annotate + forms: not
/// allowed", opening with an EMPTY user password. The user-visible
/// contract: copy/export/edit/annotate actions refuse with a toast (never
/// a silent no-op), while search and rendering — excise's internal,
/// accessibility-relevant extraction — keep working. Redaction is
/// deliberately not gated (see MainWindowViewModel.Permissions.cs).
/// </summary>
public class DocumentPermissionEnforcementTests : IDisposable
{
    private const string RestrictedFixtureRelativePath =
        "test-pdfs/poppler/unittestcases/Gday garçon - owner.pdf";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-permission-enforcement-{Guid.NewGuid():N}");

    public DocumentPermissionEnforcementTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    // #1706 — TestRepoLayout, not a hand-rolled walk to .git/excise.sln. The
    // old walk stopped at a worktree's .git FILE, short of the MAIN checkout
    // where the gitignored corpora below actually live.
    private static string FindRepoRoot() =>
        TestRepoLayout.MainCheckoutRoot
        ?? throw new DirectoryNotFoundException("Could not find repository root from test base directory.");

    private static string? RestrictedFixturePathOrNull()
    {
        var path = Path.Combine(FindRepoRoot(), RestrictedFixtureRelativePath);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// #1768: a generated fixture BESIDE the poppler-corpus tests below, not
    /// instead of them -- a mask excise writes and reads with its own
    /// PdfEncryptionOptions is a self-oracle for whether excise's own /P
    /// PARSING is correct (CLAUDE.md #1617), which is exactly what the
    /// poppler `Gday garçon` fixture corroborates independently. This one
    /// exists so the copy-block contract still has a row when that corpus is
    /// absent (Linux CI, a fresh clone), covering the core case
    /// (CopyTextCommand) rather than duplicating all 13 corpus-gated tests.
    /// </summary>
    private string SaveWithPermissions(string text, long permissions)
    {
        var plainPath = Path.Combine(_tempDir, "plain-for-encrypt.pdf");
        using (var plain = Excise.Core.Document.PdfDocument.CreateNew())
        {
            var page = plain.Pages.AddBlank();
            using (var g = page.GetGraphics())
            {
                g.DrawString(text, Excise.Core.Graphics.PdfFont.Helvetica(14),
                    Excise.Core.Graphics.PdfBrush.Black, 72, page.Height - 100);
                g.Flush();
            }
            plain.Save(plainPath);
        }
        return Encrypt(plainPath, permissions);
    }

    private string Encrypt(string plainPath, long permissions)
    {
        var encryptedPath = Path.Combine(_tempDir, $"generated-{permissions}.pdf");
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(plainPath));
        doc.Save(encryptedPath, new Excise.Core.Security.PdfEncryptionOptions
        {
            UserPassword = "",
            OwnerPassword = "owner-1768",
            Permissions = permissions,
        });
        return encryptedPath;
    }

    [FixedAvaloniaFact]
    public async Task CopyTextCommand_CopyForbiddenGeneratedFixture_BlocksWithToast_AndKeepsClipboardEmpty()
    {
        var fixturePath = SaveWithPermissions("Copy-forbidden generated text", -4 & ~16L); // bit 5 cleared: extraction not allowed
        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath);
        vm.SelectedText = "Copy-forbidden generated text";

        await vm.CopyTextCommand.Execute();

        vm.ClipboardHistory.Should().BeEmpty(
            "a copy-forbidden document's text must not reach the clipboard or its history");
        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"),
            "a blocked copy must give visible feedback, not silently no-op");
    }

    private static (MainWindowViewModel vm, List<ToastService.ToastEventArgs> toasts) CreateViewModel()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var toastService = new ToastService();
        var toasts = new List<ToastService.ToastEventArgs>();
        toastService.ToastRequested += (_, args) => toasts.Add(args);
        var vm = MainWindowViewModelTestFactory.Create(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            new PdfDocumentService(NullLogger<PdfDocumentService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(null),
            new FilenameSuggestionService(),
            toastService);
        return (vm, toasts);
    }

    private static async Task<(MainWindowViewModel vm, List<ToastService.ToastEventArgs> toasts)>
        CreateViewModelWithRestrictedFixtureAsync(string fixturePath)
    {
        var (vm, toasts) = CreateViewModel();
        await vm.LoadDocumentHeadlessAsync(fixturePath);
        return (vm, toasts);
    }

    // ---- copy ------------------------------------------------------------

    [FixedAvaloniaFact]
    public async Task CopyTextCommand_CopyForbiddenDocument_BlocksWithToast_AndKeepsClipboardEmpty()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

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
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

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
        using (var doc = Excise.Core.Document.PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.Save(blankPath);
        }

        var (vm, toasts) = CreateViewModel();
        await vm.LoadDocumentHeadlessAsync(blankPath);

        await vm.SetSelectedTextAndCopyAsync("hello");

        vm.ClipboardHistory.Should().ContainSingle(e => e.Text == "hello",
            "permission enforcement must not affect unrestricted documents");
        toasts.Should().NotContain(t => t.Message.Contains("Blocked by document permissions"));
    }

    [FixedAvaloniaFact]
    public async Task SetSelectedTextAndCopyAsync_IgnoreDocumentPermissions_Overrides()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

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
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);
        var outputPath = Path.Combine(_tempDir, "blocked-export.png");

        await vm.ExportCurrentPageToImageAsync(outputPath);

        File.Exists(outputPath).Should().BeFalse("a blocked export must not produce a file");
        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"));
    }

    [FixedAvaloniaFact]
    public async Task ExportPagesToImages_CopyForbidden_BlocksDirectCaller_AndWritesNoFiles()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);
        var outputFolder = Path.Combine(_tempDir, "blocked-bulk-export");
        Directory.CreateDirectory(outputFolder);

        await vm.ExportPagesToImagesAsync(outputFolder);

        Directory.GetFiles(outputFolder).Should().BeEmpty(
            "the scripting-reachable bulk export entry point must enforce the same copy gate as the command");
        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"));
    }

    // ---- edit / annotate -------------------------------------------------

    [FixedAvaloniaFact]
    public async Task ToggleTypewriterMode_ModifyForbidden_StaysOff_WithToast()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        await vm.ToggleTypewriterModeCommand.Execute();

        vm.IsTypewriterMode.Should().BeFalse("the fixture denies /P bit 4 (modify)");
        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"));
    }

    [FixedAvaloniaFact]
    public async Task ToggleFormAuthoringMode_ModifyForbidden_StaysOff_WithToast()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        await vm.ToggleFormAuthoringModeCommand.Execute();

        vm.IsFormAuthoringMode.Should().BeFalse("the fixture denies /P bit 4 (modify)");
        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"));
    }

    [FixedAvaloniaFact]
    public async Task ToggleFormAuthoringMode_AnnotateForbiddenButModifyAllowed_StaysOff_WithToast()
    {
        // Table 22: creating form fields needs bit 6 AND bit 4. Bit 6 (value 32) cleared, bit 4 kept.
        var fixturePath = SaveWithPermissions("Bit 6 cleared", -4 & ~32L);
        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath);

        await vm.ToggleFormAuthoringModeCommand.Execute();

        vm.IsFormAuthoringMode.Should().BeFalse("bit 4 alone does not grant creating form fields");
        toasts.Should().ContainSingle(t =>
            t.Message.Contains("Blocked by document permissions") &&
            t.Details != null && t.Details.Contains("/P bits 4 and 6"));
    }

    [FixedAvaloniaFact]
    public async Task AddStickyNoteAnnotation_AnnotateForbidden_BlocksWithToast()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        await vm.AddStickyNoteAnnotationAsync("note text");

        toasts.Should().ContainSingle(t => t.Message.Contains("Blocked by document permissions"),
            "the fixture denies /P bit 6 (annotate)");
    }

    // ---- page assembly (#1850) --------------------------------------------

    [FixedAvaloniaFact]
    public async Task RotatePageRightCommand_AssembleForbidden_LeavesThePage_WithToast()
    {
        var fixturePath = SaveWithPermissions("Assemble-forbidden page", -4 & ~1024L); // bit 11 cleared
        var (vm, toasts) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath);

        await vm.RotatePageRightCommand.Execute();

        vm.SaveDocumentForTests!.GetPage(1).Rotation.Should().Be(0, "the fixture denies /P bit 11 (assemble)");
        vm.CanUndo.Should().BeFalse("a refused rotation records no undo step");
        toasts.Should().ContainSingle(t => t.Details != null && t.Details.Contains("Blocked by document permissions"),
            "the rotate command used to log a failure and show nothing");
    }

    // ---- form fill (#1874) ----------------------------------------------

    /// <summary>
    /// A text field holding "Original", an unchecked checkbox and a choice set to "US", each
    /// with the /TU qpdf reports it by; encrypted with <paramref name="permissions"/>, or left
    /// plain when null.
    /// </summary>
    private string SaveFormWithPermissions(long? permissions)
    {
        var plainPath = Path.Combine(_tempDir, "plain-form.pdf");
        using (var plain = PdfDocument.CreateNew())
        {
            plain.Pages.AddBlank();
            plain.AddTextField(1, new PdfRectangle(72, 600, 300, 624), "name", defaultValue: "Original", tooltip: "name");
            plain.AddCheckBox(1, new PdfRectangle(72, 560, 92, 580), "accept", tooltip: "accept");
            plain.AddChoiceField(1, new PdfRectangle(72, 520, 200, 540), "country", new[] { "US", "UK" },
                defaultValue: "US", tooltip: "country");
            plain.Save(plainPath);
        }
        return permissions is { } p ? Encrypt(plainPath, p) : plainPath;
    }

    /// <summary>
    /// #1874: the viewer stored a form edit BEFORE the form-fill check ran, so a refused edit
    /// stayed in the field and the next save wrote it. Typed and toggled with real input in the
    /// default (continuous) view (the choice is picked by setting its selection, as the overlay
    /// tests do), saved through Save As, which no /P bit gates, and read back with qpdf, not excise.
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData(true, false)]   // /P clears bits 6 and 9: refused
    [InlineData(false, false)]  // unencrypted control: lands
    [InlineData(true, true)]    // IgnoreDocumentPermissions overrides: lands
    public async Task FillingAForm_InTheContinuousView_FollowsTheFormFillPermission_InTheSavedFile(
        bool fillForbidden, bool ignorePermissions)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var path = SaveFormWithPermissions(fillForbidden ? -4 & ~(32L | 256L) : null);
        var saved = Path.Combine(_tempDir, "filled.pdf");
        var lands = !fillForbidden || ignorePermissions;
        var (vm, toasts) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        try
        {
            window.Show();
            await Task.Delay(200);
            await vm.LoadDocumentAsync(path);
            vm.IgnoreDocumentPermissions = ignorePermissions;
            vm.ViewMode.Should().Be(PdfViewMode.Continuous, "continuous scroll is the default view");
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;

            var box = await ContinuousFormFillTests.WaitForFieldOnPageAsync<TextBox>(window, viewer, "name");
            await ClickCentreAsync(window, box);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                box.SelectAll();
                window.KeyTextInput("Typed");
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            });
            var checkBox = await ContinuousFormFillTests.WaitForFieldOnPageAsync<CheckBox>(window, viewer, "accept");
            await ClickCentreAsync(window, checkBox);
            var combo = await ContinuousFormFillTests.WaitForFieldOnPageAsync<ComboBox>(window, viewer, "country");
            await Dispatcher.UIThread.InvokeAsync(() => combo.SelectedItem = "UK");
            await Task.Delay(100);

            box.Text.Should().Be(lands ? "Typed" : "Original", "a refused edit must not stay on screen as if stored");
            checkBox.IsChecked.Should().Be(lands);
            combo.SelectedItem.Should().Be(lands ? "UK" : "US");
            vm.CanUndo.Should().Be(lands, "a refused edit records no undo step");
            vm.FileState.HasUnsavedChanges.Should().Be(lands);
            toasts.Where(t => t.Message.Contains("Blocked by document permissions")
                    && t.Details != null && t.Details.Contains("/P bit 6 or 9"))
                .Should().HaveCount(lands ? 0 : 3, "each refused edit is told to the user, once");

            await vm.SaveFileAsAsync(saved);
            ContinuousFormFillTests.QpdfFormFields(saved).ValueByTooltip.Should().Equal(
                new Dictionary<string, string?>
                {
                    ["name"] = lands ? "Typed" : "Original",
                    ["accept"] = lands ? "/Yes" : "/Off",
                    ["country"] = lands ? "UK" : "US",
                },
                "a document whose /P forbids filling forms must save its fields unchanged, " +
                "and the permission must not block an unrestricted document or the override");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task ClickCentreAsync(MainWindow window, Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
        });
    }

    // ---- what must KEEP working -----------------------------------------

    [Fact]
    public void Search_CopyForbiddenDocument_StillFindsText()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

        // Search is excise-internal extraction — the accessibility carve-out
        // and plain layering sense both say it must not be gated by bit 5.
        var searchService = new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        var matches = searchService.Search(fixturePath!, "garçon");

        matches.Should().NotBeEmpty("search must keep working on copy-forbidden documents");
    }

    [Fact]
    public async Task Rendering_CopyForbiddenDocument_StillRenders()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

        using var document = PdfDocument.Open(File.ReadAllBytes(fixturePath!));
        using var bitmap = await Task.Run(() => new SkiaRenderer().RenderPage(
            document.GetPage(1),
            new RenderOptions { Dpi = 72 }));

        bitmap.Should().NotBeNull("on-screen rendering must keep working on copy-forbidden documents");
    }

    // ---- scripting surface ----------------------------------------------

    [FixedAvaloniaFact]
    public async Task ScriptExtractAllText_CopyForbidden_Throws_WithAccessibilityAndOverrideGuidance()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

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
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

        var (vm, _) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);

        var text = vm.ExtractAllText(forAccessibility: true);

        text.Should().Contain("garçon",
            "the fixture grants /P bit 10 (extract for accessibility) while denying bit 5");
    }

    [FixedAvaloniaFact]
    public async Task ScriptExtractAllText_IgnoreDocumentPermissions_Overrides()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, TestRepoLayout.AbsenceReason("restricted poppler corpus fixture", RestrictedFixtureRelativePath));

        var (vm, _) = await CreateViewModelWithRestrictedFixtureAsync(fixturePath!);
        vm.IgnoreDocumentPermissions = true;

        vm.ExtractAllText().Should().Contain("garçon");
    }
}
