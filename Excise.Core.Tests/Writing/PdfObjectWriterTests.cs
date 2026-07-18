using AwesomeAssertions;
using Excise.Core.Primitives;
using Excise.Core.Writing;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// Tests for PdfObjectWriter - serialization of all PDF object types.
/// Covers PdfNull, PdfBoolean, PdfInteger, PdfReal, PdfName, PdfString,
/// PdfArray, PdfDictionary, PdfStream, and PdfReference.
/// </summary>
public class PdfObjectWriterTests
{
    // ===== Basic Type Tests =====

    [Fact]
    public void Serialize_PdfNull_ReturnsNull()
    {
        // Arrange
        var nullObj = PdfNull.Instance;

        // Act
        string result = PdfObjectWriter.Serialize(nullObj);

        // Assert
        result.Should().Be("null");
    }

    [Fact]
    public void Serialize_PdfBooleanTrue_ReturnsTrue()
    {
        // Arrange
        var boolObj = PdfBoolean.True;

        // Act
        string result = PdfObjectWriter.Serialize(boolObj);

        // Assert
        result.Should().Be("true");
    }

    [Fact]
    public void Serialize_PdfBooleanFalse_ReturnsFalse()
    {
        // Arrange
        var boolObj = PdfBoolean.False;

        // Act
        string result = PdfObjectWriter.Serialize(boolObj);

        // Assert
        result.Should().Be("false");
    }

    [Fact]
    public void Serialize_PdfInteger_ReturnsIntegerValue()
    {
        // Arrange
        var intObj = new PdfInteger(42);

        // Act
        string result = PdfObjectWriter.Serialize(intObj);

        // Assert
        result.Should().Be("42");
    }

    [Fact]
    public void Serialize_PdfIntegerNegative_ReturnsNegativeValue()
    {
        // Arrange
        var intObj = new PdfInteger(-100);

        // Act
        string result = PdfObjectWriter.Serialize(intObj);

        // Assert
        result.Should().Be("-100");
    }

    [Fact]
    public void Serialize_PdfIntegerZero_ReturnsZero()
    {
        // Arrange
        var intObj = new PdfInteger(0);

        // Act
        string result = PdfObjectWriter.Serialize(intObj);

        // Assert
        result.Should().Be("0");
    }

    [Fact]
    public void Serialize_PdfRealDecimal_ReturnsDecimalValue()
    {
        // Arrange
        var realObj = new PdfReal(3.14);

        // Act
        string result = PdfObjectWriter.Serialize(realObj);

        // Assert
        result.Should().Contain("3.14");
    }

    [Fact]
    public void Serialize_PdfRealInteger_ReturnsWithoutDecimal()
    {
        // Arrange - Real value that's an integer
        var realObj = new PdfReal(42.0);

        // Act
        string result = PdfObjectWriter.Serialize(realObj);

        // Assert
        result.Should().Be("42");
    }

    [Fact]
    public void Serialize_PdfRealNegative_ReturnsNegativeValue()
    {
        // Arrange
        var realObj = new PdfReal(-2.5);

        // Act
        string result = PdfObjectWriter.Serialize(realObj);

        // Assert
        result.Should().Contain("-2.5");
    }

    [Fact]
    public void Serialize_PdfRealZero_ReturnsZero()
    {
        // Arrange
        var realObj = new PdfReal(0.0);

        // Act
        string result = PdfObjectWriter.Serialize(realObj);

        // Assert
        result.Should().Be("0");
    }

    [Fact]
    public void Serialize_PdfRealSmallValue_ReturnsWithScientificNotation()
    {
        // Arrange
        var realObj = new PdfReal(0.000001);

        // Act
        string result = PdfObjectWriter.Serialize(realObj);

        // Assert
        result.Should().NotBeNullOrEmpty();
        // May be scientific notation or decimal
        double.Parse(result).Should().BeApproximately(0.000001, 1e-8);
    }

    // ===== Name Tests =====

