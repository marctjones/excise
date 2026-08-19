using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1070 — a /FreeText with no /AP drew its background rectangle and NOTHING
/// ELSE: no border, no text.
///
/// <para>FreeText is the one markup subtype whose content is meant to be
/// legible on the page without opening a popup (§12.5.6.6). Drawing the box
/// and not the text renders it as an empty coloured rectangle — the reader can
/// see something is there and cannot read it, which is worse than not drawing
/// it. It was reported from the GUI as "the text is the same colour as the
/// background"; it was not there at all.</para>
///
/// <para><b>This deliberately overturns a previous, documented decision.</b>
/// <c>RenderFreeTextDefault</c> used to skip the text on the reasoning that
/// "the oracles disagree sharply about it ... so there is no agreed answer to
/// copy", measured on <c>freetext_no_appearance.pdf</c> (mutool 6067 px,
/// pdftocairo 24). That measurement was real but OVER-GENERALISED: the
/// divergence is about MULTI-LINE COMPLEX SCRIPT layout, not about whether
/// text is drawn. Split into three cases, the oracles agree in two:</para>
///
/// <list type="number">
///   <item><b>No /DA</b> — both fill with /C and draw neither border nor text.</item>
///   <item><b>Simple /DA + single-line /Contents</b> — both fill with /C,
///     stroke a border IN THE /DA COLOUR, and draw the text.</item>
///   <item><b>Multi-line / RTL</b> — genuine disagreement; not chased.</item>
/// </list>
///
/// <para>Cases 1 and 2 are pinned here, in both directions. Case 1 is the
/// regression risk of the fix and is pinned precisely BECAUSE it is the case
/// that must NOT change.</para>
/// </summary>
public class FreeTextContentSynthesisTests : IDisposable
{
    private const int Dpi = 144;
    private const int PageSize = 200;
    private const string Note = "Hello";

    private readonly List<string> _temp = new();

    /// <summary>
    /// The defect itself. Blue is counted rather than total ink because the
    /// background fill is /C (pale yellow) and would dominate any plain ink
    /// count — a test that measured total ink passed on the broken renderer,
    /// which is exactly how this shipped.
    /// </summary>
    [Fact]
    public void FreeTextWithDa_DrawsItsContentsText()
    {
        var path = WriteTemp(FreeTextPdf(withDa: true, withColor: true));
        using var bmp = RenderWithExcise(path);

        BluePixels(bmp).Should().BeGreaterThan(120,
            "the /DA sets 0 0 1 rg, so the border and the glyphs are blue — before " +
            "#1070 excise drew only the pale /C background and this was 0");
    }

    [Fact]
    public void FreeTextWithDa_TextIsMoreThanJustTheBorder()
    {
        var path = WriteTemp(FreeTextPdf(withDa: true, withColor: true));
        using var bmp = RenderWithExcise(path);

        // Anti-vacuity: a border alone is blue too. Count only well INSIDE the
        // rect, where no border can reach, so this can only be glyphs.
        InteriorBluePixels(bmp).Should().BeGreaterThan(40,
            "blue pixels far from every edge can only be glyphs — without this the " +
            "test would pass on a renderer that drew the border and skipped the text, " +
            "which is a state excise was actually in");
    }

