using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1916 — <c>DrawStickyNoteText</c> (the resting post-it card's own plain-text
/// renderer, added by #1794) called <c>SKCanvas.DrawText</c> directly and
/// stepped its baseline from <c>textRect.Top</c> upward, on a canvas that is
/// PDF space with Y UP (see <c>RenderDefaultAppearance</c>). Two bugs from one
/// cause: every glyph drew upside down (<c>DrawText</c> itself assumes Y
/// DOWN — the same reason <c>DrawTypesetLine</c> flips locally about the
/// baseline for the FreeText complex-script path), and each later line landed
/// ABOVE the one before it (<c>textRect.Top</c> is the visual BOTTOM of the
/// box in this Y-up space, not the top — see
/// <c>BaselineForTopAlignedLineBox</c>'s own comment).
///
/// <para>No spec or independent renderer has an opinion on excise's own
/// bespoke post-it visual (see <see cref="StickyNoteIconTests"/>'s reasoning
/// for the same point), so these gates are geometric self-checks against a
/// known glyph shape and known source order, not against an oracle.</para>
/// </summary>
public class StickyNoteTextOrientationTests : IDisposable
{
    private const int Dpi = 288;

    private readonly List<string> _temp = new();

    /// <summary>
    /// A capital "T" is top-heavy (a full-width crossbar sits at the top of
    /// its glyph box, over a single narrow stem) — upright, its ink is
    /// concentrated in the top half of its own bounding box. Rendered upside
    /// down (the pre-fix bug), the crossbar lands at the BOTTOM instead.
    /// </summary>
    [Fact]
    public void SingleLineGlyph_InksMoreOfItsTopHalf_ThanItsBottomHalf()
    {
        using var bmp = RenderWithExcise(WriteTemp(StickyNotePdf(contents: "T")));

        var bounds = InkBounds(bmp);
        bounds.Should().NotBeNull("the card must draw the glyph");

        var (minX, minY, maxX, maxY) = bounds!.Value;
        int mid = (minY + maxY) / 2;
        int topInk = 0, bottomInk = 0;
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            if (!IsInk(bmp.GetPixel(x, y))) continue;
            if (y <= mid) topInk++; else bottomInk++;
        }

        topInk.Should().BeGreaterThan(bottomInk,
            "a \"T\" drawn upright inks its crossbar (top) more than its bare stem " +
            "(bottom); more ink in the bottom half means the glyph rendered upside down");
    }

    /// <summary>
    /// Two lines with visibly different ink AREA (one narrow, one wide) must
    /// appear top-to-bottom in SOURCE order. Distinguishing by area rather
    /// than shape keeps this from needing character recognition.
    /// </summary>
    [Fact]
    public void MultiLineContents_DrawsInSourceOrder_TopToBottom()
    {
        using var bmp = RenderWithExcise(WriteTemp(StickyNotePdf(contents: "I\nWWWWWWWW")));

        // Find the two ink bands by scanning rows for a gap.
        var inkRows = new List<int>();
        for (int y = 0; y < bmp.Height; y++)
        {
            bool any = false;
            for (int x = 0; x < bmp.Width && !any; x++)
                if (IsInk(bmp.GetPixel(x, y))) any = true;
            if (any) inkRows.Add(y);
        }
        inkRows.Should().NotBeEmpty("the card must draw both lines");

        var bands = SplitIntoBands(inkRows);
        bands.Should().HaveCount(2, "two lines of text must occupy two separate row bands");

        var topBandInk = InkInBand(bmp, bands[0]);
        var bottomBandInk = InkInBand(bmp, bands[1]);
        bands[0].start.Should().BeLessThan(bands[1].start, "bands must be reported top-first");

        topBandInk.Should().BeLessThan(bottomBandInk,
            "\"I\" (line 1, few ink pixels) must be the TOP band and \"WWWWWWWW\" " +
            "(line 2, many ink pixels) the BOTTOM band — reversed bands mean the lines " +
            "drew in reverse order");
    }

    /// <summary>
    /// #1797's truncation path (<c>TruncateWithEllipsis</c>) runs on the LAST
    /// VISIBLE line — after #1916's fix that is the BOTTOM-most drawn line,
    /// matching where the card actually clips overflow. Seven lines of
    /// monotonically increasing ink area, in the default (~6-line-tall) card,
    /// must show fewer than 7 bands, strictly increasing top-to-bottom:
    /// source order preserved even on the branch that drops lines, not just
    /// the branch that draws all of them.
    /// </summary>
    [Fact]
    public void OverflowingContents_TruncatesLines_PreservingSourceOrderOfWhatRemains()
    {
        using var bmp = RenderWithExcise(
            WriteTemp(StickyNotePdf(contents: "W\nWW\nWWW\nWWWW\nWWWWW\nWWWWWW\nWWWWWWW", pageSize: 260)));

        var inkRows = new List<int>();
        for (int y = 0; y < bmp.Height; y++)
        {
            bool any = false;
            for (int x = 0; x < bmp.Width && !any; x++)
                if (IsInk(bmp.GetPixel(x, y))) any = true;
            if (any) inkRows.Add(y);
        }
        inkRows.Should().NotBeEmpty();

        var bands = SplitIntoBands(inkRows);
        bands.Should().HaveCountLessThan(7,
            "the default-sized card is too short for all 7 lines — some must be dropped, not overflow the card");

        var inkByBand = bands.Select(b => InkInBand(bmp, b)).ToList();
        for (int i = 1; i < inkByBand.Count; i++)
        {
            inkByBand[i].Should().BeGreaterThan(inkByBand[i - 1],
                $"band {i} (\"W\" x {i + 1}, plus an ellipsis on the last visible one) must ink " +
                $"more than band {i - 1} — bands out of this order mean truncation reversed the " +
                "surviving lines");
        }
    }

    /// <summary>
    /// #1916: the drop shadow's offset had the same Y-up sign bug — it fell
    /// up-right of the card instead of down-right. A geometric check: more
    /// shadow ink must sit below the card's own bottom edge than above its
    /// top edge (device pixels, so "below" is the larger-y band here).
    /// </summary>
    [Fact]
    public void DropShadow_FallsBelowAndRightOfTheCard_NotAboveIt()
    {
        using var bmp = RenderWithExcise(WriteTemp(StickyNotePdf(contents: "x", pageSize: 300)));

        var cardBounds = CardBounds(bmp);
        cardBounds.Should().NotBeNull("the card body must draw");
        var (cLeft, cTop, cRight, cBottom) = cardBounds!.Value;

        int aboveShadow = ShadowInkInBand(bmp, 0, cTop - 1);
        int belowShadow = ShadowInkInBand(bmp, cBottom + 1, bmp.Height - 1);

        belowShadow.Should().BeGreaterThan(aboveShadow,
            "the shadow must read as falling below the card (the card sitting ON the " +
            "page), not above it");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] StickyNotePdf(string contents, int? pageSize = null)
    {
        var size = pageSize ?? 260;
        var annot = "<< /Type /Annot /Subtype /Text /F 4 /Rect [20 20 220 170] " +
                    $"/Contents ({contents}) /C [1 0.85 0.2] /Name /Note >>";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {size} {size}] >>\nendobj\n",
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
            new RenderOptions { Dpi = Dpi, AntiAlias = true, BackgroundColor = SKColors.White });
    }

    /// <summary>The dark glyph ink, not the card's own yellow fill/border.</summary>
    private static bool IsInk(SKColor c) => c.Red < 150 && c.Green < 150 && c.Blue < 100;

    /// <summary>The card body's own fill (the /C yellow), used to find its edges.</summary>
    private static bool IsCardFill(SKColor c) => c.Red > 200 && c.Green > 180 && c.Blue < 180;

    private static (int minX, int minY, int maxX, int maxY)? InkBounds(SKBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            if (!IsInk(bmp.GetPixel(x, y))) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return maxX >= minX ? (minX, minY, maxX, maxY) : null;
    }

    private static (int left, int top, int right, int bottom)? CardBounds(SKBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            if (!IsCardFill(bmp.GetPixel(x, y))) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return maxX >= minX ? (minX, minY, maxX, maxY) : null;
    }

    private static int ShadowInkInBand(SKBitmap bmp, int yStart, int yEnd)
    {
        int count = 0;
        for (int y = Math.Max(0, yStart); y <= Math.Min(bmp.Height - 1, yEnd); y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            // The shadow is translucent black over white: neither pure white
            // nor the card's own yellow/ink.
            if (c.Red < 250 && c.Red == c.Green && c.Green == c.Blue) count++;
        }
        return count;
    }

    private static int InkInBand(SKBitmap bmp, (int start, int end) band)
    {
        int count = 0;
        for (int y = band.start; y <= band.end; y++)
        for (int x = 0; x < bmp.Width; x++)
            if (IsInk(bmp.GetPixel(x, y))) count++;
        return count;
    }

    /// <summary>Splits a sorted list of ink row indices into contiguous bands (a gap of &gt;1 row starts a new band).</summary>
    private static List<(int start, int end)> SplitIntoBands(List<int> rows)
    {
        var bands = new List<(int start, int end)>();
        int start = rows[0], prev = rows[0];
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i] - prev > 1)
            {
                bands.Add((start, prev));
                start = rows[i];
            }
            prev = rows[i];
        }
        bands.Add((start, prev));
        return bands;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-orientation-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
