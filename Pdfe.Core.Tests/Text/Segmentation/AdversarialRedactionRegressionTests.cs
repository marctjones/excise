using System.IO;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Text.Segmentation;
using Xunit;

namespace Pdfe.Core.Tests.Text.Segmentation;

/// <summary>
/// Security-oriented redaction regressions for content that can overlap the
/// page visually while living outside ordinary page content streams.
/// </summary>
public sealed class AdversarialRedactionRegressionTests
{
    [Fact]
    public void RedactArea_OverAcroFormField_RemovesValueAndAppearanceBytes()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /F1 6 0 R >> >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [100 650 260 675] " +
                "/P 3 0 R /V (FORMSECRET) /DV (FORMSECRET) /AP << /N 7 0 R >> >>"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 160 25] " +
                   "/Resources << /Font << /F1 6 0 R >> >>",
                   "BT /F1 12 Tf 2 8 Td (FORMSECRET) Tj ET"));

        Encoding.Latin1.GetString(pdf).Should().Contain("FORMSECRET");

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("FORMSECRET",
            "AcroForm values are part of searchable and redactable page text");

        page.RedactArea(new PdfRectangle(95, 645, 265, 680));

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("FORMSECRET",
            "redacting the widget area must remove both /V and stale /AP appearance text");

        using var reopened = PdfDocument.Open(saved);
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value))
            .Should().NotContain("FORMSECRET");
    }

    [Fact]
    public void RedactArea_OverAnnotation_RemovesContentsAndAppearanceBytes()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /F1 7 0 R >> >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /FreeText /Rect [90 690 280 725] " +
                "/Contents (ANNOTSECRET) /RC (ANNOTSECRET) /AP << /N 6 0 R >> >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 190 35] " +
                   "/Resources << /Font << /F1 7 0 R >> >>",
                   "BT /F1 12 Tf 4 12 Td (ANNOTSECRET) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        Encoding.Latin1.GetString(pdf).Should().Contain("ANNOTSECRET");

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).GetAnnotations().Should().ContainSingle();

        doc.GetPage(1).RedactArea(new PdfRectangle(80, 680, 290, 735));

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("ANNOTSECRET",
            "annotation contents and appearance streams must not survive an overlapping redaction");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).GetAnnotations().Should().BeEmpty();
    }

    [Fact]
    public void RedactText_FindsAndRemovesFreeTextAnnotationContent()
    {
        // #660: before this fix, FreeText /Contents was invisible to
        // page.Text/page.Letters entirely — RedactText("ANNOTSECRET") would
        // find zero matches and report success while the annotation
        // survived untouched. Verified via saved bytes (the ONLY carrier
        // that can prove removal, not page.Text re-reading pdfe's own
        // synthetic letters — the purest form of the self-oracle mistake
        // CLAUDE.md's redaction-code requirements exist to prevent).
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Annots [5 0 R] /Resources << /Font << /F1 7 0 R >> >> >>"),
            Stream("", ""),
            Obj("<< /Type /Annot /Subtype /FreeText /Rect [90 690 280 725] " +
                "/Contents (ANNOTSECRET) /RC (ANNOTSECRET) /AP << /N 6 0 R >> >>"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 190 35] " +
                   "/Resources << /Font << /F1 7 0 R >> >>",
                   "BT /F1 12 Tf 4 12 Td (ANNOTSECRET) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("ANNOTSECRET",
            "FreeText content must be findable by search/RedactText, not just page.GetAnnotations()");

        var removed = doc.RedactText("ANNOTSECRET", drawBlackRect: false);
        removed.Should().BeGreaterThan(0, "RedactText must actually find the annotation content");

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("ANNOTSECRET",
            "a word RedactText reports as removed must actually be gone from the saved bytes — " +
            "'found but not removable' is a new leak, not a fix");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).GetAnnotations().Should().BeEmpty(
            "the whole annotation (Contents + AP) must be gone, not just made unfindable");
    }

    [Fact]
    public void RedactArea_PartialGlyphOverlap_RemovesGlyphButKeepsNeighbor()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
            Stream("", "BT /F1 24 Tf 100 700 Td (AB) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var b = page.Letters.Single(l => l.Value == "B");
        var partialB = new PdfRectangle(
            b.GlyphRectangle.Left + (b.GlyphRectangle.Width * 0.5),
            b.GlyphRectangle.Bottom,
            b.GlyphRectangle.Right + 1,
            b.GlyphRectangle.Top);

        page.RedactArea(partialB);

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);
        var text = string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value));
        text.Should().Be("A");

        Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes())
            .Should().Contain("(A)").And.NotContain("(B)");
    }

    [Fact]
    public void RedactArea_OverRotatedText_RemovesSavedBytes()
    {
        var pdf = Build(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
            Stream("", "BT /F1 24 Tf 0 1 -1 0 300 600 Tm (ROTSECRET) Tj ET"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("ROTSECRET");

        var letters = page.Letters.Where(l => "ROTSECRET".Contains(l.Value)).ToList();
        var bounds = new PdfRectangle(
            letters.Min(l => l.GlyphRectangle.Left),
            letters.Min(l => l.GlyphRectangle.Bottom),
            letters.Max(l => l.GlyphRectangle.Right),
            letters.Max(l => l.GlyphRectangle.Top));

        page.RedactArea(bounds);

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain("ROTSECRET");

        using var reopened = PdfDocument.Open(saved);
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value))
            .Should().NotContain("ROTSECRET");
    }

    [Fact]
    public void RedactText_DefaultIncludesHiddenOptionalContentText()
    {
        var pdf = BuildHiddenOcgPdf();

        using (var inspected = PdfDocument.Open(pdf))
        {
            var page = inspected.GetPage(1);
            string.Concat(page.Letters.Where(l => !l.IsInHiddenOptionalContent).Select(l => l.Value))
                .Should().Contain("VISIBLE");
            string.Concat(page.Letters.Where(l => l.IsInHiddenOptionalContent).Select(l => l.Value))
                .Should().Be("HIDDENSECRET");
        }

        using (var excluded = PdfDocument.Open(pdf))
        {
            excluded.RedactText("HIDDENSECRET", includeHiddenLayers: false).Should().Be(0);
            Encoding.Latin1.GetString(excluded.SaveToBytes()).Should().Contain("HIDDENSECRET",
                "callers can explicitly exclude hidden layers when they are not doing security redaction");
        }

        using (var included = PdfDocument.Open(pdf))
        {
            included.RedactText("HIDDENSECRET").Should().Be(1);
            var saved = Encoding.Latin1.GetString(included.SaveToBytes());
            saved.Should().NotContain("HIDDENSECRET",
                "security redaction must include text hidden in default-off optional-content layers");
            saved.Should().Contain("VISIBLE");
        }
    }

    [Fact]
    public void RedactArea_OverHiddenOptionalContentText_RemovesSavedBytes()
    {
        var pdf = BuildHiddenOcgPdf();

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var hiddenLetters = page.Letters.Where(l => l.IsInHiddenOptionalContent).ToList();
        hiddenLetters.Should().NotBeEmpty();

        page.RedactArea(BoundingBoxOf(hiddenLetters));

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());
        saved.Should().NotContain("HIDDENSECRET");
        saved.Should().Contain("VISIBLE");
    }

    private static string Obj(string body) => body;

    private static PdfRectangle BoundingBoxOf(IReadOnlyList<Pdfe.Core.Text.Letter> letters)
    {
        return new PdfRectangle(
            letters.Min(l => l.GlyphRectangle.Left),
            letters.Min(l => l.GlyphRectangle.Bottom),
            letters.Max(l => l.GlyphRectangle.Right),
            letters.Max(l => l.GlyphRectangle.Top));
    }

    private static byte[] BuildHiddenOcgPdf()
    {
        return Build(
            Obj("<< /Type /Catalog /Pages 2 0 R " +
                "/OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> " +
                "/Properties << /HiddenLayer 6 0 R >> >> >>"),
            Stream("",
                "BT /F1 12 Tf 100 720 Td (VISIBLE) Tj ET\n" +
                "/OC /HiddenLayer BDC\n" +
                "BT /F1 12 Tf 100 690 Td (HIDDENSECRET) Tj ET\n" +
                "EMC"),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Obj("<< /Type /OCG /Name (Confidential Layer) >>"));
    }

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
