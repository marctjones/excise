using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #355: redaction must reach content drawn by Form XObjects. The text/image
/// passes only see the page content stream, so a form painting over the
/// redaction area would be merely covered. RedactArea flattens overlapping
/// forms into the page (so the existing passes can remove their content) and
/// then frees the now-orphaned form objects — without that prune the writer,
/// which serializes every in-use object, would re-emit the form and leak the
/// very text the redaction removed.
/// </summary>
public class FormXObjectRedactionTests
{
    [Fact]
    public void RedactArea_OverFormText_RemovesItFromSavedPdfBytes()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm0 6 0 R >> >> >>"),
            Stream("", "q /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 5 0 R >> >>",
                   "BT /F1 12 Tf 100 700 Td (SECRETFORM) Tj ET"));

        Encoding.Latin1.GetString(pdf).Should().Contain("SECRETFORM");

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(90, 695, 250, 716));
        var saved = doc.SaveToBytes();

        Encoding.Latin1.GetString(saved).Should().NotContain("SECRETFORM",
            "the form's redacted text must be absent from the saved bytes, not merely covered");

        using var re = PdfDocument.Open(saved);
        string.Concat(re.GetPage(1).Letters.Select(l => l.Value))
            .Should().NotContain("SECRETFORM");
    }

    [Fact]
    public void RedactArea_FormWithTwoStrings_RemovesInsideKeepsOutside()
    {
        // Proves flattening reproduces the form faithfully: only the string in
        // the redaction band is removed; the other survives, correctly placed.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm0 6 0 R >> >> >>"),
            Stream("", "q /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 5 0 R >> >>",
                   "BT /F1 12 Tf 100 700 Td (INSIDEBAND) Tj ET " +
                   "BT /F1 12 Tf 100 600 Td (OUTSIDEBAND) Tj ET"));

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(90, 695, 300, 716));
        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().NotContain("INSIDEBAND", "the string in the redaction band must be removed");
        saved.Should().Contain("OUTSIDEBAND", "the string outside the band must survive flattening");
    }

    [Fact]
    public void RedactArea_NestedForm_RemovesInnermostText()
    {
        // Page → Fm0 (outer form) → Fm1 (inner form, draws the secret).
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm0 6 0 R >> >> >>"),
            Stream("", "q /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                   "/Resources << /XObject << /Fm1 7 0 R >> >>",
                   "q /Fm1 Do Q"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 5 0 R >> >>",
                   "BT /F1 12 Tf 100 700 Td (NESTEDSECRET) Tj ET"));

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(90, 695, 280, 716));
        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().NotContain("NESTEDSECRET",
            "text drawn by a nested form must be removed and both orphaned forms pruned");
    }

    [Fact]
    public void RedactArea_FormSharedByAnotherPage_LeavesOtherPageIntact()
    {
        // Both pages invoke the same form. Redacting page 1 must not disturb
        // page 2's rendering — the shared form object stays (still reachable).
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm0 6 0 R >> >> >>"),
            Stream("", "q /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 5 0 R >> >>",
                   "BT /F1 12 Tf 100 700 Td (SHAREDFORM) Tj ET"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 8 0 R " +
                "/Resources << /XObject << /Fm0 6 0 R >> >> >>"),
            Stream("", "q /Fm0 Do Q"));

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(90, 695, 250, 716));
        var saved = doc.SaveToBytes();

        // The shared form is still reachable from page 2, so it must NOT be
        // pruned — its content legitimately remains for the untouched page.
        // (The text extractor doesn't recurse into forms, so page 2's Letters
        // are empty either way; the durable contract is "object preserved".)
        Encoding.Latin1.GetString(saved).Should().Contain("SHAREDFORM",
            "a form still referenced by another page must survive the prune");

        using var re = PdfDocument.Open(saved);
        string.Concat(re.GetPage(1).Letters.Select(l => l.Value))
            .Should().NotContain("SHAREDFORM", "page 1's inlined copy is redacted");
        re.GetPage(2).GetXObject("Fm0").Should().NotBeNull(
            "page 2 still resolves the shared form");
    }

    [Fact]
    public void RedactArea_FormResourceNameCollision_RenamesAndKeepsBothFonts()
    {
        // Page /F1 and the form's /F1 are DIFFERENT font objects. Flattening
        // must rename the form's font so both resolve; the surviving form text
        // and the page text must both extract correctly after redaction.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 7 0 R >> >> >>"),
            Stream("", "BT /F1 12 Tf 100 500 Td (PAGEOWNTEXT) Tj ET q /Fm0 Do Q"),
            Obj(HelveticaFont),  // page's /F1
            Obj(HelveticaFont),  // form's /F1 — a distinct object (obj 6)
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 6 0 R >> >>",
                   "BT /F1 12 Tf 100 700 Td (FORMGONE) Tj ET " +
                   "BT /F1 12 Tf 100 600 Td (FORMKEEP) Tj ET"));

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(90, 695, 250, 716));

        using var re = PdfDocument.Open(doc.SaveToBytes());
        var text = string.Concat(re.GetPage(1).Letters.Select(l => l.Value));
        text.Should().NotContain("FORMGONE", "the redacted form string is removed");
        text.Should().Contain("FORMKEEP", "the form string outside the band survives with a resolvable font");
        text.Should().Contain("PAGEOWNTEXT", "the page's own text is untouched");
    }

    [Fact]
    public void RedactArea_FormWhoseBBoxMissesArea_IsLeftAsDoAndNotFlattened()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm0 6 0 R >> >> >>"),
            Stream("", "q /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 60 60] " +
                   "/Resources << /Font << /F1 5 0 R >> >>",
                   "BT /F1 8 Tf 5 25 Td (CORNERONLY) Tj ET"));

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(400, 700, 560, 740)); // far from the form
        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().Contain("CORNERONLY",
            "a form whose bbox doesn't intersect the area must be left untouched");
        saved.Should().Contain("/Fm0", "the Do invocation must survive (not flattened)");
    }

    [Fact]
    public void RedactArea_FormWithMatrixUnderPageCm_FlattensAndRedacts()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm0 6 0 R >> >> >>"),
            Stream("", "q 1 0 0 1 0 0 cm /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Matrix [1 0 0 1 30 0] " +
                   "/Resources << /Font << /F1 5 0 R >> >>",
                   "BT /F1 12 Tf 70 700 Td (MATRIXTEXT) Tj ET")); // +30 x => ~page (100,700)

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(90, 695, 300, 716));
        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().NotContain("MATRIXTEXT",
            "the matrix-positioned form text inside the area must be removed");
    }

    [Fact]
    public void RedactArea_PageWithImageAndForm_RedactsFormLeavesImageDo()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Im0 7 0 R /Fm0 6 0 R >> >> >>"),
            Stream("", "q 1 0 0 1 0 500 cm 1 0 0 1 0 0 cm /Im0 Do Q q /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 5 0 R >> >>",
                   "BT /F1 12 Tf 100 700 Td (FORMOVERIMAGE) Tj ET"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 " +
                   "/BitsPerComponent 8 /ColorSpace /DeviceGray", "\xFF"));

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(90, 695, 280, 716));
        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().NotContain("FORMOVERIMAGE", "the form text is flattened and redacted");
        doc.GetPage(1).GetXObject("Im0").Should().NotBeNull("the image XObject is untouched");
    }

    [Fact]
    public void RedactArea_FormViaInheritedPageResources_IsFlattened()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /XObject << /Fm0 6 0 R >> >> >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>"),
            Stream("", "q /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 5 0 R >> >>",
                   "BT /F1 12 Tf 100 700 Td (INHERITEDRES) Tj ET"));

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(90, 695, 260, 716));

        // The form is reached via the Pages node's /Resources, so it stays
        // reachable (every page under that node inherits it) and is NOT pruned
        // — like a shared form. The page's inlined copy is still redacted.
        using var re = PdfDocument.Open(doc.SaveToBytes());
        var pageContent = Encoding.Latin1.GetString(re.GetPage(1).GetContentStreamBytes());
        pageContent.Should().NotContain("INHERITEDRES",
            "the flattened page content must have the form text removed");
    }

    [Fact]
    public void RedactArea_FormWithCollidingExtGStateAndRealMatrix_RenamesAndFlattens()
    {
        // Page and form both define /GS0 (different objects) → the flattener
        // must rename the form's into the page resources and rewrite its `gs`
        // operand. The form's /Matrix uses reals and a non-identity translation.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm0 6 0 R >> /ExtGState << /GS0 7 0 R >> >> >>"),
            Stream("", "q /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Matrix [1.0 0 0 1.0 5.5 0] " +
                   "/Resources << /ExtGState << /GS0 8 0 R >> /Font << /F1 5 0 R >> >>",
                   "/GS0 gs BT /F1 12 Tf 94.5 700 Td (GSCOLLIDE) Tj ET"),
            Obj("<< /Type /ExtGState /ca 1 >>"),    // page's GS0
            Obj("<< /Type /ExtGState /ca 0.5 >>")); // form's GS0 (different object)

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(90, 695, 300, 716));

        using var re = PdfDocument.Open(doc.SaveToBytes());
        var page = re.GetPage(1);
        Encoding.Latin1.GetString(page.GetContentStreamBytes())
            .Should().NotContain("GSCOLLIDE", "the matrix-positioned form text is redacted");
        page.Resources!.GetDictionaryOrNull("ExtGState")!.Count
            .Should().BeGreaterThanOrEqualTo(2,
                "the form's colliding /GS0 must be merged under a fresh name, keeping both");
    }

    [Fact]
    public void FlattenOverlapping_FormCollidingEveryResourceCategory_RenamesAndRewritesAllOps()
    {
        // A form colliding with the page across ExtGState/ColorSpace/Shading/
        // Pattern/Properties/XObject (each name → a different object) plus an
        // inline image whose /CS names a colliding colorspace. Drives every
        // resource-rewrite branch (gs/cs/sh/scn/BDC/Do/BI-CS) and the rename
        // machinery. Calls the flattener directly to avoid text-extraction.
        var pageRes =
            "/Resources << /XObject << /Fm0 6 0 R /Im0 7 0 R >> " +
            "/ExtGState << /GS0 << /ca 1 >> >> /ColorSpace << /CS0 /DeviceRGB >> " +
            "/Shading << /Sh0 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 1] >> >> " +
            "/Pattern << /Pat0 << /PatternType 1 >> >> /Properties << /MC0 << /Type /OCG >> >> >>";
        var formRes =
            "/Resources << /ExtGState << /GS0 << /ca 0.5 >> >> /ColorSpace << /CS0 /DeviceGray >> " +
            "/Shading << /Sh0 << /ShadingType 3 /ColorSpace /DeviceGray /Coords [0 0 0 0 1 1] >> >> " +
            "/Pattern << /Pat0 << /PatternType 2 >> >> /Properties << /MC0 << /Type /Pagination >> >> " +
            "/XObject << /Im0 8 0 R >> /Font << /F1 5 0 R >> >>";
        var formContent =
            "/GS0 gs /CS0 cs /Pat0 scn /Sh0 sh /OC /MC0 BDC /Im0 Do EMC " +
            "BI /W 1 /H 1 /BPC 8 /CS /CS0 /L 1 ID \xFF EI " +
            "BT /F1 12 Tf 100 700 Td (X) Tj ET";
        var img = "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray";

        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R {pageRes} >>"),
            Stream("", "q /Fm0 Do Q"),
            Obj(HelveticaFont),
            Stream($"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Matrix [1.0 0 0 1.0 5.5 0] {formRes}",
                   formContent),
            Stream(img, "\xFF"),   // page's /Im0
            Stream(img, "\x80"));  // form's /Im0 (different object)

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var ops = page.GetContentStream().Operators;

        bool changed = FormXObjectFlattener.FlattenOverlapping(
            page, ops, new PdfRectangle(90, 695, 300, 716), out var output, out var inlined);

        changed.Should().BeTrue("the overlapping form must be inlined");
        inlined.Should().Contain(6, "the form object number is recorded for pruning");
        output.Should().Contain(o => o.Name == "BI", "the form's inline image is inlined");

        // Each colliding category gained a renamed entry, so the form's content
        // resolves against the page after flattening.
        foreach (var cat in new[] { "ExtGState", "ColorSpace", "Shading", "Pattern", "Properties" })
            page.Resources!.GetDictionaryOrNull(cat)!.Count
                .Should().BeGreaterThanOrEqualTo(2, $"{cat} should hold both the page's and the renamed form's entry");

        // The form's `gs` operand was rewritten away from the original name.
        var gs = output.First(o => o.Name == "gs");
        gs.GetName(0).Should().NotBe("GS0", "the colliding ExtGState name must be rewritten");
    }

    // ---- builders ----

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private static byte[] Obj(string dict) => Encoding.Latin1.GetBytes(dict);

    private static byte[] Stream(string dictExtra, string content)
    {
        var data = Encoding.Latin1.GetBytes(content);
        var head = Encoding.Latin1.GetBytes($"<< {dictExtra} /Length {data.Length} >>\nstream\n");
        var tail = Encoding.Latin1.GetBytes("\nendstream");
        return head.Concat(data).Concat(tail).ToArray();
    }

    /// <summary>Assemble object bodies (object 1..N) into a valid PDF file.</summary>
    private static byte[] Build(params byte[][] objects)
    {
        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }

        W("%PDF-1.4\n");
        var offsets = new long[objects.Length + 1];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            W($"{i + 1} 0 obj\n");
            ms.Write(objects[i], 0, objects[i].Length);
            W("\nendobj\n");
        }

        long xref = ms.Position;
        W($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Length; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        W($"trailer\n<< /Root 1 0 R /Size {objects.Length + 1} >>\nstartxref\n{xref}\n%%EOF");

        return ms.ToArray();
    }
}
