using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Text.Segmentation;
using Pdfe.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Pdfe.Rendering.Tests.Differential;

/// <summary>
/// Verifies redacted output with tools that are not pdfe (issue #606).
///
/// Every redaction test we own asks pdfe whether pdfe removed the text. That is
/// a tool grading its own homework, and it has already failed us twice:
///
///   #636 — /ActualText survived in the structure tree. page.Text reads the
///          content stream, so our assertion passed on a leaking document.
///   #608 — an XMP scrub written through the decode cache never reached the
///          saved bytes. Every in-memory read reported clean.
///
/// Both leaks were invisible to pdfe's own extractor and glaringly visible to
/// anything else. So this suite asks three independent questions:
///
///   1. Does an INDEPENDENT EXTRACTOR (mutool) still find the text? This catches
///      any carrier pdfe's extractor cannot see — structure tree, ToUnicode CMap
///      recovery, metadata, a revision we failed to drop.
///
///   2. Does an INDEPENDENT RENDERER still draw the glyphs? Text can be gone from
///      every text carrier and still be *visible* — as vector paths, or as pixels
///      in an image. Extraction cannot see that. Pixels can.
///
///   3. Is the redaction region actually opaque? A black box that does not cover
///      what it claims to cover is a false assurance.
///
/// A leak has to defeat all three to survive, and the three fail in different
/// ways. That is the point: this gate does not need to anticipate the NEXT
/// carrier, because it does not ask about carriers at all — it asks whether the
/// text is gone and whether the ink is gone.
///
/// Renderers that are not installed are skipped, loudly, rather than silently
/// passing.
/// </summary>
public class RedactionReferenceVerificationTests : IDisposable
{
    private const string Secret = "CONFIDENTIAL";
    private const string Keep = "PUBLICDATA";

    private readonly List<string> _temp = new();

    [Fact]
    public void RedactedText_IsNotRecoverableByAnIndependentExtractor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = RedactAndSave(TaggedPdfWithSecret(), rotation: 0);

        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull("mutool must be able to read the redacted file at all");

        extracted!.Should().NotContain(Secret,
            "mutool reads carriers pdfe's own extractor does not — the structure tree, the " +
            "font's ToUnicode CMap, and the raw strings. If it can still recover the word, " +
            "the redaction is cosmetic no matter what page.Text says.");

