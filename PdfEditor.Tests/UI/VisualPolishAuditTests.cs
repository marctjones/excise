using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using PdfEditor.Tests.Utilities;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using Xunit;

namespace PdfEditor.Tests.UI;

[Collection("AvaloniaTests")]
public class VisualPolishAuditTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void MainShell_UsesVectorIconsForToolbarAndMenuAffordances()
    {
        var mainWindow = Read("PdfEditor/Views/MainWindow.axaml");
        var iconResources = Read("PdfEditor/Styles/Icons.axaml");
        var styles = Read("PdfEditor/Styles/Controls.axaml");

        mainWindow.Should().Contain("IconFolderOpen");
        mainWindow.Should().Contain("IconRedact");
        mainWindow.Should().Contain("PathIcon Classes=\"toolbar-icon\"");
        mainWindow.Should().Contain("PathIcon Classes=\"menu-icon\"");
        mainWindow.Should().NotContain("Content=\"📁");
        mainWindow.Should().NotContain("Content=\"💾");
        mainWindow.Should().NotContain("Content=\"🔍");
        mainWindow.Should().NotContain("Content=\"📋");
        mainWindow.Should().NotContain("Text=\"📄");

        iconResources.Should().Contain("IconFolderOpen");
        iconResources.Should().Contain("IconSave");
        iconResources.Should().Contain("IconSearch");
        iconResources.Should().Contain("IconRedact");
        iconResources.Should().Contain("IconPage");
        styles.Should().Contain("PathIcon.toolbar-icon");
        styles.Should().Contain("PathIcon.menu-icon");
    }

    [Fact]
    public void MainShell_PathIconResources_AllResolve()
    {
        var mainWindow = Read("PdfEditor/Views/MainWindow.axaml");
        var iconResources = Read("PdfEditor/Styles/Icons.axaml");

        var referencedKeys = Regex.Matches(
                mainWindow,
                "PathIcon\\b[^>]*\\bData=\"\\{StaticResource (?<key>Icon[A-Za-z0-9_]+)\\}\"",
                RegexOptions.Singleline)
            .Select(match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var definedKeys = Regex.Matches(
                iconResources,
                "x:Key=\"(?<key>Icon[A-Za-z0-9_]+)\"")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        referencedKeys.Should().NotBeEmpty("the main shell should use vector icon resources");
        referencedKeys.Where(key => !definedKeys.Contains(key)).Should().BeEmpty(
            "every menu and toolbar PathIcon StaticResource must resolve at runtime");
    }

    [Fact]
    public void ToolbarIconButtons_HaveTooltipsAndAccessibilityCommandIds()
    {
        var mainWindow = Read("PdfEditor/Views/MainWindow.axaml");
        var requiredCommands = new[]
        {
            "view.toggleOutline",
            "view.toggleThumbnails",
            "view.toggleContinuous",
            "app.open",
            "app.save",
            "form.saveFlattenedCopy",
            "edit.selectTextMode",
            "edit.typewriterMode",
            "annotation.addHighlight",
            "annotation.addStickyNote",
            "redaction.toggleMode",
            "redaction.apply",
            "form.toggleAuthoring",
            "form.autoDetectFields",
            "search.open",
            "document.rotateLeft",
            "document.rotateRight",
            "view.zoomOut",
            "view.zoomIn",
            "view.zoomFitWidth",
        };

        foreach (var command in requiredCommands)
        {
            mainWindow.Should().Contain($"CommandId=\"{command}\"",
                $"{command} should remain part of the audited toolbar/menu surface");
        }

        mainWindow.Should().Contain("ToolTip.Tip=\"Open PDF (Ctrl+O)\"");
        mainWindow.Should().Contain("ToolTip.Tip=\"Redaction Mode (R)\"");
        mainWindow.Should().Contain("ToolTip.Tip=\"Form Authoring Mode");
        mainWindow.Should().Contain("ToolTip.Tip=\"Find Text (Ctrl+F)\"");
    }

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
            var vm = new MainWindowViewModel();
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

            vm.IsRedactionMode = false;
            vm.IsFormAuthoringMode = true;
            await WaitForUi();
            captures.Add(await CaptureWindow(window, output, "05-forms.png",
                "Form authoring mode, field-type picker, auto-detect affordance."));

            vm.IsFormAuthoringMode = false;
            vm.IsTypewriterMode = true;
            vm.SelectedText = "Page 2";
            vm.CurrentTextSelectionPageArea = Pdfe.Core.Document.PdfPageRect.ViewerDips(2, 20, 20, 150, 24, 120);
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
        renderTarget.Save(path);

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
        var fromEnvironment = Environment.GetEnvironmentVariable("PDFE_UX_AUDIT_OUTPUT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);

        return Path.Combine(AppContext.BaseDirectory, "UI", "test-output", "ux-icon-audit");
    }

    private static string Read(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pdfe.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
