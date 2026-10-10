using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// The owner's decision of 2026-10-10 (Refs #1715): by default a redaction does
/// not reflow, move or redraw anything else on the page. The default width
/// policy is <see cref="WidthPolicy.OvershootPreserveLayout"/>: every removed
/// glyph keeps its advance and the covering box is rounded up into the space
/// beside it without covering surviving text (#1189). Maximum still closes the
/// width (<see cref="WidthPolicy.FixedMarker"/>).
///
/// <para>Positions are read with <c>mutool -F stext</c> and ink with mutool's
/// renderer, never with excise's own geometry; mutool must not extract the
/// term and the saved bytes must not hold it.</para>
/// </summary>
public class RedactionDefaultKeepsLayoutTests : IDisposable
{
    private const string Term = "SECRET";
    private const double Size = 12;
    private const double Leading = 14.4;
    private const double Top = 500;
    private const double PageWidth = 600;
    private const double PageHeight = 792;
    private const int Dpi = 144;

    private static readonly string[] Paragraph =
    {
        "The quick brown fox jumps over the lazy dog again and again",
        "Pack my box with SECRET liquor jugs today",
        "Sphinx of black quartz judge my vow",
        "How vexingly quick daft zebras jump over it all",
    };

    public enum Layout { Left, Centred, RightAligned, Justified, ScaledTm }

    private readonly List<string> _temp = new();

    [Fact]
    public void TheDefaultWidthPolicy_KeepsLayout_AndMaximumClosesTheWidth()
    {
        RedactionOptions.Default.Width.Should().Be(WidthPolicy.OvershootPreserveLayout);
        RedactionOptions.ForProfile(RedactionProfile.Standard).Width.Should().Be(WidthPolicy.OvershootPreserveLayout);
        RedactionOptions.ForProfile(RedactionProfile.Maximum).Width.Should().Be(WidthPolicy.FixedMarker,
            "Maximum keeps closing the width; only the Standard default changed");
    }

    [Theory]
    [InlineData(Layout.Left)]
    [InlineData(Layout.Centred)]
    [InlineData(Layout.RightAligned)]
    [InlineData(Layout.Justified)]
    [InlineData(Layout.ScaledTm)]
    public void DefaultRedaction_MovesNoSurvivingGlyph(Layout layout)
    {
        var (before, after, saved, notes) = Redact(Content(layout), RedactionOptions.Default);

        notes.Should().BeEmpty("no width is closed, so no WIDTH NOT CLOSED note can apply");
        SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty();

        var termLine = OnLine(before, 1);
        var at = IndexOfTerm(termLine);
        var survivors = termLine.Take(at).Concat(termLine.Skip(at + Term.Length)).ToList();
        AssertSamePositions(OnLine(after, 1), survivors, $"{layout}: the redacted line");
        foreach (var line in new[] { 0, 2, 3 })
            AssertSamePositions(OnLine(after, line), OnLine(before, line), $"{layout}: line {line}");
        after.Should().HaveCount(before.Count - Term.Length, "every glyph but the term's is still on the page");
    }

    [Theory]
    [InlineData(Layout.Left)]
    [InlineData(Layout.Centred)]
    [InlineData(Layout.RightAligned)]
    [InlineData(Layout.Justified)]
    [InlineData(Layout.ScaledTm)]
    public void DefaultBox_CoversTheRemovedRun_AndNoSurvivingNeighbour(Layout layout)
    {
        var content = Content(layout);
        var (before, _, saved, _) = Redact(content, RedactionOptions.Default);
        var termLine = OnLine(before, 1);
        var at = IndexOfTerm(termLine);
        var termLeft = termLine[at].X;
        var termRight = termLine[at + Term.Length].X;   // where the glyph after the term starts
        var baselineY = Top - Leading;   // PDF space

        var box = AppendedBoxes(saved).Should().ContainSingle().Subject;
        box.Left.Should().BeLessThanOrEqualTo(termLeft + 0.05);
        box.Right.Should().BeGreaterThanOrEqualTo(termRight - 0.05,
            "the box is at least as wide as the removed run");
        // #1189: rounded UP toward a whole em, as far as the spaces beside it
        // allow (two Helvetica 12 spaces give ~5 pt), so it is not the run's
        // exact extent.
        ((box.Right - box.Left) - (termRight - termLeft)).Should().BeGreaterThan(1.0,
            $"{layout}: the box is widened, not drawn to the removed run's exact width");

        using var beforeBmp = MutoolReferenceRenderer.RenderPage(WriteTemp(Fixture(content)), 1, Dpi)!;
        using var afterBmp = MutoolReferenceRenderer.RenderPage(WriteTemp(saved), 1, Dpi)!;

        // The removed run itself is covered (cap height, shrunk off the edges).
        InkFractionIn(afterBmp, new PdfRectangle(termLeft + 0.5, baselineY + 1, termRight - 0.5, baselineY + 8))
            .Should().BeGreaterThan(0.95);

        // The word on each side keeps exactly the ink it had: the box grew into
        // the spaces only.
        var spaceBefore = at - 1;
        var wordBefore = termLine.FindLastIndex(spaceBefore - 1, g => g.Char == " ") + 1;
        var wordAfterEnd = termLine.FindIndex(at + Term.Length + 1, g => g.Char == " ");
        var neighbours = new[]
        {
            new PdfRectangle(termLine[wordBefore].X, baselineY - 3, termLine[spaceBefore].X, baselineY + 9),
            new PdfRectangle(termLine[at + Term.Length + 1].X, baselineY - 3, termLine[wordAfterEnd].X, baselineY + 9),
        };
        foreach (var n in neighbours)
            InkFractionIn(afterBmp, n).Should().BeApproximately(InkFractionIn(beforeBmp, n), 0.01,
                $"{layout}: the box must not cover surviving text");
    }

