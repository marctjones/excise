using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1613: a render that releases image samples also releases the page's
/// inflated content once it is drawn; one that keeps its pages warm (the
/// viewer) keeps it. A second render of a released page draws the same pixels.
/// </summary>
public class ContentStreamReleaseRenderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RenderPage_ReleasesContentOnlyWhenAskedTo_AndReRendersIdentically(bool release)
    {
        using var doc = PdfDocument.Open(SavedDocument());
        var page = doc.GetPage(1);
        var content = (PdfStream)doc.Resolve(page.Dictionary.GetOptional("Contents")!);
        content.IsFiltered.Should().BeTrue("precondition: Flate-encoded content");
        var options = new RenderOptions { Dpi = 72, ReleaseDecodedImageSamples = release };
        var renderer = new SkiaRenderer();

        using var first = renderer.RenderPage(page, options);
        content.IsDecoded.Should().Be(!release);

        using var second = renderer.RenderPage(page, options);
        second.Bytes.Should().Equal(first.Bytes, "the re-decoded content draws exactly what the first render drew");
        first.Bytes.Should().Contain(b => b < 128, "precondition: the page draws ink");
    }

    private static byte[] SavedDocument()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        var filler = string.Concat(Enumerable.Repeat("0 0 m 1 1 l n\n", 50));
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("0 0 0 rg 20 20 100 100 re f\n" + filler));
        return doc.SaveToBytes();
    }
}
