using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// <c>/Font</c> in an ExtGState — §8.4.5, Table 58: <c>[ fontRef size ]</c>.
/// It is the <c>Tf</c> equivalent, except the font arrives as a direct
/// reference to a font dictionary instead of a name looked up in
/// <c>/Resources /Font</c>. <c>ApplyExtGState</c> read CA, ca, LW, LC, LJ, ML,
/// OP, op, OPM, BM and SMask, and not this.
///
/// Nine corpus pages (veraPDF 6-1-12-t02 x5, TWG A001 x4) rendered blank
/// because of it: their content streams carry no <c>Tf</c> and their pages
/// carry no <c>/Font</c> resource, so the ExtGState is the only place the font
/// is named. Nothing was wrong with the font machinery — their subsets have an
/// ordinary (3,1) cmap resolving the glyph to a real outline. All nine now
/// PASS against mutool, pdftocairo and pdfium.
///
/// Those nine were filed under #886 ("code→GID mapping fails on embedded
/// subsets") because the clustering script matched <c>/FontFile</c> in the
/// document. They are not font-program bugs, which is why the fix is here and
/// not in the font code.
///
/// The fixture is authored in this file, so this runs on CI without the
/// gitignored corpora.
/// </summary>
public class ExtGStateFontTests : IDisposable
{
    private const int Dpi = 72;
    private const int PageSize = 200;

    private readonly List<string> _temp = new();

    [Fact]
    public void FontFromExtGState_IsUsedWhenThereIsNoTf()
    {
        var path = WriteTemp(ExtGStateFontPdf());
        using var doc = PdfDocument.Open(path);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        InkPixels(bmp).Should().BeGreaterThan(0,
            "the page names its font ONLY through the ExtGState, so ignoring /Font " +
            "leaves the text with no font at all and the page renders blank");
    }

    [Fact]
    public void FontFromExtGState_MatchesAnIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(ExtGStateFontPdf());
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        using var doc = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        InkPixels(reference!).Should().BeGreaterThan(0,
            "mutool honours ExtGState /Font — otherwise the fixture, not excise, is wrong");
        InkPixels(excise).Should().BeGreaterThan(0);
    }

    /// <summary>
    /// A later <c>Tf</c> must still win. The ExtGState sets the font as part of
    /// the graphics state; it does not pin it.
    /// </summary>
    [Fact]
    public void ExplicitTf_AfterTheGsOperator_StillTakesEffect()
    {
        // Same page, but a real /Font resource and a Tf selecting a 40pt font
        // AFTER `gs` set 4pt. If the ExtGState won, the glyphs would be tiny.
        var path = WriteTemp(ExtGStateThenTfPdf());
        using var doc = PdfDocument.Open(path);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        InkPixels(bmp).Should().BeGreaterThan(200,
            "a 40pt Tf issued after `gs` must override the ExtGState's 4pt — a large " +
            "glyph inks far more than a tiny one, so the count discriminates");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] ExtGStateFontPdf()
    {
        const string content = "/GS1 gs BT 20 90 Td (HHHH) Tj ET";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            // NOTE: no /Font resource at all — only the ExtGState.
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /ExtGState << /GS1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /ExtGState /Font [6 0 R 36] >>\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        });
    }

    private static byte[] ExtGStateThenTfPdf()
    {
        const string content = "/GS1 gs BT /F1 40 Tf 20 90 Td (HHHH) Tj ET";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /ExtGState << /GS1 5 0 R >> /Font << /F1 6 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /ExtGState /Font [6 0 R 4] >>\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        });
    }

    private static byte[] Assemble(string[] objects)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static int InkPixels(SKBitmap bmp)
    {
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) ink++;
            }
        return ink;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-gsfont-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
