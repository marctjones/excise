using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// Headless-GUI test that drives MainWindow + PdfViewerControl through
/// a real Find workflow: load a PDF, set SearchText, wait, then
/// verify Rectangle children are actually present on the
/// SearchHighlightsLayer Canvas. This is the test that would have
/// caught the user's "search doesn't find anything visible" report;
/// the unit/integration tests pass against the service and VM in
/// isolation but didn't exercise the View glue that draws the
/// highlights.
/// </summary>
[Collection("AvaloniaTests")]
public class SearchHighlightOverlayTests
{
    private readonly ITestOutputHelper _out;
    public SearchHighlightOverlayTests(ITestOutputHelper o) { _out = o; }

    private const string PragmaticBook =
        "/home/marc/Downloads/business-success-with-open-source_P1.0.pdf";

    [FixedAvaloniaFact]
    public async Task SearchInPragmaticBook_DrawsHighlightRectangles()
    {
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(100);

        await vm.LoadDocumentAsync(PragmaticBook);

        // Trigger search the same way Ctrl+F → typing does in the GUI.
        vm.SearchText = "open source";

        // Poll for SearchMatches population — the VM debounces ~300 ms
        // and then walks the document. Cross-test dispatcher contention
        // can stretch this, so we give it generous slack.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(100);

        // Settle the highlight-overlay update which goes:
        //   VM.UpdateSearchHighlights → CurrentPageSearchHighlights.Add →
        //   MainWindow.OnSearchHighlightsChanged →
        //   PdfViewerControl.AddSearchHighlight → Rectangle in Canvas.
        await Task.Delay(800);

        vm.SearchMatches.Should().NotBeEmpty(
            "service is known to find 481 matches for 'open source'");
        vm.CurrentPageSearchHighlights.Should().NotBeEmpty(
            "VM should compute highlights for the current page");

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull("MainWindow exposes PdfViewerControl by name");

        // Walk into the visual tree to find the SearchHighlightsLayer
        // Canvas — the actual drawing target inside the PdfViewerControl.
        var searchLayer = FindNamedDescendant<Canvas>(viewer!, "SearchHighlightsLayer");
        searchLayer.Should().NotBeNull(
            "SearchHighlightsLayer Canvas must exist in PdfViewerControl");

        var rectangleChildren = searchLayer!.Children.OfType<Rectangle>().ToList();
        _out.WriteLine($"SearchHighlightsLayer has {rectangleChildren.Count} Rectangle children");
        _out.WriteLine($"VM.CurrentPageSearchHighlights has {vm.CurrentPageSearchHighlights.Count} entries");

        rectangleChildren.Should().NotBeEmpty(
            "SearchHighlightsLayer Canvas must contain Rectangle children — " +
            "if VM has highlights but the Canvas is empty, the View glue " +
            "(MainWindow.OnSearchHighlightsChanged → AddSearchHighlight) is broken.");
        rectangleChildren.Count.Should().Be(vm.CurrentPageSearchHighlights.Count,
            "every VM highlight should map to one Rectangle in the layer");

        // Sample one rectangle and verify its position is inside the
        // bitmap area — pre-fix (DPI 150) the highlights were ~25%
        // outside the bitmap on the right.
        var page = vm.PdfCoreDocument!.GetPage(vm.CurrentPageIndex + 1);
        var viewerPage = PdfCoordinateMapper.ToViewerDips(
            page,
            PdfPageRect.VisualPoints(page.PageNumber, 0, 0, page.VisualWidth, page.VisualHeight),
            120);
        var maxX = viewerPage.Width;
        var maxY = viewerPage.Height;
        foreach (var r in rectangleChildren.Take(3))
        {
            double left = Canvas.GetLeft(r);
            double top = Canvas.GetTop(r);
            _out.WriteLine($"rectangle: ({left:F1},{top:F1}) {r.Width:F1}×{r.Height:F1}");
            (left + r.Width).Should().BeLessThanOrEqualTo(maxX + 1,
                "highlight must fit inside the bitmap horizontally");
            (top + r.Height).Should().BeLessThanOrEqualTo(maxY + 1,
                "highlight must fit inside the bitmap vertically");
        }
    }

    private static T? FindNamedDescendant<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T t) return t;
        if (root is Panel p)
        {
            foreach (var child in p.Children)
            {
                if (child is Control c)
                {
                    var hit = FindNamedDescendant<T>(c, name);
                    if (hit != null) return hit;
                }
            }
        }
        if (root is Decorator d && d.Child is Control dc)
        {
            var hit = FindNamedDescendant<T>(dc, name);
            if (hit != null) return hit;
        }
        if (root is ContentControl cc && cc.Content is Control ccChild)
        {
            var hit = FindNamedDescendant<T>(ccChild, name);
            if (hit != null) return hit;
        }
        // Fall back to FindControl which searches the named-scope tree.
        return root.FindControl<T>(name);
    }
}
