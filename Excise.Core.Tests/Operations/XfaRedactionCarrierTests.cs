using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Operations;

/// <summary>
/// #943: XFA is a complete second copy of many form labels and values. It is
/// outside page content and invisible to mutool text extraction, so every test
/// here inspects the saved XFA object graph or the saved bytes directly.
/// </summary>
public class XfaRedactionCarrierTests
{
    private const string Secret = "SECRETNAME";

    [Fact]
    public void ScrubTerms_PacketArray_RemovesXmlValuesAndGarbageCollectsOldPackets()
    {
        using var doc = CreateDocument();
        AddPacketArray(doc,
            ("preamble", Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                              "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">")),
            ("template", Utf8(
                "<template xmlns=\"urn:xfa-template\" fieldName=\"SECRETNAME field\">" +
                "<text>SECRETNAME visible</text>" +
                "<desc><![CDATA[SECRETNAME cdata]]></desc>" +
                "<!-- SECRETNAME comment --><?audit SECRETNAME?>" +
                "<keep>Quarterly Report</keep></template>")),
            ("datasets", Utf8("<datasets xmlns=\"urn:xfa-data\"><value>public</value></datasets>")),
            ("postamble", Utf8("</xdp:xdp>")));

        PdfDocumentSanitizer.ScrubTerms(doc, new[] { Secret }).Should().BeTrue();

        var saved = Save(doc);
        CombinedEncodings(saved).Should().NotContain(Secret,
            "the original packet streams must become unreachable on full save, not merely be " +
            "replaced in the AcroForm dictionary");

        using var reopened = PdfDocument.Open(saved);
        var xml = ReadCompleteXfa(reopened);
        xml.Should().NotContain(Secret);
        xml.Should().Contain("Quarterly Report");
        XDocument.Parse(xml).Should().NotBeNull("the rewritten XDP must remain well-formed XML");
        ResolveXfa(reopened).Should().BeOfType<PdfStream>(
            "a changed packet array is legally normalized to one complete XDP stream");
    }

    [Fact]
    public void ScrubTerms_SingleUtf16Stream_PreservesEncodingAndWellFormedXml()
    {
        using var doc = CreateDocument();
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
                  $"<template label=\"{Secret}\"><text>{Secret} and public</text></template>";
        AddSingleStream(doc, Utf16BigEndian(xml));

        PdfDocumentSanitizer.ScrubTerms(doc, new[] { Secret }).Should().BeTrue();

        using var reopened = PdfDocument.Open(Save(doc));
        var stream = ResolveXfa(reopened).Should().BeOfType<PdfStream>().Subject;
        stream.DecodedData.Should().StartWith(new byte[] { 0xFE, 0xFF });

        using var input = new MemoryStream(stream.DecodedData);
        using var reader = XmlReader.Create(input);
        var parsed = XDocument.Load(reader);
        parsed.ToString().Should().NotContain(Secret);
        parsed.ToString().Should().Contain("and public");
    }

    [Fact]
    public void ScrubTerms_CompressedXfaStream_IsDecodedAndRewrittenSafely()
    {
        using var initial = CreateDocument();
        var raw = Utf8($"<template><text>{Secret}</text><keep>public</keep></template>");
        var dict = new PdfDictionary();
        dict.SetName("Filter", "FlateDecode");
        var compressed = new PdfStream(dict, Deflate(raw));
        compressed["Length"] = new PdfInteger(compressed.EncodedData.Length);
        AddSingleStream(initial, compressed);

        var source = Save(initial);
        using var parsed = PdfDocument.Open(source);
        PdfDocumentSanitizer.ScrubTerms(parsed, new[] { Secret }).Should().BeTrue();

        using var reopened = PdfDocument.Open(Save(parsed));
        var xfa = ReadCompleteXfa(reopened);
        xfa.Should().NotContain(Secret);
        xfa.Should().Contain("public");
    }

    [Fact]
    public void ScrubTerms_CaseSensitivityMatchesPageRedactionPolicy()
    {
        using var insensitive = CreateDocument();
        AddSingleStream(insensitive, Utf8("<template><text>SecretName SECRETNAME</text></template>"));
        PdfDocumentSanitizer.ScrubTerms(insensitive, new[] { "secretname" }, caseSensitive: false)
            .Should().BeTrue();
        ReadCompleteXfa(insensitive).Should().NotContain("SecretName").And.NotContain(Secret);

        using var sensitive = CreateDocument();
        AddSingleStream(sensitive, Utf8("<template><text>SecretName SECRETNAME</text></template>"));
        PdfDocumentSanitizer.ScrubTerms(sensitive, new[] { "secretname" }, caseSensitive: true)
            .Should().BeFalse();
        ReadCompleteXfa(sensitive).Should().Contain("SecretName").And.Contain(Secret);
    }

    [Fact]
    public void SplitAcrossXmlNodes_IsReportedInsteadOfBroadlyDeletingSiblingContent()
    {
        using var doc = CreateDocument();
        AddSingleStream(doc, Utf8(
            "<template><text>SECRET<b>NAME</b></text><field>unrelated</field></template>"));

        var result = XfaXmlCarrier.ScrubTerms(doc, new[] { Secret }, caseSensitive: false);

        result.Changed.Should().BeFalse(
            "rewriting across element boundaries can join unrelated XFA fields and over-redact");
        result.UnexaminedPacketCount.Should().Be(1,
            "a semantic match split by inline markup must not be silently certified as clean");
        ReadCompleteXfa(doc).Should().Contain("SECRET<b>NAME</b>");
    }

    [Theory]
    [InlineData("<template><text>SECRETNAME</template>")]
    [InlineData("<!DOCTYPE template [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]>" +
                "<template><text>&xxe; SECRETNAME</text></template>")]
    public void UnsafeOrMalformedXml_IsNotByteSplicedAndIsReported(string xml)
    {
        using var doc = CreateDocument();
        AddSingleStream(doc, Utf8(xml));

        var result = XfaXmlCarrier.ScrubTerms(doc, new[] { Secret }, caseSensitive: false);

        result.Changed.Should().BeFalse();
        result.UnexaminedPacketCount.Should().Be(1);
        ReadCompleteXfa(doc).Should().Be(xml,
            "malformed XML and DTD-bearing packets must be surfaced, never modified with " +
            "an XML-unaware byte replacement");
    }

    [Fact]
    public void RealIrsW9_RedactTextRemovesFormFromXfaCarrierValues()
    {
        const string fixture = "../../../../test-pdfs/smoke/irs-w9.pdf";
        if (!File.Exists(fixture)) return;

        using var doc = PdfDocument.Open(fixture);
        doc.RedactText("Form", drawBlackRect: false).Should().BeGreaterThan(0,
            "fixture sanity: the W-9 page content must contain the reported term");

        var saved = Save(doc);
        using var reopened = PdfDocument.Open(saved);
        var parsed = ParseXfa(reopened);
        var carrierValues = parsed.Descendants().Attributes()
            .Where(a => !a.IsNamespaceDeclaration).Select(a => a.Value)
            .Concat(parsed.DescendantNodes().OfType<XText>().Select(n => n.Value))
            .Concat(parsed.DescendantNodes().OfType<XComment>().Select(n => n.Value))
            .Concat(parsed.DescendantNodes().OfType<XProcessingInstruction>().Select(n => n.Data));

        carrierValues.Should().NotContain(value =>
                value.Contains("Form", StringComparison.OrdinalIgnoreCase),
            "the real W-9 carries dozens of page labels in /XFA; mutool cannot see them, so " +
            "the saved XFA XML itself is the oracle");
        CombinedEncodings(saved).Should().NotContain("Form W-9",
            "the exact leak reported in #943 must not remain recoverable in saved bytes");
    }

    private static PdfDocument CreateDocument()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (public page text) Tj ET";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var obj in objects) { offsets.Add(sb.Length); sb.Append(obj); }
        var xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(sb.ToString()));
    }

    private static void AddSingleStream(PdfDocument doc, byte[] data) =>
        AddSingleStream(doc, new PdfStream(data));

    private static void AddSingleStream(PdfDocument doc, PdfStream stream)
    {
        var acroForm = new PdfDictionary
        {
            ["XFA"] = doc.AddIndirectObject(stream),
        };
        doc.Catalog["AcroForm"] = doc.AddIndirectObject(acroForm);
    }

    private static void AddPacketArray(PdfDocument doc, params (string Name, byte[] Data)[] packets)
    {
        var array = new PdfArray();
        foreach (var packet in packets)
        {
            array.Add((PdfObject)new PdfString(packet.Name));
            array.Add(doc.AddIndirectObject(new PdfStream(packet.Data)));
        }

        var acroForm = new PdfDictionary { ["XFA"] = array };
        doc.Catalog["AcroForm"] = doc.AddIndirectObject(acroForm);
    }

    private static PdfObject ResolveXfa(PdfDocument doc)
    {
        var acroForm = doc.Resolve(doc.Catalog.GetOptional("AcroForm")!)
            .Should().BeOfType<PdfDictionary>().Subject;
        return doc.Resolve(acroForm.GetOptional("XFA")!);
    }

    private static string ReadCompleteXfa(PdfDocument doc)
    {
        var xfa = ResolveXfa(doc);
        var bytes = xfa switch
        {
            PdfStream stream => stream.DecodedData,
            PdfArray packets => packets.Select(doc.Resolve).OfType<PdfStream>()
                .SelectMany(stream => stream.DecodedData).ToArray(),
            _ => throw new InvalidDataException("Unsupported XFA object shape."),
        };

        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    private static XDocument ParseXfa(PdfDocument doc)
    {
        var xfa = ResolveXfa(doc).Should().BeOfType<PdfStream>().Subject;
        using var input = new MemoryStream(xfa.DecodedData);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static byte[] Save(PdfDocument doc)
    {
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }

    private static byte[] Utf8(string value) => new UTF8Encoding(false).GetBytes(value);

    private static byte[] Utf16BigEndian(string value)
    {
        var payload = Encoding.BigEndianUnicode.GetBytes(value);
        return new byte[] { 0xFE, 0xFF }.Concat(payload).ToArray();
    }

    private static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(bytes);
        return output.ToArray();
    }

    private static string CombinedEncodings(byte[] bytes) =>
        Encoding.Latin1.GetString(bytes) + Encoding.BigEndianUnicode.GetString(bytes);
}
