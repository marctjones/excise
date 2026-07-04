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
