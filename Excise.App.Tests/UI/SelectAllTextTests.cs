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
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Select All Text (#1814): Edit menu / Cmd+A, and the right-click menu on the page under the pointer.
/// The oracle for "what the page says" is mutool, not excise's own extractor.
/// </summary>
[Collection("AvaloniaTests")]
public class SelectAllTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-selall-{Guid.NewGuid():N}");
    public SelectAllTextTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Squash(string? text) => new string((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());

    private async Task<(MainWindowViewModel Vm, MainWindow Window, PdfViewerControl Viewer, string Path)> OpenAsync(PdfViewMode mode)
    {
        var path = System.IO.Path.Combine(_dir, "three.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);
        await vm.LoadDocumentAsync(path);
        vm.ViewMode = mode;
        await Task.Delay(500);
        window.UpdateLayout();
        return (vm, window, window.FindControl<PdfViewerControl>("PdfViewerControl")!, path);
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task RightClickSelectAll_SelectsExactlyTheRightClickedPagesText()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var (vm, window, viewer, path) = await OpenAsync(PdfViewMode.Continuous);
        vm.ZoomLevel = 0.2;                                   // all three pages fully visible
        await Task.Delay(600);
        window.UpdateLayout();

        var items = viewer.FindControl<ItemsControl>("ContinuousItems")!;
        var container = (Control)items.ContainerFromIndex(2)!;   // page 3, deliberately not the most-visible page
        var border = ((container as global::Avalonia.Controls.Presenters.ContentPresenter)?.Child as Border ?? container as Border)!;
        var centre = border.TranslatePoint(new Point(border.Bounds.Width / 2, border.Bounds.Height / 2), window)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(centre, MouseButton.Right);
            window.MouseUp(centre, MouseButton.Right);
        });
        await Task.Delay(150);

        // Precondition that makes the test discriminating: the right-clicked page must differ from the
        // page the viewport would pick, or ignoring the right-click would pass by coincidence.
        viewer.MostVisiblePage.Should().NotBe(3, "the test must right-click a page other than the most visible one");
        viewer.ContextMenuPageNumber.Should().Be(3);

        var item = viewer.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header is string h && h.Contains("Select _All", StringComparison.Ordinal));
        item.Command!.Execute(null);
        await Task.Delay(300);

        Squash(vm.SelectedText).Should().Be(Squash(MutoolTextExtractor.ExtractPage(path, 3)),
            "Select All on the right-clicked page 3 must select exactly the text mutool reads there");
        vm.SelectedText.Should().NotContain("Page 1 Content", "page 1 was not the right-clicked page");
        vm.SelectedText.Should().NotContain("Page 2 Content", "page 2 was not the right-clicked page");
        vm.HasTextSelection.Should().BeTrue("the selection must carry a page area so Highlight/Redact can act on it");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task CtrlA_InContinuousView_SelectsThePageFillingTheViewport()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var (vm, window, _, path) = await OpenAsync(PdfViewMode.Continuous);

        await window.PressKeyAsync(Key.A, RawInputModifiers.Control);
        await Task.Delay(300);

        Squash(vm.SelectedText).Should().Be(Squash(MutoolTextExtractor.ExtractPage(path, 1)));
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task CtrlA_InSinglePageView_SelectsTheDisplayedPage()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var (vm, window, _, path) = await OpenAsync(PdfViewMode.SinglePage);
        vm.CurrentPageIndex = 2;
        await Task.Delay(500);

        await window.PressKeyAsync(Key.A, RawInputModifiers.Control);
        await Task.Delay(300);

        Squash(vm.SelectedText).Should().Be(Squash(MutoolTextExtractor.ExtractPage(path, 3)));
        window.Close();
    }
}
