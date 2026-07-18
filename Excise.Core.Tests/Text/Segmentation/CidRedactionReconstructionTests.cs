using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Verifies the CID/Type0 (CJK) redaction path (issue #353): the kept text of a
/// 2-byte font must be reconstructed with its ORIGINAL source codes, not Unicode
/// — a CID font cannot render Unicode bytes, so the previous Unicode path
/// corrupted (and could mis-render) the surviving text. Exercises the full
/// LetterFinder → TextSegmenter → OperationReconstructor pipeline.
/// </summary>
public class CidRedactionReconstructionTests
{
    // Two CJK glyphs, each a 2-byte Identity-H code.
    private const int Code_Zhong = 0x4E2D; // 中
    private const int Code_Wen   = 0x6587; // 文

    private static Letter CidLetter(string value, int code, double left)
    {
        var rect = new PdfRectangle(left, 100, left + 10, 112);
        return new Letter(value, rect, fontSize: 12, fontName: "F1",
            startX: left, startY: 100, width: 10, characterCode: code, codeByteLength: 2)
        {
            // Real Type0/CID fonts set this from Subtype, independent of byte
            // length (#659 — a Type0 font can legally use a 1-byte codespace).
            IsCidFont = true,
        };
    }

    [Fact]
    public void LetterFinder_PopulatesRawBytes_FromTwoByteCode()
    {
        var letters = new List<Letter> { CidLetter("中", Code_Zhong, 100), CidLetter("文", Code_Wen, 110) };

        var matches = new LetterFinder().FindOperationLetters("中文", letters);

        matches.Should().HaveCount(2);
        matches[0].RawBytes.Should().Equal(new byte[] { 0x4E, 0x2D });
        matches[1].RawBytes.Should().Equal(new byte[] { 0x65, 0x87 });
    }

    [Fact]
    public void Redaction_KeepsCidGlyph_ReencodedWithOriginalCodeNotUnicode()
    {
        var letters = new List<Letter> { CidLetter("中", Code_Zhong, 100), CidLetter("文", Code_Wen, 110) };
        var matches = new LetterFinder().FindOperationLetters("中文", letters);

        // Redact only the second glyph (文): x-range 110.5..120.5 misses 中 (100..110).
        var opBounds = new PdfRectangle(100, 100, 120, 112);
        var redactionArea = new PdfRectangle(110.5, 99, 120.5, 113);

        var kept = new TextSegmenter().BuildSegments("中文", opBounds, matches, redactionArea);

        kept.Should().ContainSingle("only 中 survives");
        kept[0].IsCidFont.Should().BeTrue("a 2-byte source code marks the segment as CID");

        var ops = new OperationReconstructor().ReconstructWithPositioning(
            kept, new OperationReconstructor.Context { FontName = "F1", FontSize = 12 });

        var tj = ops.Should().ContainSingle(o => o.Name == "Tj").Subject;
        var operand = tj.Operands[0] as PdfString;
        operand.Should().NotBeNull();
        // The surviving glyph must be its original 2-byte CID (中 = 0x4E2D),
        // NOT the 3-byte UTF-8 encoding of '中' (0xE4 0xB8 0xAD) that the old
        // Unicode path would have emitted.
        operand!.Bytes.Should().Equal(new byte[] { 0x4E, 0x2D });
    }

    [Fact]
    public void SimpleFont_StillUsesUnicodePath_NotRegressed()
    {
        // A 1-byte (simple) font must keep using the plain-text Tj path.
        var letters = new List<Letter>
        {
            new("A", new PdfRectangle(100, 100, 110, 112), 12, "F1", 100, 100, 10, 'A'),
            new("B", new PdfRectangle(110, 100, 120, 112), 12, "F1", 110, 100, 10, 'B'),
        };
        var matches = new LetterFinder().FindOperationLetters("AB", letters);

        var kept = new TextSegmenter().BuildSegments("AB",
            new PdfRectangle(100, 100, 120, 112), matches,
            new PdfRectangle(110.5, 99, 120.5, 113)); // redact B only

        kept.Should().ContainSingle();
        kept[0].IsCidFont.Should().BeFalse("a 1-byte font is not CID");

        var ops = new OperationReconstructor().ReconstructWithPositioning(
            kept, new OperationReconstructor.Context { FontName = "F1", FontSize = 12 });
        var tj = ops.Should().ContainSingle(o => o.Name == "Tj").Subject;
        ((PdfString)tj.Operands[0]).Value.Should().Be("A");
    }
}
