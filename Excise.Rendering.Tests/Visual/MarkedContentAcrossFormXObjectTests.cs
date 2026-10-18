using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// #1899: where a marked-content (BMC/BDC/EMC) sequence opens in the page stream and is
/// closed or opened inside a Do'd form XObject, SkiaRenderer follows ISO 32000-2 §14.6
/// (a sequence lives in ONE content stream: a form's stray EMC is ignored, its unclosed
/// span ends with it), which is Poppler's reading. MuPDF and Ghostscript keep one stack
/// across Do and differ; the extractor (#1894) flags a letter hidden only when both
/// readings hide it, so every hidden flag is backed by Skia hiding the same text.
/// Measured 2026-10-18 at 72 dpi, ink pixels in the text row (page-after-Do / form):
/// stray   - skia 0/0, pdftoppm 0/0, mutool 833/961, gs 833/965;
/// unclosed - skia 827/0 (after the fix; was 0/0), pdftoppm 827/0, mutool 0/0, gs 0/0.
/// </summary>
public sealed class MarkedContentAcrossFormXObjectTests
{
    private const int Dpi = 72;
    private const int PageHeight = 792;

    // Fixtures carry no q/Q and no font state shared with the form beyond what the form
    // declares itself. Rows: page text after the Do at y=700, form text at y=600.
    public static TheoryData<string, string, string, bool, bool> Cases => new()
    {
        // name, page, form, pageTextPainted, formTextPainted (per §14.6)
        // The form closes a span it never opened: the page span stays open, so the page text
        // after the Do stays hidden (and the form, invoked while hidden, does not paint).
        { "stray", "/OC /MC0 BDC\n/Fx Do\nBT /F1 24 Tf 100 700 Td (AFTERDO) Tj ET\nEMC",
                   "EMC\nBT /F1 24 Tf 100 600 Td (FORMTEXT) Tj ET", false, false },
        // The form opens a hidden span it never closes: it ends with the form, so the page
        // text after the Do is painted.
        { "unclosed", "/Fx Do\nBT /F1 24 Tf 100 700 Td (AFTERDO) Tj ET",
                      "/OC /MC0 BDC\nBT /F1 24 Tf 100 600 Td (FORMTEXT) Tj ET", true, false },
        // Control: the form's own hidden span is balanced (its TAIL paints in the page row), so every viewer agrees.
        { "balanced", "/Fx Do\nBT /F1 24 Tf 100 700 Td (AFTERDO) Tj ET",
                      "/OC /MC0 BDC\nBT /F1 24 Tf 100 600 Td (FORMTEXT) Tj ET\nEMC\nBT /F1 24 Tf 400 700 Td (TAIL) Tj ET",
                      true, false },
        // Control: a hidden page span around a form whose own spans are balanced.
        { "pagehidden", "/OC /MC0 BDC\n/Fx Do\nBT /F1 24 Tf 100 700 Td (AFTERDO) Tj ET\nEMC",
                        "/Span BMC\nBT /F1 24 Tf 100 600 Td (FORMTEXT) Tj ET\nEMC", false, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SkiaFollowsThePerStreamScopeOfSection14_6(
        string name, string page, string form, bool pagePainted, bool formPainted)
    {
        var bytes = BuildFormOcPdf(page, form);
        using var doc = PdfDocument.Open(bytes);
        using var skia = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = Dpi, AntiAlias = false });
        (Ink(skia, 700) > 50).Should().Be(pagePainted, $"{name}: page text after the Do");
        (Ink(skia, 600) > 50).Should().Be(formPainted, $"{name}: form text");

        // Independent reading: Poppler scopes spans per content stream, as §14.6 does.
        if (!PdftoppmReferenceRenderer.IsAvailable)
            return;
        var path = Path.Combine(Path.GetTempPath(), $"mc1899-{name}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var reference = PdftoppmReferenceRenderer.RenderPage(path, 1, Dpi)!;
            (Ink(reference, 700) > 50).Should().Be(pagePainted, $"{name}: pdftoppm page text");
            (Ink(reference, 600) > 50).Should().Be(formPainted, $"{name}: pdftoppm form text");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExtractorNeverCallsHiddenWhatSkiaPaints(
        string name, string page, string form, bool pagePainted, bool formPainted)
    {
        // Every extractor hidden flag must be backed by Skia hiding the same text. The
        // reverse (the stray EMC case) is the deliberate conservative gap: MuPDF and
        // Ghostscript paint that text, so a visible-only redaction must still reach it.
        using var doc = PdfDocument.Open(BuildFormOcPdf(page, form));
        var hidden = string.Concat(doc.GetPage(1).Letters.Where(l => l.IsInHiddenOptionalContent).Select(l => l.Value));
        if (hidden.Contains("AFTERDO"))
            pagePainted.Should().BeFalse($"{name}: extractor-hidden page text must be hidden in Skia");
        if (hidden.Contains("FORMTEXT"))
            formPainted.Should().BeFalse($"{name}: extractor-hidden form text must be hidden in Skia");
    }

    private static int Ink(SKBitmap bmp, int pdfY)
    {
        int y0 = PageHeight - pdfY - 30, y1 = PageHeight - pdfY + 10, n = 0;
        for (var y = Math.Max(0, y0); y < Math.Min(bmp.Height, y1); y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red < 200 || p.Green < 200 || p.Blue < 200) n++;
            }
        return n;
    }

    private static byte[] BuildFormOcPdf(string pageContent, string formContent) => Build(
        "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> /XObject << /Fx 7 0 R >> /Properties << /MC0 6 0 R >> >> >>",
        Stream(pageContent),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /OCG /Name (Alpha) >>",
        Stream(formContent, "/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> /Properties << /MC0 6 0 R >> >> "));

    private static string Stream(string content, string entries = "")
    {
        var length = Encoding.Latin1.GetBytes(content).Length;
        return $"<< {entries}/Length {length} >>\nstream\n{content}\nendstream";
    }

    private static byte[] Build(params string[] bodies)
    {
        using var ms = new MemoryStream();
        void Write(string value) { var b = Encoding.Latin1.GetBytes(value); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++) { offsets[i + 1] = ms.Position; Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n"); }
        var xref = ms.Position;
        Write($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
