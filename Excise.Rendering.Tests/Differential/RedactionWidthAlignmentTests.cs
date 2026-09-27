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
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1752 — closing the gap moves the text after it, not where the line is
/// anchored, so a centred, right-aligned or justified line kept stating the
/// removed width at its edges (the bench recovered it for 6-8 of 8 fixtures per
/// layout). The line is re-anchored the way it was aligned; a left-aligned line
/// keeps its start.
///
/// <para>Paragraph fixtures of four Helvetica 12 pt lines, each its own text
/// object as the #1752 bench generator writes them; SECRET is in line 2.
/// Positions are read with <c>mutool -F stext</c>, and mutool must not extract
/// the term.</para>
/// </summary>
public class RedactionWidthAlignmentTests : IDisposable
{
    private const string Term = "SECRET";
    private const double Size = 12;
    private const double Leading = 14.4;
    private const double Top = 500;
    private const double PageWidth = 600;

    private static readonly string[] Paragraph =
    {
        "The quick brown fox jumps over the lazy dog again and again",
        "Pack my box with SECRET liquor jugs today",
        "Sphinx of black quartz judge my vow",
        "How vexingly quick daft zebras jump over it all",
    };

    private readonly List<string> _temp = new();

    [Fact]
    public void Centred_TheLineStaysCentred()
    {
        var (before, after, closed, saved) = RedactWithFile(Lines(Paragraph, (w, _) => 300 - w / 2));

        FirstOf(after).Should().BeApproximately(FirstOf(before) + closed / 2, 0.3, "the start moves in by half");
        LastOf(after).Should().BeApproximately(LastOf(before) - closed / 2, 0.3, "the end moves in by half");
        OtherLinesStayed(before, after);
        OldStartIsNotInTheFile(before, saved);
    }

    [Fact]
    public void RightAligned_TheLineStaysOnTheRightEdge()
    {
        var (before, after, closed, saved) = RedactWithFile(Lines(Paragraph, (w, _) => 540 - w));

        FirstOf(after).Should().BeApproximately(FirstOf(before) + closed, 0.3);
        LastOf(after).Should().BeApproximately(LastOf(before), 0.3, "the right edge is where it was");
        OtherLinesStayed(before, after);
        OldStartIsNotInTheFile(before, saved);
    }

    [Fact]
    public void Justified_TheLineStillSpansTheColumn_AndItsOldSpacingIsNotInTheFile()
    {
        // Word spacing set per line, as a justifying producer writes it.
        var content = Lines(Paragraph, (_, _) => 50, justifyTo: 490);
        var oldSpacing = WordSpacingOf(content, line: 1);

        var (before, after, _, saved) = RedactWithFile(content);

        FirstOf(after).Should().BeApproximately(FirstOf(before), 0.3);
        LastOf(after).Should().BeApproximately(LastOf(before), 0.3, "the line still ends at the column edge");
        OtherLinesStayed(before, after);
        TwOperands(saved).Should().NotContain(v => Math.Abs(v - oldSpacing) < 1e-3,
            "the rewritten spacing replaces the old one; restating it would state the removed width");
    }

    [Fact]
    public void LeftAligned_TheLineKeepsItsStart()
    {
        var (before, after, closed) = Redact(Lines(Paragraph, (_, _) => 50));

        FirstOf(after).Should().BeApproximately(FirstOf(before), 0.3);
        LastOf(after).Should().BeApproximately(LastOf(before) - closed, 0.3);
        OtherLinesStayed(before, after);
    }

    [Fact]
    public void LoneCentredLine_IsCentredOnThePage()
    {
        var (before, after, closed) = Redact(Lines(new[] { "Report on SECRET matters" }, (w, _) => 300 - w / 2),
            line: 0);

        FirstOf(after, 0).Should().BeApproximately(FirstOf(before, 0) + closed / 2, 0.3);
    }

    [Fact]
    public void FixedMarker_CentredLine_StaysBesideItsMarker_AndTheEdgeIsReported()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // The marker is drawn where the term began; re-centring would slide the
        // text before it out from beside the marker. The line keeps its start,
        // and the edge that still moves is reported rather than hidden.
        var original = Fixture(Lines(Paragraph, (w, _) => 300 - w / 2));
        using var doc = PdfDocument.Open(original);
        doc.RedactText(Term, RedactionOptions.Default with { Width = WidthPolicy.FixedMarker });
        var afterPath = WriteTemp(doc.SaveToBytes());

