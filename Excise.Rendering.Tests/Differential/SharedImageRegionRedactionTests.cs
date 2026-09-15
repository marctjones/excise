using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1493: one image XObject drawn by two pages and region-redacted on one of
/// them. The decided behaviour is per-page: the redacted page gets a zeroed
/// copy, the other page keeps the original, and <c>RedactText</c> reports that
/// page rather than silently redacting it.
/// </summary>
/// <remarks>
/// <para>The saved file is judged by mutool, not by excise, and on both sides of
/// the delta: the requested area is destroyed, and nothing else is. That
/// includes the other page. Before #1493 the redactor zeroed the shared
/// original in place, so page 2 looked redacted in excise's viewer while the
/// saved file drew it intact, and a later redaction of page 2 carried page 1's
/// blackout into page 2's copy.</para>
/// <para>The image's samples are never near black, so "black" in the rendered
/// area can only be the zeroed samples. The redaction box a GUI draws is not
/// involved: <c>RedactArea</c> does not draw one.</para>
/// </remarks>
public class SharedImageRegionRedactionTests : IDisposable
{
    private const int Dpi = 150;
    private const double PageSize = 200;
    private const int ImageSamples = 16;

    // The image fills (20,20)-(180,180) at 10 pt per sample.
    private static readonly PdfRectangle LowerLeftQuadrant = new(20, 20, 100, 100);
    private static readonly PdfRectangle UpperRightQuadrant = new(100, 100, 180, 180);

    private readonly List<string> _temp = new();

    [Fact]
    public void RegionRedactingPageOne_DestroysOnlyThatArea_AndThePageTheViewerShowsIsThePageTheFileHolds()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var doc = PdfDocument.Open(SharedImageDocument());
        doc.GetPage(1).RedactArea(LowerLeftQuadrant);

        double viewerPage2;
        using (var viewer = RenderWithExcise(doc, 2))
            viewerPage2 = BlackFraction(viewer, Inset(LowerLeftQuadrant));
        var path = SaveTemp(doc);

        using var page1 = MutoolReferenceRenderer.RenderPage(path, 1, dpi: Dpi);
        using var page2 = MutoolReferenceRenderer.RenderPage(path, 2, dpi: Dpi);
        page1.Should().NotBeNull("mutool must render the redacted page");
        page2.Should().NotBeNull("mutool must render the page that shares the image");

        BlackFraction(page1!, Inset(LowerLeftQuadrant)).Should().BeGreaterThan(0.95,
            "the redacted area's samples must be destroyed in the saved file, as mutool draws it");
        BlackFraction(page1!, Inset(UpperRightQuadrant)).Should().BeLessThan(0.01,
            "only the requested area may be destroyed; the rest of page 1's image must survive");

        var filePage2 = BlackFraction(page2!, Inset(LowerLeftQuadrant));
        filePage2.Should().BeLessThan(0.01,
            "page 2 was not redacted: it draws the original image, saved from its unedited bytes");
        viewerPage2.Should().BeApproximately(filePage2, 0.01,
            "excise's in-memory render of page 2 must show what the saved file holds; before #1493 it showed " +
            "page 2 redacted while the file kept the original pixels");
    }

    [Fact]
    public void RegionRedactingBothPages_DestroysOnlyEachPagesOwnArea()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var doc = PdfDocument.Open(SharedImageDocument());
        doc.GetPage(1).RedactArea(LowerLeftQuadrant);
        doc.GetPage(2).RedactArea(UpperRightQuadrant);
        var path = SaveTemp(doc);

        using var page1 = MutoolReferenceRenderer.RenderPage(path, 1, dpi: Dpi);
        using var page2 = MutoolReferenceRenderer.RenderPage(path, 2, dpi: Dpi);
        page1.Should().NotBeNull();
        page2.Should().NotBeNull();

        BlackFraction(page1!, Inset(LowerLeftQuadrant)).Should().BeGreaterThan(0.95, "page 1's own area");
        BlackFraction(page1!, Inset(UpperRightQuadrant)).Should().BeLessThan(0.01,
            "page 2's area must not reach page 1");
        BlackFraction(page2!, Inset(UpperRightQuadrant)).Should().BeGreaterThan(0.95, "page 2's own area");
        BlackFraction(page2!, Inset(LowerLeftQuadrant)).Should().BeLessThan(0.01,
            "page 1's area must not reach page 2's copy; before #1493 page 2 carried both blackouts");
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static SKBitmap RenderWithExcise(PdfDocument doc, int pageNumber)
        => new SkiaRenderer().RenderPage(doc.GetPage(pageNumber),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

    /// <summary>Shrinks an area by a whole sample so interpolation at its edges is not measured.</summary>
    private static PdfRectangle Inset(PdfRectangle area)
        => new(area.Left + 10, area.Bottom + 10, area.Right - 10, area.Top - 10);

    /// <summary>
    /// Fraction of near-black pixels inside <paramref name="box"/> (PDF
    /// coordinates, bottom-left origin) of a page rendered at <see cref="Dpi"/>.
    /// </summary>
    private static double BlackFraction(SKBitmap bmp, PdfRectangle box)
    {
        double scale = Dpi / 72.0;
        int x0 = Math.Max(0, (int)Math.Ceiling(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)Math.Floor(box.Right * scale));
        int y0 = Math.Max(0, (int)Math.Ceiling((PageSize - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)Math.Floor((PageSize - box.Bottom) * scale));
        if (x1 <= x0 || y1 <= y0)
            return 0;

        int black = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 40 && p.Green < 40 && p.Blue < 40)
                black++;
        }
        return (double)black / total;
    }

    private string SaveTemp(PdfDocument doc)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-shared-image-{Guid.NewGuid():N}.pdf");
        doc.Save(path);
        _temp.Add(path);
        return path;
    }

    /// <summary>
    /// Two 200 pt pages, each drawing the same Flate DeviceRGB image object
    /// under its own resource dictionary. Every sample is between 90 and 229.
    /// </summary>
    private static byte[] SharedImageDocument()
    {
        var samples = Enumerable.Range(0, ImageSamples * ImageSamples * 3)
            .Select(i => (byte)(90 + (i * 53) % 140))
            .ToArray();

        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetInt("Width", ImageSamples);
        dict.SetInt("Height", ImageSamples);
        dict.SetInt("BitsPerComponent", 8);
        dict.SetName("ColorSpace", "DeviceRGB");
        dict.SetName("Filter", "FlateDecode");

        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(new PdfStream(dict, Flate(samples)));
        for (int n = 0; n < 2; n++)
        {
            var page = doc.Pages.AddBlank(PageSize, PageSize);
            var xobjects = new PdfDictionary();
            xobjects["Im0"] = imageRef;
            var resources = new PdfDictionary();
            resources["XObject"] = xobjects;
            page.Dictionary["Resources"] = resources;
            page.SetContentStreamBytes(Encoding.ASCII.GetBytes("q 160 0 0 160 20 20 cm /Im0 Do Q"));
        }
        return doc.SaveToBytes();
    }

    private static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }
}
