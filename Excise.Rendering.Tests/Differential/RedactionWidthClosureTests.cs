using System;
using System.Collections.Generic;
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
/// #1751 — a width-closing policy closes the gap on the whole LINE, not only
/// inside the operator that held the removed glyphs. Real producers split a
/// line into separately positioned runs; before the ledger, the run after the
/// first positioning operator stayed put and the gap reappeared there, still
/// stating the removed width.
///
/// <para>Every position is read with <c>mutool -F stext</c> (an independent
/// oracle), and every test also checks that mutool cannot extract the term.
/// The fixtures are Helvetica 20 pt, where "Name: SECRET" is 145.58 pt, so a
/// run placed 145.58 pt from the line start abuts the term.</para>
/// </summary>
public class RedactionWidthClosureTests : IDisposable
{
    private const string Term = "SECRET";
    private readonly List<string> _temp = new();

    [Fact]
    public void RunPlacedByItsOwnTd_MovesLeftByTheRemovedWidth()
    {
        AssertLineClosed(
            "BT /F1 20 Tf 40 100 Td (Name: SECRET) Tj 145.58 0 Td ( Zoo tail.) Tj ET",
            WidthPolicy.CloseGap);
    }

    [Fact]
    public void RunInALaterTextObject_MovesLeftByTheRemovedWidth()
    {
        AssertLineClosed(
            "BT /F1 20 Tf 40 100 Td (Name: SECRET) Tj ET\nBT /F1 20 Tf 185.58 100 Td ( Zoo tail.) Tj ET",
            WidthPolicy.CloseGap);
    }

    [Fact]
    public void EveryWordItsOwnTextObject_TheWholeRestOfTheLineCloses()
    {
        // issue14627's shape: each word is its own positioned text object, so
        // before #1751 nothing after the term moved at all.
        AssertLineClosed(
            "BT /F1 20 Tf 40 100 Td (Name: ) Tj ET\nBT /F1 20 Tf 104.46 100 Td (SECRET) Tj ET\n" +
            "BT /F1 20 Tf 185.58 100 Td ( Zoo) Tj ET\nBT /F1 20 Tf 225.6 100 Td ( tail.) Tj ET",
            WidthPolicy.CloseGap);
    }

    [Fact]
    public void RunPastAColumnGutter_DoesNotMove()
    {
        // A second column on the same baseline: moving it would measure the
        // removed width against every other line of that column.
        var (before, after, _) = Redact(
            "BT /F1 20 Tf 40 100 Td (Name: SECRET) Tj 145.58 0 Td ( Zoo.) Tj ET\n" +
            "BT /F1 20 Tf 400 100 Td (Qx) Tj ET",
            WidthPolicy.CloseGap);

        XOf(after, "Q").Should().BeApproximately(XOf(before, "Q"), 0.3, "the other column stays where it was");
        XOf(after, "Z").Should().BeLessThan(XOf(before, "Z") - 50, "the text of the redacted column still closed up");
    }

    [Fact]
    public void ReconstructedOperator_DoesNotReopenTheGapAtTheNextRun()
    {
        // ' cannot be operand-split, so the term's operator goes through the
        // reconstruction fallback, and its #758 compensation keeps the chained
        // " Zoo" run placed. Replaying the WHOLE removed advance there (the
        // pre-#1751 behaviour) kept " Zoo" where it was while the reconstructed
        // "Name: " stayed put: the gap reopened at the run boundary.
        AssertLineClosed(
            "BT /F1 20 Tf 0 TL 40 100 Td (Name: SECRET) ' ( Zoo tail.) Tj ET",
            WidthPolicy.CloseGap);
    }

    [Fact]
    public void ReconstructedOperator_TheFileDoesNotStateTheRemovedAdvance()
    {
        // The same fallback, read from the FILE. Its compensation used to replay
        // the whole operator's advance ("Name: SECRET", 7279 thousandths), and
        // anything moving " Zoo" back would then add SECRET's own 4056 — two
        // numbers whose difference is the removed width. Closing the width
        // means the compensation replays only what was kept.
        var pdf = Fixture("BT /F1 20 Tf 0 TL 40 100 Td (Name: SECRET) ' ( Zoo tail.) Tj ET");
        using var doc = PdfDocument.Open(pdf);
        doc.RedactText(Term, RedactionOptions.Default with { Width = WidthPolicy.CloseGap, DrawBox = false });
        var saved = doc.SaveToBytes();

        var numbers = TjNumbers(saved);
        numbers.Should().NotBeEmpty("the compensation still keeps the chained run placed");
        numbers.Should().OnlyContain(n => Math.Abs(n) < 4000,
            "no TJ number may carry the removed run's 4056-thousandth advance, alone or inside the whole operator's");
        MutoolTextExtractor.ExtractPage(WriteTemp(saved), 1).Should().NotContain(Term);
    }

