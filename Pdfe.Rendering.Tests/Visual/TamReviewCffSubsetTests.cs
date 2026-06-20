using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Fonts;
using Pdfe.Core.Primitives;
using Pdfe.Rendering.Fonts;
using SkiaSharp;
using Xunit;

namespace Pdfe.Rendering.Tests.Visual;

/// <summary>
/// TAMReview.pdf exposes a simple Type1C/CFF subset whose /Encoding
/// Differences are custom names such as /g18 and /g152 rather than Adobe
/// Glyph List names. There is no /ToUnicode map for the body font, so the
/// renderer must route the PDF byte codes directly to CFF glyph IDs.
/// </summary>
public sealed class TamReviewCffSubsetTests
{
    private const int RenderDpi = 150;

    [Fact]
    public void TamReviewBodyFont_CustomGNamesResolveToDrawableCffGlyphs()
    {
        var pdfPath = LocateTamReviewPdf();
        Assert.SkipUnless(File.Exists(pdfPath),
            "pdf.js TAMReview fixture not found at test-pdfs/pdfjs/TAMReview.pdf.");

        using var doc = PdfDocument.Open(pdfPath);
        var font = FindFontByBaseName(doc, "EMMOLK+Cambria");
        font.Should().NotBeNull("TAMReview body text uses this embedded Type1C subset");

        var cffBytes = ExtractFontFile3(doc, font!);
        var cffInfo = CffParser.Parse(cffBytes);
        cffInfo.Should().NotBeNull();

        cffInfo!.GlyphNameToIndex["g18"].Should().Be(1);
        cffInfo.GlyphNameToIndex["g152"].Should().Be(2);
        cffInfo.GlyphNameToIndex["g821"].Should().Be(cffInfo.NumGlyphs - 1);

        var sfnt = CffToOpenType.Wrap(cffBytes, cffInfo.NumGlyphs, new CffToOpenType.PdfFontInfo
        {
            PsName = "TamReviewCambria"
        });
        sfnt.Should().NotBeNull("the CFF subset should be wrapped into a Skia-loadable OpenType font");

        using var data = SKData.CreateCopy(sfnt);
        using var typeface = SKTypeface.FromData(data);
        typeface.Should().NotBeNull();

        using var skFont = new SKFont(typeface, 24);
        var drawableGlyphs = cffInfo.GlyphNameToIndex
            .Where(kvp => kvp.Value > 0)
            .Take(20)
            .Count(kvp => GlyphHasInk(skFont, (ushort)kvp.Value));

        drawableGlyphs.Should().BeGreaterThan(10,
            "custom /gNN glyph names must reach real CFF outlines, not .notdef");
    }

    [Fact]
    public void TamReviewPage17_BodyParagraphTextProducesInk()
    {
        var pdfPath = LocateTamReviewPdf();
        Assert.SkipUnless(File.Exists(pdfPath),
            "pdf.js TAMReview fixture not found at test-pdfs/pdfjs/TAMReview.pdf.");

        using var doc = PdfDocument.Open(pdfPath);
        using var bitmap = new SkiaRenderer().RenderPage(doc.GetPage(17), new RenderOptions { Dpi = RenderDpi });

        var bodyInk = CountDarkPixels(
            bitmap,
            xStart: PtToPx(90),
            xEnd: PtToPx(510),
            yStart: PtToPx(145),
            yEnd: PtToPx(300));

        bodyInk.Should().BeGreaterThan(3000,
            "page 17 should contain readable paragraph text below the first heading; " +
            "missing ink here means the custom CFF /gNN body font is not being drawn");
    }

    private static bool GlyphHasInk(SKFont font, ushort glyphId)
    {
        using var path = font.GetGlyphPath(glyphId);
        return path != null && !path.IsEmpty && path.PointCount > 0;
    }

    private static int CountDarkPixels(SKBitmap bitmap, int xStart, int xEnd, int yStart, int yEnd)
    {
        var count = 0;
        for (int y = Math.Max(0, yStart); y < Math.Min(bitmap.Height, yEnd); y++)
        {
            for (int x = Math.Max(0, xStart); x < Math.Min(bitmap.Width, xEnd); x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 100 && pixel.Green < 100 && pixel.Blue < 100)
                    count++;
            }
        }
        return count;
    }

    private static int PtToPx(double pt) => (int)Math.Round(pt * RenderDpi / 72.0);

    private static PdfDictionary? FindFontByBaseName(PdfDocument doc, string baseName)
    {
        foreach (var (_, _, obj) in doc.GetAllObjects())
        {
            if (obj is PdfDictionary dict &&
                dict.GetNameOrNull("Type") == "Font" &&
                dict.GetNameOrNull("BaseFont") == baseName)
                return dict;
        }
        return null;
    }

    private static byte[] ExtractFontFile3(PdfDocument doc, PdfDictionary font)
    {
        var descriptorObj = font.GetOptional("FontDescriptor");
        descriptorObj.Should().NotBeNull();
        var descriptor = doc.Resolve(descriptorObj!) as PdfDictionary;
        descriptor.Should().NotBeNull();

        var fontFileObj = descriptor!.GetOptional("FontFile3");
        fontFileObj.Should().NotBeNull();
        var stream = doc.Resolve(fontFileObj!) as PdfStream;
        stream.Should().NotBeNull();

        return stream!.DecodedData;
    }

    private static string LocateTamReviewPdf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln")))
                return Path.Combine(dir.FullName, "test-pdfs", "pdfjs", "TAMReview.pdf");
            dir = dir.Parent;
        }
        return Path.Combine("test-pdfs", "pdfjs", "TAMReview.pdf");
    }
}
