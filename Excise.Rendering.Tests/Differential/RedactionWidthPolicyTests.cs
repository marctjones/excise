using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1189 — the width / box policy, measured rather than asserted.
///
/// <para><b>The attack.</b> A covering box drawn to the exact extent of the
/// removed run is a RULER. Given a candidate list — a set of names, a set of
/// case numbers — an attacker renders each candidate in the document's own font
/// and discards every one whose width differs from the box. This is the
/// glyph-position / width side channel, the top unhardened gap in the
/// de-redaction research, and #1140 already recorded the box half of it.</para>
///
/// <para><b>What this file pins.</b> Two secrets of very different length, in
/// the same fixture between the same neighbouring words:</para>
/// <list type="bullet">
///   <item><see cref="WidthPolicy.CollapsePreserveLayout"/> (the pre-#1755
///   default) — the boxes have DIFFERENT rendered widths. The channel is open.
///   Pinned as a fact, not fixed silently.</item>
///   <item><see cref="WidthPolicy.OvershootPreserveLayout"/> — the boxes have
///   the SAME rendered width. The rendered channel is closed.</item>
///   <item><see cref="WidthPolicy.FixedMarker"/> (#1755, opt-in) — the boxes
///   have the SAME rendered width AND the content stream keeps only the
///   marker's width, never the removed run's. The only policy that answers
///   #1715 and #1725 together.</item>
/// </list>
///
/// <para>⚠️ <b>And the part a self-congratulating gate would omit.</b> Overshoot
/// preserves layout, so the content stream still carries one TJ adjustment equal
/// to the removed run's advance — a direct measurement of the removed string's
/// width to anyone who reads the FILE instead of looking at the page. The last
/// test asserts that this is still true, so nobody reads a green run here as
/// "the width side channel is closed". Only <see cref="WidthPolicy.CloseGap"/>
/// destroys that, and it reflows the line.</para>
///
/// <para>Rendered width is measured with mutool — an independent renderer. A
/// differential between excise's box geometry and excise's own rasteriser could
/// not see a defect they shared.</para>
/// </summary>
public class RedactionWidthPolicyTests : IDisposable
{
    // Two names of SIMILAR but unequal rendered width — the case a
    // candidate-list attacker is actually in. Helvetica advances:
    // ALFRED = 3.945 em, ALBERT = 3.890 em; both round up to the same 4 em
    // bucket, and the ~0.11 em of growth that needs fits in an ordinary word
    // gap.
    private const string SecretA = "ALFRED";
    private const string SecretB = "ALBERT";
    private const int Dpi = 150;

    private readonly List<string> _temp = new();

    // "Name: <secret> Ref." on one line, with the SAME words either side of the
    // secret in both fixtures. That is what makes the widths comparable: the
    // space available to a widened box is identical, so only the secret differs.
    private static byte[] Fixture(string secret) =>
        FixtureWith($"BT /F1 36 Tf 20 40 Td (Name: {secret} Ref.) Tj ET");

