using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1901, renderer half: a <c>Q</c> inside a form XObject with no matching
/// <c>q</c> in the form must not pop the page's saved state (§8.4.2, §8.10.1).
/// <c>SkiaRenderer</c> keeps its own state stack (the tracked exception to the
/// one-walk rule), so it held the same <c>Count &gt; 0</c> defect as the walker:
/// the stray <c>Q</c> popped the page's entry and the form's canvas save, and
/// the rest of the form drew without the page <c>cm</c>, the form
/// <c>/Matrix</c> or the <c>/BBox</c> clip. mutool, the independent renderer,
/// ignores the stray <c>Q</c>; filled rectangles keep the comparison free of
/// glyph rasterisation differences.
/// </summary>
public sealed class StrayRestoreInFormRenderTests : IDisposable
{
    private const int PageSize = 200;
    private readonly List<string> _temp = new();

    [Theory]
    [InlineData("", 0)]
    [InlineData("/Matrix [1 0 0 1 100 0]", 100)]
    public void StrayQInForm_DrawsTheRestOfTheFormUnderThePageState(string formMatrix, int dx)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Page: q, shift down 60, invoke the form, Q, then a blue square that
        // must sit at y 150..180 (the page cm is undone by the page's Q).
        // Form: stray Q, then a red square at form y 100..130 → page y 40..70.
        var path = Write(
            "q 1 0 0 1 0 -60 cm /Fm0 Do Q 0 0 1 rg 20 150 30 30 re f",
            formMatrix,
            "Q 1 0 0 rg 20 100 30 30 re f");

        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, 72);
        reference.Should().NotBeNull();
        using var doc = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White });
        excise.Should().NotBeNull();

        var redWhere = Box(22 + dx, 42, 48 + dx, 68);
        var redWrong = Box(22 + dx, 102, 48 + dx, 128);
        var blue = Box(22, 152, 48, 178);
        foreach (var (name, bmp) in new[] { ("mutool", reference!), ("excise", excise!) })
        {
            RedFraction(bmp, redWhere).Should().BeGreaterThan(0.9,
                $"{name}: the form's square is drawn under the page cm, past the stray Q");
            InkFraction(bmp, redWrong).Should().Be(0,
                $"{name}: nothing is drawn where the square would land without the page cm");
            BlueFraction(bmp, blue).Should().BeGreaterThan(0.9,
                $"{name}: the page square after the page's Q is unshifted");
        }
    }

    private string Write(string pageContent, string formMatrix, string formContent)
    {
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /XObject << /Fm0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 {PageSize} {PageSize}] {formMatrix} " +
            $"/Length {formContent.Length} >>\nstream\n{formContent}\nendstream\nendobj\n",
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

        var path = Path.Combine(Path.GetTempPath(), $"excise-stray-q-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        _temp.Add(path);
        return path;
    }

    private static SKRectI Box(double left, double bottom, double right, double top) =>
        new((int)left, PageSize - (int)top, (int)right, PageSize - (int)bottom);

    private static double Fraction(SKBitmap bmp, SKRectI box, Func<SKColor, bool> predicate)
    {
        int hit = 0, total = 0;
        int x0 = Math.Max(0, box.Left), x1 = Math.Min(bmp.Width - 1, box.Right);
        int y0 = Math.Max(0, box.Top), y1 = Math.Min(bmp.Height - 1, box.Bottom);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            total++;
            if (predicate(bmp.GetPixel(x, y))) hit++;
        }
        return total == 0 ? 0 : (double)hit / total;
    }

    private static double InkFraction(SKBitmap bmp, SKRectI box) =>
        Fraction(bmp, box, p => p.Red < 200 || p.Green < 200 || p.Blue < 200);

    private static double RedFraction(SKBitmap bmp, SKRectI box) =>
        Fraction(bmp, box, p => p.Red > 180 && p.Green < 100 && p.Blue < 100);

    private static double BlueFraction(SKBitmap bmp, SKRectI box) =>
        Fraction(bmp, box, p => p.Blue > 180 && p.Red < 100 && p.Green < 100);

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