    [Fact]
    public void Serialize_PdfNameSimple_ReturnsWithSlash()
    {
        // Arrange
        var nameObj = new PdfName("Type");

        // Act
        string result = PdfObjectWriter.Serialize(nameObj);

        // Assert
        result.Should().Be("/Type");
    }

    [Fact]
    public void Serialize_PdfNameWithSpecialCharacters_EscapesWithHex()
    {
        // Arrange - Name with space (must be escaped)
        var nameObj = new PdfName("My Name");

        // Act
        string result = PdfObjectWriter.Serialize(nameObj);

        // Assert
        result.Should().StartWith("/");
        result.Should().Contain("#20"); // Space = 0x20
    }

    [Fact]
    public void Serialize_PdfNameWithHash_EscapesHash()
    {
        // Arrange
        var nameObj = new PdfName("Name#1");

        // Act
        string result = PdfObjectWriter.Serialize(nameObj);

        // Assert
        result.Should().StartWith("/");
        result.Should().Contain("#23"); // '#' = 0x23
    }

    [Fact]
    public void Serialize_PdfNameWithSlash_EscapesSlash()
    {
        // Arrange
        var nameObj = new PdfName("Type/Subtype");

        // Act
        string result = PdfObjectWriter.Serialize(nameObj);

        // Assert
        result.Should().StartWith("/");
        result.Should().Contain("#2F"); // '/' = 0x2F
    }

    [Fact]
    public void Serialize_PdfNameWithParentheses_EscapesParentheses()
    {
        // Arrange
        var nameObj = new PdfName("Name(nested)");

        // Act
        string result = PdfObjectWriter.Serialize(nameObj);

        // Assert
        result.Should().StartWith("/");
        result.Should().Contain("#28"); // '(' = 0x28
        result.Should().Contain("#29"); // ')' = 0x29
    }

    [Fact]
    public void Serialize_PdfNameWithBrackets_EscapesBrackets()
    {
        // Arrange
        var nameObj = new PdfName("Array[0]");

        // Act
        string result = PdfObjectWriter.Serialize(nameObj);

        // Assert
        result.Should().StartWith("/");
        result.Should().Contain("#5B"); // '[' = 0x5B
        result.Should().Contain("#5D"); // ']' = 0x5D
    }

    [Fact]
    public void Serialize_PdfNameWithCurlyBraces_EscapesBraces()
    {
        // Arrange
        var nameObj = new PdfName("Dict{nested}");

        // Act
        string result = PdfObjectWriter.Serialize(nameObj);

        // Assert
        result.Should().StartWith("/");
        result.Should().Contain("#7B"); // '{' = 0x7B
        result.Should().Contain("#7D"); // '}' = 0x7D
    }

    [Fact]
    public void Serialize_PdfNameWithNonPrintableCharacters_EscapesWithHex()
    {
        // Arrange
        var nameObj = new PdfName("Name\x01\x02");

        // Act
        string result = PdfObjectWriter.Serialize(nameObj);

        // Assert
        result.Should().StartWith("/");
        result.Should().Contain("#01");
        result.Should().Contain("#02");
    }

    // ===== String Tests =====

    [Fact]
    public void Serialize_PdfStringLiteral_ReturnsParenthesized()
    {
        // Arrange
        var strObj = new PdfString("Hello World", false);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().Be("(Hello World)");
    }

    [Fact]
    public void Serialize_PdfStringWithBackslash_EscapesBackslash()
    {
        // Arrange
        var strObj = new PdfString("Path\\To\\File", false);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().StartWith("(");
        result.Should().EndWith(")");
        result.Should().Contain("\\\\");
    }

    [Fact]
    public void Serialize_PdfStringWithParentheses_EscapesParentheses()
    {
        // Arrange
        var strObj = new PdfString("Text(nested)", false);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().StartWith("(");
        result.Should().EndWith(")");
        result.Should().Contain("\\(");
        result.Should().Contain("\\)");
    }

