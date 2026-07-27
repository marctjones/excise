using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #816 batch 4: annotate/style + dialog/misc toolbar-menu commands.
///
/// The gap this closes: these effects were previously proven only by calling
/// the underlying VIEWMODEL METHOD directly (<c>vm.AddStickyNoteAnnotationAsync(...)</c>,
/// <c>vm.SetTypewriterColor(...)</c>) or via a <c>CommandBindingSweep</c>/
/// <c>NotBeNull</c> check — never by executing the actual
/// <c>ReactiveCommand</c> the toolbar/menu is bound to. A button mis-wired to
/// the wrong method would pass every one of those tests (the same
/// false-coverage pattern #815 hid a real bug behind). Every test here
/// executes the real <c>*Command.Execute()</c> and asserts the real effect.
/// </summary>
[Collection("AvaloniaTests")]
public class AnnotateAndDialogCommandTests
{
    // ---------------------------------------------------------------
    // Annotate / style
    // ---------------------------------------------------------------

    [FixedAvaloniaFact]
    public async Task AddHighlightAnnotationFromSelectionCommand_WithActiveSelection_CreatesHighlightAnnotation()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Selectable clause text");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        vm.CurrentTextSelectionPageArea = PdfPageRect.ViewerDips(
            1, x: 120, y: 120, width: 180, height: 30,
            renderDpi: MainWindowViewModel.DefaultViewerRenderDpi);
        vm.SelectedText = "Selected clause";

        await vm.AddHighlightAnnotationFromSelectionCommand.Execute();

        await vm.SaveFileAsAsync(output);
        using var saved = PdfDocument.Open(output);
        saved.GetPage(1).GetAnnotations().Should().Contain(a =>
            a.Subtype == PdfAnnotationSubtype.Highlight && a.Contents == "Selected clause",
            "executing the real command must produce a real Highlight annotation on the page, not just prove the underlying method works");

        window.Close();
        Cleanup(dir);
    }

    [FixedAvaloniaFact]
    public async Task AddStickyNoteAnnotationCommand_CreatesTextAnnotation()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Body text");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        // No override parameter — this is exactly what the toolbar button and
        // menu item invoke. The default (null) dialog service's PromptTextAsync
        // returns the passed-through default text, so the command must still
        // create a sticky note end to end.
        await vm.AddStickyNoteAnnotationCommand.Execute();

        await vm.SaveFileAsAsync(output);
        using var saved = PdfDocument.Open(output);
        saved.GetPage(1).GetAnnotations().Should().Contain(a =>
            a.Subtype == PdfAnnotationSubtype.Text && a.Contents == MainWindowViewModel.DefaultStickyNoteText,
            "executing the real toolbar command (no override) must create a real Text annotation on the page");

        window.Close();
        Cleanup(dir);
    }

    [FixedAvaloniaFact]
    public async Task SetTypewriterColorCommand_AppliesHexColorToTheActiveBox()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Original text");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        vm.IsTypewriterMode = true;
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);

        await vm.SetTypewriterColorCommand.Execute("#00FF00");

        var op = vm.TypewriterTextOperations.Single();
        op.Style.Color.R.Should().BeApproximately(0.0, 0.01);
        op.Style.Color.G.Should().BeApproximately(1.0, 0.01);
        op.Style.Color.B.Should().BeApproximately(0.0, 0.01);

        window.Close();
        Cleanup(dir);
    }

    // ---------------------------------------------------------------
    // Dialogs
    // ---------------------------------------------------------------

    [FixedAvaloniaFact]
    public async Task VerifySignaturesCommand_OnLoadedDocument_SurfacesVerificationSummary()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Unsigned content");

        var dialog = new RecordingDialogService();
        var vm = CreateViewModelWithDialog(dialog);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        await vm.VerifySignaturesCommand.Execute();

        dialog.Messages.Should().ContainSingle(m => m.Title == "Verify Signatures",
            "executing the real command must run the verification workflow, not merely exist");
        dialog.Messages.Single().Message.Should().Contain("No digital signatures were found",
            "the results surface must actually be populated with the real verification outcome");

        window.Close();
        Cleanup(dir);
    }

    [FixedAvaloniaFact]
    public async Task SecurityCommand_OnLoadedDocument_OpensTheSecurityDialogWindow()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Body");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        vm.MainWindowResolver = () => window;

        // ShowSecurityDialogAsync awaits ShowDialog(owner) until the dialog
        // closes, so awaiting Execute() directly here would hang the test
        // until we've already closed the window we haven't found yet. Fire
        // it and pump the dispatcher instead.
        vm.SecurityCommand.Execute().Subscribe();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var securityDialog = window.OwnedWindows.OfType<SecurityDialog>().SingleOrDefault();
        securityDialog.Should().NotBeNull(
            "executing SecurityCommand must open the real Security dialog window, not just be wired to something");

        securityDialog!.Close();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        window.Close();
        Cleanup(dir);
    }

    [FixedAvaloniaFact]
    public async Task ShowPreferencesCommand_OpensThePreferencesWindow()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Body");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        vm.MainWindowResolver = () => window;

        await vm.ShowPreferencesCommand.Execute();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var preferencesWindow = window.OwnedWindows.OfType<PreferencesWindow>().SingleOrDefault();
        preferencesWindow.Should().NotBeNull(
            "executing ShowPreferencesCommand must open the real Preferences window, not just be wired to something");

        preferencesWindow!.Close();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        window.Close();
        Cleanup(dir);
    }

    [FixedAvaloniaFact]
    public async Task AboutCommand_OpensTheAboutWindow()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        vm.MainWindowResolver = () => window;

        await vm.AboutCommand.Execute();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var aboutWindow = window.OwnedWindows.OfType<AboutWindow>().SingleOrDefault();
        aboutWindow.Should().NotBeNull(
            "executing AboutCommand must open the real About window, not just be wired to something");

        aboutWindow!.Close();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        window.Close();
    }

    [FixedAvaloniaFact]
    public async Task ShowShortcutsCommand_RequestsTheKeyboardShortcutsDialog()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        vm.MainWindowResolver = () => window;

        FluentAvalonia.UI.Controls.FAContentDialog? requested = null;
        vm.KeyboardShortcutsDialogRequested = dialog => requested = dialog;

        await vm.ShowShortcutsCommand.Execute();

        requested.Should().NotBeNull(
            "executing ShowShortcutsCommand must actually construct and request the real shortcuts dialog, not just be wired to something");
        requested!.Title.Should().Be("Keyboard Shortcuts");
        requested.Content.Should().BeOfType<string>()
            .Which.Should().Contain("Ctrl+F - Find");

        window.Close();
    }

    [FixedAvaloniaFact]
    public async Task ShowDocumentationCommand_InvokesTheDocumentationOpenPath()
    {
        var vm = new MainWindowViewModel();
        var opened = new System.Collections.Generic.List<string>();
        vm.DocumentationOpener = target => opened.Add(target);

        await vm.ShowDocumentationCommand.Execute();

        opened.Should().ContainSingle(
            "executing the real command must invoke the doc-open path exactly once, without launching a real external app/browser in the test host");
        opened[0].Should().Match(t => t.EndsWith("README.md", StringComparison.Ordinal) ||
                                       t.Contains("github.com/marctjones/excise", StringComparison.Ordinal),
            "the invoked target must be the README path or the GitHub fallback URL the command documents");
    }

    // ---------------------------------------------------------------
    // Misc
    // ---------------------------------------------------------------

    [FixedAvaloniaFact]
    public async Task ZoomFitPageCommand_ComputesTheFitPageRatio_DistinctFromFitWidth()
    {
        var (source, _, dir) = MakePaths();
        const double widthPoints = 400;
        const double heightPoints = 800;
        TestPdfGenerator.CreateCustomSizePdf(source, widthPoints, heightPoints);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        vm.ViewportWidth = 1000;
        vm.ViewportHeight = 700;

        const double dipsPerPoint = 96.0 / 72.0;
        var pageWidthDip = widthPoints * dipsPerPoint;
        var pageHeightDip = heightPoints * dipsPerPoint;
        const double margin = 8;
        var expectedFitWidth = Math.Clamp((vm.ViewportWidth - margin) / pageWidthDip, 0.25, 5.0);
        var expectedFitPage = Math.Clamp(
            Math.Min((vm.ViewportWidth - margin) / pageWidthDip, (vm.ViewportHeight - margin) / pageHeightDip),
            0.25, 5.0);
        Math.Abs(expectedFitPage - expectedFitWidth).Should().BeGreaterThan(0.1,
            "the fixture must be a non-square page/viewport combination or this test can't tell fit-page from fit-width apart");

        await vm.ZoomFitPageCommand.Execute();

        vm.ZoomLevel.Should().BeApproximately(expectedFitPage, 0.01,
            "ZoomFitPageCommand must compute the fit-PAGE ratio (bound by whichever dimension is the tighter constraint), not merely leave zoom > 0");
        Math.Abs(vm.ZoomLevel - expectedFitWidth).Should().BeGreaterThan(0.1,
            "on this non-square page, fit-page and fit-width must produce genuinely different zoom levels");

        window.Close();
        Cleanup(dir);
    }

    [FixedAvaloniaFact]
    public async Task GoToPageCommand_NavigatesToTheRequestedPage()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateMultiPagePdf(source, 5);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        vm.CurrentPageIndex.Should().Be(0);

        await vm.GoToPageCommand.Execute(3);

        vm.CurrentPageIndex.Should().Be(3,
            "executing the real command must move CurrentPageIndex, not just accept the call");

        window.Close();
        Cleanup(dir);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private sealed class RecordingDialogService : IUserDialogService
    {
        public System.Collections.Generic.List<(string Title, string Message)> Messages { get; } = new();

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private static MainWindowViewModel CreateViewModelWithDialog(IUserDialogService dialogService)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        return new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            new PdfDocumentService(NullLogger<PdfDocumentService>.Instance),
            new PdfRenderService(NullLogger<PdfRenderService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService(),
            dialogService: dialogService);
    }

    private static (string source, string output, string dir) MakePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Excise.AppAnnotateDialogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return (Path.Combine(tempDir, "source.pdf"), Path.Combine(tempDir, "output.pdf"), tempDir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