    [Fact]
    public void FollowerShownByAQuoteOperator_IsShiftedAfterItsImplicitLineMove()
    {
        // With 0 TL, ' re-shows on the same line. A TJ in front of it would be
        // discarded by its implicit T*, so the shift has to follow the T*.
        AssertLineClosed(
            "BT /F1 20 Tf 0 TL 40 100 Td (Name: SECRET) Tj 145.58 0 Td ( Zoo tail.) ' ET",
            WidthPolicy.CloseGap);
    }

    [Fact]
    public void FollowerUnderCharSpacingWordSpacingAndScaling_LandsExactly()
    {
        // Rule 7: the TJ number that moves the follower is scaled by Tfs and Th
        // but NOT by Tc or Tw (§9.4.4). The follower sets all three to
        // non-default values; the term's own run sets Tc and Tw, which its
        // removed advance must include.
        AssertLineClosed(
            "BT /F1 20 Tf 1.5 Tc 3 Tw 40 100 Td (Name: SECRET) Tj ET\n" +
            "BT /F1 20 Tf 0.5 Tc 7 Tw 60 Tz 206.58 100 Td ( Zoo tail.) Tj ET",
            WidthPolicy.CloseGap);
    }

    [Fact]
    public void SequentialRedactionsOnOneLine_EachClosesTheLine()
    {
        const string content =
            "BT /F1 20 Tf 40 100 Td (Name: SECRET) Tj 145.58 0 Td ( Zoo) Tj ET\n" +
            "BT /F1 20 Tf 225.6 100 Td ( OTHER tail.) Tj ET";
        var original = Fixture(content);
        var beforePath = WriteTemp(original);

        byte[] once;
        using (var doc = PdfDocument.Open(original))
        {
            doc.RedactText(Term, RedactionOptions.Default with { Width = WidthPolicy.CloseGap });
            once = doc.SaveToBytes();
        }
        byte[] twice;
        using (var doc = PdfDocument.Open(once))
        {
            doc.RedactText("OTHER", RedactionOptions.Default with { Width = WidthPolicy.CloseGap });
            twice = doc.SaveToBytes();
        }

        var before = Glyphs(beforePath);
        var afterPath = WriteTemp(twice);
        var after = Glyphs(afterPath);
        var widthTerm = XOf(before, " ", 1) - XOf(before, "S");
        var widthOther = XOf(before, " ", 3) - XOf(before, "O");

        XOf(after, "Z").Should().BeApproximately(XOf(before, "Z") - widthTerm, 0.3);
        XOf(after, "t").Should().BeApproximately(XOf(before, "t") - widthTerm - widthOther, 0.3,
            "the second redaction closed on top of the first, instead of reopening it");
        var text = MutoolTextExtractor.ExtractPage(afterPath, 1) ?? "";
        text.Should().NotContain(Term).And.NotContain("OTHER").And.Contain("Zoo").And.Contain("tail");
    }

    [Fact]
    public void TextAFormXObjectDrawsAfterTheTerm_IsReportedNotSilentlyLeftBehind()
    {
        // The form sits on the line past the term, outside the redaction area,
        // so it is not flattened, and nothing in the page stream can move it.
        var pdf = Fixture(
            "BT /F1 20 Tf 40 100 Td (Name: SECRET) Tj ET\nq 1 0 0 1 190 100 cm /Fm1 Do Q",
            "/XObject << /Fm1 6 0 R >>",
            Stream("BT /F1 20 Tf 0 0 Td (Zoo) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 -10 100 30] /Resources << /Font << /F1 5 0 R >> >>"));
        using var doc = PdfDocument.Open(pdf);
        doc.RedactText(Term, RedactionOptions.Default with { Width = WidthPolicy.CloseGap });

        doc.RedactionLedger.WidthNotes.Should().ContainSingle()
            .Which.Should().Contain("form XObject");
        MutoolTextExtractor.ExtractPage(WriteTemp(doc.SaveToBytes()), 1).Should().NotContain(Term);
    }

    [Fact]
    public void RotatedText_IsReportedAsClosedInsideItsOwnRunOnly()
    {
        var pdf = Fixture("BT /F1 20 Tf 0 1 -1 0 100 40 Tm (Name: SECRET) Tj 145.58 0 Td ( Zoo.) Tj ET");
        using var doc = PdfDocument.Open(pdf);
        doc.RedactText(Term, RedactionOptions.Default with { Width = WidthPolicy.CloseGap });

        doc.RedactionLedger.WidthNotes.Should().ContainSingle().Which.Should().Contain("rotated");
        MutoolTextExtractor.ExtractPage(WriteTemp(doc.SaveToBytes()), 1).Should().NotContain(Term);
    }

