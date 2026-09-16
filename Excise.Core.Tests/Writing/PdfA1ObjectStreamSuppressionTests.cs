using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Security;
using Xunit;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// PDF/A-1 forbids object streams and cross-reference streams (ISO 19005-1
/// 6.1.4; veraPDF PDFA-1B 6.1.4#3 <c>containsXRefStream == false</c>), so the
/// writer must suppress both for any document that declares PDF/A-1 — and it
/// must NOT suppress them for anything else, because that would silently
/// inflate every other file.
///
/// <para><b>#1524.</b> The suppression was gated on a substring match for the
/// ELEMENT spelling <c>&lt;pdfaid:part&gt;1&lt;/pdfaid:part&gt;</c>. XMP permits
/// the same simple property as an ATTRIBUTE of the <c>rdf:Description</c>
/// (<c>pdfaid:part="1"</c>), and a file written that way was not recognised: on
/// save, excise wrote object streams into a file whose whole point is archival
/// conformance, with no error and nothing in the output to say so.</para>
///
/// <para><b>The true precondition is a PAIR</b>, which is why the rows below
/// vary both axes. Compression also requires a header of at least 1.5, so the
/// ordinary <c>%PDF-1.4</c> PDF/A-1 file was saved by the version gate rather
/// than by the detector. It is not a hypothetical pair: veraPDF's PDFA-1B
/// profile pins no version in the header rule (<c>headerOffset == 0 &amp;&amp;
/// /%PDF-\d\.\d/.test(header)</c>), so a <c>%PDF-1.7</c> PDF/A-1 file is one
/// the reference validator is willing to pass — and excise's OWN authored
/// documents carry a 1.7 header (<c>PdfDocument.CreateNew</c>), so this is the
/// header a PDF/A file coming out of the builder has.</para>
///
/// <para>The conformance VERDICT belongs to veraPDF, not to these assertions —
/// see <c>Excise.Rendering.Tests/Differential/PdfA1SerialisationConformanceTests</c>.
/// What is asserted here is the byte-level property excise controls, on every
/// combination of serialisation and header version, deterministically and with
/// no external tool.</para>
/// </summary>
public class PdfA1ObjectStreamSuppressionTests
{
    internal enum Serialisation
    {
        /// <summary><c>&lt;pdfaid:part&gt;1&lt;/pdfaid:part&gt;</c> — what excise emits.</summary>
        Element,

        /// <summary><c>pdfaid:part="1"</c> — equally legal XMP; #1524's blind spot.</summary>
        Attribute,

        /// <summary>The same, single-quoted.</summary>
        SingleQuotedAttribute,

        /// <summary><c>pdfaid:part = "1"</c> — whitespace around the equals sign.</summary>
        SpacedAttribute,

        /// <summary>A typed element: <c>&lt;pdfaid:part rdf:datatype="…"&gt;1&lt;/…&gt;</c>.</summary>
        TypedElement,
    }

    // ── The defect: attribute form + a header that permits compression ────────

    [Fact]
    public void AttributeFormPdfA1_WithA17Header_SavesWithoutObjectStreams()
    {
        var saved = SaveWithIdentification(Serialisation.Attribute, part: 1, version: "1.7");

        saved.Should().NotContain("/Type /ObjStm",
            "#1524: PDF/A-1 forbids object streams, and 'pdfaid:part=\"1\"' is a "
            + "PDF/A-1 declaration — recognising only the element form put a forbidden "
            + "construct into an archival file");
        saved.Should().NotContain("/Type /XRef",
            "the xref stream goes with the object streams, and it is the construct "
            + "veraPDF names (PDFA-1B 6.1.4#3)");
        saved.Should().Contain("\nxref\n", "a classic cross-reference table must be written instead");
        saved.Should().Contain("\ntrailer\n");
    }

