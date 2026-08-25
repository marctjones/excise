using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1115 — leak score by CANARY INJECTION, not term sampling.
///
/// <para>Sampling a real document's own words to measure leaks cannot work even
/// in principle: leak rate tracked term FREQUENCY (12% rare → 57% frequent),
/// because a common word ("your") lives in JavaScript, field names and other
/// carriers that have nothing to do with the redaction. A unique token removes
/// the confound entirely:</para>
///
/// <list type="bullet">
///   <item>it cannot occur incidentally, so any survival IS a leak;</item>
///   <item>its absence everywhere is a real pass, not a lucky sample.</item>
/// </list>
///
/// <para>One tiny corpus-free fixture per carrier, the token placed in exactly
/// ONE of them; redact the token; then scan EVERY carrier with the
/// carrier-agnostic, decompress-aware <see cref="SavedPdfLeakScanner"/>. The
/// result is a checklist with a definite answer per row, not a probability.
/// This is the measurement #1114 could not make.</para>
/// </summary>
public class CanaryInjectionLeakTests
{
    // Deliberately unique and un-guessable. Long enough to clear the 3-char
    // carrier-scrub floor, and hyphenated so it survives verbatim in dict values.
    private const string Canary = "EXCISE-CANARY-7Q4F";

    public enum Carrier
    {
        PageContent,        // the baseline
        TjSplit,            // canary split across a TJ array — defeats byte matching
        FormXObject,        // #355 / #1040
        AnnotationContents, // #608
        AcroFormValue,      // /V — #1038
        AcroFormFieldName,  // /T — #1130 (fixed this session)
        AcroFormTooltip,    // /TU — #1130
        StructActualText,   // /ActualText — #636
        InfoTitle,          // /Info — #608
        XmpCatalog,         // catalog /Metadata — #608
        XmpPage,            // page /Metadata — #1129 (fixed this session)
        OutlineTitle,       // #608
        EmbeddedFile,       // never tested before #1115
        JavaScript,         // never tested before #1115
    }

    // Carriers that CURRENTLY leak — RedactText's document-level scrub does not
    // yet cover them (measured, not assumed: ScrubTerms covers /Info, XMP,
    // /T+/TU, outlines and annotation /Contents, and nothing else). Listed so the
    // checklist stays green while each leak is tracked, exactly as the issue asks
    // ("a carrier checklist with a definite answer per row"). Bidirectionally
    // enforced below: a listed carrier that has become clean FAILS until its
    // entry is deleted, so the checklist can never drift back to claiming
    // coverage it lost. Each MUST carry an issue number. All four are #1151.
    // #1151 CLOSED — all four carriers the #1115 checklist found are now scrubbed
    // by ScrubTerms and stay clean, verified here: StructActualText
    // (ScrubStructTree), AcroFormValue (/V,/DV), JavaScript (ScrubJavaScript),
    // EmbeddedFile (ScrubEmbeddedFiles removes the matching attachment). An entry
    // reappearing here is a regression; the checklist enforces both directions.
    private static readonly Dictionary<Carrier, string> KnownLeaks = new();

    [Theory]
    [InlineData(Carrier.PageContent)]
    [InlineData(Carrier.TjSplit)]
    [InlineData(Carrier.FormXObject)]
    [InlineData(Carrier.AnnotationContents)]
    [InlineData(Carrier.AcroFormValue)]
    [InlineData(Carrier.AcroFormFieldName)]
    [InlineData(Carrier.AcroFormTooltip)]
    [InlineData(Carrier.StructActualText)]
    [InlineData(Carrier.InfoTitle)]
    [InlineData(Carrier.XmpCatalog)]
    [InlineData(Carrier.XmpPage)]
    [InlineData(Carrier.OutlineTitle)]
    [InlineData(Carrier.EmbeddedFile)]
    [InlineData(Carrier.JavaScript)]
    public void RedactingTheCanary_RemovesItFromEveryCarrier(Carrier carrier)
    {
        var pdf = BuildCanaryPdf(carrier);

        // TjSplit lives in the content stream as three kerned fragments, so the
        // contiguous token is NOT in the bytes — a byte scan cannot see it in
        // either direction (the exact confound #1114 hit). Measure it through the
        // extractor, which reassembles the run, instead of the carrier scanner.
        if (carrier == Carrier.TjSplit)
        {
            using (var before = PdfDocument.Open(pdf))
                before.GetPage(1).Text.Replace(" ", "").Should().Contain(Canary,
                    "guard: the extractor must reassemble the split canary before redaction");
            using var doc0 = PdfDocument.Open(pdf);
            doc0.RedactText(Canary);
            doc0.GetPage(1).Text.Replace(" ", "").Should().NotContain(Canary,
                "the split canary must be gone from the extracted text after redaction");
            return;
        }

        // GUARD: the fixture must actually put the canary in this carrier, or the
        // assertion below passes on a document that never held it — the exact
        // way three leaks shipped past a green suite (CLAUDE.md).
        SavedPdfLeakScanner.FindTerm(pdf, Canary).Should().NotBeEmpty(
            $"the {carrier} fixture must contain the canary before redaction");

        byte[] saved;
        using (var doc = PdfDocument.Open(pdf))
        {
            doc.RedactText(Canary);
            using var ms = new MemoryStream();
            doc.Save(ms);
            saved = ms.ToArray();
        }

        var hits = SavedPdfLeakScanner.FindTerm(saved, Canary);

        if (KnownLeaks.TryGetValue(carrier, out var issue))
        {
            hits.Should().NotBeEmpty(
                $"{carrier} is a KNOWN unscrubbed carrier ({issue}); if it is now clean, " +
                "delete its KnownLeaks entry so the checklist tells the truth");
            return;
        }

        hits.Should().BeEmpty(
            $"redacting the canary must remove it from the {carrier} carrier — a survival " +
            "here is a definite leak (the token cannot occur incidentally)");
    }