    /// <summary>
    /// Redact <see cref="Term"/> and assert, glyph by glyph along the line, that
    /// everything before the term stayed and everything after it moved left by
    /// exactly the term's width in the original (the distance from its first
    /// glyph to the first glyph after it).
    /// </summary>
    private void AssertLineClosed(string content, WidthPolicy width)
    {
        var (before, after, afterPath) = Redact(content, width);

        var at = IndexOfTerm(before);
        var removedWidth = before[at + Term.Length].X - before[at].X;
        removedWidth.Should().BeGreaterThan(50, "the fixture places text right after the term");

        var kept = before.Take(at).Concat(before.Skip(at + Term.Length)).ToList();
        after.Select(g => g.Char).Should().Equal(kept.Select(g => g.Char),
            "exactly the term's glyphs are gone and nothing else");
        for (var i = 0; i < kept.Count; i++)
        {
            var expected = i < at ? kept[i].X : kept[i].X - removedWidth;
            after[i].X.Should().BeApproximately(expected, 0.3,
                $"glyph {i} '{kept[i].Char}' must {(i < at ? "stay put" : "close up by the removed width")}");
        }

        var text = MutoolTextExtractor.ExtractPage(afterPath, 1) ?? "";
        text.Should().NotContain(Term);

        // And the FILE does not state it either: no positioning or advance
        // operand is the removed width (in points or in TJ units at 20 pt), the
        // follower's original place, or its original offset from the line start.
        var follower = before[at + Term.Length].X;
        var stated = new[]
        {
            (Value: removedWidth, Tolerance: 0.3), (Value: removedWidth / 20 * 1000, Tolerance: 2.0),
            (Value: follower, Tolerance: 0.3), (Value: follower - before[0].X, Tolerance: 0.3),
        };
        PositionAndAdvanceNumbers(File.ReadAllBytes(afterPath))
            .Where(n => stated.Any(f => Math.Abs(Math.Abs(n) - f.Value) < f.Tolerance))
            .Should().BeEmpty("a closed gap must not be restated by the operands that place the text after it");
    }

    private (List<MutoolGlyphPositions.Glyph> Before, List<MutoolGlyphPositions.Glyph> After, string AfterPath)
        Redact(string content, WidthPolicy width)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var original = Fixture(content);
        var beforePath = WriteTemp(original);
        byte[] redacted;
        using (var doc = PdfDocument.Open(original))
        {
            doc.RedactText(Term, RedactionOptions.Default with { Width = width, DrawBox = false })
                .VerifiedRemovals.Should().Be(1);
            redacted = doc.SaveToBytes();
        }
        var afterPath = WriteTemp(redacted);
        return (Glyphs(beforePath), Glyphs(afterPath), afterPath);
    }

    private static List<MutoolGlyphPositions.Glyph> Glyphs(string path)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var glyphs = MutoolGlyphPositions.ExtractPage(path, 1);
        glyphs.Should().NotBeNull();
        return glyphs!.OrderByDescending(g => Math.Round(g.Y)).ThenBy(g => g.X).ToList();
    }

    private static int IndexOfTerm(List<MutoolGlyphPositions.Glyph> glyphs)
    {
        var chars = string.Concat(glyphs.Select(g => g.Char));
        var at = chars.IndexOf(Term, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, "the original shows the term");
        return at;
    }

    /// <summary>Every numeric operand of a Td, TD or Tm, and every number inside a TJ array, on page 1.</summary>
    private static List<double> PositionAndAdvanceNumbers(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var numbers = TjNumbers(pdf);
        foreach (var op in doc.GetPage(1).GetContentStream().Operators)
        {
            if (op.Name is not ("Td" or "TD" or "Tm")) continue;
            foreach (var operand in op.Operands)
                if (operand.TryGetNumber(out var n)) numbers.Add(n);
        }
        return numbers;
    }

    /// <summary>Every number inside a TJ array of page 1's content stream.</summary>
    private static List<double> TjNumbers(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var numbers = new List<double>();
        foreach (var op in doc.GetPage(1).GetContentStream().Operators)
        {
            if (op.Name != "TJ" || op.Operands.Count == 0) continue;
            if (op.Operands[0] is not Excise.Core.Primitives.PdfArray array) continue;
            foreach (var element in array)
            {
                if (element is Excise.Core.Primitives.PdfInteger i) numbers.Add(i.Value);
                else if (element is Excise.Core.Primitives.PdfReal r) numbers.Add(r.Value);
            }
        }
        return numbers;
    }

    private static double XOf(List<MutoolGlyphPositions.Glyph> glyphs, string c, int nth = 0) =>
        glyphs.Where(g => g.Char == c).ElementAt(nth).X;

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-closure-{Guid.NewGuid():N}.pdf");
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

    private static byte[] Fixture(string content, string extraResources = "", params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 200] /Contents 4 0 R " +
            $"/Resources << /Font << /F1 5 0 R >> {extraResources} >> >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        bodies.AddRange(extraObjects);

        using var ms = new MemoryStream();
        void Write(string value) => ms.Write(Encoding.Latin1.GetBytes(value));
        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Count + 1];
        for (var i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        var xref = ms.Position;
        Write($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Count; i++)
            Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Root 1 0 R /Size {bodies.Count + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    private static string Stream(string content, string dict = "")
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< {dict} /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }
}