    [Theory]
    [InlineData(Serialisation.Element, "1.4")]
    [InlineData(Serialisation.Element, "1.5")]
    [InlineData(Serialisation.Element, "1.7")]
    [InlineData(Serialisation.Attribute, "1.4")]
    [InlineData(Serialisation.Attribute, "1.5")]
    [InlineData(Serialisation.Attribute, "1.7")]
    [InlineData(Serialisation.SingleQuotedAttribute, "1.7")]
    [InlineData(Serialisation.SpacedAttribute, "1.7")]
    [InlineData(Serialisation.TypedElement, "1.7")]
    public void EveryPdfA1Declaration_SavesWithoutObjectStreams(Serialisation form, string version)
    {
        var saved = SaveWithIdentification(form, part: 1, version: version);

        saved.Should().NotContain("/Type /ObjStm",
            $"a PDF/A-1 file declaring its identification as {form} with a %PDF-{version} "
            + "header is a PDF/A-1 file, whichever spelling it uses");
        saved.Should().NotContain("/Type /XRef");
    }

    [Fact]
    public void PdfA1WithAConformanceExciseCannotValidate_StillSuppressesObjectStreams()
    {
        // A lower-case conformance is not a token ISO 19005 defines, so the
        // fully validated read (PdfAIdentityXmp.TryParse) refuses this packet.
        // The file still unambiguously claims PDF/A-1, and the cost of the two
        // possible mistakes is not symmetric: a few kilobytes against a broken
        // archival guarantee. This is why the writer reads the DECLARED part
        // rather than the validated identity (#1524/#1526).
        var saved = SaveWithIdentification(
            Serialisation.Element, part: 1, version: "1.7", conformance: "b");

        saved.Should().NotContain("/Type /ObjStm");
    }

    // ── The other half: nothing else may be suppressed ────────────────────────

    [Fact]
    public void WithoutAnyIdentification_A17DocumentStillUsesObjectStreams()
    {
        // The guard that keeps every row above from passing vacuously: if this
        // fixture never compressed, "no /Type /ObjStm" would prove nothing.
        var saved = Save(BuildPdf("1.7", xmp: null));

        saved.Should().Contain("/Type /ObjStm",
            "the fixture must compress when nothing forbids it, or the suppression "
            + "assertions above are measuring the wrong thing");
        saved.Should().Contain("/Type /XRef");
    }

    [Theory]
    [InlineData(Serialisation.Element)]
    [InlineData(Serialisation.Attribute)]
    public void PdfA2_InEitherSerialisation_StillUsesObjectStreams(Serialisation form)
    {
        // PDF/A-2 onward permits both constructs (ISO 19005-2 is built on PDF
        // 1.7). Over-suppression would be a silent regression for every PDF/A-2
        // file, and the fix must not buy #1524 at that price.
        var saved = SaveWithIdentification(form, part: 2, version: "1.7");

        saved.Should().Contain("/Type /ObjStm",
            "PDF/A-2 does not forbid object streams; only part 1 does");
    }

    [Fact]
    public void APartNumberThatMerelyStartsWithOne_IsNotTreatedAsPdfA1()
    {
        // "10" is not part 1. A prefix or substring match would read it as one,
        // which is the same class of mistake as #1524 in the other direction.
        var saved = SaveWithIdentification(Serialisation.Attribute, part: 10, version: "1.7");

        saved.Should().Contain("/Type /ObjStm");
    }

    [Fact]
    public void EncryptedPdfA1_StillAvoidsObjectStreams()
    {
        // Encryption already forces the classic table for unrelated reasons; the
        // row exists so a future change to that path cannot quietly reintroduce
        // compressed objects for a PDF/A-1 document.
        using var doc = PdfDocument.Open(
            BuildPdf("1.7", XmpFor(Serialisation.Attribute, part: 1, conformance: "B")));

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes(new PdfEncryptionOptions
        {
            Algorithm = PdfEncryptionAlgorithm.Aes256,
            UserPassword = "user",
            OwnerPassword = "owner",
        }));

        saved.Should().NotContain("/Type /ObjStm");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static string SaveWithIdentification(
        Serialisation form, int part, string version, string conformance = "B")
        => Save(BuildPdf(version, XmpFor(form, part, conformance)));

