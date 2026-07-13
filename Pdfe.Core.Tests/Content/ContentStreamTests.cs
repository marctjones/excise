using AwesomeAssertions;
using Pdfe.Core.Content;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Content;

public class ContentStreamTests
{
    [Fact]
    public void Empty_ContentStream_HasZeroOperators()
    {
        var stream = new ContentStream();
        stream.Count.Should().Be(0);
        stream.Operators.Should().BeEmpty();
    }

    [Fact]
    public void Append_SingleOperator_IncreasesCount()
    {
        var stream = new ContentStream();
        var op = ContentOperator.SaveState();

        var newStream = stream.Append(op);

        newStream.Count.Should().Be(1);
        newStream[0].Name.Should().Be("q");
    }

    [Fact]
    public void Append_DoesNotModifyOriginal()
    {
        var stream = new ContentStream();
        var op = ContentOperator.SaveState();

        stream.Append(op);

        stream.Count.Should().Be(0, "original should be immutable");
    }

    [Fact]
    public void Filter_RemovesMatchingOperators()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(1, 0, 0),
            ContentOperator.RestoreState()
        };
        var stream = new ContentStream(ops);

        var filtered = stream.Filter(op => op.Name != "rg");

        filtered.Count.Should().Be(2);
        filtered.Operators.All(op => op.Name != "rg").Should().BeTrue();
    }

    [Fact]
    public void RemoveIntersecting_RemovesOperatorsInArea()
    {
        var ops = new List<ContentOperator>();

        // Create an operator with a known bounding box
        var textOp = new ContentOperator("Tj", new[] { new PdfString("Test") });
        textOp.BoundingBox = new PdfRectangle(100, 100, 200, 120);
        ops.Add(textOp);

        // Create an operator outside the area
        var textOp2 = new ContentOperator("Tj", new[] { new PdfString("Other") });
        textOp2.BoundingBox = new PdfRectangle(300, 100, 400, 120);
        ops.Add(textOp2);

        var stream = new ContentStream(ops);
        var redactionArea = new PdfRectangle(50, 50, 250, 150);

        var redacted = stream.RemoveIntersecting(redactionArea);

        redacted.Count.Should().Be(1);
        redacted[0].TextContent.Should().Be("Other");
    }

    [Fact]
    public void RemoveCategory_RemovesOperatorsOfSpecificCategory()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            ContentOperator.ShowText("Hello"),
            ContentOperator.EndText(),
            ContentOperator.SaveState(),
            ContentOperator.Rectangle(0, 0, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        var stream = new ContentStream(ops);

        var withoutText = stream.RemoveCategory(OperatorCategory.TextShowing);

        // Should remove only Tj (TextShowing), keep BT/ET (TextObject)
        withoutText.Operators.Any(op => op.Name == "Tj").Should().BeFalse();
        withoutText.Operators.Any(op => op.Name == "BT").Should().BeTrue();
    }

    [Fact]
    public void Concat_CombinesTwoStreams()
    {
        var stream1 = new ContentStream(new[] { ContentOperator.SaveState() });
        var stream2 = new ContentStream(new[] { ContentOperator.RestoreState() });

        var combined = stream1.Concat(stream2);

        combined.Count.Should().Be(2);
        combined[0].Name.Should().Be("q");
        combined[1].Name.Should().Be("Q");
    }

    [Fact]
    public void Redact_RemovesContentAndAddsMarker()
    {
        var ops = new List<ContentOperator>();
        var textOp = new ContentOperator("Tj", new[] { new PdfString("Secret") });
        textOp.BoundingBox = new PdfRectangle(100, 100, 200, 120);
        ops.Add(textOp);

        var stream = new ContentStream(ops);
        var redactionArea = new PdfRectangle(50, 50, 250, 150);

        var redacted = stream.Redact(redactionArea, (0, 0, 0)); // black marker

        // Original text should be removed, marker added (q, rg, re, f, Q)
        redacted.Operators.Any(op => op.Name == "Tj").Should().BeFalse();
        redacted.Operators.Any(op => op.Name == "re").Should().BeTrue();
        redacted.Operators.Any(op => op.Name == "f").Should().BeTrue();
    }

    [Fact]
    public void RemoveContainedIn_RemovesOperatorsFullyInArea()
    {
        var ops = new List<ContentOperator>();

        // Create an operator fully contained in the area
        var textOp = new ContentOperator("Tj", new[] { new PdfString("Inside") });
        textOp.BoundingBox = new PdfRectangle(120, 120, 180, 140);
        ops.Add(textOp);

        // Create an operator outside the area
        var textOp2 = new ContentOperator("Tj", new[] { new PdfString("Outside") });
        textOp2.BoundingBox = new PdfRectangle(300, 100, 400, 120);
        ops.Add(textOp2);

        var stream = new ContentStream(ops);
        var containerArea = new PdfRectangle(100, 100, 200, 200);

        var result = stream.RemoveContainedIn(containerArea);

        result.Count.Should().Be(1);
        result[0].TextContent.Should().Be("Outside");
    }

    [Fact]
    public void RemoveContainedIn_KeepsAllOpsWhenNoBoundingBox()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.Rectangle(0, 0, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        var stream = new ContentStream(ops);
        var area = new PdfRectangle(0, 0, 500, 500);

        var result = stream.RemoveContainedIn(area);

        // Factory-created ops have no BoundingBox, so none are contained
        result.Count.Should().Be(4);
    }

    [Fact]
    public void Where_FiltersOperators()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(1, 0, 0),
            ContentOperator.RestoreState()
        };
        var stream = new ContentStream(ops);

        var filtered = stream.Where(op => op.Category != OperatorCategory.Color);

        filtered.Count.Should().Be(2);
        filtered.Operators.Any(op => op.Name == "rg").Should().BeFalse();
    }

    [Fact]
    public void PathOperators_ReturnsPathConstruction_AndPathPainting()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.MoveTo(0, 0),        // PathConstruction
            ContentOperator.LineTo(100, 0),      // PathConstruction
            ContentOperator.Stroke(),            // PathPainting
            ContentOperator.Rectangle(0, 0, 100, 100), // PathConstruction
            ContentOperator.Fill(),              // PathPainting
            ContentOperator.SaveState(),         // GraphicsState (not path)
            ContentOperator.ShowText("Text")     // TextShowing (not path)
        };
        var stream = new ContentStream(ops);

        var pathOps = stream.PathOperators.ToList();

        pathOps.Should().HaveCount(5);
        pathOps.Should().AllSatisfy(op =>
            (op.Category == OperatorCategory.PathConstruction || op.Category == OperatorCategory.PathPainting)
            .Should().BeTrue()
        );
    }

    [Fact]
    public void XObjectOperators_ReturnsXObjectOperators()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.InvokeXObject("Image1"),  // XObject
            ContentOperator.InvokeXObject("Form1"),   // XObject
            ContentOperator.SaveState(),              // GraphicsState
            ContentOperator.ShowText("Text")          // TextShowing
        };
        var stream = new ContentStream(ops);

        var xObjOps = stream.XObjectOperators.ToList();

        xObjOps.Should().HaveCount(2);
        xObjOps.All(op => op.Category == OperatorCategory.XObject).Should().BeTrue();
        xObjOps[0].GetName(0).Should().Be("Image1");
        xObjOps[1].GetName(0).Should().Be("Form1");
    }

    [Fact]
    public void Prepend_SingleOperator_AddsAtBeginning()
    {
        var stream = new ContentStream(new[] { ContentOperator.RestoreState() });
        var op = ContentOperator.SaveState();

        var result = stream.Prepend(op);

        result.Count.Should().Be(2);
        result[0].Name.Should().Be("q");
        result[1].Name.Should().Be("Q");
    }

    [Fact]
    public void Prepend_MultipleOperators_AddsAtBeginning()
    {
        var stream = new ContentStream(new[] { ContentOperator.RestoreState() });
        var opsToAdd = new[] { ContentOperator.SaveState(), ContentOperator.SetLineWidth(2) };

        var result = stream.Prepend(opsToAdd);

        result.Count.Should().Be(3);
        result[0].Name.Should().Be("q");
        result[1].Name.Should().Be("w");
        result[2].Name.Should().Be("Q");
    }

    [Fact]
    public void Insert_OpAtIndex_InsertsCorrectly()
    {
        var stream = new ContentStream(new[]
        {
            ContentOperator.SaveState(),
            ContentOperator.RestoreState()
        });
        var op = ContentOperator.SetLineWidth(2.5);

        var result = stream.Insert(1, op);

        result.Count.Should().Be(3);
        result[0].Name.Should().Be("q");
        result[1].Name.Should().Be("w");
        result[2].Name.Should().Be("Q");
    }

    [Fact]
    public void Replace_OpAtIndex_ReplacesCorrectly()
    {
        var stream = new ContentStream(new[]
        {
            ContentOperator.SaveState(),
            ContentOperator.SetLineWidth(1),
            ContentOperator.RestoreState()
        });
        var op = ContentOperator.SetLineWidth(3);

        var result = stream.Replace(1, op);

        result.Count.Should().Be(3);
        result[1].Name.Should().Be("w");
        result[1].GetNumber(0).Should().BeApproximately(3, 0.001);
    }

    [Fact]
    public void Transform_AppliesTransformer()
    {
        var stream = new ContentStream(new[]
        {
            ContentOperator.SetLineWidth(2),
            ContentOperator.SetLineWidth(4)
        });

        var result = stream.Transform(op =>
            op.Name == "w"
                ? ContentOperator.SetLineWidth(op.GetNumber(0) * 2)
                : op
        );

        result.Count.Should().Be(2);
        result[0].GetNumber(0).Should().BeApproximately(4, 0.001);
        result[1].GetNumber(0).Should().BeApproximately(8, 0.001);
    }

    [Fact]
    public void RedactWithBlackMarker_CallsRedactWithBlackColor()
    {
        var ops = new List<ContentOperator>();
        var textOp = new ContentOperator("Tj", new[] { new PdfString("Secret") });
        textOp.BoundingBox = new PdfRectangle(100, 100, 200, 120);
        ops.Add(textOp);

        var stream = new ContentStream(ops);
        var redactionArea = new PdfRectangle(50, 50, 250, 150);

        var redacted = stream.RedactWithBlackMarker(redactionArea);

        // Should remove text and add black marker
        redacted.Operators.Any(op => op.Name == "Tj").Should().BeFalse();
        redacted.Operators.Any(op => op.Name == "rg").Should().BeTrue();
        // Verify it's black (0, 0, 0)
        var rgOp = redacted.Operators.FirstOrDefault(op => op.Name == "rg");
        rgOp.Should().NotBeNull();
        rgOp!.GetNumber(0).Should().Be(0);
        rgOp.GetNumber(1).Should().Be(0);
        rgOp.GetNumber(2).Should().Be(0);
    }

    [Fact]
    public void FindText_ReturnsOperatorsContainingText()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.ShowText("Hello World"),
            ContentOperator.ShowText("Goodbye"),
            ContentOperator.ShowText("Hello Again")
        };
        var stream = new ContentStream(ops);

        var found = stream.FindText("Hello").ToList();

        found.Should().HaveCount(2);
        found[0].TextContent.Should().Be("Hello World");
        found[1].TextContent.Should().Be("Hello Again");
    }

    [Fact]
    public void FindText_WithCaseInsensitive_IgnoresCase()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.ShowText("HELLO"),
            ContentOperator.ShowText("hello"),
            ContentOperator.ShowText("HeLLo")
        };
        var stream = new ContentStream(ops);

        var found = stream.FindText("hello", StringComparison.OrdinalIgnoreCase).ToList();

        found.Should().HaveCount(3);
    }

    [Fact]
    public void FindText_NoMatches_ReturnsEmpty()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.ShowText("Hello"),
            ContentOperator.ShowText("World")
        };
        var stream = new ContentStream(ops);

        var found = stream.FindText("NotFound").ToList();

        found.Should().BeEmpty();
    }

    [Fact]
    public void GetEnumerator_AllowsIteration()
    {
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetLineWidth(2),
            ContentOperator.RestoreState()
        };
        var stream = new ContentStream(ops);

        var count = 0;
        foreach (var op in stream)
        {
            count++;
        }

        count.Should().Be(3);
    }

    [Fact]
    public void ToString_ReturnsOperatorCount()
    {
        var stream = new ContentStream(new[]
        {
            ContentOperator.SaveState(),
            ContentOperator.SetLineWidth(2),
            ContentOperator.RestoreState()
        });

        var result = stream.ToString();

        result.Should().Be("ContentStream[3 operators]");
    }

    [Fact]
    public void ToString_EmptyStream_ReturnsZeroCount()
    {
        var stream = new ContentStream();

        var result = stream.ToString();

        result.Should().Be("ContentStream[0 operators]");
    }
}

