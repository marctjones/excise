using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1958 — <c>RequiresComplexShaping</c> is the script gate that makes the
/// annotation text paths fail closed (FreeText routes to the typesetter,
/// sticky notes decline with a diagnostic). It enumerated UTF-16 chars over
/// BMP-only ranges, so a supplementary-plane Arabic-family script (a surrogate
/// pair) could never match and was drawn unshaped, which renders plausible but
/// WRONG text. The gate now walks Unicode scalar values.
/// </summary>
public class ComplexShapingGateTests
{
    private const string StickyNoteDiagnostic = "Sticky note /Contents not drawn";

    [Theory]
    [InlineData(0x1E900, "Adlam first")]
    [InlineData(0x1E921, "Adlam capital/small mid")]
    [InlineData(0x1E95F, "Adlam last")]
    [InlineData(0x10D00, "Hanifi Rohingya first")]
    [InlineData(0x10D3F, "Hanifi Rohingya last")]
    [InlineData(0x1EE00, "Arabic Math first")]
    [InlineData(0x1EEFF, "Arabic Math last")]
    [InlineData(0x0627, "Arabic alef (BMP, unchanged)")]
    [InlineData(0x05D0, "Hebrew alef (BMP, unchanged)")]
    [InlineData(0x0E01, "Thai (BMP, unchanged)")]
    public void SupplementaryAndBmpComplexScripts_AreDetected(int codePoint, string why)
    {
        RenderContext.RequiresComplexShaping("ab " + char.ConvertFromUtf32(codePoint) + " cd")
            .Should().BeTrue(why);
    }

    [Theory]
    [InlineData(0x10CFF, "just below Hanifi Rohingya")]
    [InlineData(0x10D40, "just above Hanifi Rohingya")]
    [InlineData(0x1E8FF, "just below Adlam")]
    [InlineData(0x1E960, "just above Adlam")]
    [InlineData(0x1EDFF, "just below Arabic Math")]
    [InlineData(0x1EF00, "just above Arabic Math")]
    [InlineData(0x1D400, "Mathematical Alphanumeric (non-Arabic)")]
    [InlineData(0x1F600, "emoji")]
    [InlineData(0x20000, "CJK Extension B")]
    [InlineData(0x0041, "Latin")]
    [InlineData(0x0142, "Latin Extended l-stroke")]
    [InlineData(0x0416, "Cyrillic")]
    [InlineData(0x3042, "Hiragana")]
    [InlineData(0x4E2D, "CJK")]
    public void NearbyNonComplexCodePoints_AreNotDetected(int codePoint, string why)
    {
        RenderContext.RequiresComplexShaping("ab " + char.ConvertFromUtf32(codePoint) + " cd")
            .Should().BeFalse(why);
    }

    [Fact]
    public void LoneSurrogate_DoesNotThrow_AndIsNotComplex()
    {
        RenderContext.RequiresComplexShaping("a\uD83Ab").Should().BeFalse();
    }

    [Fact]
    public void StickyNote_WithAdlamContents_EmitsNotDrawnDiagnostic()
    {
        // U+1E900..U+1E902, a real Adlam run (a surrogate pair per letter).
        var diagnostics = Render(StickyNotePdf("﻿" + char.ConvertFromUtf32(0x1E900) +
            char.ConvertFromUtf32(0x1E901) + char.ConvertFromUtf32(0x1E902)));

        diagnostics.Should().Contain(d => d.Contains(StickyNoteDiagnostic),
            "Adlam needs joining shapes; the card must decline to draw it unshaped");
    }

    [Fact]
    public void StickyNote_WithLatinContents_EmitsNoNotDrawnDiagnostic()
    {
        var diagnostics = Render(StickyNotePdf("﻿Hello Załącznik"));

        diagnostics.Should().NotContain(d => d.Contains(StickyNoteDiagnostic));
    }

    private const string FreeTextDiagnostic = "FreeText /Contents not drawn";

    [Fact]
    public void FreeText_WithAdlamContents_RoutesToTheTypesetterAndFailsClosedWithoutACoveringFont()
    {
        // No system fallback and the stated /Helv face has no Adlam glyphs, so
        // the typesetter must decline with its diagnostic. Before #1958 the run
        // never reached it: the single-line path drew it unshaped, silently.
        var adlam = "\uFEFF" + char.ConvertFromUtf32(0x1E900) + char.ConvertFromUtf32(0x1E901);
        var diagnostics = Render(FreeTextPdf(adlam), disableSystemFontFallback: true);

        diagnostics.Should().Contain(d => d.Contains(FreeTextDiagnostic));
    }

    [Fact]
    public void FreeText_WithLatinContents_StaysOnTheSingleLinePath()
    {
        var diagnostics = Render(FreeTextPdf("\uFEFFHello Za\u0142\u0105cznik"), disableSystemFontFallback: true);

        diagnostics.Should().NotContain(d => d.Contains(FreeTextDiagnostic));
    }

    private static List<string> Render(byte[] pdf, bool disableSystemFontFallback = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-shaping-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var diagnostics = new List<string>();
            using var doc = PdfDocument.Open(path);
            using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions
                {
                    Dpi = 72, BackgroundColor = SKColors.White, Diagnostics = diagnostics,
                    DisableSystemFontFallback = disableSystemFontFallback,
                });
            return diagnostics;
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>/Contents as a UTF-16BE hex string (BOM included in the passed text).</summary>
    private static byte[] StickyNotePdf(string contents) =>
        AnnotPdf(hex => $"<< /Type /Annot /Subtype /Text /F 4 /Rect [20 20 220 170] /Contents <{hex}> /C [1 0.85 0.2] /Name /Note >>", contents);

    private static byte[] FreeTextPdf(string contents) =>
        AnnotPdf(hex => $"<< /Type /Annot /Subtype /FreeText /F 4 /Rect [20 20 220 170] /Contents <{hex}> /DA (/Helv 10 Tf 0 g) >>", contents);

    private static byte[] AnnotPdf(Func<string, string> annotation, string contents)
    {
        var hex = Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(contents));
        string[] objects =
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 260 260] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            $"4 0 obj\n{annotation(hex)}\nendobj\n",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
