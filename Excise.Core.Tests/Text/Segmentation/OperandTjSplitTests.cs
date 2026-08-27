using System;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1091 — the operand-level TJ-split, end to end. Redacting a term rewrites the
/// operator's OPERAND (byte-splices the matched glyphs, one run-level advance
/// adjustment #1045), never the operator's place in the stream. Following text
/// keeps its position; the term is gone from the bytes; per-character widths are
/// collapsed to one number so the width side-channel does not leak.
/// </summary>
public class OperandTjSplitTests
{
    private static byte[] Pdf(string content)
    {
        var body = Encoding.Latin1.GetByteCount(content);
        return Encoding.Latin1.GetBytes(
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 100] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n" +
            $"4 0 obj\n<< /Length {body} >>\nstream\n{content}endstream\nendobj\n" +
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");
    }

    [Fact]
    public void RedactingAMiddleWord_SplitsTheOperand_TermGone_FollowingTextStaysPut()
    {
        using var doc = PdfDocument.Open(Pdf("BT /F1 14 Tf 20 50 Td (Name: Louise Anne Farrar here) Tj ET\n"));
        var hereBefore = doc.GetPage(1).Letters.First(l => l.Value == "h").StartX;

        doc.RedactText("Farrar", drawBlackRect: false).VerifiedRemovals.Should().Be(1);
        var saved = doc.SaveToBytes();

        // Term gone from the saved bytes (any carrier).
        (Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved))
            .Should().NotContain("Farrar");

        using var after = PdfDocument.Open(saved);
        var content = Encoding.Latin1.GetString(after.GetPage(1).GetContentStreamBytes());

        // The operand was split into a TJ, not the operator removed or the block
        // rebuilt: the kept text survives verbatim in one TJ array.
        content.Should().Contain("TJ").And.Contain("Louise Anne").And.Contain("here");

        // #1045: the removed run is one advance adjustment, not per-glyph — and
        // it is NEGATIVE (restores the rightward advance so following text does
        // not shift). "here" stays where it was.
        after.GetPage(1).Text.Should().Contain("Louise Anne").And.Contain("here").And.NotContain("Farrar");
        after.GetPage(1).Letters.First(l => l.Value == "h").StartX
            .Should().BeApproximately(hereBefore, 1.0, "the kept run after the split must not shift");
    }

    [Fact]
    public void RedactingInsideATjArray_SplicesOnlyTheMatchedElement()
    {
        // The word "SECRET" is spread across a TJ with kerning; only its glyphs go.
        using var doc = PdfDocument.Open(Pdf("BT /F1 14 Tf 20 50 Td [(Keep )-20(SECRET)-20( tail)] TJ ET\n"));

        doc.RedactText("SECRET", drawBlackRect: false).VerifiedRemovals.Should().Be(1);
        var saved = doc.SaveToBytes();

        Encoding.ASCII.GetString(saved).Should().NotContain("SECRET");
        using var after = PdfDocument.Open(saved);
        after.GetPage(1).Text.Should().Contain("Keep").And.Contain("tail").And.NotContain("SECRET");
    }
}