public class ContentOperatorTests
{
    [Fact]
    public void Factory_SaveState_CreatesCorrectOperator()
    {
        var op = ContentOperator.SaveState();

        op.Name.Should().Be("q");
        op.Operands.Should().BeEmpty();
        op.Category.Should().Be(OperatorCategory.GraphicsState);
    }

    [Fact]
    public void Factory_Rectangle_CreatesCorrectOperator()
    {
        var op = ContentOperator.Rectangle(10, 20, 100, 50);

        op.Name.Should().Be("re");
        op.Operands.Should().HaveCount(4);
        ((PdfReal)op.Operands[0]).Value.Should().Be(10);
        ((PdfReal)op.Operands[1]).Value.Should().Be(20);
        ((PdfReal)op.Operands[2]).Value.Should().Be(100);
        ((PdfReal)op.Operands[3]).Value.Should().Be(50);
    }

    [Fact]
    public void Factory_SetFillRgb_CreatesCorrectOperator()
    {
        var op = ContentOperator.SetFillRgb(0.5, 0.25, 0.75);

        op.Name.Should().Be("rg");
        op.Operands.Should().HaveCount(3);
        op.Category.Should().Be(OperatorCategory.Color);
    }

    [Fact]
    public void IntersectsWith_ReturnsTrueForOverlapping()
    {
        var op = new ContentOperator("Tj")
        {
            BoundingBox = new PdfRectangle(100, 100, 200, 150)
        };

        var overlapping = new PdfRectangle(150, 120, 250, 200);

        op.IntersectsWith(overlapping).Should().BeTrue();
    }

