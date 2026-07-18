using AwesomeAssertions;
using Excise.Core.Primitives;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Primitives;

public class PdfStringTests
{
    [Fact]
    public void Constructor_WithBytes_StoresBytesAsLiteral()
    {
        var bytes = Encoding.ASCII.GetBytes("Hello");

        var str = new PdfString(bytes);

        str.Bytes.Should().Equal(bytes);
        str.IsHex.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithBytes_WithIsHex_MarksAsHex()
    {
        var bytes = Encoding.ASCII.GetBytes("48656C6C6F");

        var str = new PdfString(bytes, isHex: true);

        str.IsHex.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithString_CreatesFromText()
    {
        var str = new PdfString("Hello");

        str.Value.Should().Be("Hello");
        str.IsHex.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithString_WithUnicode_IncludesBOM()
    {
        var str = new PdfString("Hello世界");

        str.Bytes.Should().StartWith(new byte[] { 0xFE, 0xFF });
    }

    [Fact]
    public void Constructor_WithString_WithASCII_UsesAsciiEncoding()
    {
        var str = new PdfString("Hello");

        str.Bytes.Should().Equal(Encoding.ASCII.GetBytes("Hello"));
    }

    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException()
    {
        var action = () => new PdfString((byte[])null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ObjectType_ReturnsString()
    {
        var str = new PdfString("Hello");

        str.ObjectType.Should().Be(PdfObjectType.String);
    }

    [Fact]
    public void Value_Property_ReturnsDecodedString()
    {
        var str = new PdfString("Hello");

        str.Value.Should().Be("Hello");
    }

    [Fact]
    public void Value_WithUnicodeString_ReturnsDecodedCorrectly()
    {
        var str = new PdfString("Café");

        str.Value.Should().Contain("Café");
    }

    [Fact]
    public void Value_WithUnicodeBytes_DecodesFromBOM()
    {
        var text = "Hello";
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var bytes = new byte[2 + utf16.Length];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        Array.Copy(utf16, 0, bytes, 2, utf16.Length);

        var str = new PdfString(bytes);

        str.Value.Should().Contain("H");
    }

    [Fact]
    public void ToLiteralString_WrapsInParentheses()
    {
        var str = new PdfString("Hello");

        var result = str.ToLiteralString();

        result.Should().StartWith("(");
        result.Should().EndWith(")");
    }

    [Fact]
    public void ToLiteralString_EscapesParentheses()
    {
        var str = new PdfString("Hello(World)");

        var result = str.ToLiteralString();

        result.Should().Contain("\\(");
        result.Should().Contain("\\)");
    }

    [Fact]
    public void ToLiteralString_EscapesBackslash()
    {
        var str = new PdfString("Path\\To\\File");

        var result = str.ToLiteralString();

        result.Should().Contain("\\\\");
    }

    [Fact]
    public void ToLiteralString_EscapesNewline()
    {
        var str = new PdfString("Line1\nLine2");

        var result = str.ToLiteralString();

        result.Should().Contain("\\n");
    }

    [Fact]
    public void ToLiteralString_EscapesCarriageReturn()
    {
        var str = new PdfString("Line1\rLine2");

        var result = str.ToLiteralString();

        result.Should().Contain("\\r");
    }

    [Fact]
    public void ToLiteralString_EscapesTab()
    {
        var str = new PdfString("Col1\tCol2");

        var result = str.ToLiteralString();

        result.Should().Contain("\\t");
    }

    [Fact]
    public void ToLiteralString_EscapesFormFeed()
    {
        var str = new PdfString("Page\fBreak");

        var result = str.ToLiteralString();

        result.Should().Contain("\\f");
    }

    [Fact]
    public void ToLiteralString_EscapesBackspace()
    {
        var str = new PdfString("Back\bspace");

        var result = str.ToLiteralString();

        result.Should().Contain("\\b");
    }

    [Fact]
    public void ToHexString_WrapsInAngleBrackets()
    {
        var str = new PdfString("Hi");

        var result = str.ToHexString();

        result.Should().StartWith("<");
        result.Should().EndWith(">");
    }

    [Fact]
    public void ToHexString_EncodesAsHex()
    {
        var str = new PdfString("Hi");

        var result = str.ToHexString();

        result.Should().Contain("48");
        result.Should().Contain("69");
    }

    [Fact]
    public void ToHexString_UsesUppercaseHex()
    {
        var str = new PdfString("A");

        var result = str.ToHexString();

        result.Should().Contain("41");
    }

    [Fact]
    public void ToString_WithIsHexFalse_ReturnsLiteralString()
    {
        var str = new PdfString("Hello", isHex: false);

        var result = str.ToString();

        result.Should().StartWith("(");
    }

    [Fact]
    public void ToString_WithIsHexTrue_ReturnsHexString()
    {
        var str = new PdfString(new byte[] { 0x48, 0x69 }, isHex: true);

        var result = str.ToString();

        result.Should().StartWith("<");
    }

    [Fact]
    public void FromText_CreatesLiteralString()
    {
        var str = PdfString.FromText("Hello");

        str.Value.Should().Be("Hello");
        str.IsHex.Should().BeFalse();
    }

    [Fact]
    public void FromHex_DecodesHexString()
    {
        var str = PdfString.FromHex("48656C6C6F");

        str.Value.Should().Be("Hello");
    }

    [Fact]
    public void FromHex_IgnoresWhitespace()
    {
        var str = PdfString.FromHex("48 65 6C 6C 6F");

        str.Value.Should().Be("Hello");
    }

    [Fact]
    public void FromHex_PadsOddLengthWithZero()
    {
        var str = PdfString.FromHex("48");

        str.Bytes.Should().Equal(0x48);
    }

    [Fact]
    public void FromHex_WithOddLength_AddsTrailingZero()
    {
        var str = PdfString.FromHex("4865");

        str.Bytes.Length.Should().Be(2);
    }

    [Fact]
    public void ImplicitConversion_FromString_CreatesString()
    {
        PdfString str = "Hello";

        str.Value.Should().Be("Hello");
    }

    [Fact]
    public void ImplicitConversion_ToString_ReturnsValue()
    {
        var str = new PdfString("Hello");
        string value = str;

        value.Should().Be("Hello");
    }

    [Fact]
    public void Equals_WithSameBytes_ReturnsTrue()
    {
        var str1 = new PdfString("Hello");
        var str2 = new PdfString("Hello");

        var result = str1.Equals(str2);

        result.Should().BeTrue();
    }

    [Fact]
    public void Equals_WithDifferentBytes_ReturnsFalse()
    {
        var str1 = new PdfString("Hello");
        var str2 = new PdfString("World");

        var result = str1.Equals(str2);

        result.Should().BeFalse();
    }

    [Fact]
    public void Equals_WithNull_ReturnsFalse()
    {
        var str = new PdfString("Hello");

        var result = str.Equals(null as PdfString);

        result.Should().BeFalse();
    }

    [Fact]
    public void ObjectEquals_WithSameBytes_ReturnsTrue()
    {
        var str1 = new PdfString("Hello");
        object str2 = new PdfString("Hello");

        var result = str1.Equals(str2);

        result.Should().BeTrue();
    }

    [Fact]
    public void ObjectEquals_WithDifferentType_ReturnsFalse()
    {
        var str = new PdfString("Hello");
        object other = "Hello";

        var result = str.Equals(other);

        result.Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_WithSameBytes_ReturnsSameHash()
    {
        var str1 = new PdfString("Hello");
        var str2 = new PdfString("Hello");

        str1.GetHashCode().Should().Be(str2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_WithDifferentBytes_ReturnsDifferentHash()
    {
        var str1 = new PdfString("Hello");
        var str2 = new PdfString("World");

        str1.GetHashCode().Should().NotBe(str2.GetHashCode());
    }

    [Fact]
    public void ReplaceBytes_UpdatesBytes()
    {
        var str = new PdfString("Hello");
        var newBytes = Encoding.ASCII.GetBytes("World");

        str.ReplaceBytes(newBytes);

        str.Value.Should().Be("World");
    }

    [Fact]
    public void ReplaceBytes_WithNull_ThrowsArgumentNullException()
    {
        var str = new PdfString("Hello");

        var action = () => str.ReplaceBytes(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EmptyString_DecodesCorrectly()
    {
        var str = new PdfString("");

        str.Value.Should().Be("");
    }

    [Fact]
    public void FromHex_WithEmptyString_CreatesEmptyString()
    {
        var str = PdfString.FromHex("");

        str.Bytes.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithEmptyBytes_CreatesEmptyString()
    {
        var str = new PdfString(Array.Empty<byte>());

        str.Value.Should().Be("");
    }

    [Fact]
    public void BinaryData_IsPreserved()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var str = new PdfString(bytes);

        str.Bytes.Should().Equal(bytes);
    }

    [Fact]
    public void ToLiteralString_WithBinaryData_EncodesNonPrintable()
    {
        var str = new PdfString(new byte[] { 0x00 });

        var result = str.ToLiteralString();

        result.Should().Contain("\\000");
    }

    // ---- PDFDocEncoding (ISO 32000-1 Annex D.3): the 0x80–0x9F / 0x18–0x1F
    // / 0xA0 code points are typographic characters, not the C1 control
    // characters a plain (char)b cast produces. Decoding them wrong showed
    // tofu boxes in outline titles etc. (#361). ----

    [Fact]
    public void PdfDocEncoding_EmDash_DecodesToU2014()
    {
        // "Part I—Fundamentals": byte 0x84 is an em dash, not a C1 control box.
        var str = new PdfString(new byte[]
            { (byte)'P', (byte)'a', (byte)'r', (byte)'t', (byte)' ', (byte)'I', 0x84,
              (byte)'F', (byte)'O', (byte)'S', (byte)'S' });

        str.Value.Should().Be("Part I—FOSS");
        str.Value.Should().NotContain("", "0x84 must not decode to the C1 control char");
    }

    [Fact]
    public void PdfDocEncoding_RightSingleQuote_DecodesToU2019()
    {
        var str = new PdfString(new byte[] { (byte)'D', 0x90, (byte)'t' });

        str.Value.Should().Be("D’t"); // Doesn’t-style apostrophe
    }

    [Theory]
    [InlineData(0x80, '•')] // bullet
    [InlineData(0x85, '–')] // en dash
    [InlineData(0x8D, '“')] // left double quote
    [InlineData(0x8E, '”')] // right double quote
    [InlineData(0x92, '™')] // trademark
    [InlineData(0x96, 'Œ')] // OE ligature
    [InlineData(0xA0, '€')] // euro
    [InlineData(0x1A, 'ˆ')] // modifier circumflex
    public void PdfDocEncoding_SpecialCodePoints_MapToUnicode(int rawByte, char expected)
    {
        var str = new PdfString(new byte[] { (byte)rawByte });

        str.Value.Should().Be(expected.ToString());
    }

    [Fact]
    public void PdfDocEncoding_PlainAsciiAndLatin1_Unchanged()
    {
        // Printable ASCII and printable Latin-1 high range still pass through.
        var str = new PdfString(Encoding.Latin1.GetBytes("Café (déjà) 2024"));

        str.Value.Should().Be("Café (déjà) 2024");
    }

    [Fact]
    public void Utf16BomString_TakesPrecedenceOverPdfDocEncoding()
    {
        // A string with the UTF-16BE BOM must decode as UTF-16, never via the
        // PDFDocEncoding table.
        var str = new PdfString("Hello世界");

        str.Bytes.Should().StartWith(new byte[] { 0xFE, 0xFF });
        str.Value.Should().Be("Hello世界");
    }
}
