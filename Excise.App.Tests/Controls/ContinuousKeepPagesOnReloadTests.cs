using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Graphics;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #1876: <see cref="PdfViewerControl.KeepPagesOnScreenUntilRendered"/> keeps the
/// continuous view's composites through ONE Document change, and only when the
/// new document is the instance it names and every page has its slot's size and
/// rotation. Anything else clears at once: a stale frame shown for different
/// content is a worse bug than a blank one.
/// </summary>
[Collection("AvaloniaTests")]
public class ContinuousKeepPagesOnReloadTests
{
    public static TheoryData<string, bool> Reloads() => new()
    {
        { "same content", true },
        { "page 1 turned 180 degrees", false },
        { "pages 1 and 2 swapped", false },
        { "a page fewer", false },
        { "request named another instance", false },
        { "no request", false },
    };

    [FixedAvaloniaTheory(Timeout = 120000)]
    [MemberData(nameof(Reloads))]
    public async Task DocumentChange_KeepsThePage_OnlyForTheRequestedReloadOfTheSamePages(string reload, bool keeps)
    {
        var bytes = Fixture();
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(bytes);
        var next = PdfCoreDocument.Open(bytes);
        var other = PdfCoreDocument.Open(bytes);
        try
        {
            var shown = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(
                window, viewer, items, pageNumber: 1);
            var shownPixels = ContinuousTileEvictionCompositeTests.PixelCopy.Of(shown);
            shownPixels.InkFraction().Should().BeGreaterThan(0.05, "fixture: page 1 must show its box");

            switch (reload)
            {
                case "page 1 turned 180 degrees": next.GetPage(1).Rotation = 180; break;
                case "pages 1 and 2 swapped": next.Pages.Move(0, 1); break;
                case "a page fewer": next.Pages.RemoveAt(2); break;
            }
            if (reload != "no request")
                viewer.KeepPagesOnScreenUntilRendered(reload == "request named another instance" ? other : next);
            var previous = viewer.Document;
            viewer.Document = next;
            previous?.Dispose();

            var page1 = items.ItemsSource!.Cast<PdfPageSlot>().First(s => s.PageNumber == 1);
            if (!keeps)
            {
                page1.Bitmap.Should().BeNull($"{reload}: the old page must leave the screen with the old document");
                await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
                return;
            }

            page1.Bitmap.Should().BeSameAs(shown, "a save's reload keeps the page on screen while it re-renders");
            var rerendered = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(
                window, viewer, items, pageNumber: 1, notThis: shown);
            ContinuousTileEvictionCompositeTests.PixelCopy.Of(rerendered).MismatchFraction(shownPixels, channelTolerance: 32)
                .Should().BeLessThan(0.005, "the reloaded document renders the same page it replaced");
        }
        finally
        {
            window.Close();
            viewer.Document?.Dispose();
            other.Dispose();
        }
    }

    /// <summary>Three pages of different sizes, each with a box off-centre, so a turn or a swap shows.</summary>
    private static byte[] Fixture()
    {
        using var doc = PdfCoreDocument.CreateNew();
        foreach (var (width, height) in new[] { (612.0, 792.0), (612.0, 1008.0), (595.0, 842.0) })
        {
            var page = doc.Pages.AddBlank(width, height);
            using var g = page.GetGraphics();
            g.DrawRectangle(72, height - 372, 300, 300, PdfBrush.Black);
            g.Flush();
        }
        return doc.SaveToBytes();
    }
}