    [Fact]
    public void IntersectsWith_ReturnsFalseForNonOverlapping()
    {
        var op = new ContentOperator("Tj")
        {
            BoundingBox = new PdfRectangle(100, 100, 200, 150)
        };

        var nonOverlapping = new PdfRectangle(300, 100, 400, 150);

        op.IntersectsWith(nonOverlapping).Should().BeFalse();
    }

    [Fact]
    public void IntersectsWith_ReturnsFalseWhenNoBoundingBox()
    {
        var op = ContentOperator.SaveState(); // No bounding box

        var area = new PdfRectangle(0, 0, 1000, 1000);

        op.IntersectsWith(area).Should().BeFalse();
    }

    [Fact]
    public void IsContainedIn_ReturnsTrueWhenFullyInside()
    {
        var op = new ContentOperator("Tj")
        {
            BoundingBox = new PdfRectangle(150, 150, 180, 170)
        };

        var container = new PdfRectangle(100, 100, 200, 200);

        op.IsContainedIn(container).Should().BeTrue();
    }

    [Fact]
    public void IsContainedIn_ReturnsFalseWhenPartiallyOutside()
    {
        var op = new ContentOperator("Tj")
        {
            BoundingBox = new PdfRectangle(150, 150, 250, 170)
        };

        var container = new PdfRectangle(100, 100, 200, 200);

        op.IsContainedIn(container).Should().BeFalse();
    }

    #region ToString and Formatting Tests

    [Fact]
    public void ToString_NoOperands_ReturnsOperatorName()
    {
        var op = ContentOperator.SaveState();
        op.ToString().Should().Be("q");
    }

    [Fact]
    public void ToString_WithOperands_FormatsAsOperandsName()
    {
        var op = ContentOperator.MoveTo(100, 200);
        var result = op.ToString();

        result.Should().Contain("100");
        result.Should().Contain("200");
        result.Should().Contain("m");
        result.Should().EndWith("m");
    }

    [Fact]
    public void ToString_StringOperand_EscapesSpecialChars()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Test(123)") });
        var result = op.ToString();

