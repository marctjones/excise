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
}