    [Fact]
    public void EmbeddedFileScrub_RemovesTheAttachmentWithTheTerm_KeepsUnrelatedOnes()
    {
        // #1151 — selective, not wholesale: an attachment containing the term is
        // removed; an UNRELATED attachment must survive (over-removal is
        // collateral). Two attachments, one carries the canary.
        var content = "BT /F1 14 Tf 72 700 Td (Body text) Tj ET\n";
        var body = Encoding.Latin1.GetBytes(content);
        var secret = $"note: {Canary}\n";
        var keep = "unrelated attachment content\n";
        var pdf = Encoding.Latin1.GetBytes(
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 6 0 R >> >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n" +
            $"4 0 obj\n<< /Length {body.Length} >>\nstream\n{content}endstream\nendobj\n" +
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" +
            "6 0 obj\n<< /Names [(secret.txt) 7 0 R (keep.txt) 8 0 R] >>\nendobj\n" +
            "7 0 obj\n<< /Type /Filespec /F (secret.txt) /EF << /F 9 0 R >> >>\nendobj\n" +
            "8 0 obj\n<< /Type /Filespec /F (keep.txt) /EF << /F 10 0 R >> >>\nendobj\n" +
            $"9 0 obj\n<< /Type /EmbeddedFile /Length {secret.Length} >>\nstream\n{secret}endstream\nendobj\n" +
            $"10 0 obj\n<< /Type /EmbeddedFile /Length {keep.Length} >>\nstream\n{keep}endstream\nendobj\n" +
            "trailer\n<< /Root 1 0 R /Size 11 >>\n%%EOF\n");

        byte[] saved;
        using (var doc = PdfDocument.Open(pdf))
        {
            doc.RedactText(Canary);
            using var ms = new MemoryStream();
            doc.Save(ms);
            saved = ms.ToArray();
        }

        SavedPdfLeakScanner.FindTerm(saved, Canary).Should().BeEmpty(
            "the attachment carrying the term must be removed");
        using var re = PdfDocument.Open(saved);
        re.GetEmbeddedFiles().Should().ContainSingle(f => f.FileName == "keep.txt",
            "the unrelated attachment must survive — removal is selective, not wholesale");
    }

    // ── fixture assembly ────────────────────────────────────────────────────

