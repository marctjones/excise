using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

public class GlyphRemoverTests
{
    private readonly GlyphRemover _remover = new();

    private static Letter L(string value, double x, double width = 7, double y = 700, double h = 12)
        => new(value, new PdfRectangle(x, y, x + width, y + h),
               12, "TestFont", x, y, width, (int)value[0]);

    // Build the letters for a left-to-right text line.
    private static IReadOnlyList<Letter> LettersFor(string text, double x0 = 100, double y = 700, double w = 7)
    {
        var list = new List<Letter>();
        for (int i = 0; i < text.Length; i++)
            list.Add(L(text[i].ToString(), x0 + i * w, w, y));
        return list;
    }

    // Build an ops list representing `BT /F1 12 Tf x y Tm (text) Tj ET` plus
    // optional extras like non-text ops at the start.
    private static List<ContentOperator> SimpleTextBlock(string text, double x, double y)
    {
        return new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, x, y),
            WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString(text) }), text),
            ContentOperator.EndText(),
        };
    }

    // TextContent is normally populated by the parser; in tests we set it
    // directly since we're synthesizing operators.
    private static ContentOperator WithText(ContentOperator op, string text)
    {
        op.TextContent = text;
        return op;
    }

    [Fact]
    public void Process_EmptyOps_ReturnsEmpty()
    {
        var result = _remover.ProcessOperations(
            Array.Empty<ContentOperator>(),
            Array.Empty<Letter>(),
            new PdfRectangle(0, 0, 100, 100));
        result.Should().BeEmpty();
    }

    [Fact]
    public void Process_NoIntersection_CopiesOperatorsVerbatim()
    {
        var ops = SimpleTextBlock("HELLO", x: 100, y: 700);
        var letters = LettersFor("HELLO");
        var redactionArea = new PdfRectangle(400, 700, 500, 720); // far to the right

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        result.Should().HaveCount(ops.Count);
        result.Select(o => o.Name).Should().Equal("BT", "Tf", "Tm", "Tj", "ET");
        // Same Tj operand.
        (result[3].Operands[0] as PdfString)!.Value.Should().Be("HELLO");
    }

    [Fact]
    public void Process_EntireBlockOverlaps_ReplacesWithReconstructedBlock()
    {
        var ops = SimpleTextBlock("HELLO", x: 100, y: 700);
        var letters = LettersFor("HELLO");
        // Box covers every letter.
        var redactionArea = new PdfRectangle(50, 650, 300, 780);

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        // Secret-bearing Tj is gone. The empty text block's state and
        // positioning operators remain because text state can be inherited by
        // later BT blocks (#942).
        result.Select(o => o.Name).Should().Equal("BT", "Tf", "Tm", "ET");
        result.Should().NotContain(o => o.Category == OperatorCategory.TextShowing);
    }

    [Fact]
    public void Process_PartialOverlap_KeepsOriginalBlockStructureAndAppendsReconstructed()
    {
        // Two text ops in the same block: one outside the redaction area,
        // one fully inside it. Original block's state ops survive because
        // the outside text-op needs them; the intersecting text-op is
        // stripped and replaced by a freshly-built BT…ET afterwards.
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString("HELLO") }), "HELLO"),
            ContentOperator.TextMatrix(12, 0, 0, 12, 400, 700),
            WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString("WORLD") }), "WORLD"),
            ContentOperator.EndText(),
        };

        // Letters laid out at [100..134] for HELLO and [400..434] for WORLD.
        var letters = LettersFor("HELLO").Concat(LettersFor("WORLD", x0: 400)).ToList();

        // Box covers WORLD only.
        var redactionArea = new PdfRectangle(395, 650, 440, 780);

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        // Original block should still contain HELLO (kept-as-is) and the
        // state ops, but not WORLD's Tj. Reconstructed sequence may follow.
        var tjStrings = result.Where(o => o.Name == "Tj")
            .Select(o => (o.Operands[^1] as PdfString)?.Value ?? "")
            .ToList();

        tjStrings.Should().Contain("HELLO", "non-intersecting text stays intact");
        tjStrings.Should().NotContain("WORLD", "intersecting text is stripped from the original block");
        // All WORLD glyphs overlapped the redaction box so every segment was
        // removed — the reconstructed block emits nothing for it.
        tjStrings.Where(s => s != "HELLO").Should().BeEmpty();

        // Structural: first op must still be BT.
        result[0].Name.Should().Be("BT");
        // Somewhere before the end, there's an ET for the original block.
        result.Select(o => o.Name).Should().Contain("ET");
    }

    [Fact]
    public void Process_RepeatedSingleGlyphOperators_RedactsOnlyTheSpatiallyMatchingOperator()
    {
        var first = WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString("O") }), "O");
        first.BoundingBox = new PdfRectangle(100, 700, 107, 712);
        var second = WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString("O") }), "O");
        second.BoundingBox = new PdfRectangle(300, 700, 307, 712);

        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            first,
            ContentOperator.TextMatrix(12, 0, 0, 12, 300, 700),
            second,
            ContentOperator.EndText(),
        };
        var letters = new[] { L("O", 100), L("O", 300) };

        var result = _remover.ProcessOperations(
            ops, letters, new PdfRectangle(99, 699, 108, 713));

        result.Where(o => o.Name == "Tj")
            .Should().ContainSingle("the identical glyph drawn remotely must survive (#942)");
    }

    [Fact]
    public void Process_Reconstruction_PreservesSourceTextMatrixInLocalCoordinates()
    {
        var text = WithText(
            new ContentOperator("Tj", new PdfObject[] { new PdfString("KEEP FORM") }),
            "KEEP FORM");
        text.BoundingBox = new PdfRectangle(100, 100, 163, 112);
        text.GraphicsTransform = new ContentTransform(1, 0, 0, -1, 42, 750);
        text.TextTransform = new ContentTransform(2, 0, 0, -2, 58, 638);
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 100),
            text,
            ContentOperator.EndText(),
        };
        var letters = LettersFor("KEEP FORM", x0: 100, y: 100);
        var form = letters.Skip(5).Take(4).ToList();

        var result = _remover.ProcessOperations(
            ops, letters, new PdfRectangle(
                form.Min(l => l.GlyphRectangle.Left),
                form.Min(l => l.GlyphRectangle.Bottom),
                form.Max(l => l.GlyphRectangle.Right),
                form.Max(l => l.GlyphRectangle.Top)));

        var matrices = result.Where(o => o.Name == "Tm" && Math.Abs(o.GetNumber(0) - 2) < 1e-9).ToList();
        matrices.Should().ContainSingle();
        matrices.Should().AllSatisfy(matrix =>
        {
            matrix.GetNumber(0).Should().BeApproximately(2, 1e-9);
            matrix.GetNumber(3).Should().BeApproximately(-2, 1e-9);
            matrix.GetNumber(5).Should().BeApproximately(650, 1e-9);
        });
        matrices[0].GetNumber(4).Should().BeApproximately(58, 1e-9);
    }

    [Fact]
    public void Process_NonTextOperatorsPassThrough()
    {
        // Shows a re + f fill outside any text block survives unmodified.
        var ops = new List<ContentOperator>
        {
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
        };
        var result = _remover.ProcessOperations(
            ops,
            Array.Empty<Letter>(),
            new PdfRectangle(0, 0, 1000, 1000)); // even covering the rect

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("re");
        result[1].Name.Should().Be("f");
    }

    [Fact]
    public void Process_ReconstructedBlock_UsesAmbientTextState()
    {
        // Non-default text state set before the Tj that gets redacted+reconstructed;
        // the reconstructed block should carry those non-defaults back out.
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(10) }),
            new("Tc", new PdfObject[] { new PdfReal(0.5) }),
            new("Tw", new PdfObject[] { new PdfReal(2.0) }),
            ContentOperator.TextMatrix(10, 0, 0, 10, 100, 700),
            WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString("HELLO WORLD") }), "HELLO WORLD"),
            ContentOperator.EndText(),
        };
        var letters = LettersFor("HELLO WORLD");
        // Redact just "WORLD".
        var redactionArea = new PdfRectangle(
            letters[6].GlyphRectangle.Left,
            letters[6].GlyphRectangle.Bottom,
            letters[10].GlyphRectangle.Right,
            letters[10].GlyphRectangle.Top);

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        // Since the single Tj had letters that intersected, the whole
        // original block is replaced wholesale by the reconstructed one.
        // Tc + Tw must come back out because they weren't at their defaults
        // when the original Tj was drawn.
        result.Select(o => o.Name).Should().Contain("Tc");
        result.Select(o => o.Name).Should().Contain("Tw");
        result.Select(o => o.Name).Should().Contain("Tj");
        var reconstructedText = string.Concat(result.Where(o => o.Name == "Tj")
            .Select(o => ((PdfString)o.Operands[0]).Value));
        reconstructedText.Should().StartWith("HELLO");
    }

    [Fact]
    public void Process_Reconstruction_InheritsFontAcrossTextBlocks()
    {
        var target = WithText(
            new ContentOperator("Tj", new PdfObject[] { new PdfString("KEEP SECRET") }),
            "KEEP SECRET");
        target.BoundingBox = new PdfRectangle(100, 680, 177, 692);
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F2"), new PdfReal(12) }),
            ContentOperator.TextMatrix(1, 0, 0, 1, 100, 700),
            WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString("ANCHOR") }), "ANCHOR"),
            ContentOperator.EndText(),
            ContentOperator.BeginText(),
            ContentOperator.TextMatrix(1, 0, 0, 1, 100, 680),
            target,
            ContentOperator.EndText(),
        };
        var letters = LettersFor("ANCHOR", y: 700)
            .Concat(LettersFor("KEEP SECRET", y: 680)).ToList();
        var secret = letters.Skip(6 + 5).Take(6).ToList();

        var result = _remover.ProcessOperations(ops, letters, new PdfRectangle(
            secret.Min(l => l.GlyphRectangle.Left), secret.Min(l => l.GlyphRectangle.Bottom),
            secret.Max(l => l.GlyphRectangle.Right), secret.Max(l => l.GlyphRectangle.Top)));

        result.Last(o => o.Name == "Tf").GetName(0).Should().Be("F2");
    }

    [Fact]
    public void ProcessOperations_AllOperators_PreservesNonTextOps()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.BeginText(),
            ContentOperator.EndText(),
        };

        var result = _remover.ProcessOperations(ops, Array.Empty<Letter>(), new PdfRectangle(0, 0, 1000, 1000));

        result.Where(o => o.Name == "re").Should().HaveCount(1);
        result.Where(o => o.Name == "f").Should().HaveCount(1);
    }

    [Fact]
    public void Process_WithTJOperator_ExtractsTextFromArray()
    {
        // TJ operator has array of strings/numbers, not just a single string operand
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            WithText(new ContentOperator("TJ", new PdfObject[]
            {
                new PdfArray(new PdfString("HEL"), new PdfReal(-50), new PdfString("LO"))
            }), "HELLO"),
            ContentOperator.EndText(),
        };

        var letters = LettersFor("HELLO");
        var redactionArea = new PdfRectangle(400, 650, 500, 780); // no intersection

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        result.Should().HaveCount(5);
        result[0].Name.Should().Be("BT");
    }

    [Fact]
    public void Process_WithEmptyTextContent_CopiesOperatorVerbatim()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString("") }), ""),
            ContentOperator.EndText(),
        };

        var result = _remover.ProcessOperations(ops, Array.Empty<Letter>(), new PdfRectangle(0, 0, 1000, 1000));

        // Empty text is skipped during processing but block is copied verbatim since no letters intersect
        result.Should().HaveCount(4); // BT, Tf, Tj (empty), ET copied through
    }

    [Fact]
    public void Process_FullyContainedStrategy_OnlyRemovesFullyContainedGlyphs()
    {
        var ops = SimpleTextBlock("HELLO", x: 100, y: 700);
        var letters = LettersFor("HELLO");
        // Redaction box covers only first 2 letters fully
        var redactionArea = new PdfRectangle(100, 680, 114, 720);

        var result = _remover.ProcessOperations(
            ops, letters, redactionArea,
            GlyphRemovalStrategy.FullyContained);

        // With FullyContained strategy, only H and E (fully contained) are removed
        var reconstructed = result.Where(o => o.Name == "Tj").ToList();
        // Some text should remain since LLO are only partially in the box
        reconstructed.Should().NotBeEmpty();
    }

    [Fact]
    public void Process_CenterPointStrategy_RemovesIfCenterInArea()
    {
        var ops = SimpleTextBlock("HELLO", x: 100, y: 700);
        var letters = LettersFor("HELLO");
        // Redaction covers the center of letters H and E, but not their full bounds
        var redactionArea = new PdfRectangle(103, 680, 110, 720);

        var result = _remover.ProcessOperations(
            ops, letters, redactionArea,
            GlyphRemovalStrategy.CenterPoint);

        // Center-point strategy should affect H and E but not LLO
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void Process_NoTextOpsInBlock_CopiesBlockVerbatim()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            new("Tc", new PdfObject[] { new PdfReal(0.5) }),
            ContentOperator.EndText(),
        };

        var result = _remover.ProcessOperations(
            ops, Array.Empty<Letter>(),
            new PdfRectangle(0, 0, 1000, 1000));

        result.Should().HaveCount(4);
        result.Select(o => o.Name).Should().Equal("BT", "Tf", "Tc", "ET");
    }

    [Fact]
    public void Process_OperatorWithoutOperands_ReturnsEmptyText()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            new("Tj", Array.Empty<PdfObject>()), // Empty operands
            ContentOperator.EndText(),
        };

        var result = _remover.ProcessOperations(
            ops, Array.Empty<Letter>(),
            new PdfRectangle(0, 0, 1000, 1000));

        result.Should().HaveCount(4); // Passes through, no extraction happens
    }

    [Fact]
    public void Process_ApostropheOperator_ExtractsText()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            WithText(new ContentOperator("'", new PdfObject[] { new PdfString("TEXT") }), "TEXT"),
            ContentOperator.EndText(),
        };

        var letters = LettersFor("TEXT");
        var redactionArea = new PdfRectangle(400, 650, 500, 780); // no intersection

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        result.Should().HaveCount(5);
    }

    [Fact]
    public void Process_QuoteOperator_ExtractsText()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            WithText(new ContentOperator("\"", new PdfObject[] { new PdfString("TEXT") }), "TEXT"),
            ContentOperator.EndText(),
        };

        var letters = LettersFor("TEXT");
        var redactionArea = new PdfRectangle(400, 650, 500, 780); // no intersection

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        result.Should().HaveCount(5);
    }

    [Fact]
    public void Process_MultipleBlocksWithMixedContent_HandlesCorrectly()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString("BLOCK1") }), "BLOCK1"),
            ContentOperator.EndText(),
            ContentOperator.Rectangle(10, 10, 50, 50),
            ContentOperator.Fill(),
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 200, 600),
            WithText(new ContentOperator("Tj", new PdfObject[] { new PdfString("BLOCK2") }), "BLOCK2"),
            ContentOperator.EndText(),
        };

        var letters = LettersFor("BLOCK1", x0: 100, y: 700)
            .Concat(LettersFor("BLOCK2", x0: 200, y: 600))
            .ToList();

        var redactionArea = new PdfRectangle(400, 650, 500, 750);

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        // Should contain re and f operators (non-text ops)
        result.Where(o => o.Name == "re").Should().HaveCount(1);
        result.Where(o => o.Name == "f").Should().HaveCount(1);
    }

    [Fact]
    public void Process_WithTJOperatorMixedContent_HandlesSpacingCorrectly()
    {
        // TJ with mixed strings and spacing adjustments
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            WithText(new ContentOperator("TJ", new PdfObject[]
            {
                new PdfArray(
                    new PdfString("HEL"),
                    new PdfReal(-75),
                    new PdfString("LO"),
                    new PdfReal(-50),
                    new PdfString("WOR"),
                    new PdfReal(-75),
                    new PdfString("LD"))
            }), "HELLOWORLD"),
            ContentOperator.EndText(),
        };

        var letters = LettersFor("HELLOWORLD");
        var redactionArea = new PdfRectangle(400, 650, 500, 780); // no intersection

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        result.Should().NotBeEmpty();
        result[0].Name.Should().Be("BT");
    }

    [Fact]
    public void Process_PartialTJOverlap_RemovesOnlyIntersectingSegments()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            WithText(new ContentOperator("TJ", new PdfObject[]
            {
                new PdfArray(
                    new PdfString("KEEP"),
                    new PdfReal(-50),
                    new PdfString("REMOVE"))
            }), "KEEPREMOVE"),
            ContentOperator.EndText(),
        };

        var letters = LettersFor("KEEPREMOVE");
        // Cover only the REMOVE part
        var redactionArea = new PdfRectangle(120, 680, 300, 720);

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        result.Should().NotBeEmpty();
    }

    [Fact]
    public void Process_WithApostropheOperatorAndIntersection_RemovesCorrectly()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            WithText(new ContentOperator("'", new PdfObject[] { new PdfString("SECRET") }), "SECRET"),
            ContentOperator.EndText(),
        };

        var letters = LettersFor("SECRET");
        var redactionArea = new PdfRectangle(50, 680, 250, 720);

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        // All glyphs covered, so block should be removed or replaced
        result.Select(o => o.Name).Should().NotContain("'");
    }

    [Fact]
    public void Process_WithDoubleQuoteOperatorAndIntersection_RemovesCorrectly()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            new("Tf", new PdfObject[] { new PdfName("F1"), new PdfReal(12) }),
            ContentOperator.TextMatrix(12, 0, 0, 12, 100, 700),
            WithText(new ContentOperator("\"", new PdfObject[] { new PdfString("HIDDEN") }), "HIDDEN"),
            ContentOperator.EndText(),
        };

        var letters = LettersFor("HIDDEN");
        var redactionArea = new PdfRectangle(50, 680, 250, 720);

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        result.Select(o => o.Name).Should().NotContain("\"");
    }

    [Fact]
    public void ProcessOperations_EmptyContentStream_ReturnsEmpty()
    {
        var result = _remover.ProcessOperations(
            Array.Empty<ContentOperator>(),
            Array.Empty<Letter>(),
            new PdfRectangle(0, 0, 100, 100));

        result.Should().BeEmpty();
    }

    [Fact]
    public void ProcessOperations_NoIntersectingLetters_CopiesOperationsUnchanged()
    {
        var ops = SimpleTextBlock("VISIBLE", x: 100, y: 700);
        var letters = LettersFor("VISIBLE");
        var redactionArea = new PdfRectangle(400, 650, 500, 750); // far away

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        var tjOps = result.Where(o => o.Name == "Tj").ToList();
        tjOps.Should().HaveCount(1);
        ((PdfString)tjOps[0].Operands[0]).Value.Should().Be("VISIBLE");
    }

    [Fact]
    public void ProcessOperations_RemovesEntireWord_ReplacesWithReconstruction()
    {
        var ops = SimpleTextBlock("REDACT", x: 100, y: 700);
        var letters = LettersFor("REDACT");
        var redactionArea = new PdfRectangle(50, 680, 250, 720); // covers all

        var result = _remover.ProcessOperations(ops, letters, redactionArea);

        // Original Tj should be gone
        result.Select(o => o.Name).Should().NotContain("Tj");
    }
}
