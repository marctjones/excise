using System.Text;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Authoring;

/// <summary>
/// <see cref="PdfAIdentityXmp"/> is the ONE pdfaid parser (#1526), and these
/// pin the three questions it answers separately — presence, declared token,
/// validated identity — because collapsing any two of them has already shipped
/// a defect:
///
/// <list type="bullet">
///   <item>#1524: <c>PdfDocumentWriter.IsPdfA1</c> matched only the ELEMENT
///     serialisation, so a valid PDF/A-1 file using the attribute form was not
///     recognised and the writer put object streams — forbidden by ISO 19005-1
///     — into it.</item>
///   <item>#1526's warning in the other direction: reading PRESENCE through the
///     validated identity would make a claim excise cannot validate read as
///     "not PDF/A", and something PDF/A forbids would be emitted back into a
///     file that claims conformance.</item>
/// </list>
///
/// <para>Every packet here is hand-written rather than produced by excise: a
/// parser tested only against the serialisation excise itself emits is exactly
/// the blind spot #1524 was.</para>
/// </summary>
public class PdfAIdentificationParserTests
{
    private const string NamespaceDeclaration =
        "xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"";

    /// <summary>The element serialisation — what excise's own PDF/A writer emits.</summary>
    private static string ElementForm(int part, string conformance = "B") =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
        + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
        + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + $"<rdf:Description rdf:about=\"\" {NamespaceDeclaration}>"
        + $"<pdfaid:part>{part}</pdfaid:part>"
        + $"<pdfaid:conformance>{conformance}</pdfaid:conformance>"
        + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>
    /// The attribute serialisation — equally legal XMP (a simple property may
    /// be written as an attribute of the <c>rdf:Description</c>), emitted by
    /// Adobe tooling among others, and the form #1524 was blind to.
    /// </summary>
    private static string AttributeForm(int part, string conformance = "B", char quote = '"') =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
        + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
        + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + $"<rdf:Description rdf:about=\"\" {NamespaceDeclaration} "
        + $"pdfaid:part={quote}{part}{quote} pdfaid:conformance={quote}{conformance}{quote}/>"
        + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    // ── The declared token: both serialisations, no value judgement ───────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ReadDeclaredPart_ReadsTheElementSerialisation(int part) =>
        PdfAIdentityXmp.ReadDeclaredPart(ElementForm(part)).Should().Be(part.ToString());

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ReadDeclaredPart_ReadsTheAttributeSerialisation(int part) =>
        PdfAIdentityXmp.ReadDeclaredPart(AttributeForm(part)).Should().Be(part.ToString(),
            "#1524: XMP permits a simple property as an attribute, and a PDF/A-1 "
            + "file written that way is still a PDF/A-1 file");

    [Fact]
    public void ReadDeclaredPart_ReadsASingleQuotedAttribute() =>
        PdfAIdentityXmp.ReadDeclaredPart(AttributeForm(1, quote: '\'')).Should().Be("1",
            "XML gives both quote characters equal standing");

    [Theory]
    [InlineData("pdfaid:part=\"1\"")]
    [InlineData("pdfaid:part = \"1\"")]
    [InlineData("pdfaid:part\t=\t\"1\"")]
    [InlineData("pdfaid:part=\n\"1\"")]
    public void ReadDeclaredPart_ToleratesWhitespaceAroundTheAttributeEquals(string attribute) =>
        PdfAIdentityXmp.ReadDeclaredPart(
            $"<rdf:Description rdf:about=\"\" {NamespaceDeclaration} {attribute}/>")
            .Should().Be("1");

    [Theory]
    [InlineData("<pdfaid:part>1</pdfaid:part>")]
    [InlineData("<pdfaid:part> 1 </pdfaid:part>")]
    [InlineData("<pdfaid:part>\n   1\n  </pdfaid:part>")]
    [InlineData("<pdfaid:part rdf:datatype=\"http://www.w3.org/2001/XMLSchema#integer\">1</pdfaid:part>")]
    public void ReadDeclaredPart_ToleratesWhitespaceAndAttributesOnTheElement(string element) =>
        PdfAIdentityXmp.ReadDeclaredPart(
            $"<rdf:Description rdf:about=\"\" {NamespaceDeclaration}>{element}</rdf:Description>")
            .Should().Be("1",
            "the same 'one exact spelling' assumption that hid the attribute form (#1524) "
            + "would also hide a typed or whitespace-padded element");

