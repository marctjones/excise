using System.Text;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// #1431/#1432/#1434 (one root cause) -- since #923 turned on object-stream
/// compression by default (PDF 1.5+, unencrypted, non-PDF/A-1), AcroForm
/// field/widget dictionaries and font resource dictionaries could land inside
/// a compressed <c>/ObjStm</c>, same as any other non-carrier dictionary.
/// That's spec-valid (qpdf's own decode always saw the data -- confirmed
/// while diagnosing these issues) but breaks a raw-byte scan for
/// <c>/TU</c>/<c>/Widget</c>/a font's <c>/BaseFont</c>, exactly the kind of
/// inspection <see cref="PdfDocumentWriter.ContainsDocumentCarrierText"/>
/// already exists to keep working for <c>/Title</c>/<c>/Author</c>/etc. This
/// class pins the same guarantee for the two dictionary kinds those three
/// issues actually hit.
/// </summary>
public class ObjectStreamGreppabilityTests
{
    [Fact]
    public void WidgetAndFieldDicts_StayOutOfObjectStreams()
    {
        var bytes = PdfDocumentBuilder.Create()
            .TextField("label", tooltip: "some tooltip")
            .Dropdown("choice", ["A", "B"])
            .SaveToBytes();

        var raw = Encoding.Latin1.GetString(bytes);
        raw.Should().Contain("/Widget",
            "widget annotations must stay findable by a raw byte scan, not only via a PDF parser (#1434)");
        System.Text.RegularExpressions.Regex.Matches(raw, @"/TU\s*\(").Count.Should().BeGreaterThan(0,
            "a field's /TU tooltip must stay findable by a raw byte scan (#1431)");
    }

    [Fact]
    public void FontDicts_StayOutOfObjectStreams()
    {
        var fontBytes = Fixtures.TestFontFixtures.LoadDejaVuSansBytes();
        var bytes = PdfDocumentBuilder.Create()
            .DefaultFont(PdfFont.FromTrueType(fontBytes, 11))
            .PdfA()
            .Paragraph("Hello — café · naïve")
            .SaveToBytes();

        var raw = Encoding.Latin1.GetString(bytes);
        raw.Should().Contain("DejaVuSans",
            "an embedded font's name must stay findable by a raw byte scan, not only via a PDF parser (#1432)");
    }

    // ---------------------------------------------------------------------
    // The two shapes a TYPE-MARKER predicate misses. Both are load-and-save
    // cases: PdfDocumentBuilder only ever authors merged terminal field+widget
    // dictionaries and well-formed /Type /Font dicts, so neither of the two
    // tests above can exercise them -- which is exactly how the gap shipped.
    // ---------------------------------------------------------------------

    /// <summary>
    /// ISO 32000-2 §12.7.3.2: a field's type is on the terminal leaf and
    /// ancestors inherit it, so a NON-TERMINAL field node legitimately carries
    /// <c>/T</c> and <c>/TU</c> with <c>/Kids</c> but no <c>/FT</c> and no
    /// <c>/Subtype /Widget</c>. Measured on the smoke corpus, this is not
    /// hypothetical: 6 such nodes in irs-w4, 4 in irs-w9, 30 in irs-1040.
    /// A predicate keyed on type markers packs them into an <c>/ObjStm</c>,
    /// reproducing #1431's original symptom on the load-edit-save path.
    /// </summary>
    [Fact]
    public void NonTerminalFieldNode_TextCarriers_StayOutOfObjectStreams()
    {
        var saved = SaveThroughWriter(BuildHierarchicalFormPdf());
        var raw = Encoding.Latin1.GetString(saved);

        raw.Should().Contain("/Type /ObjStm",
            "the test is vacuous unless the writer actually built an object stream");

        raw.Should().Contain("PARENT_TOOLTIP_CANARY",
            "a non-terminal field node's /TU must stay findable by a raw byte scan "
            + "even though the node has no /FT and no /Subtype /Widget (#1431)");
        raw.Should().Contain("PARENT_FIELD_NAME_CANARY",
            "a non-terminal field node's /T must stay findable by a raw byte scan");

        // Controls: the shapes 1f53e9ee already covered must not regress.
        raw.Should().Contain("TerminalChildTooltip",
            "a terminal field's /TU must remain greppable");
    }

    /// <summary>
    /// <c>/Type</c> is routinely absent from font dictionaries in permissive
    /// real-world files, leaving <c>/BaseFont</c> as the only font identity.
    /// A predicate keyed on <c>/Type /Font</c> misses those (#1432).
    /// </summary>
    [Fact]
    public void FontDictWithoutTypeKey_StaysOutOfObjectStreams()
    {
        var saved = SaveThroughWriter(BuildHierarchicalFormPdf());
        var raw = Encoding.Latin1.GetString(saved);

        raw.Should().Contain("/Type /ObjStm",
            "the test is vacuous unless the writer actually built an object stream");

        raw.Should().Contain("CanaryFontNoType",
            "a font dictionary's /BaseFont must stay findable by a raw byte scan "
            + "even when the dictionary carries no /Type /Font (#1432)");

        // Control: the well-formed font dict 1f53e9ee already covered.
        raw.Should().Contain("CanaryFontWithType",
            "a /Type /Font dictionary's /BaseFont must remain greppable");
    }

    private static byte[] SaveThroughWriter(byte[] source)
    {
        using var doc = PdfDocument.Open(source);
        return doc.SaveToBytes();
    }

    /// <summary>
    /// A PDF 1.5 file whose AcroForm field tree has a genuine non-terminal
    /// parent, plus two font dictionaries differing only in whether they
    /// declare <c>/Type /Font</c>. The padding dictionaries exist so the writer
    /// has enough packable objects to build an object stream at all -- without
    /// them <c>CanPackIntoObjectStream</c> finds nothing to pack and every
    /// assertion here would pass for the wrong reason.
    /// </summary>
    private static byte[] BuildHierarchicalFormPdf()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 0 Tf 0 g) >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 8 0 R "
                + "/Resources << /Font << /FA 6 0 R /FB 7 0 R >> >> /Annots [5 0 R] >>",
            // 4: NON-TERMINAL parent field -- /T and /TU, /Kids, but no /FT and
            //    no /Subtype /Widget. This is the shape type markers miss.
            "<< /T (PARENT_FIELD_NAME_CANARY) /TU (PARENT_TOOLTIP_CANARY) /Kids [5 0 R] >>",
            // 5: terminal child field + widget (the already-covered control).
            "<< /Type /Annot /Subtype /Widget /FT /Tx /Parent 4 0 R /T (child) "
                + "/TU (TerminalChildTooltip) /Rect [100 700 300 720] /F 4 >>",
            // 6: font dict with NO /Type -- identity only in /BaseFont.
            "<< /Subtype /Type1 /BaseFont /CanaryFontNoType /Encoding /WinAnsiEncoding >>",
            // 7: well-formed font dict (the already-covered control).
            "<< /Type /Font /Subtype /Type1 /BaseFont /CanaryFontWithType /Encoding /WinAnsiEncoding >>",
        };

        const string content = "BT /FA 12 Tf 72 720 Td (hello) Tj ET";
        objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");

        for (var i = 0; i < 30; i++)
            objects.Add($"<< /PadKey{i} /PadValue{i} /Note (padding object {i}) >>");

        return Assemble(objects);
    }

    /// <summary>Assemble literal object bodies into a PDF with a classic xref table.</summary>
    private static byte[] Assemble(List<string> objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.5\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefOffset = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets) sb.Append($"{off:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n"
            + $"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