    /// <summary>
    /// A one-page PDF with the canary in exactly <paramref name="carrier"/>. The
    /// page always carries innocent body text so it is a valid page whichever
    /// carrier is exercised; RedactText scrubs document-level carriers even when
    /// no glyph on the page matches (#896).
    /// </summary>
    private static byte[] BuildCanaryPdf(Carrier carrier)
    {
        string PageText() => carrier switch
        {
            Carrier.PageContent => $"BT /F1 14 Tf 72 700 Td (Body {Canary} text) Tj ET\n",
            Carrier.TjSplit =>
                // Canary split across TJ elements with kerning — the non-contiguous
                // case that defeats a raw byte scan of the content stream.
                "BT /F1 14 Tf 72 700 Td [(EXCISE-) -30 (CANARY-) -30 (7Q4F)] TJ ET\n",
            // The XObject must actually be DRAWN, or its canary is never rendered,
            // never extracted, never matched — a fixture that proves nothing.
            Carrier.FormXObject => "q 1 0 0 1 100 600 cm /Fm0 Do Q\n",
            _ => "BT /F1 14 Tf 72 700 Td (Body text only) Tj ET\n",
        };

        var content = Encoding.Latin1.GetBytes(PageText());

        var catalogExtras = new StringBuilder();
        var pageExtras = new StringBuilder();
        var extraObjects = new List<string>();   // object bodies for obj 6..N
        int nextObj = 6;
        int Reserve() => nextObj++;

        switch (carrier)
        {
            case Carrier.FormXObject:
            {
                int xo = Reserve();
                pageExtras.Append($" /Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 {xo} 0 R >> >>");
                var xoContent = $"BT /F1 12 Tf 5 20 Td ({Canary}) Tj ET\n";
                extraObjects.Add($"{xo} 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 300 60] " +
                    $"/Resources << /Font << /F1 5 0 R >> >> /Length {xoContent.Length} >>\nstream\n{xoContent}endstream\nendobj\n");
                break;
            }
            case Carrier.AnnotationContents:
            {
                int an = Reserve();
                pageExtras.Append($" /Annots [{an} 0 R]");
                extraObjects.Add($"{an} 0 obj\n<< /Type /Annot /Subtype /Text /Rect [72 700 92 720] " +
                    $"/Contents ({Canary}) >>\nendobj\n");
                break;
            }
            case Carrier.AcroFormValue:
            case Carrier.AcroFormFieldName:
            case Carrier.AcroFormTooltip:
            {
                int fld = Reserve();
                catalogExtras.Append($" /AcroForm << /Fields [{fld} 0 R] >>");
                var t  = carrier == Carrier.AcroFormFieldName ? Canary : "field1";
                var tu = carrier == Carrier.AcroFormTooltip   ? Canary : "Enter a value";
                var v  = carrier == Carrier.AcroFormValue     ? Canary : "unrelated";
                extraObjects.Add($"{fld} 0 obj\n<< /FT /Tx /T ({t}) /TU ({tu}) /V ({v}) >>\nendobj\n");
                break;
            }
            case Carrier.StructActualText:
            {
                int st = Reserve(); int el = Reserve();
                catalogExtras.Append($" /StructTreeRoot {st} 0 R");
                extraObjects.Add($"{st} 0 obj\n<< /Type /StructTreeRoot /K {el} 0 R >>\nendobj\n");
                extraObjects.Add($"{el} 0 obj\n<< /Type /StructElem /S /P /ActualText ({Canary}) >>\nendobj\n");
                break;
            }
            case Carrier.InfoTitle:
                // /Info is wired through the trailer below.
                break;
            case Carrier.XmpCatalog:
            {
                int md = Reserve();
                catalogExtras.Append($" /Metadata {md} 0 R");
                extraObjects.Add(XmpObject(md));
                break;
            }
            case Carrier.XmpPage:
            {
                int md = Reserve();
                pageExtras.Append($" /Metadata {md} 0 R");
                extraObjects.Add(XmpObject(md));
                break;
            }
            case Carrier.OutlineTitle:
            {
                int ol = Reserve(); int item = Reserve();
                catalogExtras.Append($" /Outlines {ol} 0 R");
                extraObjects.Add($"{ol} 0 obj\n<< /Type /Outlines /First {item} 0 R /Last {item} 0 R /Count 1 >>\nendobj\n");
                extraObjects.Add($"{item} 0 obj\n<< /Title ({Canary}) /Parent {ol} 0 R >>\nendobj\n");
                break;
            }
            case Carrier.EmbeddedFile:
            {
                int names = Reserve(); int ef = Reserve(); int fs = Reserve();
                catalogExtras.Append($" /Names << /EmbeddedFiles {names} 0 R >>");
                extraObjects.Add($"{names} 0 obj\n<< /Names [(attachment.txt) {fs} 0 R] >>\nendobj\n");
                extraObjects.Add($"{fs} 0 obj\n<< /Type /Filespec /F (attachment.txt) /EF << /F {ef} 0 R >> >>\nendobj\n");
                var efData = $"secret note: {Canary}\n";
                extraObjects.Add($"{ef} 0 obj\n<< /Type /EmbeddedFile /Length {efData.Length} >>\nstream\n{efData}endstream\nendobj\n");
                break;
            }
            case Carrier.JavaScript:
            {
                int names = Reserve(); int act = Reserve();
                catalogExtras.Append($" /Names << /JavaScript {names} 0 R >>");
                extraObjects.Add($"{names} 0 obj\n<< /Names [(canaryScript) {act} 0 R] >>\nendobj\n");
                extraObjects.Add($"{act} 0 obj\n<< /S /JavaScript /JS (var note = \"{Canary}\";) >>\nendobj\n");
                break;
            }
        }

        var infoValue = carrier == Carrier.InfoTitle ? Canary : "Untitled";

        // Assemble with a real xref so PdfDocument opens it cleanly.
        var sb = new StringBuilder();
        var offsets = new List<long>();
        void Obj(string body)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append(body);
        }

        sb.Append("%PDF-1.7\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R{catalogExtras} >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var resources = pageExtras.ToString().Contains("/Resources")
            ? ""
            : " /Resources << /Font << /F1 5 0 R >> >>";
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R{resources}{pageExtras} >>\nendobj\n");
        offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
        sb.Append($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        sb.Append(Encoding.Latin1.GetString(content));
        sb.Append("\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        foreach (var o in extraObjects) Obj(o);
        int infoNum = nextObj;
        Obj($"{infoNum} 0 obj\n<< /Title ({infoValue}) /Author (excise) >>\nendobj\n");

        // No cross-reference table needed — PdfDocument reconstructs from object
        // scanning, and every fixture here is small. The trailer names the Info.
        sb.Append($"trailer\n<< /Root 1 0 R /Info {infoNum} 0 R /Size {infoNum + 1} >>\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string XmpObject(int num)
    {
        var xmp =
            "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?>" +
            "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF " +
            "xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
            "<rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/'>" +
            $"<dc:title>{Canary}</dc:title></rdf:Description></rdf:RDF>" +
            "</x:xmpmeta><?xpacket end='w'?>";
        return $"{num} 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream\nendobj\n";
    }
}
