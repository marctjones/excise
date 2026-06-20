using AwesomeAssertions;
using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;
using System.Text;
using Xunit;

namespace Pdfe.Core.Tests.Parsing;

public class PdfParserTests
{
    [Fact]
    public void ParseObject_Integer_ReturnsPdfInteger()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("123"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfInteger>();
        ((PdfInteger)obj).Value.Should().Be(123);
    }

    [Fact]
    public void ParseObject_NegativeInteger_ReturnsPdfInteger()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("-45"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfInteger>();
        ((PdfInteger)obj).Value.Should().Be(-45);
    }

    [Fact]
    public void ParseObject_Real_ReturnsPdfReal()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("3.14159"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfReal>();
        ((PdfReal)obj).Value.Should().BeApproximately(3.14159, 0.00001);
    }

    [Fact]
    public void ParseObject_True_ReturnsPdfBooleanTrue()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("true"));

        var obj = parser.ParseObject();

        obj.Should().Be(PdfBoolean.True);
    }

    [Fact]
    public void ParseObject_False_ReturnsPdfBooleanFalse()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("false"));

        var obj = parser.ParseObject();

        obj.Should().Be(PdfBoolean.False);
    }

    [Fact]
    public void ParseObject_Null_ReturnsPdfNull()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("null"));

        var obj = parser.ParseObject();

        obj.Should().Be(PdfNull.Instance);
    }

    [Fact]
    public void ParseObject_Name_ReturnsPdfName()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("/Type"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfName>();
        ((PdfName)obj).Value.Should().Be("Type");
    }

    [Fact]
    public void ParseObject_LiteralString_ReturnsPdfString()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("(Hello World)"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfString>();
        ((PdfString)obj).Value.Should().Be("Hello World");
    }

    [Fact]
    public void ParseObject_HexString_ReturnsPdfString()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("<48656C6C6F>")); // "Hello"

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfString>();
        ((PdfString)obj).Value.Should().Be("Hello");
    }

    [Fact]
    public void ParseObject_Array_ReturnsPdfArray()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("[1 2 3]"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfArray>();
        var arr = (PdfArray)obj;
        arr.Count.Should().Be(3);
        arr.GetInt(0).Should().Be(1);
        arr.GetInt(1).Should().Be(2);
        arr.GetInt(2).Should().Be(3);
    }

    [Fact]
    public void ParseObject_NestedArray_ReturnsPdfArray()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("[[1 2] [3 4]]"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfArray>();
        var arr = (PdfArray)obj;
        arr.Count.Should().Be(2);
        arr[0].Should().BeOfType<PdfArray>();
        arr[1].Should().BeOfType<PdfArray>();
    }

    [Fact]
    public void ParseObject_Dictionary_ReturnsPdfDictionary()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("<< /Type /Page /Count 10 >>"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfDictionary>();
        var dict = (PdfDictionary)obj;
        dict.GetName("Type").Should().Be("Page");
        dict.GetInt("Count").Should().Be(10);
    }

    [Fact]
    public void ParseObject_NestedDictionary_ReturnsPdfDictionary()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("<< /Outer << /Inner /Value >> >>"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfDictionary>();
        var dict = (PdfDictionary)obj;
        var inner = dict.GetDictionary("Outer");
        inner.GetName("Inner").Should().Be("Value");
    }

    [Fact]
    public void ParseObject_Reference_ReturnsPdfReference()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("5 0 R"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfReference>();
        var reference = (PdfReference)obj;
        reference.ObjectNum.Should().Be(5);
        reference.Generation.Should().Be(0);
    }

    [Fact]
    public void ParseObject_MixedArray_ParsesCorrectly()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("[1 3.14 /Name (string) true null]"));

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfArray>();
        var arr = (PdfArray)obj;
        arr.Count.Should().Be(6);
        arr[0].Should().BeOfType<PdfInteger>();
        arr[1].Should().BeOfType<PdfReal>();
        arr[2].Should().BeOfType<PdfName>();
        arr[3].Should().BeOfType<PdfString>();
        arr[4].Should().Be(PdfBoolean.True);
        arr[5].Should().Be(PdfNull.Instance);
    }

    [Fact]
    public void ParseIndirectObject_SimpleObject_ParsesCorrectly()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("5 0 obj\n123\nendobj"));

        var indObj = parser.ParseIndirectObject();

        indObj.ObjectNumber.Should().Be(5);
        indObj.Generation.Should().Be(0);
        indObj.Value.Should().BeOfType<PdfInteger>();
        ((PdfInteger)indObj.Value).Value.Should().Be(123);
    }

    [Fact]
    public void ParseIndirectObject_Dictionary_ParsesCorrectly()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("1 0 obj\n<< /Type /Page >>\nendobj"));

        var indObj = parser.ParseIndirectObject();

        indObj.ObjectNumber.Should().Be(1);
        indObj.Value.Should().BeOfType<PdfDictionary>();
    }

    [Fact]
    public void ParseIndirectObject_Stream_ParsesCorrectly()
    {
        var streamContent = "Hello World";
        var pdfData = $"1 0 obj\n<< /Length {streamContent.Length} >>\nstream\n{streamContent}\nendstream\nendobj";
        using var parser = new PdfParser(Encoding.ASCII.GetBytes(pdfData));

        var indObj = parser.ParseIndirectObject();

        indObj.Value.Should().BeOfType<PdfStream>();
        var stream = (PdfStream)indObj.Value;
        stream.EncodedData.Should().BeEquivalentTo(Encoding.ASCII.GetBytes(streamContent));
    }

    [Fact]
    public void ParseStream_LengthAsIndirectReference_ResolvesViaCallback()
    {
        // PDFs from XEP/LibreOffice and other professional toolchains routinely
        // store stream /Length as an indirect reference (the writer doesn't
        // know the compressed length until it's done). PdfDocument wires up
        // a resolver that looks up the int via the xref/object cache.
        var streamContent = "Hello World";
        var pdfData =
            $"1 0 obj\n<< /Length 2 0 R >>\nstream\n{streamContent}\nendstream\nendobj\n" +
            $"2 0 obj\n{streamContent.Length}\nendobj";
        using var parser = new PdfParser(Encoding.ASCII.GetBytes(pdfData));

        parser.IndirectObjectResolver = num =>
            num == 2 ? new PdfInteger(streamContent.Length) : null;

        var indObj = parser.ParseIndirectObject();

        indObj.Value.Should().BeOfType<PdfStream>();
        ((PdfStream)indObj.Value).EncodedData
            .Should().BeEquivalentTo(Encoding.ASCII.GetBytes(streamContent));
    }

    [Fact]
    public void ParseStream_LengthAsIndirectReference_ThrowsWhenNoResolver()
    {
        var pdfData = "1 0 obj\n<< /Length 2 0 R >>\nstream\nHello\nendstream\nendobj";
        using var parser = new PdfParser(Encoding.ASCII.GetBytes(pdfData));

        // Default — no resolver wired up — surfaces a clear error rather
        // than silently producing a corrupt PdfStream.
        Action act = () => parser.ParseIndirectObject();
        act.Should().Throw<PdfParseException>()
            .WithMessage("*indirect reference but no resolver*");
    }

    [Fact]
    public void PdfParser_ConstructorWithStream()
    {
        var data = Encoding.ASCII.GetBytes("123");
        using var stream = new MemoryStream(data);

        using var parser = new PdfParser(stream);

        var obj = parser.ParseObject();
        ((PdfInteger)obj).Value.Should().Be(123);
    }

    [Fact]
    public void PdfParser_Position_Property()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("123 456"));

        var pos1 = parser.Position;
        parser.ParseObject();
        var pos2 = parser.Position;

        pos1.Should().Be(0);
        pos2.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PdfParser_Seek_MovesToPosition()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("123 456"));

        parser.ParseObject();
        parser.Seek(0);
        var obj = parser.ParseObject();

        ((PdfInteger)obj).Value.Should().Be(123);
    }

    [Fact]
    public void TryParseIndirectObject_ValidObject_ReturnsObject()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("5 0 obj\n123\nendobj"));

        var indObj = parser.TryParseIndirectObject();

        indObj.Should().NotBeNull();
        indObj!.ObjectNumber.Should().Be(5);
        ((PdfInteger)indObj.Value).Value.Should().Be(123);
    }

    [Fact]
    public void TryParseIndirectObject_NoObjectNumber_ReturnsNull()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("true"));

        var indObj = parser.TryParseIndirectObject();

        indObj.Should().BeNull();
    }

    [Fact]
    public void TryParseIndirectObject_OnlyObjectNumber_ReturnsNull()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("5 true"));

        var indObj = parser.TryParseIndirectObject();

        indObj.Should().BeNull();
    }

    [Fact]
    public void TryParseIndirectObject_MissingObjKeyword_ReturnsNull()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("5 0 123"));

        var indObj = parser.TryParseIndirectObject();

        indObj.Should().BeNull();
    }

    [Fact]
    public void TryParseIndirectObject_ResetsPositionOnFailure()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("false 456"));

        var indObj = parser.TryParseIndirectObject();
        var obj = parser.ParseObject();

        indObj.Should().BeNull();
        obj.Should().Be(PdfBoolean.False);
    }

    [Fact]
    public void ParseDictionaryContents_WithMixedValueTypes()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("<< /Int 42 /Real 3.14 /Name /Type /String (test) /Bool true >>"));

        var dict = (PdfDictionary)parser.ParseObject();

        dict.GetInt("Int").Should().Be(42);
        ((PdfReal)dict["Real"]).Value.Should().BeApproximately(3.14, 0.01);
        dict.GetName("Name").Should().Be("Type");
        dict.GetString("String").Should().Be("test");
        dict["Bool"].Should().Be(PdfBoolean.True);
    }

    [Fact]
    public void ParseDictionaryContents_EmptyDictionary()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("<< >>"));

        var dict = (PdfDictionary)parser.ParseObject();

        dict.Count.Should().Be(0);
    }

    [Fact]
    public void ParseDictionaryContents_DuplicateKey_LastValueWins()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("<< /Key 1 /Key 2 >>"));

        var dict = (PdfDictionary)parser.ParseObject();

        dict.GetInt("Key").Should().Be(2);
    }

    [Fact]
    public void ParseArray_EmptyArray()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("[]"));

        var arr = (PdfArray)parser.ParseObject();

        arr.Count.Should().Be(0);
    }

    [Fact]
    public void ParseObjectFromToken_Eof_ThrowsException()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes(""));

        Action act = () => parser.ParseObject();

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Unexpected end of file*");
    }

    [Fact]
    public void ParseObjectFromToken_InvalidKeyword_ThrowsException()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("invalid"));

        Action act = () => parser.ParseObject();

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Unexpected keyword*");
    }

    [Fact]
    public void ParseIndirectObject_InvalidObjectNumber_ThrowsException()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("true 0 obj\n123\nendobj"));

        Action act = () => parser.ParseIndirectObject();

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Expected object number*");
    }

    [Fact]
    public void ParseIndirectObject_InvalidGenerationNumber_ThrowsException()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("5 false obj\n123\nendobj"));

        Action act = () => parser.ParseIndirectObject();

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Expected generation number*");
    }

    [Fact]
    public void ParseIndirectObject_MissingObjKeyword_ThrowsException()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("5 0 notobj\n123\nendobj"));

        Action act = () => parser.ParseIndirectObject();

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Expected 'obj'*");
    }

    [Fact]
    public void ParseIndirectObject_MissingEndobjKeyword_ThrowsException()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("5 0 obj\n123\nnotendobj"));

        Action act = () => parser.ParseIndirectObject();

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Expected 'endobj'*");
    }

    [Fact]
    public void ParseStream_MissingLength_RecoversByEndstreamMarker()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("1 0 obj\n<< >>\nstream\ndata\nendstream\nendobj"));

        var indObj = parser.ParseIndirectObject();

        indObj.Value.Should().BeOfType<PdfStream>();
        var stream = (PdfStream)indObj.Value;
        Encoding.ASCII.GetString(stream.EncodedData).Should().Be("data");
    }

    [Fact]
    public void ParseStream_InvalidLengthType_RecoversByEndstreamMarker()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("1 0 obj\n<< /Length /NotANumber >>\nstream\ndata\nendstream\nendobj"));

        var indObj = parser.ParseIndirectObject();

        indObj.Value.Should().BeOfType<PdfStream>();
        var stream = (PdfStream)indObj.Value;
        Encoding.ASCII.GetString(stream.EncodedData).Should().Be("data");
    }

    [Fact]
    public void ParseStream_ResolvedIndirectLengthNotInteger_RecoversByEndstreamMarker()
    {
        var pdfData = "1 0 obj\n<< /Length 2 0 R >>\nstream\nHello\nendstream\nendobj";
        using var parser = new PdfParser(Encoding.ASCII.GetBytes(pdfData));

        parser.IndirectObjectResolver = num => num == 2 ? new PdfName("NotInt") : null;

        var indObj = parser.ParseIndirectObject();

        indObj.Value.Should().BeOfType<PdfStream>();
        var stream = (PdfStream)indObj.Value;
        Encoding.ASCII.GetString(stream.EncodedData).Should().Be("Hello");
    }

    [Fact]
    public void ParseArray_UntermatedArray_ThrowsException()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("[1 2 3"));

        Action act = () => parser.ParseObject();

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Unterminated array*");
    }

    [Fact]
    public void ParseDictionary_UntermatedDictionary_ThrowsException()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("<< /Key 1"));

        Action act = () => parser.ParseObject();

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Unterminated dictionary*");
    }

    [Fact]
    public void ParseDictionary_NonNameKey_ThrowsException()
    {
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("<< 123 /Value >>"));

        Action act = () => parser.ParseObject();

        act.Should().Throw<PdfParseException>()
            .WithMessage("*Expected name in dictionary*");
    }

    [Fact]
    public void PdfParseException_WithMessage()
    {
        var ex = new PdfParseException("Test error");

        ex.Message.Should().Be("Test error");
    }

    [Fact]
    public void PdfParseException_WithMessageAndInnerException()
    {
        var inner = new InvalidOperationException("Inner error");
        var ex = new PdfParseException("Test error", inner);

        ex.Message.Should().Be("Test error");
        ex.InnerException.Should().Be(inner);
    }

    [Fact]
    public void PdfEncryptionNotSupportedException_DefaultConstructor()
    {
        var ex = new PdfEncryptionNotSupportedException();

        ex.Message.Should().Contain("encrypted");
        ex.Message.Should().Contain("allowEncrypted");
    }

    [Fact]
    public void PdfEncryptionNotSupportedException_WithMessage()
    {
        var ex = new PdfEncryptionNotSupportedException("Custom message");

        ex.Message.Should().Be("Custom message");
    }

    #region Unexpected Token Tests

    [Fact]
    public void ParseObject_UnexpectedTokenType_ThrowsPdfParseException()
    {
        // Test line 111: "Unexpected token" - thrown when ParseObject encounters an unexpected token type
        // Use a token type that would be unexpected in an object context (e.g., DictionaryEnd without matching DictionaryStart)
        using var parser = new PdfParser(Encoding.ASCII.GetBytes(">>"));

        var action = () => parser.ParseObject();

        action.Should().Throw<PdfParseException>()
            .WithMessage("*Unexpected token*");
    }

    [Fact]
    public void ParseObject_ArrayEndWithoutStart_ThrowsParseException()
    {
        // Stray array end token
        using var parser = new PdfParser(Encoding.ASCII.GetBytes("]"));

        var action = () => parser.ParseObject();

        action.Should().Throw<PdfParseException>();
    }

    #endregion

    #region Stream Endstream Recovery Tests

    [Fact]
    public void ParseIndirectObject_EndstreamEmbeddedInKeyword_Recovers()
    {
        // Test lines 256-264: endstream recovery when "endstream" is embedded in keyword
        // The code checks: token.Value.StartsWith("endstream") as recovery
        var pdfData = "1 0 obj\n<< /Length 4 >>\nstream\ntest\nendstreamExtra\nendobj";

        using var parser = new PdfParser(Encoding.ASCII.GetBytes(pdfData));

        var indObj = parser.ParseIndirectObject();

        indObj.Should().NotBeNull();
        indObj.Value.Should().BeOfType<PdfStream>();
        var stream = (PdfStream)indObj.Value;
        stream.EncodedData.Length.Should().Be(4);
        Encoding.ASCII.GetString(stream.EncodedData).Should().Be("test");
    }

    [Fact]
    public void ParseIndirectObject_ValidEndstream_ParsesSuccessfully()
    {
        // Positive test: standard endstream token
        var pdfData = "1 0 obj\n<< /Length 5 >>\nstream\nhello\nendstream\nendobj";

        using var parser = new PdfParser(Encoding.ASCII.GetBytes(pdfData));

        var indObj = parser.ParseIndirectObject();

        indObj.Should().NotBeNull();
        var stream = (PdfStream)indObj.Value;
        Encoding.ASCII.GetString(stream.EncodedData).Should().Be("hello");
    }

    [Fact]
    public void ParseIndirectObject_StreamWhitespaceBeforeEol_DoesNotShiftDeclaredBytes()
    {
        // Some conformance fixtures use "stream \n"; the space is producer
        // whitespace before the required EOL, not the first byte of stream data.
        var compressedEmptyStream = new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 };
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("1 0 obj\n<< /Length 8 /Filter /FlateDecode >>\nstream \n"));
        ms.Write(compressedEmptyStream);
        ms.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj"));

        using var parser = new PdfParser(ms.ToArray());

        var indObj = parser.ParseIndirectObject();

        var stream = indObj.Value.Should().BeOfType<PdfStream>().Subject;
        stream.EncodedData.Should().Equal(compressedEmptyStream);
    }

    [Fact]
    public void ParseIndirectObject_WrongTokenAfterData_ThrowsException()
    {
        // Negative test: invalid token instead of endstream
        var pdfData = "1 0 obj\n<< /Length 4 >>\nstream\ntest\ngarbage\nendobj";

        using var parser = new PdfParser(Encoding.ASCII.GetBytes(pdfData));

        var action = () => parser.ParseIndirectObject();

        action.Should().Throw<PdfParseException>()
            .WithMessage("*endstream*");
    }

    #endregion

    #region Recursion-depth hardening (issue #346)

    [Fact]
    public void ParseObject_DeeplyNestedArrays_ThrowsInsteadOfStackOverflow()
    {
        // 5000 levels of nested arrays would overflow the stack without a guard.
        var data = Encoding.ASCII.GetBytes(new string('[', 5000) + new string(']', 5000));
        using var parser = new PdfParser(data);

        var action = () => parser.ParseObject();

        action.Should().Throw<PdfParseException>()
            .WithMessage("*nesting depth*");
    }

    [Fact]
    public void ParseObject_DeeplyNestedDictionaries_ThrowsInsteadOfStackOverflow()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++) sb.Append("<< /K ");
        for (int i = 0; i < 5000; i++) sb.Append(" >>");
        using var parser = new PdfParser(Encoding.ASCII.GetBytes(sb.ToString()));

        var action = () => parser.ParseObject();

        action.Should().Throw<PdfParseException>()
            .WithMessage("*nesting depth*");
    }

    [Fact]
    public void ParseObject_NestingWithinLimit_Succeeds()
    {
        // Comfortably below the 512 default — must parse cleanly.
        const int depth = 100;
        var data = Encoding.ASCII.GetBytes(new string('[', depth) + new string(']', depth));
        using var parser = new PdfParser(data);

        var obj = parser.ParseObject();

        obj.Should().BeOfType<PdfArray>();
    }

    [Fact]
    public void MaxNestingDepth_IsConfigurable()
    {
        var data = Encoding.ASCII.GetBytes(new string('[', 20) + new string(']', 20));
        using var parser = new PdfParser(data) { MaxNestingDepth = 5 };

        var action = () => parser.ParseObject();

        action.Should().Throw<PdfParseException>().WithMessage("*nesting depth (5)*");
    }

    #endregion
}