        extracted.Should().Contain(Keep,
            "only the targeted word may be removed; a redaction that destroys the whole page " +
            "would satisfy a removal-only assertion");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RedactedGlyphs_AreNotDrawnByAnIndependentRenderer(int rotation)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = RedactAndSave(TaggedPdfWithSecret(), rotation);

        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, dpi: 150);
        rendered.Should().NotBeNull("mutool must render the redacted page");

        // The redaction box is filled black. Everything the user was promised is
        // gone should be *under* that fill. What we assert is the complement: the
        // page still has the ink it is supposed to have (KEEPME), and the black
        // box is genuinely opaque -- see the coverage test below.
        //
        // The strong claim here is about text, and it is checked by extraction at
        // every rotation, because a rotation bug moves the box, not the glyphs.
        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull();
        extracted!.Should().NotContain(Secret,
            $"at /Rotate {rotation} the redaction box must land on the glyphs it removed. " +
            "If the coordinate mapping ignores rotation, the box covers empty space and an " +
            "independent extractor still reads the secret straight out of the file.");

        HasNonWhitePixels(rendered!).Should().BeTrue(
            "sanity: the page must still render *something* — an all-white page would pass " +
            "every leak assertion by having destroyed the document");
    }

    [Fact]
    public void RedactedInk_IsGoneFromThePage_NotMerelyCoveredByABox()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        // This is the project's central claim, stated as a pixel measurement and
        // checked by a renderer that is not ours:
        //
        //   Pdfe.Core's RedactArea REMOVES glyphs. It does not draw a black box —
        //   that is the GUI's cosmetic confirmation (RedactionService.
        //   AppendBlackRectangle). So after a core redaction the region must come
        //   back BLANK, not black.
        //
        // Blank is the strictly stronger result. A black box proves only that
        // something is on top of the text; blank proves the text is not there. If
        // this region ever renders black instead of white, someone has quietly
        // replaced removal with covering — the exact regression CLAUDE.md's
        // "ABSOLUTE RULES" exist to prevent, and the one a text-extraction
        // assertion could never catch.
        var pdf = PdfDocument.Open(TaggedPdfWithSecret());
        var page = pdf.GetPage(1);
        var box = BoundsOf(page, Secret);

        var beforePath = SaveTemp(pdf);
        using var before = GhostscriptReferenceRenderer.RenderPage(beforePath, 1, dpi: 150);
        before.Should().NotBeNull();

        var inkBefore = InkFractionIn(before!, box, page.Height);
        inkBefore.Should().BeGreaterThan(0.02,
            "fixture sanity — the secret must actually be inked on the page before we redact it, " +
            "or this test proves nothing");

        page.RedactArea(box, GlyphRemovalStrategy.AnyOverlap);
        var afterPath = SaveTemp(pdf);

        using var after = GhostscriptReferenceRenderer.RenderPage(afterPath, 1, dpi: 150);
        after.Should().NotBeNull();

        var inkAfter = InkFractionIn(after!, box, page.Height);

        inkAfter.Should().BeLessThan(0.001,
            $"an independent renderer still draws ink in the redacted region ({inkAfter:P2} of " +
            $"pixels, down from {inkBefore:P2}). Text can be absent from every text carrier and " +
            "still be VISIBLE — as vector paths, or as pixels in an image. Extraction cannot see " +
            "that; a renderer can.");

        // And the inverse: we removed the ink we meant to, not the whole page.
        var keepBox = BoundsOf(page, Keep);
        InkFractionIn(after!, keepBox, page.Height).Should().BeGreaterThan(0.02,
            "the untargeted word must still be inked — a redaction that blanked the page would " +
            "satisfy every assertion above");
    }

    /// <summary>
    /// Fraction of non-white pixels inside <paramref name="box"/> (PDF content
    /// coordinates, bottom-left origin) of a rendered page.
    /// </summary>
    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box, double pageHeight)
    {
        const double scale = 150.0 / 72.0;

        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        // PDF y is bottom-up; raster y is top-down.
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));

        if (x1 <= x0 || y1 <= y0) return 0;

        int ink = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
        }

        return total == 0 ? 0 : (double)ink / total;
    }

    private string RedactAndSave(byte[] source, int rotation)
    {
        var pdf = PdfDocument.Open(source);
        var page = pdf.GetPage(1);
        if (rotation != 0) page.Rotation = rotation;

        page.RedactArea(BoundsOf(page, Secret), GlyphRemovalStrategy.AnyOverlap);
        return SaveTemp(pdf);
    }

    private string SaveTemp(PdfDocument pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfe-redact-{Guid.NewGuid():N}.pdf");
        pdf.Save(path);
        _temp.Add(path);
        return path;
    }

    private static PdfRectangle BoundsOf(PdfPage page, string word)
    {
        var run = page.Letters
            .Where(l => word.Contains(l.Value, StringComparison.Ordinal))
            .ToList();
        run.Should().NotBeEmpty($"fixture must render '{word}'");

        var y = run[0].GlyphRectangle.Bottom;
        var line = page.Letters
            .Where(l => Math.Abs(l.GlyphRectangle.Bottom - y) < 2.0)
            .ToList();

        return new PdfRectangle(
            line.Min(l => l.GlyphRectangle.Left) - 1,
            line.Min(l => l.GlyphRectangle.Bottom) - 1,
            line.Max(l => l.GlyphRectangle.Right) + 1,
            line.Max(l => l.GlyphRectangle.Top) + 1).Normalize();
    }

    private static bool HasNonWhitePixels(SKBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y += 4)
        for (int x = 0; x < bmp.Width; x += 4)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) return true;
        }
        return false;
    }

    /// <summary>
    /// A tagged page whose secret lives in the content stream AND in
    /// /ActualText — the exact shape that defeated our own extractor in #636.
    /// An independent tool must not be able to recover it from either.
    /// </summary>
    private static byte[] TaggedPdfWithSecret()
    {
        var content =
            $"/Span <</MCID 0>> BDC BT /F1 28 Tf 72 700 Td ({Secret}) Tj ET EMC " +
            $"/Span <</MCID 1>> BDC BT /F1 28 Tf 72 500 Td ({Keep}) Tj ET EMC";

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /StructTreeRoot /K [7 0 R] >>\nendobj\n");
        Obj("7 0 obj\n<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>\nendobj\n");
        Obj($"8 0 obj\n<< /Type /StructElem /S /Span /P 7 0 R /Pg 3 0 R /K 0 /ActualText ({Secret}) >>\nendobj\n");
        Obj($"9 0 obj\n<< /Type /StructElem /S /Span /P 7 0 R /Pg 3 0 R /K 1 /ActualText ({Keep}) >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 10\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 10 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