    [Fact]
    public void MaximumProfile_StillClosesTheGap()
    {
        var (before, after, saved, _) = Redact(Content(Layout.Left), RedactionOptions.ForProfile(RedactionProfile.Maximum));
        SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty();

        OnLine(after, 1)[^1].X.Should().BeLessThan(OnLine(before, 1)[^1].X - 10,
            "Maximum closes the removed run's width, so the end of the line moves left");
        foreach (var line in new[] { 0, 2, 3 })
            AssertSamePositions(OnLine(after, line), OnLine(before, line), $"line {line}");
        AppendedBoxes(saved).Should().ContainSingle("FixedMarker still draws a visible mark (#1725)");
    }

    [Fact]
    public void MaximumMarker_OnTheW9Idiom_IsTwoRenderedEmsWide()
    {
        // /F1 1 Tf scaled by Tm: the marker is sized in ems of the RENDERED
        // size (12 pt), not of the 1 pt Tf size, which drew a 2 pt sliver.
        var (_, _, saved, _) = Redact(Content(Layout.ScaledTm), RedactionOptions.ForProfile(RedactionProfile.Maximum));
        var box = AppendedBoxes(saved).Should().ContainSingle().Subject;
        (box.Right - box.Left).Should().BeApproximately(2 * Size, 0.01);
    }

    [Theory]
    [InlineData(WidthPolicy.FixedMarker)]
    [InlineData(WidthPolicy.CloseGap)]
    [InlineData(WidthPolicy.QuantizeGap)]
    public void AnExplicitGapClosingPolicy_StillMovesTheLine(WidthPolicy policy)
    {
        var (before, after, saved, _) = Redact(Content(Layout.Left), RedactionOptions.Default with { Width = policy });
        SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty();
        var termLine = OnLine(before, 1);
        var at = IndexOfTerm(termLine);
        var removed = termLine[at + Term.Length].X - termLine[at].X;
        Math.Abs(OnLine(after, 1)[^1].X - termLine[^1].X).Should().BeGreaterThan(0.5,
            $"{policy} does not keep the removed run's {removed:F1} pt advance");
    }

    // ---------------------------------------------------------------- helpers

