using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Content;
using Xunit;

namespace Pdfe.Core.Tests.Content.Operators;

/// <summary>
/// Tests for inline image operators: BI, ID, EI (§8.9.7).
/// Inline images embed raw pixel data directly in the content stream.
/// Without correct BI/ID/EI handling the binary data corrupts the token
/// stream and every subsequent operator is mis-parsed.
/// </summary>
public class InlineImageTests
{
    [Fact]
    public void Parse_InlineImage_ProducesBI_Operator()
    {
        // Minimal inline image: 1x1 pixel, 1-bit grayscale
        var content = "BI\n/W 1\n/H 1\n/BPC 1\n/CS /G\nID\xFF\nEI\n";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().ContainSingle(op => op.Name == "BI",
            "inline image should produce exactly one BI operator");
    }

    [Fact]
    public void Parse_InlineImage_DoesNotLeakRawBytesAsOperators()
    {
        // The 4 raw image data bytes 0x42 0x54 0x20 0x45 spell "BT E" in ASCII —
        // which, if the tokeniser reads them, would look like the BT text operator.
        // Correct BI/ID/EI handling must consume them without emitting BT.
        byte[] raw = { 0x42, 0x54, 0x20, 0x45 }; // "BT E" — would confuse plain tokeniser
        var header = Encoding.ASCII.GetBytes("BI\n/W 4\n/H 1\n/BPC 8\n/CS /G\nID\n");
        var footer = Encoding.ASCII.GetBytes("\nEI\n");
        var content = header.Concat(raw).Concat(footer).ToArray();

        var result = new ContentStreamParser(content).Parse();

        result.Operators.Should().NotContain(op => op.Name == "BT",
            "binary image bytes must not be tokenised as PDF operators");
        result.Operators.Should().ContainSingle(op => op.Name == "BI");
    }

    [Fact]
    public void Parse_InlineImage_OperatorsBeforeAndAfterParsedCorrectly()
    {
        // Surrounding text operators must be unaffected by the inline image.
        var content = "BT (before) Tj ET\n" +
                      "BI\n/W 2\n/H 2\n/BPC 8\n/CS /G\nID\n\x00\x00\x00\x00\nEI\n" +
                      "BT (after) Tj ET\n";

        var result = new ContentStreamParser(Encoding.UTF8.GetBytes(content)).Parse();

        var tjOps = result.Operators.Where(op => op.Name == "Tj").ToList();
        tjOps.Should().HaveCount(2, "both Tj operators must survive inline image parsing");
        tjOps[0].TextContent.Should().Be("before");
        tjOps[1].TextContent.Should().Be("after");
    }

    [Fact]
    public void Parse_InlineImage_AbbreviatedParameterNamesAccepted()
    {
        // PDF allows abbreviated names for inline image parameters (e.g. /W for /Width)
        var content = "BI /W 8 /H 4 /BPC 1 /CS /G /F /A85 ID XXXXXX EI ";

        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        result.Operators.Should().ContainSingle(op => op.Name == "BI");
    }

    [Fact]
    public void Parse_MultipleInlineImages_AllProduceSeparateBIOperators()
    {
        var img = "BI /W 1 /H 1 /BPC 1 /CS /G ID \xFF EI ";
        var content = img + img + img;

        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        result.Operators.Count(op => op.Name == "BI").Should()
            .Be(3, "three sequential inline images must each produce a BI operator");
    }

    /// <summary>
    /// PDF Association ruling (pdf-issues#3, errata to ISO 32000-2:2020):
    /// when an inline image dict contains BOTH the abbreviated and the full
    /// form of a key (e.g. /W AND /Width), the abbreviated form takes
    /// precedence — regardless of the order they appear in the source.
    /// Without this, parsers diverge on the SafeDocs `issue14256.pdf`
    /// fixture and most of the time get out of sync trying to read the
    /// wrong number of image-data bytes.
    /// </summary>
    [Fact]
    public void Parse_InlineImage_AbbreviatedKeyTakesPrecedence_AbbreviatedFirst()
    {
        // /W 10 first, then /Width 999. Abbreviated wins regardless of order.
        var content = "BI /W 10 /H 5 /Width 999 /Height 998 /BPC 1 /CS /G ID \xFF EI ";

        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        var bi = result.Operators.Single(op => op.Name == "BI");
        var dict = (Pdfe.Core.Primitives.PdfDictionary)bi.Operands[0];
        // Storage uses the abbreviated form regardless of source spelling.
        dict.GetInt("W").Should().Be(10);
        dict.GetInt("H").Should().Be(5);
        // Full-form key must NOT be in the dict — the parser normalizes.
        dict.ContainsKey("Width").Should().BeFalse();
        dict.ContainsKey("Height").Should().BeFalse();
    }

