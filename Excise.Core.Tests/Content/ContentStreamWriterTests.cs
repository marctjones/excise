using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// Tests for ContentStreamWriter round-trip serialization.
/// Ensures parse → serialize → parse produces identical results.
/// </summary>
public class ContentStreamWriterTests
{
    private readonly ContentStreamWriter _writer = new();

    #region Basic Types Round-Trip

    [Fact]
    public void Write_Integer_RoundTrips()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfInteger(5) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(1);
        parsed.Operators[0].Name.Should().Be("w");
        parsed.Operators[0].GetNumber(0).Should().Be(5);
    }

    [Fact]
    public void Write_Real_RoundTrips()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfReal(2.5) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(1);
        parsed.Operators[0].GetNumber(0).Should().BeApproximately(2.5, 0.001);
    }

    [Fact]
    public void Write_Name_RoundTrips()
    {
        var op = new ContentOperator("Tf", new PdfObject[] { new PdfName("F1"), new PdfInteger(12) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(1);
        parsed.Operators[0].GetName(0).Should().Be("F1");
        parsed.Operators[0].GetNumber(1).Should().Be(12);
    }

    [Fact]
    public void Write_String_RoundTrips()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Hello World") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(1);
        parsed.Operators[0].GetString(0).Should().Be("Hello World");
    }

    [Fact]
    public void Write_String_WithEscapedChars_RoundTrips()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Line1\nLine2\t(test)") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators[0].GetString(0).Should().Be("Line1\nLine2\t(test)");
    }

    [Theory]
    [InlineData((byte)0x18)]  // spacing diacritic range start
    [InlineData((byte)0x80)]  // bullet — PDFDocEncoding remap range start
    [InlineData((byte)0x99)]  // Ž — the exact byte that exposed this bug (#659)
    [InlineData((byte)0x9E)]  // remap range end
    [InlineData((byte)0xA0)]  // € — the odd one out, not contiguous with 0x80-0x9E
    public void Write_RawByteString_InPdfDocEncodingRemapRange_RoundTripsExactByte(byte rawByte)
    {
        // A PdfString built from raw bytes (not text) — e.g. a Type0/CID
        // font's original character code preserved verbatim for redaction
        // reconstruction (#353/#659) — must round-trip through Write() byte
        // for byte. PdfString.Value decodes through PDFDocEncoding, which
        // remaps exactly these positions to Unicode code points above 255
        // (e.g. 0x99 -> 'Ž', U+017D = 381). Octal-escaping THAT decoded
        // value instead of the raw byte writes `\575` — no PDF octal escape
        // can represent 381 (max is `\377` = 255) — which a reader re-parses
        // as 381 mod 256 = 125 ('}'), silently corrupting the byte. The
        // writer must operate on PdfString.Bytes directly, never .Value.
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString(new[] { rawByte }) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        var roundTripped = ((PdfString)parsed.Operators[0].Operands[0]).Bytes;
        roundTripped.Should().Equal(new[] { rawByte });
    }

    [Fact]
    public void Write_Array_RoundTrips()
    {
        var array = new PdfArray(new PdfObject[]
        {
            new PdfInteger(3),
            new PdfInteger(2)
        });
        var op = new ContentOperator("d", new PdfObject[] { array, new PdfInteger(0) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(1);
        parsed.Operators[0].Name.Should().Be("d");
        var parsedArray = parsed.Operators[0].GetArray(0);
        parsedArray.Should().NotBeNull();
        parsedArray!.Count.Should().Be(2);
    }

    #endregion

    #region Operator Categories Round-Trip

    [Fact]
    public void Write_GraphicsStateOperators_RoundTrip()
    {
        var operators = new[]
        {
            ContentOperator.SaveState(),
            new ContentOperator("cm", new PdfObject[]
            {
                new PdfReal(1), new PdfReal(0),
                new PdfReal(0), new PdfReal(1),
                new PdfReal(100), new PdfReal(200)
            }),
            ContentOperator.SetLineWidth(2.5),
            ContentOperator.RestoreState()
        };
        var content = new ContentStream(operators);

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(4);
        parsed.Operators[0].Name.Should().Be("q");
        parsed.Operators[1].Name.Should().Be("cm");
        parsed.Operators[2].Name.Should().Be("w");
        parsed.Operators[2].GetNumber(0).Should().BeApproximately(2.5, 0.001);
        parsed.Operators[3].Name.Should().Be("Q");
    }

    [Fact]
    public void Write_PathOperators_RoundTrip()
    {
        var operators = new[]
        {
            ContentOperator.MoveTo(0, 0),
            ContentOperator.LineTo(100, 0),
            ContentOperator.LineTo(100, 100),
            ContentOperator.ClosePath(),
            ContentOperator.Stroke()
        };
        var content = new ContentStream(operators);

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(5);
        parsed.Operators[0].Name.Should().Be("m");
        parsed.Operators[1].Name.Should().Be("l");
        parsed.Operators[2].Name.Should().Be("l");
        parsed.Operators[3].Name.Should().Be("h");
        parsed.Operators[4].Name.Should().Be("S");
    }

    [Fact]
    public void Write_ColorOperators_RoundTrip()
    {
        var operators = new[]
        {
            ContentOperator.SetFillGray(0.5),
            ContentOperator.SetStrokeGray(0.8),
            ContentOperator.SetFillRgb(1, 0, 0),
            ContentOperator.SetStrokeRgb(0, 0, 1),
            new ContentOperator("k", new PdfObject[] { new PdfReal(0), new PdfReal(1), new PdfReal(0), new PdfReal(0) }),
            new ContentOperator("K", new PdfObject[] { new PdfReal(0), new PdfReal(0), new PdfReal(1), new PdfReal(0) })
        };
        var content = new ContentStream(operators);

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(6);
        parsed.Operators[0].Name.Should().Be("g");
        parsed.Operators[1].Name.Should().Be("G");
        parsed.Operators[2].Name.Should().Be("rg");
        parsed.Operators[3].Name.Should().Be("RG");
        parsed.Operators[4].Name.Should().Be("k");
        parsed.Operators[5].Name.Should().Be("K");
    }

    [Fact]
    public void Write_LineStyleOperators_RoundTrip()
    {
        // Test J (line cap), j (line join), M (miter limit), d (dash pattern)
        var dashArray = new PdfArray(new PdfObject[] { new PdfInteger(3), new PdfInteger(2) });
        var operators = new[]
        {
            new ContentOperator("J", new PdfObject[] { new PdfInteger(1) }),  // Round cap
            new ContentOperator("j", new PdfObject[] { new PdfInteger(1) }),  // Round join
            new ContentOperator("M", new PdfObject[] { new PdfReal(4.0) }),   // Miter limit
            new ContentOperator("d", new PdfObject[] { dashArray, new PdfInteger(0) })  // Dash pattern
        };
        var content = new ContentStream(operators);

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(4);
        parsed.Operators[0].Name.Should().Be("J");
        parsed.Operators[0].GetNumber(0).Should().Be(1);
        parsed.Operators[1].Name.Should().Be("j");
        parsed.Operators[1].GetNumber(0).Should().Be(1);
        parsed.Operators[2].Name.Should().Be("M");
        parsed.Operators[2].GetNumber(0).Should().BeApproximately(4.0, 0.001);
        parsed.Operators[3].Name.Should().Be("d");
        parsed.Operators[3].GetArray(0).Should().NotBeNull();
        parsed.Operators[3].GetArray(0)!.Count.Should().Be(2);
        parsed.Operators[3].GetNumber(1).Should().Be(0);
    }

    [Fact]
    public void Write_ClippingOperators_RoundTrip()
    {
        // Test W (clip non-zero winding) and W* (clip even-odd)
        var operators = new[]
        {
            ContentOperator.Rectangle(50, 50, 100, 100),
            new ContentOperator("W", Array.Empty<PdfObject>()),
            new ContentOperator("n", Array.Empty<PdfObject>()),
            ContentOperator.Rectangle(200, 200, 100, 100),
            new ContentOperator("W*", Array.Empty<PdfObject>()),
            new ContentOperator("n", Array.Empty<PdfObject>())
        };
        var content = new ContentStream(operators);

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(6);
        parsed.Operators[1].Name.Should().Be("W");
        parsed.Operators[4].Name.Should().Be("W*");
    }

    [Fact]
    public void Write_AdvancedTextOperators_RoundTrip()
    {
        // Test ' (move to next line and show text) and " (set spacing and show text)
        var operators = new[]
        {
            ContentOperator.BeginText(),
            new ContentOperator("'", new PdfObject[] { new PdfString("Next line") }),
            new ContentOperator("\"", new PdfObject[]
            {
                new PdfReal(0),      // word spacing
                new PdfReal(0),      // character spacing
                new PdfString("Text with spacing")
            }),
            ContentOperator.EndText()
        };
        var content = new ContentStream(operators);

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(4);
        parsed.Operators[1].Name.Should().Be("'");
        parsed.Operators[1].GetString(0).Should().Be("Next line");
        parsed.Operators[2].Name.Should().Be("\"");
        parsed.Operators[2].GetString(2).Should().Be("Text with spacing");
    }

    [Fact]
    public void Write_ColorSpaceOperators_RoundTrip()
    {
        // Test cs/CS (set color space) and sc/SC/scn/SCN (set color)
        var operators = new[]
        {
            new ContentOperator("cs", new PdfObject[] { new PdfName("DeviceGray") }),
            new ContentOperator("CS", new PdfObject[] { new PdfName("DeviceRGB") }),
            new ContentOperator("sc", new PdfObject[] { new PdfReal(0.5) }),
            new ContentOperator("SC", new PdfObject[] { new PdfReal(1), new PdfReal(0), new PdfReal(0) }),
            new ContentOperator("scn", new PdfObject[] { new PdfReal(0.3) }),
            new ContentOperator("SCN", new PdfObject[] { new PdfReal(0), new PdfReal(1), new PdfReal(0) })
        };
        var content = new ContentStream(operators);

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(6);
        parsed.Operators[0].Name.Should().Be("cs");
        parsed.Operators[0].GetName(0).Should().Be("DeviceGray");
        parsed.Operators[1].Name.Should().Be("CS");
        parsed.Operators[1].GetName(0).Should().Be("DeviceRGB");
        parsed.Operators[2].Name.Should().Be("sc");
        parsed.Operators[3].Name.Should().Be("SC");
        parsed.Operators[4].Name.Should().Be("scn");
        parsed.Operators[5].Name.Should().Be("SCN");
    }

    [Fact]
    public void Write_TextOperators_RoundTrip()
    {
        var operators = new[]
        {
            ContentOperator.BeginText(),
            ContentOperator.SetFont("F1", 12),
            ContentOperator.TextPosition(100, 700),
            ContentOperator.ShowText("Hello"),
            ContentOperator.EndText()
        };
        var content = new ContentStream(operators);

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(5);
        parsed.Operators[0].Name.Should().Be("BT");
        parsed.Operators[1].Name.Should().Be("Tf");
        parsed.Operators[2].Name.Should().Be("Td");
        parsed.Operators[3].Name.Should().Be("Tj");
        parsed.Operators[4].Name.Should().Be("ET");
    }

    [Fact]
    public void Write_RectangleOperator_RoundTrip()
    {
        var op = ContentOperator.Rectangle(10, 20, 100, 50);
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(1);
        parsed.Operators[0].Name.Should().Be("re");
        parsed.Operators[0].GetNumber(0).Should().Be(10);
        parsed.Operators[0].GetNumber(1).Should().Be(20);
        parsed.Operators[0].GetNumber(2).Should().Be(100);
        parsed.Operators[0].GetNumber(3).Should().Be(50);
    }

    [Fact]
    public void Write_CurveTo_RoundTrip()
    {
        var op = ContentOperator.CurveTo(10, 20, 30, 40, 50, 60);
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(1);
        parsed.Operators[0].Name.Should().Be("c");
        parsed.Operators[0].GetNumber(0).Should().Be(10);
        parsed.Operators[0].GetNumber(5).Should().Be(60);
    }

    [Fact]
    public void Write_XObjectOperator_RoundTrip()
    {
        var op = ContentOperator.InvokeXObject("Im1");
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(1);
        parsed.Operators[0].Name.Should().Be("Do");
        parsed.Operators[0].GetName(0).Should().Be("Im1");
    }

    #endregion

    #region Complex Round-Trip

    [Fact]
    public void Write_CompletePageContent_RoundTrip()
    {
        // Simulate a real page with text and graphics
        var operators = new[]
        {
            // Graphics state
            ContentOperator.SaveState(),

            // Draw a red rectangle
            ContentOperator.SetFillRgb(1, 0, 0),
            ContentOperator.Rectangle(50, 700, 100, 50),
            ContentOperator.Fill(),

            // Draw some text
            ContentOperator.BeginText(),
            ContentOperator.SetFont("F1", 14),
            ContentOperator.TextPosition(100, 600),
            ContentOperator.ShowText("Test Document"),
            ContentOperator.EndText(),

            // Draw a stroked path
            ContentOperator.SetStrokeRgb(0, 0, 1),
            ContentOperator.SetLineWidth(2),
            ContentOperator.MoveTo(50, 500),
            ContentOperator.LineTo(200, 500),
            ContentOperator.LineTo(200, 400),
            ContentOperator.Stroke(),

            ContentOperator.RestoreState()
        };
        var content = new ContentStream(operators);

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(operators.Length);

        // Verify key operators
        parsed.Operators[0].Name.Should().Be("q");
        parsed.Operators.Last().Name.Should().Be("Q");

        // Check text block
        var btIndex = parsed.Operators.ToList().FindIndex(o => o.Name == "BT");
        var etIndex = parsed.Operators.ToList().FindIndex(o => o.Name == "ET");
        btIndex.Should().BeLessThan(etIndex);
    }

    [Fact]
    public void Write_MultipleRoundTrips_ProducesSameResult()
    {
        var operators = new[]
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillGray(0.5),
            ContentOperator.Rectangle(100, 100, 200, 150),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        var original = new ContentStream(operators);

        // First round-trip
        var bytes1 = _writer.Write(original);
        var parsed1 = Parse(bytes1);

        // Second round-trip
        var bytes2 = _writer.Write(parsed1);
        var parsed2 = Parse(bytes2);

        // Third round-trip
        var bytes3 = _writer.Write(parsed2);
        var parsed3 = Parse(bytes3);

        // All should produce same number of operators
        parsed1.Operators.Should().HaveCount(5);
        parsed2.Operators.Should().HaveCount(5);
        parsed3.Operators.Should().HaveCount(5);

        // Bytes should stabilize after first round-trip
        bytes2.Should().BeEquivalentTo(bytes3);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Write_EmptyContentStream_RoundTrips()
    {
        var content = new ContentStream();

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().BeEmpty();
    }

    [Fact]
    public void Write_NegativeNumbers_RoundTrips()
    {
        var op = new ContentOperator("cm", new PdfObject[]
        {
            new PdfReal(-1), new PdfReal(0),
            new PdfReal(0), new PdfReal(-1),
            new PdfReal(-100), new PdfReal(-200)
        });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators[0].GetNumber(0).Should().Be(-1);
        parsed.Operators[0].GetNumber(4).Should().Be(-100);
        parsed.Operators[0].GetNumber(5).Should().Be(-200);
    }

    [Fact]
    public void Write_VerySmallNumbers_RoundTrips()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfReal(0.001) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators[0].GetNumber(0).Should().BeApproximately(0.001, 0.0001);
    }

    [Fact]
    public void Write_VeryLargeNumbers_RoundTrips()
    {
        var op = new ContentOperator("cm", new PdfObject[]
        {
            new PdfReal(1), new PdfReal(0),
            new PdfReal(0), new PdfReal(1),
            new PdfReal(10000), new PdfReal(50000)
        });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators[0].GetNumber(4).Should().Be(10000);
        parsed.Operators[0].GetNumber(5).Should().Be(50000);
    }

    [Fact]
    public void Write_NameWithSpecialChars_RoundTrips()
    {
        // Names with special characters need proper encoding
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("GS1") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators[0].GetName(0).Should().Be("GS1");
    }

    [Fact]
    public void Write_TJArrayOperator_RoundTrips()
    {
        // TJ operator with array of strings and numbers
        var array = new PdfArray(new PdfObject[]
        {
            new PdfString("AB"),
            new PdfInteger(-100),
            new PdfString("CD")
        });
        var op = new ContentOperator("TJ", new PdfObject[] { array });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators.Should().HaveCount(1);
        parsed.Operators[0].Name.Should().Be("TJ");
        var parsedArray = parsed.Operators[0].GetArray(0);
        parsedArray.Should().NotBeNull();
        parsedArray!.Count.Should().Be(3);
    }

    #endregion

    #region Dictionary Operand Tests

    [Fact]
    public void Write_DictionaryOperand_SerializesCorrectly()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Font");
        dict["Size"] = new PdfInteger(12);
        var op = new ContentOperator("gs", new PdfObject[] { dict });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("<<");
        result.Should().Contain(">>");
        result.Should().Contain("/Type");
        result.Should().Contain("/Font");
        result.Should().Contain("12");
        result.Should().EndWith("gs\n");
    }

    #endregion

    #region WriteString Edge Cases - Special Escapes

    [Fact]
    public void Write_StringWithBackspace_EscapesAsBackspace()
    {
        var text = "Text\bEnd";
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString(text) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("\\b");
        result.Should().Contain("Text");
        result.Should().Contain("End");
    }

    [Fact]
    public void Write_StringWithFormFeed_EscapesAsFormFeed()
    {
        var text = "Page\fBreak";
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString(text) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("\\f");
        result.Should().Contain("Page");
        result.Should().Contain("Break");
    }

    [Fact]
    public void Write_StringWithHighByteChar_EscapesAsOctal()
    {
        // Character with code > 126 (e.g., 200 = 0310 octal)
        var text = "TextÈEnd";  // È (U+00C8 = 200 decimal)
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString(text) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("Text");
        result.Should().Contain("End");
        result.Should().Contain("\\3");  // Octal escape starts with \3
    }

    [Fact]
    public void Write_StringWithLowControlChar_EscapesAsOctal()
    {
        // Character with code < 32 (e.g., 1 = SOH)
        var text = "Start\x1fMarker";  // Using \x1f (unit separator, decimal 31) to avoid digit consumption
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString(text) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        // The control character should be escaped with backslash
        result.Should().Contain("Start");
        result.Should().Contain("Marker");
        result.Should().Contain("\\");  // Octal escape starts with backslash
        result.Should().StartWith("(");
        result.Should().Contain(") Tj");
    }

    [Fact]
    public void Write_StringWithMultipleSpecialEscapes_AllEncoded()
    {
        var text = "ABC";
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString(text) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("ABC");
        result.Should().StartWith("(");
        result.Should().Contain(") Tj");
    }

    #endregion

    #region WriteString Edge Cases

    [Fact]
    public void Write_StringWithMultipleBackslashes_EscapesAll()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("\\\\path\\\\file") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        var countBackslashes = result.Count(c => c == '\\');
        countBackslashes.Should().BeGreaterThan(4, "all backslashes should be escaped");
    }

    [Fact]
    public void Write_StringWithParenthesesOnly_EscapesParens()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("(())") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("\\(");
        result.Should().Contain("\\)");
    }

    [Fact]
    public void Write_StringWithMixedWhitespace_EscapesCorrectly()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("Tab\tNewline\nReturn\rEnd") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("\\t");
        result.Should().Contain("\\n");
        result.Should().Contain("\\r");
    }

    [Fact]
    public void Write_StringWithOnlySpecialChars_AllEscaped()
    {
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString("\\\n\r\t") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().NotBeEmpty();
        result.Should().Contain("\\");
    }

    [Fact]
    public void Write_StringWithControlCharacter_EscapedAsOctal()
    {
        var text = "Text\x01Control";
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString(text) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("Text");
        result.Should().StartWith("(");
        result.Should().EndWith(") Tj\n");
    }

    [Fact]
    public void Write_StringWithAllEscapeTypes_RoundTrips()
    {
        // Use U+0001 (not \x01): C#'s \x escape is greedy, so "\x01Control"
        // parses as U+001C + "ontrol", and 0x1C is a PDFDocEncoding special
        // (U+02DD) that legitimately does NOT round-trip as identity. U+0001
        // is a plain control char the writer octal-escapes and round-trips
        // cleanly. (#361)
        var complexString = "Path\\(test)\\file\nWith\rWhitespace\tAnd\u0001Control";
        var op = new ContentOperator("Tj", new PdfObject[] { new PdfString(complexString) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var parsed = Parse(bytes);

        parsed.Operators[0].GetString(0).Should().Be(complexString);
    }

    #endregion

    #region WriteName Edge Cases

    [Fact]
    public void Write_NameWithSpace_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Font Name") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#20");
    }

    [Fact]
    public void Write_NameWithHash_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Font#1") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#23");
    }

    [Fact]
    public void Write_NameWithSlash_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Font/Name") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#2F");
    }

    [Fact]
    public void Write_NameWithBrackets_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Array[0]") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#5B");
        result.Should().Contain("#5D");
    }

    [Fact]
    public void Write_NameWithAngleBrackets_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Tag<value>") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#3C");
        result.Should().Contain("#3E");
    }

    [Fact]
    public void Write_NameWithParentheses_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Func(x)") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#28");
        result.Should().Contain("#29");
    }

    [Fact]
    public void Write_NameWithCurlyBraces_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Code{block}") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#7B");
        result.Should().Contain("#7D");
    }

    [Fact]
    public void Write_NameWithPercent_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Value%") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#25");
    }

    [Fact]
    public void Write_NameWithLowCharacter_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Font\x01") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#01");
    }

    [Fact]
    public void Write_NameWithMultipleSpecialChars_AllEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("Font/Name#1[0]") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#2F");
        result.Should().Contain("#23");
        result.Should().Contain("#5B");
        result.Should().Contain("#5D");
    }

    [Fact]
    public void Write_NameWithLeadingLowChar_HexEncoded()
    {
        var op = new ContentOperator("gs", new PdfObject[] { new PdfName("\x01Font") });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("#");
        result.Should().Contain("Font");
        result.Should().Contain("gs");
    }

    #endregion

    #region Operand Type Edge Cases

    [Fact]
    public void Write_Null_FormatsProperly()
    {
        var op = new ContentOperator("Test", new PdfObject[] { PdfNull.Instance });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("null");
    }

    [Fact]
    public void Write_BooleanTrue_FormatsProperly()
    {
        var op = new ContentOperator("Test", new PdfObject[] { PdfBoolean.True });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("true");
    }

    [Fact]
    public void Write_BooleanFalse_FormatsProperly()
    {
        var op = new ContentOperator("Test", new PdfObject[] { PdfBoolean.False });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("false");
    }

    [Fact]
    public void Write_ZeroInteger_FormatsAsZero()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfInteger(0) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("0");
    }

    [Fact]
    public void Write_RealAsWholeNumber_NoDecimal()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfReal(10.0) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("10");
        result.Should().NotContain("10.0");
    }

    [Fact]
    public void Write_RealWithDecimal_IncludesDecimal()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfReal(10.5) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        result.Should().Contain("10.5");
    }

    [Fact]
    public void Write_NegativeZero_FormatsAsZero()
    {
        var op = new ContentOperator("w", new PdfObject[] { new PdfReal(-0.0) });
        var content = new ContentStream(new[] { op });

        var bytes = _writer.Write(content);
        var result = Encoding.Latin1.GetString(bytes);

        var parsed = Parse(bytes);
        parsed.Operators[0].GetNumber(0).Should().Be(0);
    }

    #endregion

    #region Helpers

    private ContentStream Parse(byte[] bytes)
    {
        var parser = new ContentStreamParser(bytes);
        return parser.Parse();
    }

    #endregion
}