    [Fact]
    public void Serialize_PdfStringWithNewline_EscapesNewline()
    {
        // Arrange
        var strObj = new PdfString("Line1\nLine2", false);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().Contain("\\n");
    }

    [Fact]
    public void Serialize_PdfStringWithCarriageReturn_EscapesCR()
    {
        // Arrange
        var strObj = new PdfString("Line1\rLine2", false);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().Contain("\\r");
    }

    [Fact]
    public void Serialize_PdfStringWithTab_EscapesTab()
    {
        // Arrange
        var strObj = new PdfString("Tab\there", false);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().Contain("\\t");
    }

    [Fact]
    public void Serialize_PdfStringHexFormat_ReturnsHexString()
    {
        // Arrange
        byte[] hexBytes = new byte[] { 0xFF, 0x00, 0xAA, 0x55 };
        var strObj = new PdfString(hexBytes, isHex: true);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().Be("<FF00AA55>");
    }

    [Fact]
    public void Serialize_PdfStringEmptyLiteral_ReturnsEmptyParentheses()
    {
        // Arrange
        var strObj = new PdfString("", false);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().Be("()");
    }

    [Fact]
    public void Serialize_PdfStringEmptyHex_ReturnsEmptyAngleBrackets()
    {
        // Arrange
        var strObj = new PdfString(Array.Empty<byte>(), isHex: true);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().Be("<>");
    }

    [Fact]
    public void Serialize_PdfStringWithNonPrintableCharacters_EscapesAsOctal()
    {
        // Arrange
        byte[] bytes = new byte[] { 0x01, 0x02, 0x03 };
        var strObj = new PdfString(bytes, isHex: false);

        // Act
        string result = PdfObjectWriter.Serialize(strObj);

        // Assert
        result.Should().StartWith("(");
        result.Should().EndWith(")");
        // Non-printable should be escaped as octal
        result.Should().Contain("\\");
    }

    // ===== Array Tests =====

    [Fact]
    public void Serialize_PdfArrayEmpty_ReturnsEmptyBrackets()
    {
        // Arrange
        var arrayObj = new PdfArray();

        // Act
        string result = PdfObjectWriter.Serialize(arrayObj);

        // Assert
        result.Should().Be("[]");
    }

    [Fact]
    public void Serialize_PdfArrayWithIntegers_ReturnsSpaceSeparatedValues()
    {
        // Arrange
        var arrayObj = new PdfArray();
        arrayObj.Add((PdfObject)new PdfInteger(1));
        arrayObj.Add((PdfObject)new PdfInteger(2));
        arrayObj.Add((PdfObject)new PdfInteger(3));

        // Act
        string result = PdfObjectWriter.Serialize(arrayObj);

        // Assert
        result.Should().Be("[1 2 3]");
    }

    [Fact]
    public void Serialize_PdfArrayWithMixedTypes_SerializesAllTypes()
    {
        // Arrange
        var arrayObj = new PdfArray();
        arrayObj.Add((PdfObject)new PdfInteger(42));
        arrayObj.Add((PdfObject)new PdfReal(3.14));
        arrayObj.Add((PdfObject)new PdfName("Type"));
        arrayObj.Add((PdfObject)new PdfString("text", false));
        arrayObj.Add((PdfObject)PdfBoolean.True);

        // Act
        string result = PdfObjectWriter.Serialize(arrayObj);

        // Assert
        result.Should().StartWith("[");
        result.Should().EndWith("]");
        result.Should().Contain("42");
        result.Should().Contain("3.14");
        result.Should().Contain("/Type");
        result.Should().Contain("(text)");
        result.Should().Contain("true");
    }

