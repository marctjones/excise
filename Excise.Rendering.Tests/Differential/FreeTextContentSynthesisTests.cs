using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.Rendering.Fonts;
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
    /// Case 3 (#1363). Complex-script <c>/Contents</c> used to draw no glyphs
    /// at all. The first cut of #1070's fix fed Arabic through the Latin-1 Tj
    /// path and drew a row of tofu on pdf.js <c>freetext_no_appearance.pdf</c>,
    /// where mutool shapes and draws the Arabic. The guard that replaced it drew
    /// nothing. Real shaping now draws it. These tests pin both halves: shaped
    /// text draws, and with no covering font it still draws NOTHING rather than
    /// tofu.
    /// </summary>
    private const string Arabic = "الإنترنت";

    [Fact]
    public void FreeTextWithArabicContents_DrawsShapedGlyphsInsideRect()
    {
        RequireArabicSystemFont();

        var path = WriteTemp(FreeTextPdf(withDa: true, withColor: false,
            contents: Arabic, border: "/Border [0 0 0]"));
        using var bmp = RenderWithExcise(path);

        InteriorBluePixels(bmp).Should().BeGreaterThan(40,
            "with /Border [0 0 0] and no /C, blue ink inside the rect can only be the " +
            "shaped Arabic; before #1363 this was 0");
        InkOutsideAnnotationRect(bmp).Should().Be(0,
            "the synthesised appearance is clipped to /Rect");
    }

    [Fact]
    public void FreeTextWithArabicContents_MutoolDrawsItToo()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(FreeTextPdf(withDa: true, withColor: false,
            contents: Arabic, border: "/Border [0 0 0]"));
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        InteriorBluePixels(reference!).Should().BeGreaterThan(40,
            "mutool shapes and draws Arabic /Contents; excise drawing it is only correct " +
            "because an engine that is not excise does it too");
    }

    [Fact]
    public void FreeTextWithArabicContents_DrawsEveryLine()
    {
        RequireArabicSystemFont();

        // Three short paragraphs at 14 pt with 1.2 leading: one line is at most
        // ~34 px tall at 144 dpi, so an ink box taller than two lines needs at
        // least two lines drawn. Before #1363 only the first line was ever
        // considered.
        const string word = "بيت";
        var path = WriteTemp(FreeTextPdf(withDa: true, withColor: false,
            contents: word + "\n" + word + "\r\n" + word, border: "/Border [0 0 0]"));
        using var bmp = RenderWithExcise(path);

        var bounds = InkBounds(bmp);
        bounds.Should().NotBeNull("three lines of Arabic must draw something");
        bounds!.Value.Height.Should().BeGreaterThan(2 * 14 * Dpi / 72,
            "the ink spans more than two text lines, so the later paragraphs were drawn");
        InkOutsideAnnotationRect(bmp).Should().Be(0,
            "every line stays inside /Rect");
    }

    [Fact]
    public void FreeTextWithComplexScript_NoCoveringFont_DrawsNoGlyphs()
    {
        var path = WriteTemp(FreeTextPdf(withDa: true, withColor: true, contents: Arabic));
        var diagnostics = new List<string>();
        using var doc = PdfDocument.Open(path);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions
        {
            Dpi = Dpi,
            AntiAlias = false,
            BackgroundColor = SKColors.White,
            DisableSystemFontFallback = true,
            Diagnostics = diagnostics,
        });

        diagnostics.Should().Contain(d => d.Contains("no available font covers U+0627"),
            "this test is about the no-covering-font branch. If the /Helv substitute on this " +
            "machine covers Arabic, the fixture no longer reaches that branch");

        InteriorBluePixels(bmp).Should().Be(0,
            "with no font covering the script, drawing anything would be tofu, which is " +
            "strictly worse than the empty rectangle. The typesetter fails closed");

        BluePixels(bmp).Should().BeGreaterThan(0,
            "the border must still draw: the reader should know the annotation is there");
    }

    /// <summary>
    /// The #1363 decision: the synthesised appearance is RENDER-ONLY. A
    /// generated <c>/AP</c> saved into the file would be a text carrier the
    /// redaction scrubber was never taught about.
    /// </summary>
    [Fact]
    public void SynthesisedFreeTextAppearance_IsNeverWrittenToTheSavedFile()
    {
        var path = WriteTemp(FreeTextPdf(withDa: true, withColor: false,
            contents: Arabic, border: "/Border [0 0 0]"));

        using var doc = PdfDocument.Open(path);
        using (new SkiaRenderer().RenderPage(doc.GetPage(1),
                   new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White }))
        {
        }

        doc.GetPage(1).GetAnnotations().Single().RawDictionary.GetOptional("AP").Should().BeNull(
            "rendering must not attach an appearance to the in-memory annotation");

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        reopened.GetPage(1).GetAnnotations().Single().RawDictionary.GetOptional("AP").Should().BeNull(
            "a synthesised /AP must never reach a saved file");
    }

    /// <summary>
    /// A STATED border width of 0 means no border (§12.5.4 — /Border is
    /// [h v w]). Only an ABSENT width defaults to 1.
    ///
    /// <para>Caught by the annotation bench rather than by reasoning: on pdf.js
    /// <c>bug1871353.pdf</c>, whose FreeText carries <c>/Border [0 0 0]</c> and
    /// no <c>/C</c>, the oracle majority inks 6 tiles — its two glyphs and
    /// nothing else — where excise inked 34, of which 31 were extra. Drawing a
    /// box the file explicitly declined is the same class of error as not
    /// drawing text it asked for, just in the other direction.</para>
    /// </summary>
    [Fact]
    public void AStatedZeroBorderWidth_DrawsNoBorder()
    {
        var zero = InkPixels(RenderWithExcise(WriteTemp(
            FreeTextPdf(withDa: true, withColor: false, border: "/Border [0 0 0]"))));
        var absent = InkPixels(RenderWithExcise(WriteTemp(
            FreeTextPdf(withDa: true, withColor: false))));

        zero.Should().BeLessThan(absent,
            "/Border [0 0 0] states a width of zero; only an omitted width defaults to 1");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] FreeTextPdf(
        bool withDa, bool withColor, string? contents = null, string border = "")
    {
        var text = contents == null
            ? $"({Note})"
            // UTF-16BE with a BOM — how a PDF text string carries anything
            // outside PDFDocEncoding (§7.9.2.2).
            : "<FEFF" + string.Concat(contents.Select(c => ((int)c).ToString("X4"))) + ">";
        var annot = "<< /Type /Annot /Subtype /FreeText /F 4 /Rect [20 120 180 175] " +
                    $"/Contents {text}" +
                    (withColor ? " /C [0.95 0.95 0.8]" : "") +
                    (withDa ? " /DA (0 0 1 rg /Helv 14 Tf)" : "") +
                    (border.Length > 0 ? " " + border : "") + " >>";
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

    private static SKBitmap RenderWithExcise(string path, bool disableSystemFontFallback = false)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions
            {
                Dpi = Dpi,
                AntiAlias = false,
                BackgroundColor = SKColors.White,
                DisableSystemFontFallback = disableSystemFontFallback,
            });
    }

    private static void RequireArabicSystemFont()
    {
        bool covered;
        lock (FontManagerLock.Instance)
        {
            covered = SKFontManager.Default.MatchCharacter(0x0627) != null;
        }

        Assert.SkipWhen(!covered,
            "No system font covers Arabic (U+0627), so there is nothing to shape with. " +
            "The fail-closed branch is covered by FreeTextWithComplexScript_NoCoveringFont_DrawsNoGlyphs.");
    }

    private static SKRectI AnnotationDeviceRect()
    {
        float s = Dpi / 72f;
        return new SKRectI((int)(20 * s), (int)((PageSize - 175) * s),
                           (int)Math.Ceiling(180 * s), (int)Math.Ceiling((PageSize - 120) * s));
    }

    /// <summary>Ink more than one pixel outside the annotation rect.</summary>
    private static int InkOutsideAnnotationRect(SKBitmap bmp)
    {
        var r = AnnotationDeviceRect();
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                if (x >= r.Left - 1 && x < r.Right + 1 && y >= r.Top - 1 && y < r.Bottom + 1) continue;
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) n++;
            }
        return n;
    }

    private static SKRectI? InkBounds(SKBitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red >= 240 && c.Green >= 240 && c.Blue >= 240) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private static bool IsBlue(SKColor c) => c.Blue > 140 && c.Red < 120 && c.Green < 120;

    private static int InkPixels(SKBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) n++;
            }
        return n;
    }

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
