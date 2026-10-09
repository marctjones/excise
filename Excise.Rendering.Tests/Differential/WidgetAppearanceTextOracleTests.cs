using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;
using F = Excise.TestSupport.WidgetDisplayTextFixtures;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2039: a widget's text is what its appearance draws, at the drawn glyphs. Checked against tools
/// that are not excise: mutool's text and its glyph positions (<c>draw -F stext</c>) read the
/// appearance as written (the fixture sets no <c>/NeedAppearances</c>, so nothing is redrawn from
/// <c>/V</c>); after a redaction of the displayed text, mutool, pdftotext, qpdf's decoded object
/// dump, the inflating byte scanner and mutool's pixels must all find it gone. The planted run (the
/// fixture saved unredacted) shows each oracle sees the text when it is there.
/// </summary>
public class WidgetAppearanceTextOracleTests : IDisposable
{
    private const int Dpi = 72;
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static void RequireTools()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed [requires: tool:mutool]");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed [requires: tool:qpdf]");
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not installed [requires: tool:pdftotext]");
    }

    private string Write(byte[] bytes, string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-2039-{tag}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    // The choice widget's box, inset 1 pt from /Rect.
    private static readonly PdfRectangle ChoiceBox = new(F.ChoiceRect[0] + 1, F.ChoiceRect[1] + 1, F.ChoiceRect[2] - 1, F.ChoiceRect[3] - 1);

    [Fact]
    public void Planted_Unredacted_EveryOracleSeesTheDisplayedText()
    {
        RequireTools();
        var path = Write(F.Build(), "plant");

        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain(F.ChoiceDisplay, "mutool paints the appearance");
        PdftotextTextExtractor.ExtractPage(path, 1).Should().Contain(F.ChoiceDisplay, "so does Poppler");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(path), F.ChoiceDisplay).Should().NotBeEmpty();
        Encoding.Latin1.GetString(QpdfReferenceTool.DecodedObjectDump(path)!).Should().Contain(F.ChoiceDisplay);
        QpdfReferenceTool.AcroFormFieldValues(path)!["Country"].Should().Be(F.ChoiceSave);
        using var raster = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(raster!, ChoiceBox).Should().BeGreaterThan(0.01, "the display text has ink");
    }

    [Fact]
    public void ExcisePlacesAppearanceGlyphs_WhereMutoolDrawsThem()
    {
        RequireTools();
        var path = Write(F.Build(), "positions");
        var mutool = MutoolGlyphPositions.ExtractPage(path, 1);
        mutool.Should().NotBeNull();

        using var document = PdfDocument.Open(File.ReadAllBytes(path));
        var page = document.GetPage(1);
        foreach (var term in new[] { F.ChoiceDisplay, F.TextValue })
        {
            var match = PdfDocumentRedactionExtensions.FindTextMatches(page.Letters, term, caseSensitive: true);
            match.Should().HaveCount(1, term);
            var first = match[0][0];
            // stext: y down from the top of the page, at the glyph origin.
            var drawn = mutool!.Where(g => g.Char == first.Value
                    && Math.Abs(g.X - first.StartX) < 1.0
                    && Math.Abs(g.Y - (792 - first.StartY)) < 1.0)
                .ToList();
            drawn.Should().NotBeEmpty(
                $"mutool draws '{first.Value}' of '{term}' where excise reads it ({first.StartX:F2}, {first.StartY:F2}); mutool has "
                + string.Join(", ", mutool!.Where(g => g.Char == first.Value).Select(g => $"({g.X:F2}, {792 - g.Y:F2})")));
        }
    }

    [Fact]
    public void RedactingTheDisplayedText_LeavesNoCopy_ForAnyOracle()
    {
        RequireTools();
        byte[] saved;
        using (var document = PdfDocument.Open(F.Build()))
        {
            document.RedactText(F.ChoiceDisplay, RedactionOptions.Default with { DrawBox = false })
                .VerifiedRemovals.Should().BeGreaterThan(0);
            saved = document.SaveToBytes();
        }
        var path = Write(saved, "redacted");

        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(F.ChoiceDisplay);
        PdftotextTextExtractor.ExtractPage(path, 1).Should().NotContain(F.ChoiceDisplay);
        SavedPdfLeakScanner.FindTerm(saved, F.ChoiceDisplay).Should().BeEmpty();
        var dump = Encoding.Latin1.GetString(QpdfReferenceTool.DecodedObjectDump(path)!);
        dump.Should().NotContain(F.ChoiceDisplay, "no appearance, option or value keeps it");
        dump.Should().NotContain($"({F.ChoiceSave})", "nor the save value that names the same option");
        QpdfReferenceTool.AcroFormFieldValues(path)!["Country"].Should().NotBe(F.ChoiceSave);
        using var raster = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(raster!, ChoiceBox).Should().BeLessThan(0.001, "the display text's ink is gone");

        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain(F.TextValue, "the other fields are not part of the delta");
    }

    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box)
    {
        // 72 dpi: one pixel per point; the bitmap's height is the page height.
        int x0 = Math.Max(0, (int)Math.Floor(box.Left)), x1 = Math.Min(bmp.Width - 1, (int)Math.Ceiling(box.Right));
        int y0 = Math.Max(0, (int)Math.Floor(bmp.Height - box.Top)), y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling(bmp.Height - box.Bottom));
        int ink = 0, total = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                total++;
                var c = bmp.GetPixel(x, y);
                if (c.Red < 160 || c.Green < 160 || c.Blue < 160) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }
}