    private static byte[] FixtureWith(string content, int pageWidth = 500) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} 120] /Contents 4 0 R " +
        "/Resources << /Font << /F1 5 0 R >> >> >>",
        Stream(content),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    private byte[] Redact(string secret, WidthPolicy width)
    {
        using var doc = PdfDocument.Open(Fixture(secret));
        doc.RedactText(secret, RedactionOptions.Default with { Width = width });
        return doc.SaveToBytes();
    }

    [Fact]
    public void Collapse_BoxWidthMeasuresTheRemovedString_TheChannelBeingClosed()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var widthA = RenderedBoxWidth(Redact(SecretA, WidthPolicy.CollapsePreserveLayout));
        var widthB = RenderedBoxWidth(Redact(SecretB, WidthPolicy.CollapsePreserveLayout));

        widthA.Should().BeGreaterThan(0, "the covering box is drawn");
        Math.Abs(widthA - widthB).Should().BeGreaterThanOrEqualTo(3,
            "under the default policy the box is a RULER for the removed string. " +
            "Measured here: ALFRED and ALBERT are the same letter count and differ " +
            "by 0.055 em of advance, and that difference survives all the way to " +
            "the rendered pixels an attacker can measure — enough to discard one " +
            "of two otherwise identical candidates. This is the #1189 side " +
            "channel, pinned as a FACT: the default is not silently changed.");
    }

    [Fact]
    public void Overshoot_MakesTheBoxTheSameWidthForBothSecrets()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var widthA = RenderedBoxWidth(Redact(SecretA, WidthPolicy.OvershootPreserveLayout));
        var widthB = RenderedBoxWidth(Redact(SecretB, WidthPolicy.OvershootPreserveLayout));

        widthA.Should().BeGreaterThan(0);
        Math.Abs(widthB - widthA).Should().BeLessThanOrEqualTo(2,
            "the box grows to the space between the surviving neighbours, which is " +
            "the same in both documents, so measuring it no longer narrows the " +
            "candidate set (allowing 2px for rasterisation)");
    }

    [Fact]
    public void Overshoot_DoesNotEatTheNeighbouringWords()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // The bound that makes overshoot usable: it may take the empty space, not
        // the document. An independent extractor confirms the neighbours survive
        // and the secret does not.
        var path = WriteTemp(Redact(SecretB, WidthPolicy.OvershootPreserveLayout));
        var text = MutoolTextExtractor.ExtractPage(path, 1) ?? "";

        text.Should().Contain("Name", "the word before the redaction survives");
        text.Should().Contain("Ref", "the word after the redaction survives");
        text.Should().NotContain(SecretB, "the secret is removed");
    }

    [Fact]
    public void Overshoot_LeavesTheContentStreamAdvanceIntact_SoTheFileStillMeasuresIt()
    {
        // ⚠️ THE HONEST HALF. Overshoot closes the RENDERED channel and nothing
        // more. Preserving layout means one TJ adjustment equal to the removed
        // run's total advance survives in the content stream, and that number
        // measures the removed string as precisely as the old box did — for a
        // reader who opens the file rather than looks at the page.
        //
        // If this test ever starts failing because the numbers became equal,
        // that is a real improvement and the docs must be updated with it. It
        // exists so a green suite cannot be read as "the width channel is
        // closed".
        var adjA = FirstNegativeTjAdjustment(Redact(SecretA, WidthPolicy.OvershootPreserveLayout));
        var adjB = FirstNegativeTjAdjustment(Redact(SecretB, WidthPolicy.OvershootPreserveLayout));

        adjA.Should().NotBeNull();
        adjB.Should().NotBeNull();
        adjB.Should().NotBe(adjA,
            "the surviving TJ advance still distinguishes the two secrets — " +
            "Overshoot does NOT close the content-stream width channel; CloseGap does");

        // And the contrast that makes CloseGap's cost worth paying.
        FirstNegativeTjAdjustment(Redact(SecretA, WidthPolicy.CloseGap)).Should().BeNull(
            "CloseGap emits no compensating advance at all");
        FirstNegativeTjAdjustment(Redact(SecretB, WidthPolicy.CloseGap)).Should().BeNull();
    }

    /// <summary>
    /// #1755 — FixedMarker is the new DEFAULT. It has to answer both #1715
    /// (the width channel Collapse leaves open, pinned above) and #1725
    /// (CloseGap draws no box at all, so a width-closed redaction has no
    /// visible mark) AT THE SAME TIME — this is the measurement that proves it
    /// does, rather than trading one for the other.
    /// </summary>
    [Fact]
    public void FixedMarker_MakesTheBoxTheSameWidthForBothSecrets_AndVisible()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var widthA = RenderedBoxWidth(Redact(SecretA, WidthPolicy.FixedMarker));
        var widthB = RenderedBoxWidth(Redact(SecretB, WidthPolicy.FixedMarker));

        // #1725: a mark is actually drawn — unlike CloseGap, which draws none
        // once the gap is closed (Overshoot_DoesNotEatTheNeighbouringWords'
        // sibling policy has a box; CloseGap has none).
        widthA.Should().BeGreaterThan(0, "FixedMarker must leave a visible mark (#1725)");
        widthB.Should().BeGreaterThan(0);

        // #1715: unlike Overshoot (which rounds UP from the removed width and
        // so still correlates with it, just more coarsely), FixedMarker's box
        // is the SAME size regardless of what was removed — not merely
        // "close enough", exactly equal, because the width computation never
        // reads the removed run's own width at all.
        Math.Abs(widthB - widthA).Should().BeLessThanOrEqualTo(2,
            "the marker's width is a function of font size ONLY — it carries no " +
            "information about the removed string's length, which is the whole " +
            "point (allowing 2px for rasterisation)");
    }

    [Fact]
    public void FixedMarker_KeepsOnlyTheMarkersWidthInTheContentStream()
    {
        // #1715/#1755: FixedMarker has to close the FILE-level channel too, not
        // just the rendered one — the same honesty check
        // Overshoot_LeavesTheContentStreamAdvanceIntact exists for. The one TJ
        // number it leaves is the room kept for the marker (#1725): the SAME
        // for both secrets, and neither secret's advance.
        var a = FirstNegativeTjAdjustment(Redact(SecretA, WidthPolicy.FixedMarker));
        var b = FirstNegativeTjAdjustment(Redact(SecretB, WidthPolicy.FixedMarker));

        a.Should().NotBeNull("FixedMarker keeps room for its marker");
        b.Should().Be(a, "the room kept is a function of font size only");
        var overshootA = FirstNegativeTjAdjustment(Redact(SecretA, WidthPolicy.OvershootPreserveLayout));
        var overshootB = FirstNegativeTjAdjustment(Redact(SecretB, WidthPolicy.OvershootPreserveLayout));
        a.Should().NotBe(overshootA, "that would be ALFRED's own advance");
        a.Should().NotBe(overshootB, "that would be ALBERT's own advance");
    }

    [Fact]
    public void FixedMarker_RemovesTheSecretAndKeepsTheNeighbours()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Same shape as Overshoot_DoesNotEatTheNeighbouringWords: an
        // INDEPENDENT extractor confirms the neighbours survive (as TEXT --
        // whether they are also visually COVERED by the marker box is a
        // separate question, see the next test) and the secret does not.
        var path = WriteTemp(Redact(SecretB, WidthPolicy.FixedMarker));
        var text = MutoolTextExtractor.ExtractPage(path, 1) ?? "";

        text.Should().Contain("Name", "the word before the redaction survives");
        text.Should().Contain("Ref", "the word after the redaction survives");
        text.Should().NotContain(SecretB, "the secret is removed");
    }

    /// <summary>
    /// #1725 — the marker covers nothing that follows it. FixedMarker used to
    /// reuse CloseGap's shift, moving the following text to the removed run's
    /// OWN left edge — exactly where the fixed-width marker is drawn from — so
    /// at 36 pt the box covered 3 of the 4 characters of "Ref.". The line now
    /// keeps the marker's width, and the neighbour starts where it ends.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(0, 0, 50)]
    [InlineData(3, 9, 100)]
    [InlineData(2, 6, 150)]
    public void FixedMarker_TheReflowedNeighbourClearsTheMarker(double tc, double tw, double tz)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Rule 7: the room is a TJ number, which Tz scales and Tc/Tw do not —
        // pinned with each set, and with none. Between the marker and "R" there
        // is exactly the kept space: (0.278 em × 36 + Tc + Tw) × Th.
        var space = (0.278 * 36 + tc + tw) * tz / 100;
        foreach (var secret in new[] { SecretA, SecretB })
        {
            var (boxRight, reflowedR, gapAfterMarker) = MarkerAndNeighbour(
                $"BT /F1 36 Tf {tc} Tc {tw} Tw {tz} Tz 20 40 Td (Name: {secret} Ref.) Tj ET", secret);

            reflowedR.Should().BeGreaterThanOrEqualTo(boxRight - 0.05,
                $"the reflowed neighbour must start where the marker ends ({secret}, Tc {tc} Tw {tw} Tz {tz})");
            gapAfterMarker.Should().BeApproximately(space, 0.1,
                "the room kept is the marker's, not the removed run's: only the kept space follows it");
        }
    }

    [Fact]
    public void FixedMarker_TheGapAfterTheMarkerIsTheSameForBothSecrets()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var a = MarkerAndNeighbour($"BT /F1 36 Tf 20 40 Td (Name: {SecretA} Ref.) Tj ET", SecretA);
        var b = MarkerAndNeighbour($"BT /F1 36 Tf 20 40 Td (Name: {SecretB} Ref.) Tj ET", SecretB);

        b.GapAfterMarker.Should().BeApproximately(a.GapAfterMarker, 0.05,
            "where the text after the marker sits must not depend on what was removed");
    }

    [Fact]
    public void FixedMarker_TermSplitAcrossTwoRuns_KeepsRoomForOneMarker()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // One term, two operators: still one area and one marker, so one room.
        // Keeping it per run would double the gap and state the run structure.
        var split = MarkerAndNeighbour("BT /F1 36 Tf 20 40 Td (Name: ALB) Tj (ERT Ref.) Tj ET", SecretB);
        var whole = MarkerAndNeighbour($"BT /F1 36 Tf 20 40 Td (Name: {SecretB} Ref.) Tj ET", SecretB);

        split.ReflowedR.Should().BeGreaterThanOrEqualTo(split.BoxRight - 0.05);
        split.GapAfterMarker.Should().BeApproximately(whole.GapAfterMarker, 0.05);
    }

    [Fact]
    public void FixedMarker_AreaRedaction_TheNeighbourStartsAtTheAreaEdge()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // An area redaction's marker is the area itself (#1834).
        byte[] pdf;
        double areaRight;
        using (var doc = PdfDocument.Open(Fixture(SecretB)))
        {
            var page = doc.GetPage(1);
            var term = page.Letters.OrderBy(l => l.StartX).Skip("Name: ".Length).Take(SecretB.Length).ToList();
            string.Concat(term.Select(l => l.Value)).Should().Be(SecretB);
            var area = new PdfRectangle(term.Min(l => l.GlyphRectangle.Left) - 1, 35,
                                        term.Max(l => l.GlyphRectangle.Right) + 4, 70);
            areaRight = area.Right;
            page.RedactArea(area, RedactionOptions.Default with { Width = WidthPolicy.FixedMarker });
            pdf = doc.SaveToBytes();
        }

        var path = WriteTemp(pdf);
        var glyphs = MutoolGlyphPositions.ExtractPage(path, 1)!;
        glyphs.Single(g => g.Char == "R").X.Should().BeGreaterThanOrEqualTo(areaRight - 0.05,
            "the area's box covers nothing that follows it");
        (MutoolTextExtractor.ExtractPage(path, 1) ?? "").Should().NotContain(SecretB).And.Contain("Ref");
    }

    /// <summary>
    /// Redact <paramref name="secret"/> from <paramref name="content"/> under
    /// FixedMarker. The marker's right edge is read from the drawn rectangle;
    /// where the reflowed "R" of "Ref." landed, and the kept space before it,
    /// from mutool — an independent glyph-position reader.
    /// </summary>
    private (double BoxRight, double ReflowedR, double GapAfterMarker) MarkerAndNeighbour(
        string content, string secret)
    {
        byte[] pdf;
        using (var doc = PdfDocument.Open(FixtureWith(content, pageWidth: 800)))
        {
            doc.RedactText(secret, RedactionOptions.Default with { Width = WidthPolicy.FixedMarker });
            pdf = doc.SaveToBytes();
        }

        double boxRight;
        using (var doc = PdfDocument.Open(pdf))
        {
            var box = FindLastFilledRectangle(doc.GetPage(1).GetContentStream().Operators);
            box.Should().NotBeNull("FixedMarker must have drawn a covering rectangle");
            boxRight = box!.Value.Right;
        }

        var path = WriteTemp(pdf);
        (MutoolTextExtractor.ExtractPage(path, 1) ?? "").Should().NotContain(secret);
        var glyphs = MutoolGlyphPositions.ExtractPage(path, 1);
        glyphs.Should().NotBeNull();
        var reflowedR = glyphs!.Last(g => g.Char == "R").X;
        return (boxRight, reflowedR, reflowedR - boxRight);
    }

    /// <summary>The last <c>x y w h re</c> ... <c>f</c> filled rectangle in the
    /// content stream — the shape <c>AppendBlackRectangle</c> always emits.</summary>
    private static (double Left, double Right)? FindLastFilledRectangle(
        IReadOnlyList<Excise.Core.Content.ContentOperator> ops)
    {
        (double Left, double Right)? found = null;
        foreach (var op in ops)
        {
            if (op.Name != "re" || op.Operands.Count < 4) continue;
            double At(int i) => op.Operands[i] switch
            {
                Excise.Core.Primitives.PdfInteger n => n.Value,
                Excise.Core.Primitives.PdfReal r => r.Value,
                _ => 0.0,
            };
            var x = At(0);
            var w = At(2);
            found = (x, x + w);
        }
        return found;
    }

    [Fact]
    public void Overshoot_CannotReachTheBucketWhenTheLineHasNoSlack()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // ⚠️ THE LIMIT, pinned rather than papered over. The box may only take
        // empty space; where the secret is wedged between neighbours with no gap
        // to grow into, overshoot cannot round the width up and the residue is
        // unchanged. A user choosing this policy is choosing a mitigation whose
        // strength depends on the document.
        byte[] Wedged(string secret) => Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 120] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            // No spaces: the neighbours abut the secret on both sides.
            Stream($"BT /F1 36 Tf 20 40 Td (Name:{secret}Ref.) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        byte[] RedactWedged(string secret, WidthPolicy width)
        {
            using var doc = PdfDocument.Open(Wedged(secret));
            doc.RedactText(secret, RedactionOptions.Default with { Width = width });
            return doc.SaveToBytes();
        }

        var a = RenderedBoxWidth(RedactWedged(SecretA, WidthPolicy.OvershootPreserveLayout));
        var b = RenderedBoxWidth(RedactWedged(SecretB, WidthPolicy.OvershootPreserveLayout));

        Math.Abs(a - b).Should().BeGreaterThanOrEqualTo(3,
            "with no slack on the line the box cannot be widened, so the width " +
            "residue survives. Overshoot is a mitigation bounded by the document, " +
            "not a guarantee — CloseGap is the policy that does not depend on " +
            "available space");
    }

    /// <summary>
    /// Width in pixels of the widest fully-black horizontal run, as an
    /// INDEPENDENT renderer draws it. The covering box is the only solid black
    /// region on these fixtures.
    /// </summary>
    private int RenderedBoxWidth(byte[] pdf)
    {
        var path = WriteTemp(pdf);
        using var bitmap = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        bitmap.Should().NotBeNull("mutool must render the fixture");

        var best = 0;
        for (var y = 0; y < bitmap!.Height; y++)
        {
            var run = 0;
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                var dark = c.Red < 40 && c.Green < 40 && c.Blue < 40 && c.Alpha > 200;
                if (dark) { run++; if (run > best) best = run; }
                else run = 0;
            }
        }
        return best;
    }

    /// <summary>
    /// The first negative number inside a TJ array in the page content — the
    /// compensating advance #1045 emits for a removed run. Null when none.
    /// </summary>
    private static int? FirstNegativeTjAdjustment(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        foreach (var op in doc.GetPage(1).GetContentStream().Operators)
        {
            if (op.Name != "TJ" || op.Operands.Count == 0) continue;
            if (op.Operands[0] is not Excise.Core.Primitives.PdfArray array) continue;
            foreach (var element in array)
            {
                var value = element switch
                {
                    Excise.Core.Primitives.PdfInteger i => (double)i.Value,
                    Excise.Core.Primitives.PdfReal r => r.Value,
                    _ => 0.0,
                };
                if (value < 0) return (int)Math.Round(value);
            }
        }
        return null;
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-width-{Guid.NewGuid():N}.pdf");
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

    private static string Stream(string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }

    private static byte[] Build(params string[] bodies)
    {
        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

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