    [Theory]
    [InlineData("")]
    [InlineData("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF/></x:xmpmeta>")]
    [InlineData("<rdf:Description dc:title=\"pdfaid:part is only mentioned in prose\"/>")]
    public void ReadDeclaredPart_IsNullWhenNothingDeclaresAPart(string xmp) =>
        PdfAIdentityXmp.ReadDeclaredPart(xmp).Should().BeNull();

    [Fact]
    public void ReadDeclaredPart_IsNullForNullXmp() =>
        PdfAIdentityXmp.ReadDeclaredPart(null).Should().BeNull();

    [Theory]
    [InlineData("<rdf:Description xpdfaid:part=\"1\"/>")]
    [InlineData("<rdf:Description mypdfaid:part=\"1\"/>")]
    public void ReadDeclaredPart_DoesNotMatchALongerPrefixEndingInPdfaid(string xmp) =>
        PdfAIdentityXmp.ReadDeclaredPart(xmp).Should().BeNull(
            "a property whose prefix merely ENDS with 'pdfaid' is a different property; "
            + "widening the match to catch more files must not catch other schemas");

    [Fact]
    public void ReadDeclaredPart_ReportsAnUnknownPartVerbatim() =>
        PdfAIdentityXmp.ReadDeclaredPart(AttributeForm(10)).Should().Be("10",
            "the token is reported as the file spells it; judging it is TryParse's job, "
            + "and a caller comparing to \"1\" must not be fooled by a prefix match");

    [Fact]
    public void ReadDeclaredConformance_ReadsBothSerialisations()
    {
        PdfAIdentityXmp.ReadDeclaredConformance(ElementForm(2)).Should().Be("B");
        PdfAIdentityXmp.ReadDeclaredConformance(AttributeForm(2)).Should().Be("B");
        PdfAIdentityXmp.ReadDeclaredConformance(AttributeForm(2, conformance: "A")).Should().Be("A");
        PdfAIdentityXmp.ReadDeclaredConformance("<rdf:Description/>").Should().BeNull();
    }

    // ── Presence: a claim excise cannot validate is still a claim (#1526) ─────

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void DeclaresAnyIdentification_IsTrueForEitherSerialisation(int part)
    {
        PdfAIdentityXmp.DeclaresAnyIdentification(ElementForm(part)).Should().BeTrue();
        PdfAIdentityXmp.DeclaresAnyIdentification(AttributeForm(part)).Should().BeTrue();
    }