    private static string Save(byte[] input)
    {
        using var doc = PdfDocument.Open(input);
        return Encoding.Latin1.GetString(doc.SaveToBytes());
    }

    private static string XmpFor(Serialisation form, int part, string conformance)
    {
        var body = form switch
        {
            Serialisation.Element =>
                $"<rdf:Description rdf:about=\"\" {Ns}>"
                + $"<pdfaid:part>{part}</pdfaid:part>"
                + $"<pdfaid:conformance>{conformance}</pdfaid:conformance>"
                + "</rdf:Description>",

            Serialisation.TypedElement =>
                $"<rdf:Description rdf:about=\"\" {Ns}>"
                + "<pdfaid:part rdf:datatype=\"http://www.w3.org/2001/XMLSchema#integer\">"
                + $"{part}</pdfaid:part>"
                + $"<pdfaid:conformance>{conformance}</pdfaid:conformance>"
                + "</rdf:Description>",

            Serialisation.Attribute =>
                $"<rdf:Description rdf:about=\"\" {Ns} "
                + $"pdfaid:part=\"{part}\" pdfaid:conformance=\"{conformance}\"/>",

            Serialisation.SingleQuotedAttribute =>
                $"<rdf:Description rdf:about=\"\" {Ns} "
                + $"pdfaid:part='{part}' pdfaid:conformance='{conformance}'/>",

            Serialisation.SpacedAttribute =>
                $"<rdf:Description rdf:about=\"\" {Ns} "
                + $"pdfaid:part = \"{part}\" pdfaid:conformance = \"{conformance}\"/>",

            _ => throw new ArgumentOutOfRangeException(nameof(form)),
        };

        return "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
            + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + body
            + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
    }

    private const string Ns = "xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"";

    /// <summary>
    /// A one-page document with enough indirect dictionaries to be worth
    /// compressing, an explicit header version, and optionally a catalog
    /// <c>/Metadata</c> XMP packet. Written by hand so the pdfaid serialisation
    /// is the test's to choose — a fixture produced by excise's own PDF/A writer
    /// could only ever carry the element form, which is precisely the blind spot
    /// #1524 was.
    /// </summary>
    private static byte[] BuildPdf(string version, string? xmp)
    {
        var content = "BT /F1 12 Tf 72 720 Td (Archival body text) Tj ET";
        var packet = xmp == null ? null : Encoding.UTF8.GetBytes(xmp);
        var count = packet == null ? 5 : 6;

        var body = new StringBuilder();
        var offsets = new long[count + 1];
        void Mark(int n) => offsets[n] = body.Length;

        body.Append($"%PDF-{version}\n");
        Mark(1);
        body.Append("1 0 obj <</Type/Catalog/Pages 2 0 R")
            .Append(packet == null ? "" : "/Metadata 6 0 R")
            .Append(">> endobj\n");
        Mark(2);
        body.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3);
        body.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]"
            + "/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>> endobj\n");
        Mark(4);
        body.Append($"4 0 obj <</Length {content.Length}>>\nstream\n{content}\nendstream endobj\n");
        Mark(5);
        body.Append("5 0 obj <</Type/Font/Subtype/Type1/BaseFont/Helvetica"
            + "/Encoding/WinAnsiEncoding>> endobj\n");

        // The XMP packet is ASCII here by construction (see XmpFor), so writing
        // the whole file as Latin-1 keeps byte offsets equal to string indices.
        if (packet != null)
        {
            Mark(6);
            body.Append($"6 0 obj <</Type/Metadata/Subtype/XML/Length {packet.Length}>>\nstream\n")
                .Append(Encoding.Latin1.GetString(packet))
                .Append("\nendstream endobj\n");
        }

        var xrefPos = body.Length;
        body.Append($"xref\n0 {count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= count; i++)
            body.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        body.Append($"trailer <</Size {count + 1}/Root 1 0 R>>\nstartxref\n")
            .Append(xrefPos).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(body.ToString());
    }
}
