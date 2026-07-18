using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #670 and #671: both are the same underlying fallback — resolve a widget's
/// field/page association from the page's own /Annots array rather than
/// trusting incomplete or missing /AcroForm linkage metadata.
///
/// #670: a Widget annotation may legally BE its own field dictionary (a
/// "merged" field/widget, §12.7.3.1). Such a widget is invisible to
/// <see cref="PdfPage.GetFormFields"/> when it isn't reachable by walking
/// /AcroForm/Fields, even though it lives in the page's own /Annots array
/// with real /FT and /V entries.
///
/// #671: a widget's /P (page) entry is OPTIONAL per spec — page association
/// can be established purely by the widget's presence in a page's own
/// /Annots array. When /P is absent, <see cref="PdfAcroFormParser"/> must
/// still resolve the page instead of leaving <see cref="PdfField.PageNumber"/>
/// null (which <see cref="PdfPage.GetFormFields"/>'s <c>PageNumber == pageNum</c>
/// filter would otherwise silently drop).
/// </summary>
public sealed class OrphanedWidgetPageResolutionTests
{
    // ─── #670: merged field/widget outside /AcroForm/Fields ────────────────

    [Fact]
    public void GetFormFields_MergedWidgetNotInAcroFormFields_IsSurfaced()
    {
        // No /AcroForm at all — the widget's /FT + /V are the only signal
        // that it's a field, exactly like issue17069.pdf's SANDBOX watermark
        // and "test value" widgets (#670).
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Orphan) " +
                "/V (ORPHANVALUE) /Rect [100 650 260 675] /P 3 0 R >>"));

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        var fields = page.GetFormFields();

        fields.Should().ContainSingle();
        fields[0].FullName.Should().Be("Orphan");
        fields[0].Value.Should().Be("ORPHANVALUE");
        fields[0].PageNumber.Should().Be(1);
    }

    [Fact]
    public void GetFormFields_MergedWidgetOmittedFromExistingAcroFormFields_IsSurfaced()
    {
        // /AcroForm DOES exist and has other, properly-linked fields — the
        // orphan is simply missing from /Fields, which is the more common
        // real-world shape (a producer/incremental-update bug omits one
        // widget from the tree without removing it from the page).
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R 6 0 R] /Resources << >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Orphan) " +
                "/V (ORPHANVALUE) /Rect [100 650 260 675] /P 3 0 R >>"),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Linked) " +
                "/V (LINKEDVALUE) /Rect [100 600 260 625] /P 3 0 R >>"));

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        var fields = page.GetFormFields();

        fields.Select(f => f.FullName).Should().BeEquivalentTo("Orphan", "Linked");
        fields.Single(f => f.FullName == "Orphan").Value.Should().Be("ORPHANVALUE");
        fields.Single(f => f.FullName == "Linked").Value.Should().Be("LINKEDVALUE");
    }

    [Fact]
    public void GetFormFields_WidgetWithoutFT_IsNotTreatedAsOrphanedField()
    {
        // A plain Widget annotation with no field semantics of its own (no
        // /FT) must NOT be surfaced as a field — only a genuinely merged
        // field/widget (carrying /FT directly) should be.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /Rect [100 650 260 675] /P 3 0 R >>"));

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).GetFormFields().Should().BeEmpty();
    }

    [Fact]
    public void GetFormFields_ProperlyLinkedWidgetAlsoInAnnots_IsNotDoubleCounted()
    {
        // Ordinary case: the SAME widget object is both in /AcroForm/Fields
        // and in the page's own /Annots array (the normal, spec-compliant
        // shape). It must appear exactly once, not twice.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) " +
                "/V (NORMALVALUE) /Rect [100 650 260 675] /P 3 0 R >>"));

        using var doc = PdfDocument.Open(pdf);
        var fields = doc.GetPage(1).GetFormFields();

        fields.Should().ContainSingle();
        fields[0].Value.Should().Be("NORMALVALUE");
    }

    // ─── #671: widget page resolution without /P ────────────────────────────

    [Fact]
    public void GetFormFields_LinkedFieldWidgetWithoutP_ResolvesPageFromAnnots()
    {
        // The field is properly linked via /AcroForm/Fields (unlike #670's
        // scenario), but its widget carries no /P at all — exactly
        // issue19389.pdf's "Password" and "Text" fields (#671).
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (NoPage) " +
                "/V (NOPAGEVALUE) /Rect [100 650 260 675] >>"));

        using var doc = PdfDocument.Open(pdf);

        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as Excise.Core.Primitives.PdfDictionary
            ?? throw new InvalidOperationException();
        var parsed = PdfAcroFormParser.Parse(doc, acroFormDict);
        parsed.Fields.Should().ContainSingle();
        parsed.Fields[0].PageNumber.Should().Be(1,
            "the widget's page must be resolved from the page's own /Annots array when /P is absent");

        var fields = doc.GetPage(1).GetFormFields();
        fields.Should().ContainSingle();
        fields[0].Value.Should().Be("NOPAGEVALUE");
    }

    [Fact]
    public void GetFormFields_FieldWidgetWithoutPAndNotInAnyAnnots_HasNullPageNumber()
    {
        // Truly orphaned in both directions: no /P and not referenced from
        // any page's /Annots. Page association genuinely cannot be
        // determined — PageNumber must stay null rather than guessing, and
        // no page's GetFormFields() should surface it.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Resources << >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Dangling) " +
                "/V (DANGLINGVALUE) /Rect [100 650 260 675] >>"));

        using var doc = PdfDocument.Open(pdf);

        var acroFormDict = doc.Catalog.GetOptional("AcroForm") as Excise.Core.Primitives.PdfDictionary
            ?? throw new InvalidOperationException();
        var parsed = PdfAcroFormParser.Parse(doc, acroFormDict);
        parsed.Fields.Should().ContainSingle();
        parsed.Fields[0].PageNumber.Should().BeNull();

        doc.GetPage(1).GetFormFields().Should().BeEmpty();
    }

    [Fact]
    public void GetFormFields_MultiPageDocument_WidgetWithoutPResolvesCorrectPage()
    {
        // Two pages, each with its own widget missing /P. Verifies the
        // fallback distinguishes pages correctly rather than just picking
        // "the first page" or similar.
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R] >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 5 0 R /Annots [6 0 R] /Resources << >> >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 5 0 R /Annots [7 0 R] /Resources << >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (OnPageOne) " +
                "/V (PAGE1VALUE) /Rect [100 650 260 675] >>"),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (OnPageTwo) " +
                "/V (PAGE2VALUE) /Rect [100 650 260 675] >>"));

        using var doc = PdfDocument.Open(pdf);

        var page1Fields = doc.GetPage(1).GetFormFields();
        var page2Fields = doc.GetPage(2).GetFormFields();

        page1Fields.Should().ContainSingle();
        page1Fields[0].Value.Should().Be("PAGE1VALUE");
        page1Fields[0].PageNumber.Should().Be(1);

        page2Fields.Should().ContainSingle();
        page2Fields[0].Value.Should().Be("PAGE2VALUE");
        page2Fields[0].PageNumber.Should().Be(2);
    }

    // ─── Helpers (mirrors AdversarialRedactionRegressionTests's builders) ──

    private static string Obj(string body) => body;

    private static string Stream(string dictExtra, string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< {dictExtra} /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }

    private static byte[] Build(params string[] bodies)
    {
        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
