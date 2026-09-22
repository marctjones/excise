using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Screenshot capture for the UX/icon audit (<c>scripts/run-ux-icon-audit.sh</c>).
/// The three .axaml source-text audits that used to sit beside it (vector icons in
/// the toolbar/menu, every PathIcon resource resolving, the toolbar CommandIds and
/// tooltips) moved to <c>scripts/check-shell-xaml.sh</c>, a t0 gate (#1773): they
/// read three files and need no headless Avalonia session.
/// </summary>
[Collection("AvaloniaTests")]
public class VisualPolishAuditTests
{
    [FixedAvaloniaFact]
    public async Task CoreWorkflowScreenshots_AreCapturedForUxIconAudit()
    {
        var output = GetAuditOutputDirectory();
        Directory.CreateDirectory(output);

        var pdfPath = Path.Combine(output, "ux-audit-sample.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 4);

        var captures = new List<object>();

        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow
            {
                DataContext = vm,
                Width = 1280,
                Height = 900,
            };
            window.Show();

            captures.Add(await CaptureWindow(window, output, "01-empty-open.png",
                "Empty/open state with vector document icon and Open File command."));

            await vm.LoadDocumentAsync(pdfPath);
            vm.CurrentPageIndex = 1;
            vm.IsThumbnailsSidebarVisible = true;
            vm.IsOutlineSidebarVisible = true;
            await WaitForUi();
            captures.Add(await CaptureWindow(window, output, "02-document-navigation-page-organization.png",
                "Document navigation, thumbnails, page organization controls, zoom, and rotate buttons."));

            vm.IsSearchVisible = true;
            vm.SearchText = "Page";
            await WaitForUi();
            captures.Add(await CaptureWindow(window, output, "03-search.png",
                "Search bar, result controls, and search results panel."));

            vm.IsSearchVisible = false;
            vm.IsRedactionMode = true;
            vm.IsClipboardSidebarVisible = true;
            await WaitForUi();
            captures.Add(await CaptureWindow(window, output, "04-redaction.png",
                "Redaction mode, apply button, and pending-redaction sidebar area."));

            // #1476: a non-maximized width, so the audit sees the toolbar degrade
            // (labels dropped, then icons shrunk, then low-priority items hidden)
            // rather than scroll sideways or wrap.
            window.Width = 1024;
            await WaitForUi();
            captures.Add(await CaptureWindow(window, output, "04b-narrow-toolbar-1024.png",
                "Toolbar at 1024 px in redaction mode: one row, no horizontal scroll bar, degraded by priority, zoom visible (#1476)."));
            window.Width = 1280;
            await WaitForUi();

            vm.IsRedactionMode = false;
            vm.IsFormAuthoringMode = true;
            await WaitForUi();
            captures.Add(await CaptureWindow(window, output, "05-forms.png",
                "Form authoring mode, field-type picker, auto-detect affordance."));

            vm.IsFormAuthoringMode = false;
            vm.IsTypewriterMode = true;
            vm.SelectedText = "Page 2";
            vm.CurrentTextSelectionPageArea = Excise.Core.Document.PdfPageRect.ViewerDips(2, 20, 20, 150, 24, 120);
            await WaitForUi();
            captures.Add(await CaptureWindow(window, output, "06-typewriter-annotations.png",
                "Typewriter mode plus highlight and sticky-note annotation commands."));

            window.Close();

            var preferences = new PreferencesWindow
            {
                DataContext = new PreferencesViewModel(),
                Width = 720,
                Height = 520,
            };
            preferences.Show();
            captures.Add(await CaptureWindow(preferences, output, "07-preferences.png",
                "Preferences dialog spacing, focusable footer actions, and accessible labels."));
            preferences.Close();

            var manifestPath = Path.Combine(output, "ux-icon-audit.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                generatedUtc = DateTimeOffset.UtcNow,
                issue = 559,
                captures,
            }, new JsonSerializerOptions { WriteIndented = true }));

            File.Exists(manifestPath).Should().BeTrue();
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    private static async Task<object> CaptureWindow(Window window, string output, string fileName, string description)
    {
        await WaitForUi();

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
        };
    }

    private static async Task WaitForUi()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(50);
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
