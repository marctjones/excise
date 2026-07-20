using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Core.Graphics;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Reference-rendered fidelity for saved typewriter output (#610, parent #605).
///
/// Typewriter tests elsewhere prove structural creation, save/reopen, and
/// extraction — all through excise's own reader. Per the no-self-oracle rule,
/// this suite asks tools that are NOT excise whether the saved file actually
/// shows the typed text:
///
///   • an INDEPENDENT RENDERER (mutool / Ghostscript) must draw ink in the
///     region the text was typed into — and that region must have been blank
///     before, so the assertion cannot pass vacuously;
///   • an INDEPENDENT EXTRACTOR (mutool) must read the typed words back out
///     of the saved bytes, so visible output and document text agree.
///
/// Every case goes through save → reopen-from-disk → external tool, because
/// an in-memory view can be clean while the written file is wrong (#608).
/// Renderers that are not installed skip loudly rather than pass silently.
/// </summary>
public class TypewriterOutputFidelityTests : IDisposable
{
    private const string Existing = "EXISTINGCONTENT";
    private const int Dpi = 150;

    private readonly List<string> _temp = new();

    // An empty region well away from the fixture's existing line.
    private static readonly PdfRectangle TargetBox = new(100, 300, 500, 360);

    [Fact]
    public void TypedText_IsDrawnByIndependentRenderers_InTheTargetRegion()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var beforePath = SaveTemp(PdfDocument.Open(FixturePdf()));
        using (var before = MutoolReferenceRenderer.RenderPage(beforePath, 1, Dpi))
        {
            InkFractionIn(before!, TargetBox).Should().BeLessThan(0.001,
                "fixture sanity — the target region must be blank before typing, " +
                "or 'ink appeared' proves nothing");
        }

        var path = ApplyAndSave(
            PdfTypewriterTextOperation.Create(1, TargetBox, "INSERTED BY TYPEWRITER",
                new PdfTypewriterTextStyle(fontSize: 18)));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull("mutool must render the edited page");
        InkFractionIn(mutool!, TargetBox).Should().BeGreaterThan(0.01,
            "mutool must draw the typed text where it was typed — if the region is blank, " +
            "the appearance was lost in writing (coordinate mapping, content stream, or fonts)");

        if (GhostscriptReferenceRenderer.IsAvailable)
        {
            using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
            gs.Should().NotBeNull();
            InkFractionIn(gs!, TargetBox).Should().BeGreaterThan(0.01,
                "Ghostscript disagreeing with mutool would mean the appearance relies on " +
                "renderer-specific behavior");
        }
    }

    [Fact]
    public void TypedText_IsReadBackByAnIndependentExtractor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = ApplyAndSave(
            PdfTypewriterTextOperation.Create(1, TargetBox, "AGREEMENT CHECK"));

        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull("mutool must be able to read the saved file");
        extracted!.Should().Contain("AGREEMENT", "the typed text must be real text in the file, " +
            "not just ink — otherwise copy/search/redaction cannot see what the user sees");
        extracted.Should().Contain(Existing, "typing must not destroy existing page text");
    }

    [Fact]
    public void MultilineTypedText_RendersEveryLine()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var box = new PdfRectangle(100, 250, 500, 400);
        var path = ApplyAndSave(
            PdfTypewriterTextOperation.Create(1, box, "FIRSTLINE\nMIDDLELINE\nLASTLINE",
                new PdfTypewriterTextStyle(fontSize: 16)));

        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull();
        foreach (var line in new[] { "FIRSTLINE", "MIDDLELINE", "LASTLINE" })
            extracted!.Should().Contain(line, "every typed line must survive into the saved file");

        // The lines must be STACKED, not collapsed onto one baseline: the
        // vertical ink extent of the three-line insert must be well over
        // twice that of a single line typed with the same style. (Text
        // anchors at the top of the bounds, so asserting on box halves would
        // test the box size, not the line spacing.)
        var singlePath = ApplyAndSave(
            PdfTypewriterTextOperation.Create(1, box, "FIRSTLINE",
                new PdfTypewriterTextStyle(fontSize: 16)));
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var single = MutoolReferenceRenderer.RenderPage(singlePath, 1, Dpi);
        rendered.Should().NotBeNull();
        single.Should().NotBeNull();
        var threeLineRows = InkRowExtent(rendered!, box);
        var oneLineRows = InkRowExtent(single!, box);
        oneLineRows.Should().BeGreaterThan(0, "the single-line reference must be visible");
        ((double)threeLineRows / oneLineRows).Should().BeGreaterThan(2.0,
            $"three lines must span well over twice the vertical extent of one " +
            $"({threeLineRows} vs {oneLineRows} ink rows) — a line-spacing bug collapses " +
            "all lines onto one baseline");
    }

    [Fact]
    public void ColoredTypedText_KeepsItsColor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = ApplyAndSave(
            PdfTypewriterTextOperation.Create(1, TargetBox, "REDTEXT",
                new PdfTypewriterTextStyle(fontSize: 24, color: new PdfColor(0.85, 0.05, 0.05))));

        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull();

        RegionFraction(rendered!, TargetBox,
                p => p.Red > 140 && p.Green < 110 && p.Blue < 110)
            .Should().BeGreaterThan(0.005,
                "the typed text was styled red; an independent renderer finding no red pixels " +
                "in the region means the color was dropped when writing the appearance");
    }

    [Fact]
    public void FontSize_ScalesTheDrawnGlyphs()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var smallPath = ApplyAndSave(
            PdfTypewriterTextOperation.Create(1, TargetBox, "SIZESAMPLE",
                new PdfTypewriterTextStyle(fontSize: 10)));
        var largePath = ApplyAndSave(
            PdfTypewriterTextOperation.Create(1, TargetBox, "SIZESAMPLE",
                new PdfTypewriterTextStyle(fontSize: 24)));

        using var small = MutoolReferenceRenderer.RenderPage(smallPath, 1, Dpi);
        using var large = MutoolReferenceRenderer.RenderPage(largePath, 1, Dpi);
        small.Should().NotBeNull();
        large.Should().NotBeNull();

        var smallInk = InkFractionIn(small!, TargetBox);
        var largeInk = InkFractionIn(large!, TargetBox);
        smallInk.Should().BeGreaterThan(0, "the 10pt sample must be visible at all");
        (largeInk / smallInk).Should().BeGreaterThan(1.8,
            $"24pt glyphs must ink substantially more of the region than 10pt ones " +
            $"(got {largeInk:P2} vs {smallInk:P2}) — equal coverage means the font size " +
            "is not making it into the written appearance");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TypedText_SurvivesOnRotatedPages(int rotation)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var pdf = PdfDocument.Open(FixturePdf());
        pdf.GetPage(1).Rotation = rotation;
        PdfTypewriterTextApplier.Apply(pdf,
            PdfTypewriterTextOperation.Create(1, TargetBox, "ROTATEDINSERT",
                new PdfTypewriterTextStyle(fontSize: 18)));
        var path = SaveTemp(pdf);

        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull($"mutool must read the /Rotate {rotation} page");
        extracted!.Should().Contain("ROTATEDINSERT",
            $"typed text must survive on a /Rotate {rotation} page — losing it here means " +
            "the coordinate mapping only works upright");

        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull();
        HasNonWhitePixels(rendered!).Should().BeTrue("the rotated page must still render content");
    }

    [Fact]
    public void TypedText_OverExistingContent_ShowsBoth()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // The fixture's existing line sits at y≈700 (24pt at 72,700). Type
        // straight over it.
        var overlapBox = new PdfRectangle(72, 690, 480, 730);

        var beforePath = SaveTemp(PdfDocument.Open(FixturePdf()));
        double inkBefore;
        using (var before = MutoolReferenceRenderer.RenderPage(beforePath, 1, Dpi))
        {
            inkBefore = InkFractionIn(before!, overlapBox);
            inkBefore.Should().BeGreaterThan(0.005, "fixture sanity — the existing line is inked here");
        }

        var path = ApplyAndSave(
            PdfTypewriterTextOperation.Create(1, overlapBox, "OVERLAIDNOTE",
                new PdfTypewriterTextStyle(fontSize: 14)));

        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull();
        extracted!.Should().Contain(Existing, "typing over text must not remove it — typewriter " +
            "is an overlay, not a redaction");
        extracted.Should().Contain("OVERLAIDNOTE");

        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, overlapBox).Should().BeGreaterThan(inkBefore,
            "the overlay must add ink on top of the existing line");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private string ApplyAndSave(PdfTypewriterTextOperation operation)
    {
        var pdf = PdfDocument.Open(FixturePdf());
        var applied = PdfTypewriterTextApplier.Apply(pdf, operation);
        applied.Status.Should().Be(PdfEditOperationStatus.Applied, "the edit must apply");
        return SaveTemp(pdf);
    }

    private string SaveTemp(PdfDocument pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-typewriter-{Guid.NewGuid():N}.pdf");
        pdf.Save(path);
        _temp.Add(path);
        return path;
    }

    /// <summary>Fraction of non-white pixels inside a PDF-content-coordinate box.</summary>
    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box) =>
        RegionFraction(bmp, box, p => p.Red < 200 || p.Green < 200 || p.Blue < 200);

    private static double RegionFraction(SKBitmap bmp, PdfRectangle box, Func<SKColor, bool> predicate)
    {
        const double scale = Dpi / 72.0;
        const double pageHeight = 792;

        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));
        if (x1 <= x0 || y1 <= y0) return 0;

        int hit = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            total++;
            if (predicate(bmp.GetPixel(x, y))) hit++;
        }
        return total == 0 ? 0 : (double)hit / total;
    }

    /// <summary>Count of raster rows inside the box that contain any ink.</summary>
    private static int InkRowExtent(SKBitmap bmp, PdfRectangle box)
    {
        const double scale = Dpi / 72.0;
        const double pageHeight = 792;
        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));
        int rows = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) { rows++; break; }
        }
        return rows;
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

    /// <summary>One page, one line of existing 24pt text at (72, 700).</summary>
    private static byte[] FixturePdf()
    {
        var content = $"BT /F1 24 Tf 72 700 Td ({Existing}) Tj ET";

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
