using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1834: <c>page.RedactArea(area, options)</c> draws the covering box that
/// <see cref="RedactionOptions.DrawBox"/> and <see cref="RedactionOptions.BoxColor"/>
/// ask for, and none when DrawBox is off. Measured on a mutool render, and in
/// every case the removed text is checked with mutool's extractor and the
/// saved-bytes scanner: the box is cosmetic and removal must not depend on it.
/// </summary>
public class AreaRedactionBoxTests : IDisposable
{
    private const string Secret = "SECRET";
    private const string Keep = "PUBLIC";
    private const int Dpi = 150;

    // 24pt Helvetica caps at baseline 700 from x=72: ink spans about 72..169
    // by 700..717. The area is taken from this geometry, not excise's glyph boxes.
    private static readonly PdfRectangle Area = new(66, 694, 176, 724);
    private static readonly PdfRectangle AreaInterior = new(68, 696, 174, 722);

    private readonly List<string> _temp = new();

    public static TheoryData<string> Boxes() => new() { "black", "red", "none" };

    [Theory]
    [MemberData(nameof(Boxes))]
    public void RedactArea_DrawsTheRequestedBox_AndRemovesTheText(string box)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = Fixture();
        MutoolTextExtractor.ExtractPage(WriteTemp(source), 1).Should().Contain(Secret,
            "fixture sanity: an independent extractor reads the secret before redaction");

        var options = box switch
        {
            "red" => RedactionOptions.Default with { BoxColor = (1.0, 0.0, 0.0) },
            "none" => RedactionOptions.Default with { DrawBox = false },
            _ => RedactionOptions.Default,
        };
        byte[] saved;
        using (var doc = PdfDocument.Open(source))
        {
            doc.GetPage(1).RedactArea(Area, options);
            saved = doc.SaveToBytes();
        }

        var path = WriteTemp(saved);
        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull();
        extracted!.Should().NotContain(Secret).And.Contain(Keep);
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();

        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull();
        switch (box)
        {
            case "black":
                FractionIn(rendered!, AreaInterior, p => p.Red < 60 && p.Green < 60 && p.Blue < 60)
                    .Should().BeGreaterThan(0.95, "DrawBox defaults to true and BoxColor null is black");
                break;
            case "red":
                FractionIn(rendered!, AreaInterior, p => p.Red > 200 && p.Green < 60 && p.Blue < 60)
                    .Should().BeGreaterThan(0.95, "BoxColor is the fill of the area's box");
                break;
            default:
                FractionIn(rendered!, Area, p => p.Red < 200 || p.Green < 200 || p.Blue < 200)
                    .Should().BeLessThan(0.001, "DrawBox false: the area is blank, neither boxed nor inked");
                break;
        }
    }

    private static byte[] Fixture()
    {
        var content = $"BT /F1 24 Tf 72 700 Td ({Secret}) Tj ET BT /F1 24 Tf 72 600 Td ({Keep}) Tj ET";
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static double FractionIn(SKBitmap bmp, PdfRectangle box, Func<SKColor, bool> counts)
    {
        const double scale = Dpi / 72.0;
        const double pageHeight = 792;
        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));
        int hit = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            total++;
            if (counts(bmp.GetPixel(x, y))) hit++;
        }
        return total == 0 ? 0 : (double)hit / total;
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-area-box-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