    [Fact]
    public void Parse_InlineImage_AbbreviatedKeyTakesPrecedence_FullFirst()
    {
        // /Width 999 first, then /W 10. Abbreviated still wins.
        var content = "BI /Width 999 /Height 998 /W 10 /H 5 /BPC 1 /CS /G ID \xFF EI ";

        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        var bi = result.Operators.Single(op => op.Name == "BI");
        var dict = (Pdfe.Core.Primitives.PdfDictionary)bi.Operands[0];
        dict.GetInt("W").Should().Be(10);
        dict.GetInt("H").Should().Be(5);
        dict.ContainsKey("Width").Should().BeFalse();
        dict.ContainsKey("Height").Should().BeFalse();
    }

    [Fact]
    public void Parse_InlineImage_FullFormKeysNormalizedToAbbreviated()
    {
        // Only full-form keys; parser must store under the abbreviated names
        // so downstream consumers only ever see one spelling.
        var content = "BI /Width 7 /Height 3 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /ASCII85Decode ID \xFF EI ";

        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        var bi = result.Operators.Single(op => op.Name == "BI");
        var dict = (Pdfe.Core.Primitives.PdfDictionary)bi.Operands[0];
        dict.GetInt("W").Should().Be(7);
        dict.GetInt("H").Should().Be(3);
        dict.GetInt("BPC").Should().Be(8);
        dict.GetNameOrNull("CS").Should().Be("DeviceGray");
        dict.GetNameOrNull("F").Should().Be("ASCII85Decode");
        // None of the long forms should leak through.
        dict.ContainsKey("Width").Should().BeFalse();
        dict.ContainsKey("Height").Should().BeFalse();
        dict.ContainsKey("BitsPerComponent").Should().BeFalse();
        dict.ContainsKey("ColorSpace").Should().BeFalse();
        dict.ContainsKey("Filter").Should().BeFalse();
    }

    [Fact]
    public void Parse_InlineImage_AllTable91KeysCovered()
    {
        // Every full↔abbreviated pair from ISO 32000-2 Table 91.
        // Abbreviated wins regardless of source order.
        var content = "BI " +
            "/Width 999 /W 1 " +
            "/Height 998 /H 2 " +
            "/BitsPerComponent 4 /BPC 8 " +
            "/ColorSpace /Wrong /CS /DeviceGray " +
            "/Filter /Wrong /F /ASCII85Decode " +
            "/Decode [9 9] /D [0 1] " +
            "/DecodeParms << /Wrong true >> /DP << /K -1 /Columns 1 /BlackIs1 true >> " +
            "/ImageMask false /IM true " +
            "/Interpolate true /I false " +
            "/Length 999 /L 4 " +
            "ID XXXX EI ";

        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        var bi = result.Operators.Single(op => op.Name == "BI");
        var dict = (Pdfe.Core.Primitives.PdfDictionary)bi.Operands[0];
        dict.GetInt("W").Should().Be(1);
        dict.GetInt("H").Should().Be(2);
        dict.GetInt("BPC").Should().Be(8);
        dict.GetNameOrNull("CS").Should().Be("DeviceGray");
        dict.GetNameOrNull("F").Should().Be("ASCII85Decode");
        dict.GetArrayOrNull("D").Should().NotBeNull();
        var decodeParms = dict.GetDictionaryOrNull("DP");
        decodeParms.Should().NotBeNull();
        decodeParms!.GetInt("K").Should().Be(-1);
        decodeParms.GetInt("Columns").Should().Be(1);
        decodeParms.GetBool("BlackIs1").Should().BeTrue();
        dict.GetBool("IM").Should().BeTrue();
        dict.GetBool("I").Should().BeFalse();
        dict.GetInt("L").Should().Be(4);
        // Full-form keys must be normalized away.
        dict.ContainsKey("Width").Should().BeFalse();
        dict.ContainsKey("DecodeParms").Should().BeFalse();
        dict.ContainsKey("ImageMask").Should().BeFalse();
        dict.ContainsKey("Interpolate").Should().BeFalse();
    }