    [Fact]
    public void FreeTextWithDa_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(FreeTextPdf(withDa: true, withColor: true));
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        InteriorBluePixels(reference!).Should().BeGreaterThan(40,
            "mutool draws the note's text — otherwise the fixture proves nothing");
        InteriorBluePixels(RenderWithExcise(path)).Should().BeGreaterThan(40,
            "excise must draw it too; excise agreeing with excise is not evidence");
    }

    /// <summary>
    /// Case 1, and the reason this file exists in both directions: pdfium's
    /// <c>freetext_annotation_without_da.pdf</c> is filled edge to edge with
    /// /C by BOTH oracles — 1250 px on a 50x25 rect, i.e. exactly the whole
    /// rectangle and not one pixel of text or border. The fix must not start
    /// inventing text where the file gives nothing to style it with.
    /// </summary>
    [Fact]
    public void FreeTextWithoutDa_StaysAPlainFilledRectangle()
    {
        var path = WriteTemp(FreeTextPdf(withDa: false, withColor: true));
        using var bmp = RenderWithExcise(path);

        BluePixels(bmp).Should().Be(0,
            "with no /DA there is no colour and no font to draw text with, and both " +
            "oracles draw neither border nor text — inventing either would contradict " +
            "the measurement that justified the /C fill in the first place");
    }

    [Fact]
    public void FreeTextWithoutDa_AgreesWithMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(FreeTextPdf(withDa: false, withColor: true));
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        BluePixels(reference!).Should().Be(0,
            "mutool also draws no text for a /DA-less FreeText — that agreement is " +
            "what makes excise's plain rectangle correct rather than a missing feature");
    }

    /// <summary>
    /// Case 3, and the guard that stops this fix being worse than the bug.
    ///
    /// <para>The text reaches RenderText as Latin-1 bytes, exactly as a Tj
    /// operand would, so anything outside Latin-1 becomes '?' and draws as
    /// .notdef boxes. The first cut of #1070's fix did precisely that on
    /// pdf.js <c>freetext_no_appearance.pdf</c> — a row of tofu where mutool
    /// shapes and draws Arabic. An empty box reads as "an annotation is here";
    /// tofu reads as "this document is corrupt".</para>
    ///
    /// <para>So non-Latin-1 content draws the box and border and no text. The
    /// right way to lift this is real complex-script shaping, NOT widening the
    /// check — which is what this test exists to prevent.</para>
    /// </summary>
    [Fact]
    public void FreeTextWithNonLatinContents_DrawsNoGlyphs()
    {
        var path = WriteTemp(FreeTextPdf(withDa: true, withColor: true,
            contents: "\u0627\u0644\u0625\u0646\u062a\u0631\u0646\u062a"));
        using var bmp = RenderWithExcise(path);

        InteriorBluePixels(bmp).Should().Be(0,
            "Arabic cannot survive the Latin-1 round-trip, so drawing it produces " +
            ".notdef boxes — strictly worse than the empty rectangle this replaced");

        BluePixels(bmp).Should().BeGreaterThan(0,
            "the border must still draw: the reader should know the annotation is there, " +
            "which is the half of #1070 that is fixable without shaping");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] FreeTextPdf(bool withDa, bool withColor, string? contents = null)
    {
        var text = contents == null
            ? $"({Note})"
            // UTF-16BE with a BOM — how a PDF text string carries anything
            // outside PDFDocEncoding (§7.9.2.2).
            : "<FEFF" + string.Concat(contents.Select(c => ((int)c).ToString("X4"))) + ">";
        var annot = "<< /Type /Annot /Subtype /FreeText /F 4 /Rect [20 120 180 175] " +
                    $"/Contents {text}" +
                    (withColor ? " /C [0.95 0.95 0.8]" : "") +
                    (withDa ? " /DA (0 0 1 rg /Helv 14 Tf)" : "") + " >>";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            $"4 0 obj\n{annot}\nendobj\n",
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

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
    }

    private static bool IsBlue(SKColor c) => c.Blue > 140 && c.Red < 120 && c.Green < 120;

    private static int BluePixels(SKBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsBlue(bmp.GetPixel(x, y))) n++;
        return n;
    }

    /// <summary>
    /// Blue pixels at least 6 px inside the annotation rect on every side, so a
    /// border — however thick a renderer draws it — cannot contribute.
    /// </summary>
    private static int InteriorBluePixels(SKBitmap bmp)
    {
        float s = Dpi / 72f;
        const int inset = 6;
        int x0 = (int)(20 * s) + inset, x1 = (int)(180 * s) - inset;
        int y0 = (int)((PageSize - 175) * s) + inset, y1 = (int)((PageSize - 120) * s) - inset;

        int n = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(bmp.Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(bmp.Width, x1); x++)
                if (IsBlue(bmp.GetPixel(x, y))) n++;
        return n;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-freetext-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