    private (List<MutoolGlyphPositions.Glyph> Before, List<MutoolGlyphPositions.Glyph> After, byte[] Saved, List<string> Notes)
        Redact(string content, RedactionOptions options)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var original = Fixture(content);
        byte[] saved;
        List<string> notes;
        using (var doc = PdfDocument.Open(original))
        {
            doc.RedactText(Term, options).VerifiedRemovals.Should().Be(1);
            notes = doc.RedactionLedger.WidthNotes.ToList();
            saved = doc.SaveToBytes();
        }
        var afterPath = WriteTemp(saved);
        (MutoolTextExtractor.ExtractPage(afterPath, 1) ?? "").Should().NotContain(Term);
        return (Glyphs(WriteTemp(original)), Glyphs(afterPath), saved, notes);
    }

    private static void AssertSamePositions(
        List<MutoolGlyphPositions.Glyph> actual, List<MutoolGlyphPositions.Glyph> expected, string what)
    {
        actual.Select(g => g.Char).Should().Equal(expected.Select(g => g.Char), what);
        actual.Select(g => (g.X, g.Y)).Should().Equal(expected.Select(g => (g.X, g.Y)),
            (a, b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01,
            $"{what}: no glyph may move");
    }

    private static int IndexOfTerm(List<MutoolGlyphPositions.Glyph> line)
    {
        var at = string.Concat(line.Select(g => g.Char)).IndexOf(Term, StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, "fixture sanity: the term is mid-line");
        return at;
    }

    /// <summary>The page content for <paramref name="layout"/>: four lines, SECRET mid-line 2.</summary>
    private static string Content(Layout layout) => layout switch
    {
        Layout.Left => Lines((_, _) => 50),
        Layout.Centred => Lines((w, _) => PageWidth / 2 - w / 2),
        Layout.RightAligned => Lines((w, _) => 540 - w),
        Layout.Justified => Lines((_, _) => 50, justifyTo: 490),
        // The W-9 idiom: a 1 pt font scaled up by the text matrix.
        Layout.ScaledTm => Lines((_, _) => 50, scaledTm: true),
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    private static string Lines(Func<double, int, double> x, double? justifyTo = null, bool scaledTm = false)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Paragraph.Length; i++)
        {
            var natural = NaturalWidth(Paragraph[i]);
            var left = x(natural, i);
            var y = Top - i * Leading;
            var tw = justifyTo is double edge && i < Paragraph.Length - 1
                ? (edge - left - natural) / Paragraph[i].Count(c => c == ' ')
                : 0;
            sb.Append("BT ");
            if (scaledTm)
                sb.Append($"/F1 1 Tf {Fmt(Size)} 0 0 {Fmt(Size)} {Fmt(left)} {Fmt(y)} Tm ");
            else
                sb.Append($"/F1 {Fmt(Size)} Tf {Fmt(left)} {Fmt(y)} Td ");
            if (justifyTo != null) sb.Append(Fmt(tw)).Append(" Tw ");
            sb.Append('(').Append(Paragraph[i]).Append(") Tj ET\n");
        }
        return sb.ToString();
    }

    /// <summary>The line's advance with no spacing, as excise measures it — fixture construction, not the oracle.</summary>
    private static double NaturalWidth(string text)
    {
        using var doc = PdfDocument.Open(Fixture($"BT /F1 12 Tf 0 0 Td ({text}) Tj ET"));
        return doc.GetPage(1).Letters.Max(l => l.StartX + l.Width);
    }

    /// <summary>The <c>re</c> rectangles the redaction appended after the original text objects.</summary>
    private static List<PdfRectangle> AppendedBoxes(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var boxes = new List<PdfRectangle>();
        foreach (var op in doc.GetPage(1).GetContentStream().Operators.Where(o => o.Name == "re"))
        {
            var v = op.Operands.Select(o => o.TryGetNumber(out var n) ? n : double.NaN).ToArray();
            if (v.Length != 4 || v.Any(double.IsNaN)) continue;
            boxes.Add(new PdfRectangle(Math.Min(v[0], v[0] + v[2]), Math.Min(v[1], v[1] + v[3]),
                Math.Max(v[0], v[0] + v[2]), Math.Max(v[1], v[1] + v[3])));
        }
        return boxes;
    }

    private static List<MutoolGlyphPositions.Glyph> Glyphs(string path)
    {
        var glyphs = MutoolGlyphPositions.ExtractPage(path, 1);
        glyphs.Should().NotBeNull();
        return glyphs!.ToList();
    }

    /// <summary>The glyphs of paragraph line <paramref name="line"/>, left to right. mutool's y points down.</summary>
    private static List<MutoolGlyphPositions.Glyph> OnLine(List<MutoolGlyphPositions.Glyph> glyphs, int line)
    {
        var y = PageHeight - (Top - line * Leading);
        return glyphs.Where(g => Math.Abs(g.Y - y) < 3).OrderBy(g => g.X).ToList();
    }

    /// <summary>Fraction of pixels in <paramref name="box"/> (PDF space) that carry ink.</summary>
    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box)
    {
        const double scale = Dpi / 72.0;
        int x0 = Math.Max(0, (int)Math.Floor(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)Math.Ceiling(box.Right * scale) - 1);
        int y0 = Math.Max(0, (int)Math.Floor((PageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling((PageHeight - box.Bottom) * scale) - 1);
        int ink = 0, total = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var p = bmp.GetPixel(x, y);
                total++;
                if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-keeplayout-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static byte[] Fixture(string content)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(PageWidth)} {Fmt(PageHeight)}] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        using var ms = new MemoryStream();
        void Write(string value) => ms.Write(Encoding.Latin1.GetBytes(value));
        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        var xref = ms.Position;
        Write($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++)
            Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
