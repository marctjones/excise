using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// Render-mode evidence for the five annotation-subtypes.json subtypes not
/// covered by any row in tests/annotation-synthesis-policy.json (Popup,
/// Caret, Movie, Screen, Watermark) -- SelectSynthesis has no case for any of
/// them (AnnotationAppearancePolicy.cs), so they have no from-scratch drawing
/// and rely entirely on the GENERIC /AP normal-appearance path
/// (ResolveNormalAppearance + Form XObject paint) that every subtype shares.
/// This proves that generic path actually draws for each of these five
/// specific subtype tags, not just for the subtypes SelectSynthesis knows by
/// name.
/// </summary>
public class AnnotationAppearanceStreamRenderTests
{
    private const int PageSize = 100;

    [Theory]
    [InlineData("Popup")]
    [InlineData("Caret")]
    [InlineData("Movie")]
    [InlineData("Screen")]
    [InlineData("Watermark")]
    public void SubtypeWithNormalAppearanceStream_Renders(string subtype)
    {
        var path = WriteTemp(AnnotationWithApPdf(subtype));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions
                {
                    Dpi = 72,
                    AntiAlias = false,
                    BackgroundColor = SKColors.White,
                    ShowCommentAnnotations = true,
                    RevealHiddenAnnotations = true,
                });

            var c = bmp.GetPixel(PageSize / 2, PageSize / 2);
            (c.Red > 240 && c.Green < 15 && c.Blue < 15).Should().BeTrue(
                $"a /{subtype} annotation with a real /AP /N appearance stream must paint through the generic Form XObject path");
        }
        finally { File.Delete(path); }
    }

    private static byte[] AnnotationWithApPdf(string subtype)
    {
        const string apContent = "1 0 0 rg 0 0 100 100 re f";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [6 0 R] >>\nendobj\n",
            "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 {PageSize} {PageSize}] " +
            $"/Length {apContent.Length} >>\nstream\n{apContent}\nendstream\nendobj\n",
            $"6 0 obj\n<< /Type /Annot /Subtype /{subtype} /Rect [0 0 {PageSize} {PageSize}] " +
            "/AP << /N 5 0 R >> >>\nendobj\n",
        };

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

    private static string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-annot-ap-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        return p;
    }
}
