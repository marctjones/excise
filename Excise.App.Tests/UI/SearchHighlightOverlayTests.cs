using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
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
///
/// #1768: this is the ONLY test in the project that touches
/// <c>SearchHighlightsLayer</c>, and it used to return at the top on a book
/// path that was the empty string, so the View glue had no live coverage
/// while the class reported a pass. It now runs on a synthetic 4-page
/// document. The method name keeps "PragmaticBook" because
/// <c>GuiWorkflowCoverageMatrixTests</c> pins it with <c>nameof</c>.
/// </summary>
[Collection("AvaloniaTests")]
public class SearchHighlightOverlayTests : IDisposable
{
    // The layer is in viewer DIPs at the viewer's 120-DPI render scale.
    private const double DipsPerPoint = 120.0 / 72.0;

    private readonly ITestOutputHelper _out;
    private readonly ShownWindowTracker _windows = new();
    private readonly string _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "excise-highlight-" + Guid.NewGuid().ToString("N"));

    public SearchHighlightOverlayTests(ITestOutputHelper o) { _out = o; }

    public void Dispose()
    {
        _windows.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [FixedAvaloniaFact]
    public async Task SearchInPragmaticBook_DrawsHighlightRectangles()
    {
        Directory.CreateDirectory(_tempDir);
        var path = System.IO.Path.Combine(_tempDir, "four-pages.pdf");
        // Every page carries "Page N Content" (x=100pt, 100pt below the top) and
        // "Secret on Page N" (x=100pt, 200pt below the top): "Page" occurs twice
        // per page.
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 4);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        await Task.Delay(100);

        await vm.LoadDocumentAsync(path);

        // Trigger search the same way Ctrl+F → typing does in the GUI.
        vm.SearchText = "Page";

        // Poll for the VM's debounced (~300 ms) walk of the document, then for the
        // highlight-overlay update that follows it:
        //   VM.UpdateSearchHighlights → CurrentPageSearchHighlights.Add →
        //   MainWindow.OnSearchHighlightsChanged →
        //   PdfViewerControl.AddSearchHighlight → Rectangle in Canvas.
        // A timeout is not swallowed: the assertions below fail on whatever is missing.
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull("MainWindow exposes PdfViewerControl by name");
        var searchLayer = FindNamedDescendant<Canvas>(viewer!, "SearchHighlightsLayer");
        searchLayer.Should().NotBeNull("SearchHighlightsLayer Canvas must exist in PdfViewerControl");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline &&
               (vm.SearchMatches.Count < 8 || searchLayer!.Children.OfType<Rectangle>().Count() < 2))
            await Task.Delay(50);

        vm.SearchMatches.Should().HaveCount(8, "'Page' occurs twice on each of the 4 pages");
        vm.CurrentPageSearchHighlights.Should().HaveCount(2,
            "the VM computes highlights for the current page only: 'Page 1 Content' and 'Secret on Page 1'");

        var rectangleChildren = searchLayer!.Children.OfType<Rectangle>().ToList();
        _out.WriteLine($"SearchHighlightsLayer has {rectangleChildren.Count} Rectangle children");
        rectangleChildren.Should().HaveCount(vm.CurrentPageSearchHighlights.Count,
            "every VM highlight should map to one Rectangle in the layer — " +
            "if the VM has highlights but the Canvas is empty, the View glue " +
            "(MainWindow.OnSearchHighlightsChanged → AddSearchHighlight) is broken");

        // Position: the bitmap area bound (pre-fix, at DPI 150, highlights were ~25%
        // outside the bitmap on the right) AND the place the words were drawn. Both
        // lines start at x = 100 pt; "Page" in the first line sits ~100 pt below the
        // top and in the second ~200 pt below it, so the rectangles must land there
        // in DIPs — a wrong scale or an unflipped y moves them well outside these bands.
        var page = vm.PdfCoreDocument!.GetPage(vm.CurrentPageIndex + 1);
        var viewerPage = PdfCoordinateMapper.ToViewerDips(
            page,
            PdfPageRect.VisualPoints(page.PageNumber, 0, 0, page.VisualWidth, page.VisualHeight),
            120);
        var ordered = rectangleChildren.OrderBy(Canvas.GetTop).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            double left = Canvas.GetLeft(r);
            double top = Canvas.GetTop(r);
            _out.WriteLine($"rectangle: ({left:F1},{top:F1}) {r.Width:F1}×{r.Height:F1}");
            (left + r.Width).Should().BeLessThanOrEqualTo(viewerPage.Width + 1,
                "highlight must fit inside the bitmap horizontally");
            (top + r.Height).Should().BeLessThanOrEqualTo(viewerPage.Height + 1,
                "highlight must fit inside the bitmap vertically");
            r.Width.Should().BeGreaterThan(0);
            r.Height.Should().BeGreaterThan(0);

            // Text baselines: 100 pt and 200 pt below the top; the word's box is one
            // 12-pt em tall and ends on the baseline, so it spans (baseline - 12) pt to
            // baseline. Measured 2026-09-21: (166.7,146.7) 46.7x20.0 and (257.8,313.3).
            double baselineDips = (i == 0 ? 100 : 200) * DipsPerPoint;
            top.Should().BeApproximately(baselineDips - 12 * DipsPerPoint, 4,
                $"highlight {i} must sit on the line it matched");
            r.Height.Should().BeApproximately(12 * DipsPerPoint, 4, "the box is one em tall");
            if (i == 0)
                left.Should().BeApproximately(100 * DipsPerPoint, 4, "'Page' opens the first line, drawn at x = 100 pt");
            else
                left.Should().BeInRange(140 * DipsPerPoint, 170 * DipsPerPoint,
                    "'Page' follows 'Secret on ' on the second line, so it starts right of x = 100 pt");
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
