using System.Collections.Generic;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1499 — what a form scrub leaves behind, per widget.
///
/// <para>The redaction field scrub used to end with
/// <c>SetAcroFormNeedAppearances()</c> whenever ANYTHING changed. That is wrong
/// twice over: it contradicts #1098 (the flag tells the viewer to discard the
/// appearance that was just rewritten to remove the term's glyphs), and PDF/A
/// forbids the flag outright (ISO 19005-2 6.4.1), so redacting one field
/// silently cost the whole output file its conformance while its XMP went on
/// claiming PDF/A.</para>
///
/// <para>⚠️ <b>These fixtures are not conformant PDF/A files</b> — base-14
/// Helvetica, no OutputIntent — and no test here claims they are. They pin
/// excise's BEHAVIOUR under the PDF/A flag. The conformance verdict belongs to
/// veraPDF and is asserted in <c>PdfATests</c>, because a writer must not grade
/// its own output.</para>
/// </summary>
public class RedactedFormAppearanceConformanceTests
{
    /// <summary>
    /// One-page PDF with a /Tx widget whose /AP/N appearance draws
    /// "Name: SECRET Jones" in Helvetica (WinAnsi — extractable, so the #1098
    /// glyph rewrite applies). With <paramref name="declaresPdfA"/> the catalog
    /// also carries an XMP packet with the <c>pdfaid</c> identifier — the only
    /// thing a document opened from bytes has to say it is archival.
    /// </summary>
    private static byte[] BuildFieldWithAppearance(bool declaresPdfA)
    {
        var ap = "/Tx BMC q BT /Helv 12 Tf 4 4 Td (Name: SECRET Jones) Tj ET Q EMC\n";
        var apBytes = Encoding.Latin1.GetByteCount(ap);

        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">" +
            "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>\n";
        var xmpBytes = Encoding.Latin1.GetByteCount(xmp);

        var objs = new List<string>
        {
            // 1 catalog
            declaresPdfA
                ? "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R /Metadata 8 0 R >>"
                : "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>",
            // 2 pages
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // 3 page (widget 7 is its annotation)
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Annots [7 0 R] /Resources << >> >>",
            // 4 helvetica
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            // 5 appearance stream (Form XObject)
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 4 0 R >> >> /Length {apBytes} >>\nstream\n{ap}endstream",
            // 6 acroform
            "<< /Fields [7 0 R] /DR << /Font << /Helv 4 0 R >> >> >>",
            // 7 widget/field (merged)
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Name: SECRET Jones) /Rect [20 40 220 60] /P 3 0 R /AP << /N 5 0 R >> >>",
        };
        if (declaresPdfA)
        {
            // 8 XMP metadata
            objs.Add($"<< /Type /Metadata /Subtype /XML /Length {xmpBytes} >>\nstream\n{xmp}endstream");
        }

        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Count];
        for (var i = 0; i < objs.Count; i++)
        {
            offs[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static PdfDocument SaveAndReopen(PdfDocument doc)
    {
        using var ms = new System.IO.MemoryStream();
        doc.Save(ms);
        return PdfDocument.Open(ms.ToArray());
    }

    private static PdfDictionary AcroForm(PdfDocument doc) =>
        (doc.Resolve(doc.Catalog.GetOptional("AcroForm")!) as PdfDictionary)!;

    private static PdfDictionary Widget(PdfDocument doc)
    {
        var annots = doc.Resolve(doc.GetPage(1).Dictionary.GetOptional("Annots")!) as PdfArray;
        return (doc.Resolve(annots![0]) as PdfDictionary)!;
    }

    /// <summary>The widget's <c>/AP /N</c> stream, or null when it has none.</summary>
    private static PdfStream? NormalAppearance(PdfDocument doc)
    {
        if (doc.Resolve(Widget(doc).GetOptional("AP") ?? PdfNull.Instance) is not PdfDictionary ap)
            return null;
        return doc.Resolve(ap.GetOptional("N") ?? PdfNull.Instance) as PdfStream;
    }

    /// <summary>
    /// The common path, and the one that was plainly self-contradictory: #1098
    /// rewrites the appearance so it no longer draws the term, and the scrub
    /// then asked every viewer to throw that appearance away. No flag belongs
    /// here — for ANY document, not only an archival one.
    /// </summary>
    [Fact]
    public void RedactingATerm_WhoseAppearanceIsRewritten_LeavesNeedAppearancesUnset()
    {
        using var doc = PdfDocument.Open(BuildFieldWithAppearance(declaresPdfA: false));
        doc.RedactText("SECRET");

        using var after = SaveAndReopen(doc);

        NormalAppearance(after).Should().NotBeNull(
            "#1098: the appearance is rewritten, not dropped — this test is about what happens next");
        AcroForm(after).GetBool("NeedAppearances").Should().BeFalse(
            "the appearance was rewritten to remove the term's glyphs; /NeedAppearances tells the " +
            "viewer to discard it and re-typeset from /V, which undoes #1098 and — on a PDF/A file — " +
            "breaks ISO 19005-2 6.4.1");
    }

    /// <summary>
    /// The fallback branch on an archival document. Area redaction knows only a
    /// rectangle, so it drops <c>/AP</c> wholesale; the widget is then left
    /// appearance-less, which PDF/A also rejects (6.3.3 requires an appearance
    /// on a widget with a non-empty <c>/Rect</c>). An EMPTY appearance is the
    /// substitute: this branch is by definition the one where excise has no
    /// verified text to redraw, and a redacted field has nothing it is entitled
    /// to draw.
    ///
    /// <para><c>scrubDocumentCarriers: false</c> is deliberate:
    /// <c>RedactArea</c>'s default is the WHOLESALE carrier strip, which removes
    /// the catalog <c>/Metadata</c> — i.e. the pdfaid XMP this document is
    /// recognised by. That is a separate defect (#1507: area redaction cannot
    /// produce PDF/A output at all today); pinning it here would only measure
    /// it.</para>
    /// </summary>
    [Fact]
    public void AreaRedaction_OnAPdfADocument_WritesAnEmptyAppearance_InsteadOfNeedAppearances()
    {
        using var doc = PdfDocument.Open(BuildFieldWithAppearance(declaresPdfA: true));
        doc.TargetsPdfA.Should().BeTrue("the fixture's XMP carries the pdfaid identifier");

        doc.GetPage(1).RedactArea(new PdfRectangle(20, 40, 220, 60), scrubDocumentCarriers: false);

        using var after = SaveAndReopen(doc);

        AcroForm(after).GetBool("NeedAppearances").Should().BeFalse(
            "ISO 19005-2 6.4.1 forbids /NeedAppearances; a redacted PDF/A form must not acquire it");
        var appearance = NormalAppearance(after);
        appearance.Should().NotBeNull(
            "ISO 19005-2 6.3.3 requires an appearance on a widget with a non-empty /Rect");
        appearance!.DecodedData.Should().BeEmpty(
            "the substitute appearance must DRAW NOTHING — re-drawing the value here would be " +
            "drawing from a carrier excise could not verify, and the value was just redacted");
    }

    /// <summary>
    /// The behaves-as-before pin (#1499 acceptance: "Non-PDF/A documents behave
    /// as before"). Without a PDF/A declaration the flag is still the right
    /// answer for a widget left without an appearance — a viewer that honours it
    /// redraws the remaining value instead of showing an empty box. This passes
    /// before the fix too; it exists so the conditional cannot be collapsed in
    /// the wrong direction later.
    /// </summary>
    [Fact]
    public void AreaRedaction_OnANonPdfADocument_StillSetsNeedAppearances()
    {
        using var doc = PdfDocument.Open(BuildFieldWithAppearance(declaresPdfA: false));
        doc.TargetsPdfA.Should().BeFalse();

        doc.GetPage(1).RedactArea(new PdfRectangle(20, 40, 220, 60), scrubDocumentCarriers: false);

        using var after = SaveAndReopen(doc);

        AcroForm(after).GetBool("NeedAppearances").Should().BeTrue(
            "outside PDF/A the flag is still how a viewer is told to regenerate the appearance of a " +
            "widget whose /AP had to be dropped");
        NormalAppearance(after).Should().BeNull(
            "no empty appearance is manufactured for a non-archival document — that would change " +
            "what readers show for behaviour #1499 deliberately left alone");
    }

    /// <summary>
    /// The detector itself (#1498/#1499). Both sources have to work: the XMP,
    /// which is all a reopened document has, and the builder's up-front
    /// declaration, which is all a document has BEFORE the save that writes its
    /// XMP.
    /// </summary>
    [Fact]
    public void TargetsPdfA_ReadsTheXmp_AndTheUpFrontDeclaration()
    {
        using var fromXmp = PdfDocument.Open(BuildFieldWithAppearance(declaresPdfA: true));
        fromXmp.TargetsPdfA.Should().BeTrue();

        using var plain = PdfDocument.Open(BuildFieldWithAppearance(declaresPdfA: false));
        plain.TargetsPdfA.Should().BeFalse();
        plain.DeclarePdfATarget();
        plain.TargetsPdfA.Should().BeTrue(
            "PdfDocumentBuilder.PdfA() declares the intent long before the pre-save action writes " +
            "the XMP that records it");
    }
}
