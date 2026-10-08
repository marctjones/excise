using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using FluentAvalonia.Styling;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Screenshot capture for the UX/icon audit (<c>scripts/run-ux-icon-audit.sh</c>).
/// The three .axaml source-text audits that used to sit beside it (vector icons in
/// the toolbar/menu, every PathIcon resource resolving, the toolbar CommandIds and
/// tooltips) moved to <c>scripts/check-shell-xaml.sh</c>, a t0 gate (#1773): they
/// read three files and need no headless Avalonia session.
///
/// <para><b>What the captures are.</b> Synthetic headless renders for visual review,
/// not packaged-app save/reopen evidence (#1971). Each window is rendered under the
/// app's real theme and styles — FluentAvaloniaTheme plus <c>Styles/Brushes.axaml</c>
/// and <c>Styles/Controls.axaml</c> — which the shared TestApp does not load, so a
/// capture shows what a user sees (#1996). A capture is taken only once its state
/// is observably ready (search finished with matches, the page drawn after a mode
/// switch), under a bounded deadline instead of a fixed delay (#1971).</para>
/// </summary>
[Collection("AvaloniaTests")]
public class VisualPolishAuditTests
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    [FixedAvaloniaFact]
    public async Task CoreWorkflowScreenshots_AreCapturedForUxIconAudit()
    {
        var output = GetAuditOutputDirectory();
        Directory.CreateDirectory(output);

        var pdfPath = Path.Combine(output, "ux-audit-sample.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 4);

        var captures = new List<object>();
        var resources = AddAppResources();

        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = WithAppStyles(new MainWindow
            {
                DataContext = vm,
                Width = 1280,
                Height = 900,
            });
            window.Show();

            captures.Add(await CaptureWindow(window, output, "01-empty-open.png",
                "Empty/open state with vector document icon and Open File command."));

            await vm.LoadDocumentAsync(pdfPath);
            vm.CurrentPageIndex = 1;
            vm.IsThumbnailsSidebarVisible = true;
            vm.IsOutlineSidebarVisible = true;
            captures.Add(await CaptureWindow(window, output, "02-document-navigation-page-organization.png",
                "Document navigation, thumbnails, page organization controls, zoom, and rotate buttons.",
                () => PageIsDrawn(window)));

            vm.IsSearchVisible = true;
            vm.SearchText = "Page";
            captures.Add(await CaptureWindow(window, output, "03-search.png",
                "Search bar, result controls, and search results panel — after the search completed.",
                () => !vm.IsSearching && vm.SearchMatches.Count > 0 && PageIsDrawn(window)));

            vm.IsSearchVisible = false;
            vm.IsRedactionMode = true;
            vm.IsClipboardSidebarVisible = true;
            captures.Add(await CaptureWindow(window, output, "04-redaction.png",
                "Redaction mode, apply button, and pending-redaction sidebar area.",
                () => PageIsDrawn(window)));

            // #1476: a non-maximized width, so the audit sees the toolbar degrade
            // (labels dropped, then icons shrunk, then low-priority items hidden)
            // rather than scroll sideways or wrap.
            window.Width = 1024;
            captures.Add(await CaptureWindow(window, output, "04b-narrow-toolbar-1024.png",
                "Toolbar at 1024 px in redaction mode: one row, no horizontal scroll bar, degraded by priority, zoom visible (#1476).",
                () => PageIsDrawn(window)));
            window.Width = 1280;

            vm.IsRedactionMode = false;
            vm.IsFormAuthoringMode = true;
            captures.Add(await CaptureWindow(window, output, "05-forms.png",
                "Form authoring mode, field-type picker, auto-detect affordance — page redrawn after the mode switch.",
                () => vm.IsFormAuthoringMode && PageIsDrawn(window)));

            vm.IsFormAuthoringMode = false;
            vm.IsTypewriterMode = true;
            vm.SelectedText = "Page 2";
            vm.CurrentTextSelectionPageArea = Excise.Core.Document.PdfPageRect.ViewerDips(2, 20, 20, 150, 24, 120);
            captures.Add(await CaptureWindow(window, output, "06-typewriter-annotations.png",
                "Typewriter mode plus highlight and sticky-note annotation commands.",
                () => vm.IsTypewriterMode && PageIsDrawn(window)));

            window.Close();

            // Settings and dialogs (#1996): previously absent from the audit.
            await CaptureDialog(captures, output, new PreferencesWindow
            {
                DataContext = new PreferencesViewModel(),
                Width = 720,
                Height = 520,
            }, "07-preferences.png", "Preferences: cards, readable option labels, platform-ordered footer.");
            await CaptureDialog(captures, output, new SecurityDialog
            {
                DataContext = new SecurityDialogViewModel(
                    true, _ => true, (_, _, _) => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null)),
            }, "08-security.png", "Document Security: password confirmation, Show passwords, footer order.");
            await CaptureDialog(captures, output, new MakeSearchableDialog
            {
                DataContext = new MakeSearchableDialogViewModel(false, (_, _, _, _) => throw new NotSupportedException()),
            }, "09-make-searchable.png", "Make Searchable with the OCR-engine-missing InfoBar.");
            await CaptureDialog(captures, output, new BatesNumberingDialog { DataContext = new BatesNumberingDialogViewModel() },
                "10-bates.png", "Bates Numbering: label column, readable position names.");
            await CaptureDialog(captures, output, new ReduceFileSizeDialog { DataContext = new ReduceFileSizeDialogViewModel() },
                "11-reduce-file-size.png", "Reduce File Size preset picker.");
            await CaptureDialog(captures, output, new LinuxPrintDialog
            {
                DataContext = new LinuxPrintDialogViewModel([new CupsPrintQueue("Office", "idle", true)], pageCount: 4),
            }, "12-linux-print.png", "Linux printer chooser.");
            await CaptureDialog(captures, output, new AboutWindow(), "13-about.png", "About.");

            var manifestPath = Path.Combine(output, "ux-icon-audit.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                generatedUtc = DateTimeOffset.UtcNow,
                issue = 559,
                scope = "Synthetic headless captures under the app's real theme and styles; not packaged-app evidence (#1971, #1996).",
                captures,
            }, new JsonSerializerOptions { WriteIndented = true }));

            File.Exists(manifestPath).Should().BeTrue();
        }
        finally
        {
            Application.Current!.Resources.MergedDictionaries.Remove(resources);
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    /// <summary>
    /// The readiness check must be able to fail (#1971): an empty viewer — the state
    /// the old 05-forms capture recorded — is not a drawn page.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task PageIsDrawn_RejectsAViewerWithNoPage()
    {
        var resources = AddAppResources();
        try
        {
            var window = WithAppStyles(new MainWindow
            {
                DataContext = MainWindowViewModelTestFactory.Create(),
                Width = 1280,
                Height = 900,
            });
            window.Show();
            await WaitForUi();

            PageIsDrawn(window).Should().BeFalse("no document is open, so no page ink can be on screen");
            window.Close();
        }
        finally
        {
            Application.Current!.Resources.MergedDictionaries.Remove(resources);
        }
    }

    private static ResourceInclude AddAppResources()
    {
        var resources = new ResourceInclude(new Uri("avares://Excise.App/"))
            { Source = new Uri("avares://Excise.App/Styles/Brushes.axaml") };
        Application.Current!.Resources.MergedDictionaries.Add(resources);
        return resources;
    }

    private static T WithAppStyles<T>(T window) where T : Window
    {
        window.RequestedThemeVariant = ThemeVariant.Light;
        window.Styles.Add(new FluentAvaloniaTheme());
        window.Styles.Add(new StyleInclude(new Uri("avares://Excise.App/"))
            { Source = new Uri("avares://Excise.App/Styles/Controls.axaml") });
        return window;
    }

    private static async Task CaptureDialog(List<object> captures, string output, Window dialog, string fileName, string description)
    {
        WithAppStyles(dialog).Show();
        captures.Add(await CaptureWindow(dialog, output, fileName, description));
        dialog.Close();
    }

    /// <summary>
    /// True once the viewer shows page ink: it is not loading and its surface has
    /// dark (text) pixels. An empty canvas, or a page not yet drawn, has none.
    /// </summary>
    private static bool PageIsDrawn(Window window) => PageInk(window) >= MinInkPixels;

    /// <summary>Dark pixels on the viewer surface; -1 while there is no viewer or it is loading.</summary>
    private static int PageInk(Window window)
    {
        var viewer = window.GetVisualDescendants().OfType<PdfViewerControl>().FirstOrDefault();
        if (viewer == null || viewer.IsLoading || viewer.Bounds.Width < 1 || viewer.Bounds.Height < 1)
            return -1;
        using var rt = new RenderTargetBitmap(new PixelSize((int)viewer.Bounds.Width, (int)viewer.Bounds.Height));
        rt.Render(viewer);
        using var ms = new MemoryStream();
        rt.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        using var bmp = SKBitmap.Decode(ms);
        if (bmp == null)
            return -1;
        var dark = 0;
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 128 && c.Red + c.Green + c.Blue < 384)
                dark++;
        }
        return dark;
    }

    // The same ink test as GuiExpectedEffectTests (RGB sum below 384). The fixture's
    // two text lines draw a few hundred such pixels even at 67 % zoom; the empty
    // grey viewer canvas (sum ~696) and a white page draw none.
    private const int MinInkPixels = 40;

    private static async Task<object> CaptureWindow(
        Window window, string output, string fileName, string description, Func<bool>? ready = null)
    {
        await WaitForUi();
        if (ready != null)
        {
            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (!ready())
            {
                if (DateTime.UtcNow > deadline)
                {
                    // Keep the unready frame beside the others so the failure can be looked at.
                    var stale = Path.Combine(output, Path.GetFileNameWithoutExtension(fileName) + ".NOT-READY.png");
                    using (var rt = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height))))
                    {
                        rt.Render(window);
                        rt.Save(stale, PngBitmapEncoderOptions.Default);
                    }
                    throw new TimeoutException(
                        $"{fileName}: the captured state was not ready within {ReadyTimeout.TotalSeconds:0} s " +
                        $"(viewer ink {PageInk(window)} px, need {MinInkPixels}; -1 = no viewer or still loading); frame kept as {stale}.");
                }
                await Task.Delay(50);
                await WaitForUi();
            }
        }

        var width = Math.Max(1, (int)Math.Round(window.Bounds.Width > 0 ? window.Bounds.Width : window.Width));
        var height = Math.Max(1, (int)Math.Round(window.Bounds.Height > 0 ? window.Bounds.Height : window.Height));
        var path = Path.Combine(output, fileName);

        using var renderTarget = new RenderTargetBitmap(new PixelSize(width, height));
        renderTarget.Render(window);
        renderTarget.Save(path, PngBitmapEncoderOptions.Default);

        new FileInfo(path).Length.Should().BeGreaterThan(1024, $"{fileName} should be a real screenshot artifact");
        return new
        {
            file = path,
            description,
            width,
            height,
            readinessChecked = ready != null,
        };
    }

    private static async Task WaitForUi()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Dispatcher.UIThread.RunJobs();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static string GetAuditOutputDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("EXCISE_UX_AUDIT_OUTPUT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);

        return Path.Combine(AppContext.BaseDirectory, "UI", "test-output", "ux-icon-audit");
    }
}