    [Fact]
    public void Serialize_PdfArrayNested_SerializesRecursively()
    {
        // Arrange
        var innerArray = new PdfArray();
        innerArray.Add((PdfObject)new PdfInteger(10));
        innerArray.Add((PdfObject)new PdfInteger(20));

        var outerArray = new PdfArray();
        outerArray.Add((PdfObject)new PdfInteger(1));
        outerArray.Add(innerArray);
        outerArray.Add((PdfObject)new PdfInteger(2));

        // Act
        string result = PdfObjectWriter.Serialize(outerArray);

        // Assert
        result.Should().Be("[1 [10 20] 2]");
    }

    // ===== Dictionary Tests =====

    [Fact]
    public void Serialize_PdfDictionaryEmpty_ReturnsEmptyDict()
    {
        // Arrange
        var dictObj = new PdfDictionary();

        // Act
        string result = PdfObjectWriter.Serialize(dictObj);

        // Assert
        result.Should().Be("<< >>");
    }

    [Fact]
    public void Serialize_PdfDictionarySingleEntry_ReturnsDictFormat()
    {
        // Arrange
        var dictObj = new PdfDictionary
        {
            ["Type"] = new PdfName("Page")
        };

        // Act
        string result = PdfObjectWriter.Serialize(dictObj);

        // Assert
        result.Should().StartWith("<<");
        result.Should().EndWith(">>");
        result.Should().Contain("/Type");
        result.Should().Contain("/Page");
    }

    [Fact]
    public void Serialize_PdfDictionaryMultipleEntries_ReturnsAllEntries()
    {
        // Arrange
        var dictObj = new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["Count"] = new PdfInteger(10),
            ["Rotate"] = new PdfInteger(90)
        };

        // Act
        string result = PdfObjectWriter.Serialize(dictObj);