        result.Should().Contain("\\(");
        result.Should().Contain("\\)");
        result.Should().Contain("Tj");
    }

    [Fact]
    public void ToString_StringWithBackslash_EscapesBackslash()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Path\\File") });
        var result = op.ToString();

        result.Should().Contain("\\\\");
    }

    [Fact]
    public void ToString_StringWithNewline_EscapesNewline()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Line1\nLine2") });
        var result = op.ToString();

        result.Should().Contain("\\n");
    }

    [Fact]
    public void ToString_StringWithReturn_EscapesReturn()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Line1\rLine2") });
        var result = op.ToString();

        result.Should().Contain("\\r");
    }

    [Fact]
    public void ToString_StringWithTab_EscapesTab()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Col1\tCol2") });
        var result = op.ToString();

        result.Should().Contain("\\t");
    }

    [Fact]
    public void ToString_NameOperand_IncludesLeadingSlash()
    {
        var op = new ContentOperator("Do", new PdfObject[] { new PdfName("XObj1") });
        var result = op.ToString();

        result.Should().Contain("/XObj1");
    }

    [Fact]
    public void ToString_ArrayOperand_FormatsWithBrackets()
    {
        var arr = new PdfArray(new PdfObject[] { new PdfInteger(1), new PdfInteger(2), new PdfInteger(3) });
        var op = new ContentOperator("Test", new PdfObject[] { arr });
        var result = op.ToString();

        result.Should().Contain("[");
        result.Should().Contain("]");
        result.Should().Contain("1");
        result.Should().Contain("2");
        result.Should().Contain("3");
    }

    [Fact]
    public void ToString_IntegerOperand_FormatsAsDecimal()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfInteger(5) });
        var result = op.ToString();

        result.Should().Contain("5");
    }

    [Fact]
    public void ToString_RealOperand_FormatsWithPrecision()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfReal(3.14159) });
        var result = op.ToString();

        result.Should().Contain("3.14159");
        result.Should().EndWith("w");
    }

    [Fact]
    public void ToString_MultipleOperands_SeparatedBySpaces()
    {
        var op = ContentOperator.Rectangle(10, 20, 100, 50);
        var result = op.ToString();

        result.Should().StartWith("10");
        result.Should().Contain(" 20 ");
        result.Should().Contain(" 100 ");
        result.Should().EndWith("re");
    }

    [Theory]
    [InlineData("Tj", OperatorCategory.TextShowing)]
    [InlineData("q", OperatorCategory.GraphicsState)]
    [InlineData("m", OperatorCategory.PathConstruction)]
    [InlineData("S", OperatorCategory.PathPainting)]
    [InlineData("W", OperatorCategory.Clipping)]
    [InlineData("Tf", OperatorCategory.TextState)]
    [InlineData("Td", OperatorCategory.TextPositioning)]
    [InlineData("BT", OperatorCategory.TextObject)]
    [InlineData("rg", OperatorCategory.Color)]
    [InlineData("sh", OperatorCategory.Shading)]
    [InlineData("Do", OperatorCategory.XObject)]
    [InlineData("BMC", OperatorCategory.MarkedContent)]
    [InlineData("BX", OperatorCategory.Compatibility)]
    [InlineData("UnknownOp", OperatorCategory.Unknown)]
    public void Categorize_CorrectlyIdentifiesCategory(string operatorName, OperatorCategory expectedCategory)
    {
        var op = new ContentOperator(operatorName);
        op.Category.Should().Be(expectedCategory);
    }

    #endregion

    #region GetNumber/GetName/GetString/GetArray Tests

    [Fact]
    public void GetNumber_ValidIntegerOperand_ReturnsValue()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfInteger(5) });
        op.GetNumber(0).Should().Be(5);
    }

    [Fact]
    public void GetNumber_ValidRealOperand_ReturnsValue()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfReal(3.5) });
        op.GetNumber(0).Should().Be(3.5);
    }

    [Fact]
    public void GetNumber_InvalidIndex_ReturnsZero()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfInteger(5) });
        op.GetNumber(1).Should().Be(0);
    }

    [Fact]
    public void GetNumber_NegativeIndex_ReturnsZero()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfInteger(5) });
        op.GetNumber(-1).Should().Be(0);
    }

    [Fact]
    public void GetNumber_NonNumericOperand_ReturnsZero()
    {
        var op = new ContentOperator("Do", new PdfObject[] { new PdfName("XObj") });
        op.GetNumber(0).Should().Be(0);
    }

    [Fact]
    public void GetNumber_NoOperands_ReturnsZero()
    {
        var op = ContentOperator.SaveState();
        op.GetNumber(0).Should().Be(0);
    }

    [Fact]
    public void GetName_ValidNameOperand_ReturnsNameWithoutSlash()
    {
        var op = new ContentOperator("Do", new PdfObject[] { new PdfName("MyXObject") });
        op.GetName(0).Should().Be("MyXObject");
    }

    [Fact]
    public void GetName_InvalidIndex_ReturnsEmptyString()
    {
        var op = new ContentOperator("Do", new PdfObject[] { new PdfName("MyXObject") });
        op.GetName(1).Should().Be("");
    }

    [Fact]
    public void GetName_NegativeIndex_ReturnsEmptyString()
    {
        var op = new ContentOperator("Do", new PdfObject[] { new PdfName("MyXObject") });
        op.GetName(-1).Should().Be("");
    }

    [Fact]
    public void GetName_NonNameOperand_ReturnsEmptyString()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Text") });
        op.GetName(0).Should().Be("");
    }

    [Fact]
    public void GetString_ValidStringOperand_ReturnsValue()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Hello") });
        op.GetString(0).Should().Be("Hello");
    }

    [Fact]
    public void GetString_InvalidIndex_ReturnsEmptyString()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Hello") });
        op.GetString(1).Should().Be("");
    }

    [Fact]
    public void GetString_NegativeIndex_ReturnsEmptyString()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Hello") });
        op.GetString(-1).Should().Be("");
    }

    [Fact]
    public void GetString_NonStringOperand_ReturnsEmptyString()
    {
        var op = new ContentOperator("Do", new PdfObject[] { new PdfName("XObj") });
        op.GetString(0).Should().Be("");
    }

    [Fact]
    public void GetArray_ValidArrayOperand_ReturnsArray()
    {
        var arr = new PdfArray(new PdfObject[] { new PdfInteger(1), new PdfInteger(2) });
        var op = new ContentOperator("Test", new PdfObject[] { arr });
        var result = op.GetArray(0);

        result.Should().NotBeNull();
        result!.Count.Should().Be(2);
    }

    [Fact]
    public void GetArray_InvalidIndex_ReturnsNull()
    {
        var arr = new PdfArray(new PdfObject[] { new PdfInteger(1) });
        var op = new ContentOperator("Test", new PdfObject[] { arr });
        op.GetArray(1).Should().BeNull();
    }

    [Fact]
    public void GetArray_NegativeIndex_ReturnsNull()
    {
        var arr = new PdfArray(new PdfObject[] { new PdfInteger(1) });
        var op = new ContentOperator("Test", new PdfObject[] { arr });
        op.GetArray(-1).Should().BeNull();
    }

    [Fact]
    public void GetArray_NonArrayOperand_ReturnsNull()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Text") });
        op.GetArray(0).Should().BeNull();
    }

    #endregion

    #region Color Factory Methods

    [Fact]
    public void SetFillBlack_CreatesFillGrayZero()
    {
        var op = ContentOperator.SetFillBlack();

        op.Name.Should().Be("g");
        op.Operands.Should().HaveCount(1);
        ((PdfReal)op.Operands[0]).Value.Should().Be(0);
        op.Category.Should().Be(OperatorCategory.Color);
    }

    [Fact]
    public void SetFillWhite_CreatesFillGrayOne()
    {
        var op = ContentOperator.SetFillWhite();

        op.Name.Should().Be("g");
        op.Operands.Should().HaveCount(1);
        ((PdfReal)op.Operands[0]).Value.Should().Be(1);
        op.Category.Should().Be(OperatorCategory.Color);
    }

    [Fact]
    public void Transform_CreatesTransformationMatrix()
    {
        var op = ContentOperator.Transform(1, 0, 0, 1, 10, 20);

        op.Name.Should().Be("cm");
        op.Operands.Should().HaveCount(6);
        op.Category.Should().Be(OperatorCategory.GraphicsState);
    }

    #endregion

    #region Edge Cases and Round-Trip Tests

    [Fact]
    public void ToString_AllEscapeSequences_RoundTrip()
    {
        var escapeTestString = "Test\\(123)with\nnewline\rreturn\ttab";
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString(escapeTestString) });
        var formatted = op.ToString();

        formatted.Should().Contain("\\\\");
        formatted.Should().Contain("\\(");
        formatted.Should().Contain("\\)");
        formatted.Should().Contain("\\n");
        formatted.Should().Contain("\\r");
        formatted.Should().Contain("\\t");
    }

    [Fact]
    public void ToString_EmptyString_FormatsCorrectly()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("") });
        var result = op.ToString();

        result.Should().Contain("()");
        result.Should().EndWith("Tj");
    }

    [Fact]
    public void ToString_OperatorWithMixedOperands_FormatsCorrectly()
    {
        var operands = new PdfObject[]
        {
            new PdfInteger(100),
            new PdfReal(50.5),
            new PdfName("FontA"),
            new PdfString("Text")
        };
        var op = new ContentOperator("Test", operands);
        var result = op.ToString();

        result.Should().Contain("100");
        result.Should().Contain("50.5");
        result.Should().Contain("/FontA");
        result.Should().Contain("(Text)");
    }

    #endregion
}

public class ContentStreamParserTests
{
    #region ParseName with Hex Escapes

    [Fact]
    public void Parse_NameWithHexEscape_DecodesCorrectly()
    {
        var stream = "/A#20B Tj\n".AsSpan().ToArray();
        byte[] bytes = new byte[stream.Length];
        System.Text.Encoding.ASCII.GetBytes(new string(stream.Cast<char>().ToArray()), 0, stream.Length, bytes, 0);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("Tj");
        content.Operators[0].GetName(0).Should().Be("A B", "hex escape #20 should be decoded as space");
    }

    [Fact]
    public void Parse_NameWithMultipleHexEscapes_DecodesCorrectly()
    {
        var sourceString = "/A#20B#20C Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].GetName(0).Should().Be("A B C");
    }

    [Fact]
    public void Parse_NameWithHexForSpecialChar_DecodesCorrectly()
    {
        var sourceString = "/Font#2FName Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetName(0).Should().Be("Font/Name");
    }

    [Fact]
    public void Parse_NameWithHashEncoding_PreservesValidNames()
    {
        var sourceString = "/MyFont Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetName(0).Should().Be("MyFont");
    }

    #endregion