    [Fact]
    public void Parse_InlineImage_DecodeParmsDictionary_CompactSyntax()
    {
        var content = "BI /IM true /W 106 /H 100 /BPC 1 /D[1 0] /F/CCF /DP<</K -1 /Columns 106 /BlackIs1 false>> ID abc EI ";

        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        var bi = result.Operators.Single(op => op.Name == "BI");
        var dict = (Pdfe.Core.Primitives.PdfDictionary)bi.Operands[0];
        var decodeParms = dict.GetDictionaryOrNull("DP");
        decodeParms.Should().NotBeNull("Type3 CCITT image masks store K and Columns in inline /DP dictionaries");
        decodeParms!.GetInt("K").Should().Be(-1);
        decodeParms.GetInt("Columns").Should().Be(106);
        decodeParms.GetBool("BlackIs1", true).Should().BeFalse();
    }

    [Fact]
    public void Parse_InlineImage_WithLength_SkipsEmbeddedFalseEI()
    {
        // The 7 raw bytes contain the sequence <space>EI<space> at offset 1,
        // which the byte-scan fallback would mistake for the real end marker
        // and truncate the image — corrupting every operator after it.
        // With an explicit /L 7 the parser must skip exactly 7 bytes to the
        // *real* EI and resync cleanly. (Issue #347)
        byte[] raw = { 0x01, 0x20, 0x45, 0x49, 0x20, 0x02, 0x03 }; // .· E I · .. — " EI " inside
        var header = Encoding.ASCII.GetBytes("BI\n/W 7\n/H 1\n/BPC 8\n/CS /G\n/L 7\nID\n");
        var footer = Encoding.ASCII.GetBytes("\nEI\nBT (after) Tj ET\n");
        var content = header.Concat(raw).Concat(footer).ToArray();

        var result = new ContentStreamParser(content).Parse();

        result.Operators.Should().ContainSingle(op => op.Name == "BI",
            "the /L length must skip the embedded false 'EI' and find exactly one image");
        result.Operators.Should().Contain(op => op.Name == "Tj",
            "the text operator after the real EI must be parsed once the image is consumed correctly");
    }

    [Fact]
    public void Parse_InlineImage_WithoutLength_TruncatesAtFalseEI_DocumentsScanLimitation()
    {
        // Same payload, but WITHOUT /L. The scan stops at the embedded false
        // 'EI', so the trailing bytes are mis-tokenised. This documents why
        // /L is preferred (and why #354 inline-image redaction should rely on
        // declared length where available).
        byte[] raw = { 0x01, 0x20, 0x45, 0x49, 0x20, 0x02, 0x03 };
        var header = Encoding.ASCII.GetBytes("BI\n/W 7\n/H 1\n/BPC 8\n/CS /G\nID\n");
        var footer = Encoding.ASCII.GetBytes("\nEI\nBT (after) Tj ET\n");
        var content = header.Concat(raw).Concat(footer).ToArray();

        var result = new ContentStreamParser(content).Parse();

        // Still exactly one BI; the difference is the image ends early.
        result.Operators.Should().ContainSingle(op => op.Name == "BI");
    }

    // ---- #354: round-trip — the parser must retain the binary image data,
    // and ContentStreamWriter must re-emit valid BI…ID<data>EI syntax. Before
    // this fix the pixel bytes were silently dropped and the params dict was
    // serialized as a `<<…>> BI` blob, corrupting the stream on every rewrite
    // (which redaction always performs). ----

    [Fact]
    public void Parse_InlineImage_RetainsRawDataBytes()
    {
        // /L declares the exact data length so parsing is unambiguous.
        byte[] raw = { 0xDE, 0xAD, 0xBE, 0xEF }; // unique marker, non-ASCII
        var header = Encoding.Latin1.GetBytes("BI\n/W 4\n/H 1\n/BPC 8\n/CS /G\n/L 4\nID ");
        var footer = Encoding.Latin1.GetBytes("\nEI\n");
        var content = header.Concat(raw).Concat(footer).ToArray();

        var bi = new ContentStreamParser(content).Parse().Operators.Single(op => op.Name == "BI");

        bi.InlineImageData.Should().Equal(raw,
            "the bytes between ID and EI must be captured verbatim for lossless round-trip");
    }