        // Assert
        result.Should().StartWith("<<");
        result.Should().EndWith(">>");
        result.Should().Contain("/Type");
        result.Should().Contain("/Page");
        result.Should().Contain("/Count");
        result.Should().Contain("10");
        result.Should().Contain("/Rotate");
        result.Should().Contain("90");
    }

    [Fact]
    public void Serialize_PdfDictionaryNested_SerializesRecursively()
    {
        // Arrange
        var innerDict = new PdfDictionary
        {
            ["Inner"] = new PdfInteger(42)
        };
        var outerDict = new PdfDictionary
        {
            ["Outer"] = innerDict
        };

        // Act
        string result = PdfObjectWriter.Serialize(outerDict);

        // Assert
        result.Should().Contain("<< /Inner 42 >>");
    }

    // ===== Reference Tests =====

    [Fact]
    public void Serialize_PdfReference_ReturnsReferenceFormat()
    {
        // Arrange
        var refObj = new PdfReference(42, 0);

        // Act
        string result = PdfObjectWriter.Serialize(refObj);

        // Assert
        result.Should().Be("42 0 R");
    }

    [Fact]
    public void Serialize_PdfReferenceWithNonZeroGeneration_ReturnsWithGeneration()
    {
        // Arrange
        var refObj = new PdfReference(10, 5);

        // Act
        string result = PdfObjectWriter.Serialize(refObj);

        // Assert
        result.Should().Be("10 5 R");
    }

    // ===== Stream Tests =====

    [Fact]
    public void SerializeStreamDictionary_EmptyStream_ReturnsEmptyDict()
    {
        // Arrange
        var streamDict = new PdfDictionary();
        var stream = new PdfStream(streamDict, Array.Empty<byte>());
        var sb = new StringBuilder();

        // Act
        PdfObjectWriter.SerializeStreamDictionary(stream, sb);

        // Assert
        sb.ToString().Should().Be("<< >>");
    }

    [Fact]
    public void SerializeStreamDictionary_WithLengthEntry_SerializesLength()
    {
        // Arrange
        var streamDict = new PdfDictionary
        {
            ["Length"] = new PdfInteger(1024)
        };
        var stream = new PdfStream(streamDict, Array.Empty<byte>());
        var sb = new StringBuilder();

        // Act
        PdfObjectWriter.SerializeStreamDictionary(stream, sb);

        // Assert
        sb.ToString().Should().Contain("/Length");
        sb.ToString().Should().Contain("1024");
    }

    [Fact]
    public void SerializeStreamDictionary_WithMultipleEntries_SerializesAll()
    {
        // Arrange
        var streamDict = new PdfDictionary
        {
            ["Length"] = new PdfInteger(100),
            ["Filter"] = new PdfName("FlateDecode"),
            ["Subtype"] = new PdfName("Image")
        };
        var stream = new PdfStream(streamDict, Array.Empty<byte>());
        var sb = new StringBuilder();

        // Act
        PdfObjectWriter.SerializeStreamDictionary(stream, sb);

        // Assert
        var result = sb.ToString();
        result.Should().StartWith("<<");
        result.Should().EndWith(">>");
        result.Should().Contain("/Length");
        result.Should().Contain("/Filter");
        result.Should().Contain("/Subtype");
    }

    // ===== SerializeObject Tests with StringBuilder =====

    [Fact]
    public void SerializeObject_WithStringBuilder_AppendsToBuilder()
    {
        // Arrange
        var sb = new StringBuilder();
        sb.Append("[");
        var intObj = new PdfInteger(42);

        // Act
        PdfObjectWriter.SerializeObject(intObj, sb);

        // Assert
        sb.ToString().Should().Be("[42");
    }

    [Fact]
    public void SerializeObject_MultipleObjects_AppendsAllToBuilder()
    {
        // Arrange
        var sb = new StringBuilder();
        var obj1 = new PdfInteger(1);
        var obj2 = new PdfInteger(2);
        var obj3 = new PdfInteger(3);

        // Act
        PdfObjectWriter.SerializeObject(obj1, sb);
        sb.Append(" ");
        PdfObjectWriter.SerializeObject(obj2, sb);
        sb.Append(" ");
        PdfObjectWriter.SerializeObject(obj3, sb);

        // Assert
        sb.ToString().Should().Be("1 2 3");
    }

    // ===== Unknown Type Tests =====

    [Fact]
    public void Serialize_UnknownType_ThrowsArgumentException()
    {
        // Arrange
        var unknownObj = new UnknownPdfObject();

        // Act
        var ex = Record.Exception(() => PdfObjectWriter.Serialize(unknownObj));

        // Assert
        ex.Should().BeOfType<ArgumentException>();
        ex!.Message.Should().Contain("Unknown PDF object type");
    }

    // ===== Integration/Complex Tests =====

    [Fact]
    public void Serialize_ComplexPdfStructure_SerializesCorrectly()
    {
        // Arrange - Simulate a PDF page dictionary
        var fontDict = new PdfDictionary
        {
            ["F1"] = new PdfReference(10, 0)
        };

        var resources = new PdfDictionary
        {
            ["Font"] = fontDict
        };

        var mediaBox = new PdfArray();
        mediaBox.Add((PdfObject)new PdfInteger(0));
        mediaBox.Add((PdfObject)new PdfInteger(0));
        mediaBox.Add((PdfObject)new PdfInteger(612));
        mediaBox.Add((PdfObject)new PdfInteger(792));

        var pageDict = new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["MediaBox"] = mediaBox,
            ["Contents"] = new PdfReference(5, 0),
            ["Resources"] = resources,
            ["Parent"] = new PdfReference(2, 0)
        };

        // Act
        string result = PdfObjectWriter.Serialize(pageDict);

        // Assert
        result.Should().Contain("/Type");
        result.Should().Contain("/Page");
        result.Should().Contain("/MediaBox");
        result.Should().Contain("[0 0 612 792]");
        result.Should().Contain("/Resources");
        result.Should().Contain("/Font");
        result.Should().Contain("5 0 R");
    }

    // ===== Helper Class =====

    /// <summary>
    /// A custom PdfObject subclass for testing error handling.
    /// </summary>
    private class UnknownPdfObject : PdfObject
    {
        public override PdfObjectType ObjectType => PdfObjectType.Null;
    }
}