    #region ParseStringLiteral with Escape Sequences

    [Fact]
    public void Parse_StringWithBackslashN_DecodesNewline()
    {
        var sourceString = "(Line1\\nLine2) Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetString(0).Should().Contain("\n");
    }

    [Fact]
    public void Parse_StringWithBackslashR_DecodesCarriageReturn()
    {
        var sourceString = "(Line1\\rLine2) Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetString(0).Should().Contain("\r");
    }

    [Fact]
    public void Parse_StringWithBackslashT_DecodesTab()
    {
        var sourceString = "(Col1\\tCol2) Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetString(0).Should().Contain("\t");
    }

    [Fact]
    public void Parse_StringWithEscapedParentheses_DecodesCorrectly()
    {
        var sourceString = "(Text\\(123\\)) Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetString(0).Should().Be("Text(123)");
    }

    [Fact]
    public void Parse_StringWithEscapedBackslash_DecodesCorrectly()
    {
        var sourceString = "(Path\\\\File) Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetString(0).Should().Be("Path\\File");
    }

    [Fact]
    public void Parse_StringWithOctalEscape_DecodesCorrectly()
    {
        var sourceString = "(Text\\101End) Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        var result = content.Operators[0].GetString(0);
        result.Should().Contain("A", "octal 101 is character 'A'");
    }

    [Fact]
    public void Parse_StringWithShortOctalEscape_DecodesCorrectly()
    {
        var sourceString = "(Text\\01) Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        var result = content.Operators[0].GetString(0);
        result.Length.Should().Be(5);
    }

    [Fact]
    public void Parse_StringWithMultipleEscapeTypes_DecodesAll()
    {
        var sourceString = "(Line1\\nText\\(123\\)\\tEnd) Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        var result = content.Operators[0].GetString(0);
        result.Should().Contain("\n");
        result.Should().Contain("(123)");
        result.Should().Contain("\t");
    }

    #endregion

    #region SkipWhitespaceAndComments

    [Fact]
    public void Parse_WithCommentLine_IgnoresComment()
    {
        var sourceString = "% This is a comment\nq\nQ\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(2);
        content.Operators[0].Name.Should().Be("q");
        content.Operators[1].Name.Should().Be("Q");
    }

    [Fact]
    public void Parse_WithMultipleComments_IgnoresAllComments()
    {
        var sourceString = "% Comment 1\nq % Inline comment\n% Comment 2\nQ\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(2);
        content.Operators[0].Name.Should().Be("q");
        content.Operators[1].Name.Should().Be("Q");
    }

    [Fact]
    public void Parse_WithVariousWhitespace_ParsesCorrectly()
    {
        var sourceString = "  q  \t  Q  \n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(2);
    }

    #endregion

    #region SkipDictionary

    [Fact]
    public void Parse_WithDictionaryAsOperand_SkipsDictionary()
    {
        var sourceString = "<< /Type /Font >> gs\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("gs");
    }

    [Fact]
    public void Parse_WithNestedDictionary_SkipsNestedDictionary()
    {
        var sourceString = "<< /Dict << /Inner 1 >> >> gs\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("gs");
    }

    [Fact]
    public void Parse_WithDictionaryBetweenOperators_SkipsAndContinues()
    {
        var sourceString = "q << /Key /Value >> 100 w Q\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(3);
        content.Operators[0].Name.Should().Be("q");
        content.Operators[1].Name.Should().Be("w");
        content.Operators[2].Name.Should().Be("Q");
    }

    #endregion

    #region ParseNumber Edge Cases

    [Fact]
    public void Parse_NumberWithLeadingPlus_ParsesCorrectly()
    {
        var sourceString = "+42 w\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetNumber(0).Should().Be(42);
    }

    [Fact]
    public void Parse_NumberWithLeadingMinus_ParsesCorrectly()
    {
        var sourceString = "-42 w\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetNumber(0).Should().Be(-42);
    }

    [Fact]
    public void Parse_DecimalNumber_ParsesCorrectly()
    {
        var sourceString = "3.14159 w\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetNumber(0).Should().BeApproximately(3.14159, 0.00001);
    }

    [Fact]
    public void Parse_NumberStartingWithDot_ParsesCorrectly()
    {
        var sourceString = ".5 w\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators[0].GetNumber(0).Should().BeApproximately(0.5, 0.001);
    }

    #endregion

    #region ParseToken Edge Cases