    [Fact]
    public void Write_InlineImage_RoundTripsBinaryDataLosslessly()
    {
        byte[] raw = { 0xDE, 0xAD, 0xBE, 0xEF };
        var header = Encoding.Latin1.GetBytes("BI\n/W 4\n/H 1\n/BPC 8\n/CS /G\n/L 4\nID ");
        var footer = Encoding.Latin1.GetBytes("\nEI\n");
        var content = header.Concat(raw).Concat(footer).ToArray();

        var parsed = new ContentStreamParser(content).Parse();
        var written = new ContentStreamWriter().Write(parsed);

        // The exact pixel bytes survive serialization...
        var writtenStr = Encoding.Latin1.GetString(written);
        writtenStr.Should().Contain("\xDE\xAD\xBE\xEF");
        // ...and the output is valid inline-image syntax, NOT a `<<…>> BI` blob.
        writtenStr.Should().Contain("BI").And.Contain("ID ").And.Contain("EI");
        writtenStr.Should().NotContain("<<",
            "inline-image params are bare key/value pairs, never a dictionary literal");

        // Re-parsing the written bytes yields the identical image data.
        var reparsed = new ContentStreamParser(written).Parse().Operators.Single(op => op.Name == "BI");
        reparsed.InlineImageData.Should().Equal(raw);
    }

    [Fact]
    public void Write_InlineImage_PreservesSurroundingOperators()
    {
        // Binary data deliberately contains bytes that spell PDF operators
        // ("q", "BT") to prove the writer does not let them leak as tokens.
        byte[] raw = { (byte)'q', 0x0A, (byte)'B', (byte)'T', 0xFF };
        var content = Encoding.Latin1.GetBytes("BT (before) Tj ET\n")
            .Concat(Encoding.Latin1.GetBytes("BI\n/W 5\n/H 1\n/BPC 8\n/CS /G\n/L 5\nID "))
            .Concat(raw)
            .Concat(Encoding.Latin1.GetBytes("\nEI\n"))
            .Concat(Encoding.Latin1.GetBytes("BT (after) Tj ET\n"))
            .ToArray();

        var parsed = new ContentStreamParser(content).Parse();
        var written = new ContentStreamWriter().Write(parsed);
        var reparsed = new ContentStreamParser(written).Parse();

        reparsed.Operators.Count(op => op.Name == "BI").Should().Be(1);
        var tj = reparsed.Operators.Where(op => op.Name == "Tj").ToList();
        tj.Should().HaveCount(2);
        tj[0].TextContent.Should().Be("before");
        tj[1].TextContent.Should().Be("after");
    }

    [Fact]
    public void Parse_LargeInlineImage_NoLength_ScansLinearly_FindsRealEI()
    {
        // 8 MB of binary data WITHOUT /L, sprinkled with non-boundary "EI"
        // byte sequences that must NOT be mistaken for the end marker. An
        // O(n^2) scan would take minutes here; the O(n) boundary scan finds the
        // real EI quickly. (#347)
        const int size = 8 * 1024 * 1024;
        var raw = new byte[size];
        for (int i = 0; i < size; i++) raw[i] = (byte)((i % 251) + 1); // non-zero, no whitespace
        for (int i = 1000; i < size - 1000; i += 4096) { raw[i] = (byte)'E'; raw[i + 1] = (byte)'I'; }

        var header = Encoding.ASCII.GetBytes("BI /W 1 /H 1 /BPC 8 /CS /G ID ");
        var footer = Encoding.ASCII.GetBytes("\nEI\nBT (after) Tj ET\n");
        var content = header.Concat(raw).Concat(footer).ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new ContentStreamParser(content).Parse();
        sw.Stop();

        result.Operators.Should().ContainSingle(op => op.Name == "BI");
        result.Operators.Should().Contain(op => op.Name == "Tj",
            "the operator after the real word-boundary EI must be reached");
        sw.Elapsed.Should().BeLessThan(System.TimeSpan.FromSeconds(10),
            "the scan must be linear, not O(n^2)");
    }

    [Fact]
    public void Parse_InlineImage_NoLength_NoEI_ScansToEndGracefully()
    {
        // No /L and no EI marker: the scan runs to end-of-content (bounded by
        // the safety cap) and still yields exactly one BI without hanging. (#347)
        var raw = new byte[1024];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)((i % 200) + 1);
        var content = Encoding.ASCII.GetBytes("BI /W 1 /H 1 /BPC 8 /CS /G ID ").Concat(raw).ToArray();

        var result = new ContentStreamParser(content).Parse();

        result.Operators.Count(op => op.Name == "BI").Should().Be(1);
    }
}
