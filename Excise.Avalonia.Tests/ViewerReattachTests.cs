using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #1929: detaching the viewer drops its scroller subscriptions and the continuous view's
/// container hooks; attaching it again must restore them, once. Both views are covered, the
/// continuous one through its viewport, its scroll-derived page and its subscription count.
/// </summary>
public class ViewerReattachTests
{
    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    [Fact]
    public async Task ReattachedViewer_InContinuousView_ReportsViewportChanges_Once()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl();
            var window = new Window { Width = 320, Height = 240, Content = viewer };
            window.Show();
            viewer.ViewMode = PdfViewMode.Continuous;
            Settle(window);

            var reported = new List<Size>();
            int viewReports = 0;
            viewer.VisibleViewportChanged += (_, size) => reported.Add(size);
            viewer.ContinuousPart.ViewportChanged += _ => viewReports++;

            window.Width = 400;
            Settle(window);
            reported.Should().NotBeEmpty("fixture: an attached continuous viewer reports a resize");
            viewReports.Should().Be(1, "one viewport subscription while first attached");
            var beforeDetach = viewer.GetVisibleViewportSize();

            Reattach(window, viewer);
            reported.Clear();
            viewReports = 0;

            window.Width = 520;
            Settle(window);
            viewer.GetVisibleViewportSize().Width.Should().BeGreaterThan(beforeDetach.Width + 50,
                "fixture: the re-attached continuous viewport really did grow");
            reported.Should().NotBeEmpty("a re-attached continuous viewer still reports its viewport");
            viewReports.Should().Be(1, "re-attaching restores the subscription once, not a second copy");

            window.Close();
            return true;
        });
    }

    [Fact]
    public async Task ReattachedViewer_InSinglePageView_ReportsViewportChanges()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl();
            var window = new Window { Width = 320, Height = 240, Content = viewer };
            window.Show();
            Settle(window);

            var reported = new List<Size>();
            viewer.VisibleViewportChanged += (_, size) => reported.Add(size);
            var beforeDetach = viewer.GetVisibleViewportSize();

            Reattach(window, viewer);
            reported.Clear();

            window.Width = 520;
            Settle(window);
            viewer.GetVisibleViewportSize().Width.Should().BeGreaterThan(beforeDetach.Width + 50,
                "fixture: the re-attached single-page viewport really did grow");
            reported.Should().NotBeEmpty("a re-attached single-page viewer still reports its viewport");

            window.Close();
            return true;
        });
    }

    [Fact]
    public async Task ReattachedViewer_InContinuousView_KeepsItsPage_AndFollowsAScroll()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl { RenderAheadEnabled = false };
            var window = new Window { Width = 400, Height = 300, Content = viewer };
            window.Show();
            viewer.ViewMode = PdfViewMode.Continuous;
            viewer.Document = OpenBlankPages(4);
            Settle(window);

            var scroller = viewer.ContinuousPart.ContinuousScrollViewer;
            var slots = viewer.ContinuousPart.ContinuousItems.ItemsSource!.Cast<PdfPageSlot>().ToList();
            slots.Should().HaveCount(4);

            scroller.Offset = new Vector(0, slots[1].TopDip + 5);
            Settle(window);
            viewer.CurrentPage.Should().Be(2, "fixture: an attached viewer follows a scroll");

            Reattach(window, viewer);
            viewer.CurrentPage.Should().Be(2, "re-attaching does not move the reader");

            scroller.Offset = new Vector(0, slots[2].TopDip + 5);
            Settle(window);
            viewer.CurrentPage.Should().Be(3, "a re-attached continuous view still follows a scroll");

            viewer.Document = null;
            window.Close();
            return true;
        });
    }

    private static void Reattach(Window window, PdfViewerControl viewer)
    {
        window.Content = null;
        Settle(window);
        window.Content = viewer;
        Settle(window);
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>A document of <paramref name="pageCount"/> empty US-letter pages.</summary>
    private static Excise.Core.Document.PdfDocument OpenBlankPages(int pageCount)
    {
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + 2 * i} 0 R"));
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>",
        };
        for (int i = 0; i < pageCount; i++)
        {
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {4 + 2 * i} 0 R >>");
            bodies.Add("<< /Length 0 >>\nstream\n\nendstream");
        }

        var sb = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Excise.Core.Document.PdfDocument.Open(System.Text.Encoding.Latin1.GetBytes(sb.ToString()));
    }
}