    [Fact]
    public void DeclaresAnyIdentification_IsTrueForAClaimTryParseRefuses()
    {
        var claimed = AttributeForm(9);

        PdfAIdentityXmp.TryParse(claimed).Should().BeNull(
            "part 9 is not a part of ISO 19005, so there is no identity to re-emit");
        PdfAIdentityXmp.DeclaresAnyIdentification(claimed).Should().BeTrue(
            "#1526: PRESENCE decides whether to SUPPRESS something PDF/A forbids. "
            + "Reading an unvalidatable claim as 'not PDF/A' would put /NeedAppearances "
            + "back into a file claiming conformance (#1499) — the conservative answer "
            + "costs nothing and the other answer costs the claim");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF/></x:xmpmeta>")]
    public void DeclaresAnyIdentification_IsFalseWithoutAnIdentification(string? xmp) =>
        PdfAIdentityXmp.DeclaresAnyIdentification(xmp).Should().BeFalse();

    // ── The validated identity: the only read allowed to feed Write ───────────

    [Fact]
    public void TryParse_ReadsAValidatedIdentityFromEitherSerialisation()
    {
        PdfAIdentityXmp.TryParse(ElementForm(2)).Should().Be(new PdfAIdentity("2", "B", null));
        PdfAIdentityXmp.TryParse(AttributeForm(2)).Should().Be(new PdfAIdentity("2", "B", null));
    }

    [Theory]
    [InlineData("b")]      // right letter, wrong case: not a token ISO 19005 defines
    [InlineData("Z")]
    [InlineData("BB")]
    public void TryParse_RefusesAnIdentityWithAnUnknownConformance(string conformance) =>
        PdfAIdentityXmp.TryParse(AttributeForm(1, conformance)).Should().BeNull(
            "a value excise cannot validate is never re-emitted (#1507): this preserves "
            + "an identification, it does not repair one");

    [Fact]
    public void ReadDeclaredPart_StillSeesPart1WhenTheConformanceIsUnvalidatable()
    {
        // THE distinction #1524's fix turns on. The issue's suggested one-liner
        // (`TryRead(doc)?.Part == "1"`) would read this file as "not PDF/A-1"
        // and compress it, even though the old element-only grep — which looked
        // at the part alone — got it right.
        var claimed = ElementForm(1, conformance: "b");

        PdfAIdentityXmp.TryParse(claimed).Should().BeNull();
        PdfAIdentityXmp.ReadDeclaredPart(claimed).Should().Be("1",
            "the file unambiguously claims PDF/A-1; a malformed QUALIFIER cannot be "
            + "grounds for writing a construct PDF/A-1 forbids");
    }

    [Fact]
    public void TryParse_RefusesARevisionThatIsNotFourDigits()
    {
        var packet = "<rdf:Description rdf:about=\"\" " + NamespaceDeclaration
            + " pdfaid:part=\"4\" pdfaid:rev=\"20\"/>";
        PdfAIdentityXmp.TryParse(packet).Should().BeNull();
    }

    [Fact]
    public void TryParse_KeepsAFourDigitRevisionAsFound()
    {
        var packet = "<rdf:Description rdf:about=\"\" " + NamespaceDeclaration
            + " pdfaid:part=\"4\" pdfaid:rev=\"2020\"/>";
        PdfAIdentityXmp.TryParse(packet).Should().Be(new PdfAIdentity("4", null, "2020"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xmp at all")]
    public void TryParse_IsNullWithoutAPart(string? xmp) =>
        PdfAIdentityXmp.TryParse(xmp).Should().BeNull();

    // ── Round trip: what Write emits, this parser reads back ──────────────────

    [Theory]
    [InlineData("1", "B", null)]
    [InlineData("2", "B", null)]
    [InlineData("4", null, "2020")]
    public void Write_EmitsAnIdentificationThisParserReadsBack(string part, string? conformance, string? rev)
    {
        using var doc = PdfDocument.Open(MinimalPdf());
        var identity = new PdfAIdentity(part, conformance, rev);

        PdfAIdentityXmp.Write(doc, identity);

        PdfAIdentityXmp.TryRead(doc).Should().Be(identity);
        doc.TargetsPdfA.Should().BeTrue("a written identification is a claim the document makes");

        var packet = Encoding.UTF8.GetString(doc.GetXmpMetadata()!);
        PdfAIdentityXmp.ReadDeclaredPart(packet).Should().Be(part);

        // PDF/A-1 forbids a filter on the catalog metadata stream (veraPDF
        // PDFA-1B, PDMetadata) and §14.3.2 wants XMP readable without
        // decompression, so the packet must be stored raw.
        var stream = doc.Resolve(doc.Catalog.GetOptional("Metadata")!) as PdfStream;
        stream.Should().NotBeNull();
        stream!.ContainsKey("Filter").Should().BeFalse();
    }

    /// <summary>
    /// The smallest document that opens: three dictionaries, no content. Written
    /// by hand so the parser is never handed a packet excise authored.
    /// </summary>
    private static byte[] MinimalPdf()
    {
        var sb = new StringBuilder();
        var offsets = new long[4];
        sb.Append("%PDF-1.7\n");
        offsets[1] = sb.Length;
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        offsets[2] = sb.Length;
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        offsets[3] = sb.Length;
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");
        var xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        for (var i = 1; i <= 3; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 4/Root 1 0 R>>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
