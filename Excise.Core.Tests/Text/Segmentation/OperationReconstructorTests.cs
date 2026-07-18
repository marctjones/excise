using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

public class OperationReconstructorTests
{
    private readonly OperationReconstructor _reconstructor = new();

    private static TextSegment MakeSegment(string text, double x, double y,
        double width = 50, double height = 12, int startIndex = 0)
    {
        return new TextSegment
        {
            StartIndex = startIndex,
            EndIndex = startIndex + text.Length,
            Keep = true,
            StartX = x,
            StartY = y,
            Width = width,
            Height = height,
            OriginalText = text,
        };
    }

    [Fact]
    public void Reconstruct_NoSegments_ReturnsEmpty()
    {
        var ops = _reconstructor.ReconstructWithPositioning(
            new List<TextSegment>(),
            new OperationReconstructor.Context { FontName = "F1", FontSize = 12 });
        ops.Should().BeEmpty();
    }

    [Fact]
    public void Reconstruct_SingleSegment_EmitsBtTfTmTjEt()
    {
        var segments = new List<TextSegment> { MakeSegment("Hello", 100, 700) };
        var ops = _reconstructor.ReconstructWithPositioning(
            segments,
            new OperationReconstructor.Context { FontName = "F1", FontSize = 12 });

        ops.Select(o => o.Name).Should().Equal("BT", "Tf", "Tm", "Tj", "ET");
    }

    [Fact]
    public void Reconstruct_TfOperator_UsesSize1WithFontInMatrix()
    {
        var segments = new List<TextSegment> { MakeSegment("X", 0, 0) };
        var ops = _reconstructor.ReconstructWithPositioning(
            segments,
            new OperationReconstructor.Context { FontName = "F1", FontSize = 18 });

        var tf = ops.First(o => o.Name == "Tf");
        tf.GetName(0).Should().Be("F1");
        tf.GetNumber(1).Should().Be(1.0, "actual size travels in Tm");

        var tm = ops.First(o => o.Name == "Tm");
        tm.GetNumber(0).Should().Be(18, "Tm.a carries the font size");
        tm.GetNumber(3).Should().Be(18, "Tm.d carries the font size");
    }

    [Fact]
    public void Reconstruct_PositionsSegmentsByStartXStartY()
    {
        var segments = new List<TextSegment>
        {
            MakeSegment("FOO", 50, 700),
            MakeSegment("BAR", 200, 700),
        };
        var ops = _reconstructor.ReconstructWithPositioning(
            segments,
            new OperationReconstructor.Context { FontName = "F1", FontSize = 10 });

        var tms = ops.Where(o => o.Name == "Tm").ToList();
        tms.Should().HaveCount(2);
        tms[0].GetNumber(4).Should().Be(50); tms[0].GetNumber(5).Should().Be(700);
        tms[1].GetNumber(4).Should().Be(200); tms[1].GetNumber(5).Should().Be(700);

        var tjs = ops.Where(o => o.Name == "Tj").ToList();
        tjs.Should().HaveCount(2);
        (tjs[0].Operands[0] as PdfString)!.Value.Should().Be("FOO");
        (tjs[1].Operands[0] as PdfString)!.Value.Should().Be("BAR");
    }

    [Fact]
    public void Reconstruct_NonDefaultTextState_EmitsMatchingOperators()
    {
        var segments = new List<TextSegment> { MakeSegment("x", 0, 0) };
        var ops = _reconstructor.ReconstructWithPositioning(
            segments,
            new OperationReconstructor.Context
            {
                FontName = "F1",
                FontSize = 10,
                CharacterSpacing = 0.5,
                WordSpacing = 3.0,
                HorizontalScaling = 90.0,
                TextRise = 2.0,
                TextLeading = 14.0,
            });

        var names = ops.Select(o => o.Name).ToList();
        names.Should().Contain("Tc");
        names.Should().Contain("Tw");
        names.Should().Contain("Tz");
        names.Should().Contain("Ts");
        names.Should().Contain("TL");
    }

    [Fact]
    public void Reconstruct_DefaultTextState_OmitsStateOperators()
    {
        var segments = new List<TextSegment> { MakeSegment("x", 0, 0) };
        var ops = _reconstructor.ReconstructWithPositioning(
            segments,
            new OperationReconstructor.Context { FontName = "F1", FontSize = 10 });

        ops.Select(o => o.Name).Should().NotContain(new[] { "Tc", "Tw", "Tz", "Tr", "Ts", "TL" },
            "zero / default values shouldn't emit state operators");
    }

    [Fact]
    public void Reconstruct_CidFontSegment_EmitsHexStringWithRawBytes()
    {
        // Simulate a CID segment by populating letter-match raw bytes. The
        // reconstructor should use those bytes directly, not attempt to
        // re-encode the decoded Unicode text.
        var segment = new TextSegment
        {
            StartIndex = 0,
            EndIndex = 2,
            Keep = true,
            StartX = 50, StartY = 700,
            Width = 20, Height = 12,
            OriginalText = "文件", // CJK chars
            IsCidFont = true,
            LetterMatches =
            {
                new LetterMatch { CharacterIndex = 0, Letter = null!, RawBytes = new byte[]{ 0x00, 0x04 } },
                new LetterMatch { CharacterIndex = 1, Letter = null!, RawBytes = new byte[]{ 0x00, 0x05 } },
            },
        };

        var ops = _reconstructor.ReconstructWithPositioning(
            new List<TextSegment> { segment },
            new OperationReconstructor.Context { FontName = "F1", FontSize = 10 });

        var tj = ops.First(o => o.Name == "Tj");
        var operand = tj.Operands[0] as PdfString;
        operand.Should().NotBeNull();
        operand!.Bytes.Should().Equal(new byte[] { 0x00, 0x04, 0x00, 0x05 });
    }

    [Fact]
    public void Reconstruct_InvalidFontInputs_FallsBackToDefaults()
    {
        var segments = new List<TextSegment> { MakeSegment("x", 0, 0) };
        var ops = _reconstructor.ReconstructWithPositioning(
            segments,
            new OperationReconstructor.Context { FontName = "", FontSize = -1 });

        var tf = ops.First(o => o.Name == "Tf");
        tf.GetName(0).Should().Be("F1", "empty font name should fall back to F1");

        var tm = ops.First(o => o.Name == "Tm");
        tm.GetNumber(0).Should().Be(12, "invalid font size should fall back to 12pt");
    }
}