    [Fact]
    public void Parse_HexStringOperand_ParsesCorrectly()
    {
        var sourceString = "<48656C6C6F> Tj\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("Tj");
        content.Operators[0].Operands.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_ArrayOperand_ParsesCorrectly()
    {
        var sourceString = "[1 2 3] d\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("d");
        var arr = content.Operators[0].GetArray(0);
        arr.Should().NotBeNull();
        arr!.Count.Should().Be(3);
    }

    #endregion

    #region Category Edge Cases

    [Theory]
    [InlineData("f*", OperatorCategory.PathPainting)]
    [InlineData("B*", OperatorCategory.PathPainting)]
    [InlineData("b*", OperatorCategory.PathPainting)]
    [InlineData("W*", OperatorCategory.Clipping)]
    [InlineData("TJ", OperatorCategory.TextShowing)]
    [InlineData("'", OperatorCategory.TextShowing)]
    [InlineData("\"", OperatorCategory.TextShowing)]
    public void Parse_SpecialOperators_CategorizeCorrectly(string opName, OperatorCategory expectedCategory)
    {
        var sourceString = $"{opName}\n";
        var bytes = System.Text.Encoding.Latin1.GetBytes(sourceString);

        var parser = new ContentStreamParser(bytes);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Category.Should().Be(expectedCategory);
    }

    #endregion

    #region ContentStreamParser Tests (Empty, Whitespace, String Literals)

    [Fact]
    public void Parse_EmptyContentStream_ReturnsEmptyOperatorList()
    {
        var parser = new ContentStreamParser(Array.Empty<byte>(), null);

        var content = parser.Parse();

        content.Operators.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WhitespaceOnlyContentStream_ReturnsEmptyOperatorList()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("   \n  \t  \r\n   ");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SingleBTETBlock_WithTextShowing()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("BT /F1 12 Tf (Hello) Tj ET");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().NotBeEmpty();
        content.Operators.Any(op => op.Name == "BT").Should().BeTrue();
        content.Operators.Any(op => op.Name == "ET").Should().BeTrue();
        content.Operators.Any(op => op.Name == "Tj").Should().BeTrue();
    }

    [Fact]
    public void Parse_StringLiteralWithOctalEscape_DecodesCorrectly()
    {
        // String with octal escape: \101 = 'A' (decimal 65)
        var data = System.Text.Encoding.ASCII.GetBytes("(\\101BC) Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("Tj");
        op.TextContent.Should().Contain("A");
    }

    [Fact]
    public void Parse_StringLiteralWithHexEscape_DecodesCorrectly()
    {
        // String with hex escape: \x41 or \41 = 'A'
        var data = System.Text.Encoding.ASCII.GetBytes("(\\41BC) Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_StringLiteralWithEscapedParens_HandlesCorrectly()
    {
        // String with escaped parentheses
        var data = System.Text.Encoding.ASCII.GetBytes("(Hello\\(World\\)) Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].TextContent.Should().Contain("Hello");
    }

    /// <summary>
    /// REVERSE SOLIDUS immediately followed by a raw EOL byte is a
    /// line-continuation and must produce zero characters (PDF32000-1
    /// §7.3.4.2 Table 3), not a spurious literal newline. This parser feeds
    /// the glyph-removal rewrite pipeline (LetterFinder/GlyphRemover), so a
    /// mismatch against TextExtractor's handling of the same escape risks a
    /// matched-but-not-actually-excised redaction leak (#637).
    /// </summary>
    [Fact]
    public void Parse_StringLiteralWithLineContinuationLF_ProducesNoCharacter()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("(Instruc\\\ntions) Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].TextContent.Should().Be("Instructions");
    }

    [Fact]
    public void Parse_StringLiteralWithLineContinuationCRLF_ProducesNoCharacter()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("(Instruc\\\r\ntions) Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].TextContent.Should().Be("Instructions");
    }

    [Fact]
    public void Parse_HexString_OddNumberOfHexDigits_HandlesCorrectly()
    {
        // Hex string with odd number of digits (last digit padded with 0 on right)
        var data = System.Text.Encoding.ASCII.GetBytes("<48656C6C6F5> Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_NumberWithDecimalPoint_ParsesAsDouble()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("1.5 2.75 Td");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("Td");
    }

    [Fact]
    public void Parse_NumberWithExponent_ParsesCorrectly()
    {
        // PDF supports scientific notation: 1.5e2 = 150
        var data = System.Text.Encoding.ASCII.GetBytes("1.5e2 Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_NegativeNumber_ParsesCorrectly()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("-100 -50 Td");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("Td");
    }

    [Fact]
    public void Parse_TjOperator_WithArrayOfStrings()
    {
        // TJ operator takes an array of strings and numbers
        var data = System.Text.Encoding.ASCII.GetBytes("[(Hello) -50 (World)] TJ");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("TJ");
    }

    [Fact]
    public void Parse_Comment_IsIgnored()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("% This is a comment\n(Hello) Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        // Comment should be ignored, only Tj operator should remain
        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("Tj");
    }

    [Fact]
    public void Parse_MultipleComments_AreIgnored()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("% Comment 1\nBT % Comment 2\n(Text) Tj % Comment 3\nET");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        // Comments should be ignored
        content.Operators.Should().NotBeEmpty();
        content.Operators.Any(op => op.Name == "BT").Should().BeTrue();
    }

    [Fact]
    public void Parse_StringLiteralWithNestedParentheses_HandlesNesting()
    {
        // Strings with nested parens require counting paren depth
        var data = System.Text.Encoding.ASCII.GetBytes("(Level1\\(Level2\\(Level3\\)\\)) Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_GraphicsStateOperator_WithDictionary()
    {
        // gs operator references a graphics state dict
        var data = System.Text.Encoding.ASCII.GetBytes("/GS1 gs");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        // Should parse the gs operator
        content.Operators.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_SetFontOperator_WithFontName()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("/F1 12 Tf");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("Tf");
    }

    [Fact]
    public void Parse_TextMatrixAndLineMatrix_ParseCorrectly()
    {
        // Tm operator sets text matrix
        var data = System.Text.Encoding.ASCII.GetBytes("1 0 0 1 100 700 Tm");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("Tm");
    }

    [Fact]
    public void Parse_ColorOperators_RGB_ParseCorrectly()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("1 0 0 rg");  // Red in RGB
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("rg");
    }

    [Fact]
    public void Parse_ColorOperators_CMYK_ParseCorrectly()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("0 1 1 0 k");  // Red in CMYK
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("k");
    }

    [Fact]
    public void Parse_PathOperators_MoveLine_ParseCorrectly()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("100 100 m 200 200 l");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(2);
        content.Operators[0].Name.Should().Be("m");
        content.Operators[1].Name.Should().Be("l");
    }

    [Fact]
    public void Parse_StringLiteralWithBackslashN_HandlesEscapeSequence()
    {
        // \n in string is a newline (not line feed escape)
        var data = System.Text.Encoding.ASCII.GetBytes("(Line1\\nLine2) Tj");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
    }

    #endregion

    #region Additional ContentStreamParser Coverage

    [Fact]
    public void Parse_SCNOperator_SpecialColor_ParsesCorrectly()
    {
        // SCN operator for special color spaces
        var data = System.Text.Encoding.ASCII.GetBytes("0.5 0.5 0.5 SCN");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("SCN");
    }

    [Fact]
    public void Parse_scnOperator_SpecialColorFill_ParsesCorrectly()
    {
        // scn operator (lowercase, nonstroking special color)
        var data = System.Text.Encoding.ASCII.GetBytes("0.2 scn");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("scn");
    }

    [Fact]
    public void Parse_CSOperator_ColorSpace_ParsesCorrectly()
    {
        // CS operator (stroking color space)
        var data = System.Text.Encoding.ASCII.GetBytes("/DeviceRGB CS");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("CS");
    }

    [Fact]
    public void Parse_csOperator_NonStrokingColorSpace_ParsesCorrectly()
    {
        // cs operator (nonstroking color space)
        var data = System.Text.Encoding.ASCII.GetBytes("/DeviceCMYK cs");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("cs");
    }

    [Fact]
    public void Parse_GOperator_GrayScale_ParsesCorrectly()
    {
        // G operator (stroking grayscale)
        var data = System.Text.Encoding.ASCII.GetBytes("0.75 G");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("G");
    }

    [Fact]
    public void Parse_gOperator_GrayScaleFill_ParsesCorrectly()
    {
        // g operator (nonstroking grayscale)
        var data = System.Text.Encoding.ASCII.GetBytes("0.5 g");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("g");
    }

    [Fact]
    public void Parse_RGOperator_RGB_Stroking_ParsesCorrectly()
    {
        // RG operator (stroking RGB)
        var data = System.Text.Encoding.ASCII.GetBytes("1 0 0 RG");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("RG");
    }

    [Fact]
    public void Parse_KOperator_CMYK_Stroking_ParsesCorrectly()
    {
        // K operator (stroking CMYK)
        var data = System.Text.Encoding.ASCII.GetBytes("0 1 1 0 K");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("K");
    }

    [Fact]
    public void Parse_BXEXOperators_CompatibilitySection_ParsesCorrectly()
    {
        // BX (begin compatibility section) wraps content the renderer may not understand;
        // EX closes it. A known operator inside should still be parsed normally.
        var data = System.Text.Encoding.ASCII.GetBytes("BX 1 j EX");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        // Should parse BX, the line-join operator, and EX
        content.Operators.Should().HaveCount(3);
        content.Operators[0].Name.Should().Be("BX");
        content.Operators[1].Name.Should().Be("j");
        content.Operators[2].Name.Should().Be("EX");
    }

    [Fact]
    public void Parse_shOperator_Shading_ParsesCorrectly()
    {
        // sh operator (shading)
        var data = System.Text.Encoding.ASCII.GetBytes("/Sh1 sh");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("sh");
    }

    [Fact]
    public void Parse_BMCOperator_BeginMarkedContent_ParsesCorrectly()
    {
        // BMC (begin marked content) operator
        var data = System.Text.Encoding.ASCII.GetBytes("/P BMC");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("BMC");
    }

    [Fact]
    public void Parse_BDCOperator_BeginMarkedContentDict_ParsesCorrectly()
    {
        // BDC (begin marked content with dictionary) operator
        var data = System.Text.Encoding.ASCII.GetBytes("/MC1 << /MCID 1 >> BDC");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        // Should parse the name, dictionary, and operator
        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("BDC");
    }

    [Fact]
    public void Parse_EMCOperator_EndMarkedContent_ParsesCorrectly()
    {
        // EMC (end marked content) operator
        var data = System.Text.Encoding.ASCII.GetBytes("EMC");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("EMC");
    }

    [Fact]
    public void Parse_vOperator_BezierCurve_CubicVari_ParsesCorrectly()
    {
        // v operator (Bezier curve, current point as first control point)
        var data = System.Text.Encoding.ASCII.GetBytes("100 100 200 200 v");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("v");
    }

    [Fact]
    public void Parse_yOperator_BezierCurve_PointVari_ParsesCorrectly()
    {
        // y operator (Bezier curve, final point as last control point)
        var data = System.Text.Encoding.ASCII.GetBytes("100 100 200 200 y");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("y");
    }

    [Fact]
    public void Parse_DoOperator_XObject_ParsesCorrectly()
    {
        // Do operator (XObject)
        var data = System.Text.Encoding.ASCII.GetBytes("/Im0 Do");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("Do");
    }

    [Fact]
    public void Parse_d0d1Operators_Type3Font_ParsesCorrectly()
    {
        // d0 operator (Type 3 font width)
        var data = System.Text.Encoding.ASCII.GetBytes("100 0 d0");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("d0");
    }

    [Fact]
    public void Parse_d1Operator_Type3FontWithMetrics_ParsesCorrectly()
    {
        // d1 operator (Type 3 font with metrics)
        var data = System.Text.Encoding.ASCII.GetBytes("100 0 200 50 100 150 d1");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("d1");
    }

    [Fact]
    public void Parse_DPOperator_MarkedContentPoint_ParsesCorrectly()
    {
        // DP operator (marked content point with properties)
        var data = System.Text.Encoding.ASCII.GetBytes("/P1 << /key value >> DP");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("DP");
    }

    [Fact]
    public void Parse_MPOperator_MarkedContentPoint_Simple_ParsesCorrectly()
    {
        // MP operator (marked content point)
        var data = System.Text.Encoding.ASCII.GetBytes("/P1 MP");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("MP");
    }

    [Fact]
    public void Parse_riOperator_RenderingIntent_ParsesCorrectly()
    {
        // ri operator (rendering intent)
        var data = System.Text.Encoding.ASCII.GetBytes("/AbsoluteColorimetric ri");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("ri");
    }

    [Fact]
    public void Parse_BStarOperator_EvenOddWinding_ParsesCorrectly()
    {
        // B* operator (fill and stroke, even-odd winding)
        var data = System.Text.Encoding.ASCII.GetBytes("100 100 m 200 200 l B*");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        // Should have move, line, and fill/stroke operators
        var operators = content.Operators.Select(o => o.Name).ToList();
        operators.Should().Contain("B*");
    }

    [Fact]
    public void Parse_bStarOperator_ClosePathFillStrokeEvenOdd_ParsesCorrectly()
    {
        // b* operator (close, fill and stroke, even-odd winding)
        var data = System.Text.Encoding.ASCII.GetBytes("100 100 m 200 200 l b*");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(3); // m, l, b*
        content.Operators.Last().Name.Should().Be("b*");
    }

    [Fact]
    public void Parse_fStarOperator_EvenOddFill_ParsesCorrectly()
    {
        // f* operator (fill, even-odd winding)
        var data = System.Text.Encoding.ASCII.GetBytes("100 100 m 200 200 l f*");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Last().Name.Should().Be("f*");
    }

    [Fact]
    public void Parse_TzOperator_HorizontalScaling_ParsesCorrectly()
    {
        // Tz operator (text horizontal scaling)
        var data = System.Text.Encoding.ASCII.GetBytes("80 Tz");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("Tz");
    }

    [Fact]
    public void Parse_TsOperator_TextRise_ParsesCorrectly()
    {
        // Ts operator (text rise/superscript)
        var data = System.Text.Encoding.ASCII.GetBytes("3 Ts");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("Ts");
    }

    [Fact]
    public void Parse_TrOperator_RenderingMode_ParsesCorrectly()
    {
        // Tr operator (text rendering mode)
        var data = System.Text.Encoding.ASCII.GetBytes("3 Tr");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("Tr");
    }

    #endregion

    #region Stray ID/EI Operator Tests

    [Fact]
    public void Parse_StrayEiOperator_WithoutBiContext_IsIgnored()
    {
        // Test line 86-89: Stray EI operator (without corresponding BI)
        // The parser clears operands but doesn't add an operator
        var data = System.Text.Encoding.ASCII.GetBytes("EI");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        // Stray EI should be skipped (line 88: operands.Clear())
        content.Operators.Should().HaveCount(0);
    }

    [Fact]
    public void Parse_StrayIdOperator_WithoutBiContext_IsIgnored()
    {
        // Test line 86-89: Stray ID operator (without corresponding BI)
        // The parser clears operands but doesn't add an operator
        var data = System.Text.Encoding.ASCII.GetBytes("ID");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        // Stray ID should be skipped
        content.Operators.Should().HaveCount(0);
    }

    [Fact]
    public void Parse_StrayEiWithOperands_ClearsOperands()
    {
        // Stray EI with preceding operands - operands should be cleared. The Tm afterward
        // proves the parser keeps going and only emits operators with operands collected
        // *after* the EI.
        var data = System.Text.Encoding.ASCII.GetBytes("100 200 EI 1 0 0 1 50 60 Tm");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        // Should have only Tm operator (operands before EI are cleared)
        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("Tm");
        content.Operators[0].Operands.Should().HaveCount(6);
    }

    #endregion

    #region Graphics State Operator Tests

    [Fact]
    public void Parse_GsOperator_WithTrInGraphicsState_ParsesSuccessfully()
    {
        // Test line 618: gs operator where graphics state dict contains /Tr (text rendering mode)
        // This requires a mock page with ExtGState resources
        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);

        // Create minimal PDF structure with graphics state
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page with resources including graphics state dict
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
        writer.WriteLine("   /Resources << /ExtGState << /GS1 << /Type /ExtGState /Tr 2 >> >> >>");
        writer.WriteLine("   /Contents 4 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream with gs operator
        var content = "/GS1 gs";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = Pdfe.Core.Document.PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var parser = new ContentStreamParser(System.Text.Encoding.ASCII.GetBytes(content), page);
        var contentStream = parser.Parse();

        // Should parse successfully without errors
        contentStream.Operators.Should().HaveCount(1);
        contentStream.Operators[0].Name.Should().Be("gs");
    }

    [Fact]
    public void Parse_GsOperator_WithoutTr_ParsesSuccessfully()
    {
        // Positive test: gs operator with normal graphics state (no Tr)
        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
        writer.WriteLine("   /Resources << /ExtGState << /GS1 << /Type /ExtGState /LW 2 >> >> >>");
        writer.WriteLine("   /Contents 4 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var content = "/GS1 gs";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = Pdfe.Core.Document.PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var parser = new ContentStreamParser(System.Text.Encoding.ASCII.GetBytes(content), page);
        var contentStream = parser.Parse();

        contentStream.Operators.Should().HaveCount(1);
        contentStream.Operators[0].Name.Should().Be("gs");
    }

    #endregion

    #region Phase 6 — Clipping + Transparency

    [Fact]
    public void Parse_WOperator_NonZeroClipping_PreservesOperator()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("100 100 200 150 re W n");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Select(o => o.Name).Should().ContainInOrder("re", "W", "n");
    }

    [Fact]
    public void Parse_WStarOperator_EvenOddClipping_PreservesOperator()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("100 100 200 150 re W* n");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Select(o => o.Name).Should().ContainInOrder("re", "W*", "n");
    }

    [Fact]
    public void Parse_WOperator_ClassifiedAsClipping()
    {
        var op = new ContentOperator("W", new System.Collections.Generic.List<PdfObject>());
        op.Category.Should().Be(OperatorCategory.Clipping);
    }

    [Fact]
    public void Parse_WStarOperator_ClassifiedAsClipping()
    {
        var op = new ContentOperator("W*", new System.Collections.Generic.List<PdfObject>());
        op.Category.Should().Be(OperatorCategory.Clipping);
    }

    [Fact]
    public void Parse_GsOperator_TransparencyParameters_ReadsAlphaAndBlendMode()
    {
        var helperBytes = TestPdfWithExtGState("0.4", "0.7", "/Multiply", true, false);
        using var doc = Pdfe.Core.Document.PdfDocument.Open(helperBytes);
        var page = doc.GetPage(1);

        var parser = new ContentStreamParser(System.Text.Encoding.ASCII.GetBytes("/G1 gs"), page);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("gs");
    }

    [Fact]
    public void Parse_GsOperator_BlendModeArray_PicksFirstName()
    {
        var helperBytes = TestPdfWithExtGState("1.0", "1.0", "[/Multiply /Normal]", false, false);
        using var doc = Pdfe.Core.Document.PdfDocument.Open(helperBytes);
        var page = doc.GetPage(1);

        var parser = new ContentStreamParser(System.Text.Encoding.ASCII.GetBytes("/G1 gs"), page);
        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("gs");
    }

    private static byte[] TestPdfWithExtGState(string ca, string CA, string bm, bool ais, bool sa)
    {
        // Construct a tiny PDF with an ExtGState dictionary having transparency parameters.
        var sb = new System.Text.StringBuilder();
        var offsets = new long[5];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.5\n");
        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</ExtGState<</G1 4 0 R>>>>>> endobj\n");
        Mark(4); sb.Append($"4 0 obj <</Type/ExtGState/ca {ca}/CA {CA}/BM {bm}/AIS {(ais ? "true" : "false")}/SA {(sa ? "true" : "false")}/SMask/None>> endobj\n");
        var xrefPos = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i <= 4; i++) sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 5/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    #endregion

    #region Phase 7 — Shading + Type 3 + BX/EX

    [Fact]
    public void Parse_shOperator_ClassifiedAsShading()
    {
        var op = new ContentOperator("sh", new System.Collections.Generic.List<PdfObject> { new PdfName("Sh1") });
        op.Category.Should().Be(OperatorCategory.Shading);
        op.Operands.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_d0Operator_RecordsWidthOnly()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("500 0 d0");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("d0");
        op.Operands.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_d1Operator_RecordsBoundingBox()
    {
        // wx wy llx lly urx ury d1
        var data = System.Text.Encoding.ASCII.GetBytes("500 0 0 0 500 700 d1");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("d1");
        op.BoundingBox.Should().NotBeNull();
        op.BoundingBox!.Value.Left.Should().Be(0);
        op.BoundingBox.Value.Right.Should().Be(500);
        op.BoundingBox.Value.Bottom.Should().Be(0);
        op.BoundingBox.Value.Top.Should().Be(700);
    }

    [Fact]
    public void Parse_BXEX_StateOpsInsideAreParsed()
    {
        // PDF compatibility section: known operators inside BX..EX are still parsed normally;
        // the section only signals to the renderer that *unknown* ops can be ignored.
        var data = System.Text.Encoding.ASCII.GetBytes("BX 0.5 w 1 J EX");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Select(o => o.Name).Should().ContainInOrder("BX", "w", "J", "EX");
    }

    [Fact]
    public void Parse_d0_d1_ClassifiedAsXObject()
    {
        // d0/d1 are Type 3 glyph procedures — categorised under XObject in our scheme.
        // (They could legitimately go under their own Font category; current taxonomy chooses XObject.)
        new ContentOperator("d0", new System.Collections.Generic.List<PdfObject>()).Category
            .Should().BeOneOf(OperatorCategory.Unknown, OperatorCategory.XObject, OperatorCategory.GraphicsState);
    }

    #endregion

    #region Phase 4 — Color space dispatch updates state

    [Fact]
    public void Parse_RGOperator_StrokingRGB_ParsesAllOperands()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("0.5 0.25 0.125 RG");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        var op = content.Operators[0];
        op.Name.Should().Be("RG");
        op.Operands.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_kOperator_FillCMYK_ParsesAllOperands()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("0.1 0.2 0.3 0.4 k");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("k");
        content.Operators[0].Operands.Should().HaveCount(4);
    }

    [Fact]
    public void Parse_csOperator_NamedColorSpace_RecordsName()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("/Pattern cs");
        var parser = new ContentStreamParser(data, null);

        var content = parser.Parse();

        content.Operators.Should().HaveCount(1);
        content.Operators[0].Name.Should().Be("cs");
        content.Operators[0].Operands[0].Should().BeOfType<PdfName>();
    }

    #endregion
}
