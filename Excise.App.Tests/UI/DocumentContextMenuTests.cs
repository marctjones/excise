using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// The document view's right-click menu (#1659). Right-clicking a page used to do
/// nothing. The click is a real pointer event on the viewer, and each item is
/// checked to run the same command the menu bar runs, not a copy of it.
/// </summary>
[Collection("AvaloniaTests")]
public class DocumentContextMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-ctx-{Guid.NewGuid():N}");
    public DocumentContextMenuTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<(MainWindowViewModel Vm, MainWindow Window, PdfViewerControl Viewer)> OpenAsync()
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);
        await vm.LoadDocumentAsync(path);
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        await Task.Delay(300);
        window.UpdateLayout();
        return (vm, window, viewer);
    }

    private static async Task RightClickAsync(MainWindow window, PdfViewerControl viewer)
    {
        var centre = viewer.TranslatePoint(
            new Point(viewer.Bounds.Width / 2, viewer.Bounds.Height / 2), window)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(centre, MouseButton.Right);
            window.MouseUp(centre, MouseButton.Right);
        });
        await Task.Delay(150);
    }

    private static MenuItem Item(PdfViewerControl viewer, string headerContains) =>
        viewer.ContextMenu!.Items.OfType<MenuItem>()
            .Single(i => i.Header is string h && h.Contains(headerContains, StringComparison.Ordinal));

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task RightClickOnAPage_OpensAMenu_WithPageActionsOnly()
    {
        var (_, window, viewer) = await OpenAsync();

        await RightClickAsync(window, viewer);

        viewer.ContextMenu.Should().NotBeNull();
        viewer.ContextMenu!.IsOpen.Should().BeTrue("a real right-click on the page must open the menu");
        Item(viewer, "Rotate _Right").IsVisible.Should().BeTrue();
        Item(viewer, "Remove This").IsVisible.Should().BeTrue();
        Item(viewer, "_Highlight").IsVisible.Should().BeFalse(
            "selection actions are meaningless with nothing selected");
        Item(viewer, "_Copy").IsVisible.Should().BeFalse();

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task PageAction_FromTheMenu_RunsTheRealCommand_AndIsUndoable()
    {
        var (vm, window, viewer) = await OpenAsync();
        await RightClickAsync(window, viewer);

        Item(viewer, "Rotate _Right").Command!.Execute(null);
        await Task.Delay(300);

        vm.UndoMenuHeader.Should().Be("_Undo Rotate page right",
            "the menu item runs the same command as Edit ▸ Rotate, history included");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task WithASelection_TheMenuOffersMarkupCopyAndRedact_AndRightClickKeepsTheSelection()
    {
        var (vm, window, viewer) = await OpenAsync();
        vm.IsTextSelectionMode = true;
        vm.SelectedText = "some selected text";
        vm.CurrentTextSelectionPageArea = PdfPageRect.FromContentPoints(
            1, new PdfRectangle(72, 600, 300, 620));
        vm.HasTextSelection.Should().BeTrue();

        await RightClickAsync(window, viewer);

        vm.HasTextSelection.Should().BeTrue(
            "a right-press must not start a new selection and clear the one the menu acts on");
        foreach (var header in new[] { "_Copy", "_Highlight", "_Underline", "Strike_through", "S_quiggly", "_Redact Selection" })
            Item(viewer, header).IsVisible.Should().BeTrue($"'{header}' is a selection action");

        Item(viewer, "_Redact Selection").Command!.Execute(null);
        vm.RedactionWorkflow.PendingCount.Should().Be(1,
            "Redact on a selection marks the area through the shared redaction workflow");

        window.Close();
    }
    // ── #1817: page actions act on the page that was RIGHT-CLICKED ──────────────────────────

    private static string Pdfinfo(string pdfPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("pdfinfo", $"-f 1 -l 99 \"{pdfPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        // #1068/#1516: drain both pipes concurrently and bound the wait.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("pdfinfo did not exit within 30s (#1516).");
        }
        _ = stderr.GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult();
    }

    private static int PageRotation(string pdfinfoOutput, int page)
    {
        var m = System.Text.RegularExpressions.Regex.Match(pdfinfoOutput, $@"Page\s+{page} rot:\s+(\d+)");
        m.Success.Should().BeTrue($"pdfinfo must report page {page}'s rotation");
        return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task PageActionFromTheMenu_ActsOnTheRightClickedPage_NotTheViewportPage()
    {
        Assert.SkipWhen(!System.IO.File.Exists("/opt/homebrew/bin/pdfinfo") && !System.IO.File.Exists("/usr/bin/pdfinfo")
            && !System.IO.File.Exists("/usr/local/bin/pdfinfo"), "pdfinfo (poppler) is not installed [requires: tool:pdfinfo]");

        var path = System.IO.Path.Combine(_dir, "four-small.pdf");
        using (var doc = PdfDocument.CreateNew())
        {
            for (var i = 0; i < 4; i++) doc.Pages.AddBlank(200, 200);
            doc.Save(path);
        }

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);
        await vm.LoadDocumentAsync(path);
        vm.ViewMode = PdfViewMode.Continuous;
        vm.ZoomLevel = 0.3;   // several small pages visible at once
        await Task.Delay(600);
        window.UpdateLayout();
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        var items = viewer.FindControl<ItemsControl>("ContinuousItems")!;

        // Right-click the centre of the THIRD page, not the one the viewport considers current.
        var container = items.ContainerFromIndex(2) as Control;
        container.Should().NotBeNull("page 3 must be realized at this zoom");
        var border = (container as global::Avalonia.Controls.Presenters.ContentPresenter)?.Child as Border ?? container as Border;
        var centre = border!.TranslatePoint(new Point(border.Bounds.Width / 2, border.Bounds.Height / 2), window)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(centre, MouseButton.Right);
            window.MouseUp(centre, MouseButton.Right);
        });
        await Task.Delay(150);

        viewer.ContextMenuPageNumber.Should().Be(3, "the menu was opened on the third page");
        Item(viewer, "Rotate _Right").Command!.Execute(null);
        await Task.Delay(400);

        var saved = System.IO.Path.Combine(_dir, "four-small-out.pdf");
        await vm.SaveFileAsAsync(saved);
        var info = Pdfinfo(saved);
        PageRotation(info, 3).Should().Be(90, "the right-clicked page is the one rotated");
        foreach (var other in new[] { 1, 2, 4 })
            PageRotation(info, other).Should().Be(0, $"page {other} was not right-clicked and must not rotate");

        window.Close();
    }
}