        doc.RedactionLedger.WidthNotes.Should().ContainSingle().Which.Should().Contain("beside its marker");
        FirstOf(Glyphs(afterPath)).Should().BeApproximately(FirstOf(Glyphs(WriteTemp(original))), 0.05);
        (MutoolTextExtractor.ExtractPage(afterPath, 1) ?? "").Should().NotContain(Term);
    }

    [Fact]
    public void JustifiedLineWhoseWordSpacingAlsoReachesTextInsideQ_IsReportedAndThatTextStays()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // q saves the Tw line 2 set and the text inside it still uses it, so
        // rewriting that Tw would widen the spaces of a line far below.
        var lines = Lines(Paragraph, (_, _) => 50, justifyTo: 490).Split('\n').ToList();
        lines.Insert(2, "q BT /F1 12 Tf 50 300 Td (far below with spaces here) Tj ET Q");
        var original = Fixture(string.Join("\n", lines));
        using var doc = PdfDocument.Open(original);
        doc.RedactText(Term, RedactionOptions.Default with { Width = WidthPolicy.CloseGap, DrawBox = false });
        var afterPath = WriteTemp(doc.SaveToBytes());

        doc.RedactionLedger.WidthNotes.Should().ContainSingle().Which.Should().Contain("not re-justified");
        var farY = 792 - 300;
        Glyphs(afterPath).Where(g => Math.Abs(g.Y - farY) < 3).Select(g => g.X)
            .Should().Equal(Glyphs(WriteTemp(original)).Where(g => Math.Abs(g.Y - farY) < 3).Select(g => g.X),
                (a, b) => Math.Abs(a - b) < 0.05, "text outside the redacted line must not move");
        (MutoolTextExtractor.ExtractPage(afterPath, 1) ?? "").Should().NotContain(Term);
    }

    [Fact]
    public void JustifiedLineWhoseLeadInSharesTheLineAbovesWordSpacing_IsRejustifiedOverItsOwnRun()
    {
        // scotus-trump-v-us p.40's shape: the line's lead-in run shows its space
        // under the Tw that justifies the line above; the rest of the line has a
        // Tw of its own. Only that Tw is rewritten and only its spaces widen, so
        // the line still spans the column and the line above does not move.
        var lines = Lines(Paragraph, (_, _) => 50, justifyTo: 490).Split('\n');
        const string lead = "Pack ", rest = "my box with SECRET liquor jugs today";
        var sharedTw = WordSpacingOf(string.Join("\n", lines), line: 0);
        var ownTw = (490 - 50 - NaturalWidth(lead + rest) - sharedTw) / rest.Count(c => c == ' ');
        lines[1] = $"BT /F1 12 Tf 50 {Fmt(Top - Leading)} Td ({lead}) Tj {Fmt(ownTw)} Tw ({rest}) Tj ET";
        var content = string.Join("\n", lines);

        var (before, after, _, saved) = RedactWithFile(content);

        FirstOf(after).Should().BeApproximately(FirstOf(before), 0.3);
        LastOf(after).Should().BeApproximately(LastOf(before), 0.3, "the line still ends at the column edge");
        OtherLinesStayed(before, after);
        TwOperands(saved).Should().NotContain(v => Math.Abs(v - ownTw) < 1e-3)
            .And.Contain(v => Math.Abs(v - sharedTw) < 1e-3, "the shared Tw still justifies the line above");
    }

    [Fact]
    public void JustifiedLineEndingInASpace_StillEndsAtTheColumnEdge()
    {
        // scotus-trump-v-us p.40 again: a negative Tc starts the trailing space
        // inside the last glyph's box. Widening it moves nothing on the line, so
        // counting it as a word space left the line one share short.
        var lines = Lines(Paragraph, (_, _) => 50, justifyTo: 490).Split('\n');
        const string text = "Pack my box with SECRET liquor jugs today";
        const double tc = -0.05;
        var tw = (490 - 50 - NaturalWidth(text) - tc * text.Length) / text.Count(c => c == ' ');
        lines[1] = $"BT /F1 12 Tf {Fmt(tc)} Tc {Fmt(tw)} Tw 50 {Fmt(Top - Leading)} Td ({text} ) Tj 0 Tc ET";

        var (before, after, _) = Redact(string.Join("\n", lines));

        FirstOf(after).Should().BeApproximately(FirstOf(before), 0.3);
        LastOf(after).Should().BeApproximately(LastOf(before), 0.3, "the line still ends at the column edge");
        OtherLinesStayed(before, after);
    }

    [Fact]
    public void JustifiedLineWithAnInvisibleSpaceOfItsOwn_StretchesOnlyTheSpacesItShows()
    {
        // scotus-trump-v-us p.40 once more: an /Artifact space under 3 Tr sits
        // between the footnote mark and the line's first word, with a Tw of its
        // own. Stretching it widened nothing visible and moved the word after it.
        var lines = Lines(Paragraph, (_, _) => 50, justifyTo: 490).Split('\n');
        const string lead = "Pack", rest = "my box with SECRET liquor jugs today";
        var y = Fmt(Top - Leading);
        var restX = 50 + NaturalWidth(lead + " ");
        var tw = (490 - 50 - NaturalWidth(lead + " " + rest)) / rest.Count(c => c == ' ');
        lines[1] = $"BT /F1 12 Tf 50 {y} Td ({lead}) Tj ET\n" +
                   $"BT /F1 12 Tf 0 Tw 3 Tr {Fmt(50 + NaturalWidth(lead))} {y} Td ( ) Tj 0 Tr ET\n" +
                   $"BT /F1 12 Tf {Fmt(tw)} Tw {Fmt(restX)} {y} Td ({rest}) Tj ET";

        var (before, after, _) = Redact(string.Join("\n", lines));

        OnLine(after, 1).First(g => g.Char == "m").X.Should().BeApproximately(
            OnLine(before, 1).First(g => g.Char == "m").X, 0.3, "no visible space stands before it");
        LastOf(after).Should().BeApproximately(LastOf(before), 0.3, "the line still ends at the column edge");
        OtherLinesStayed(before, after);
    }

    [Fact]
    public void JustifiedWithOneSharedWordSpacing_IsReportedNotRejustified()
    {
        // Courier: every line below has the same length and spacing, so one Tw
        // justifies them all — rewriting it would move the other lines.
        const string content =
            "BT /F2 12 Tf 2.5 Tw 50 500 Td (alpha bravos delta hotel) Tj 0 -14.4 Td " +
            "(alpha SECRET delta hotel) Tj 0 -14.4 Td (gamma charly delta india) Tj ET";

        using var doc = PdfDocument.Open(Fixture(content));
        doc.RedactText(Term, RedactionOptions.Default with { Width = WidthPolicy.CloseGap, DrawBox = false });
        doc.RedactionLedger.WidthNotes.Should().ContainSingle().Which.Should().Contain("not re-justified");
        MutoolTextExtractor.ExtractPage(WriteTemp(doc.SaveToBytes()), 1).Should().NotContain(Term);
    }

    // ---------------------------------------------------------------- helpers

    private (List<MutoolGlyphPositions.Glyph> Before, List<MutoolGlyphPositions.Glyph> After, double Closed)
        Redact(string content, int line = 1)
    {
        var (before, after, closed, _) = RedactWithFile(content, line);
        return (before, after, closed);
    }

    private (List<MutoolGlyphPositions.Glyph> Before, List<MutoolGlyphPositions.Glyph> After, double Closed, byte[] Saved)
        RedactWithFile(string content, int line = 1)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var original = Fixture(content);
        byte[] saved;
        using (var doc = PdfDocument.Open(original))
        {
            doc.RedactText(Term, RedactionOptions.Default with { Width = WidthPolicy.CloseGap, DrawBox = false })
                .VerifiedRemovals.Should().Be(1);
            doc.RedactionLedger.WidthNotes.Should().BeEmpty();
            saved = doc.SaveToBytes();
        }
        var afterPath = WriteTemp(saved);
        (MutoolTextExtractor.ExtractPage(afterPath, 1) ?? "").Should().NotContain(Term);

        var before = Glyphs(WriteTemp(original));
        var after = Glyphs(afterPath);
        var termLine = OnLine(before, line);
        var chars = string.Concat(termLine.Select(g => g.Char));
        var at = chars.IndexOf(Term, StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        var closed = termLine[at + Term.Length].X - termLine[at].X;
        return (before, after, closed, saved);
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
        var y = 792 - (Top - line * Leading);
        return glyphs.Where(g => Math.Abs(g.Y - y) < 3).OrderBy(g => g.X).ToList();
    }

    private static double FirstOf(List<MutoolGlyphPositions.Glyph> glyphs, int line = 1) => OnLine(glyphs, line)[0].X;

    private static double LastOf(List<MutoolGlyphPositions.Glyph> glyphs, int line = 1) => OnLine(glyphs, line)[^1].X;

    private static void OtherLinesStayed(List<MutoolGlyphPositions.Glyph> before, List<MutoolGlyphPositions.Glyph> after)
    {
        foreach (var line in new[] { 0, 2, 3 })
        {
            var b = OnLine(before, line);
            var a = OnLine(after, line);
            a.Select(g => g.X).Should().Equal(b.Select(g => g.X), (x, y) => Math.Abs(x - y) < 0.05,
                $"line {line} is not the redacted line and must not move");
        }
    }

    /// <summary>
    /// One text object per line at x = <paramref name="x"/>(natural width,
    /// line). With <paramref name="justifyTo"/>, every line but the last gets
    /// the Tw that makes it end there; the last sets 0.
    /// </summary>
    private static string Lines(string[] lines, Func<double, int, double> x, double? justifyTo = null)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var natural = NaturalWidth(lines[i]);
            var left = x(natural, i);
            var tw = justifyTo is double edge && i < lines.Length - 1
                ? (edge - left - natural) / lines[i].Count(c => c == ' ')
                : 0;
            sb.Append("BT /F1 12 Tf ");
            if (justifyTo != null) sb.Append(Fmt(tw)).Append(" Tw ");
            sb.Append(Fmt(left)).Append(' ').Append(Fmt(Top - i * Leading)).Append(" Td (")
              .Append(lines[i]).Append(") Tj ET\n");
        }
        return sb.ToString();
    }

    private static double WordSpacingOf(string content, int line) =>
        double.Parse(content.Split('\n')[line].Split(' ')[4], CultureInfo.InvariantCulture);

    /// <summary>The line's advance with no spacing, as excise measures it — fixture construction, not the oracle.</summary>
    private static double NaturalWidth(string text)
    {
        using var doc = PdfDocument.Open(Fixture($"BT /F1 12 Tf 0 0 Td ({text}) Tj ET"));
        var letters = doc.GetPage(1).Letters;
        return letters.Max(l => l.StartX + l.Width);
    }

    /// <summary>
    /// A centred or right-aligned line's original start measures its original
    /// length, removed word included; no positioning operand may restate it.
    /// </summary>
    private static void OldStartIsNotInTheFile(List<MutoolGlyphPositions.Glyph> before, byte[] saved)
    {
        var oldStart = FirstOf(before);
        using var doc = PdfDocument.Open(saved);
        doc.GetPage(1).GetContentStream().Operators
            .Where(op => op.Name is "Td" or "TD" or "Tm")
            .SelectMany(op => op.Operands)
            .Select(o => o.TryGetNumber(out var v) ? v : double.NaN)
            .Should().NotContain(v => Math.Abs(v - oldStart) < 0.05,
                "the line was re-anchored by rewriting its positioning, not by moving it after the fact");
    }

    private static List<double> TwOperands(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetPage(1).GetContentStream().Operators
            .Where(op => op.Name == "Tw" && op.Operands.Count == 1 && op.Operands[0].TryGetNumber(out _))
            .Select(op => { op.Operands[0].TryGetNumber(out var v); return v; })
            .ToList();
    }

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-align-{Guid.NewGuid():N}.pdf");
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
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(PageWidth)} 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>",
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
