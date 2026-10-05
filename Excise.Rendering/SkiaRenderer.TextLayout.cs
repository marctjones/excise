using Excise.Rendering.Fonts;
using SkiaSharp;

namespace Excise.Rendering;

internal partial class RenderContext
{
    // Immutable conversion between glyph and text-matrix frames; owns no native resources (#1963).
    private readonly record struct SimpleTextLayoutFrame(float EffectiveSize, float SizeSign, float XYRatio);

    private SKPoint[]? BuildSimpleGlyphPositions(byte[] sourceBytes, int glyphCount,
        ResolvedRenderFont currentFont, SimpleTextLayoutFrame frame)
    {
        var effectiveSize = frame.EffectiveSize;
        var sizeSign = frame.SizeSign;
        // Character/word spacing (Tc/Tw) is applied when *advancing
        // the cursor* between Tj calls (see SumPdfWidths below), but
        // a naive default-positioned SKTextBlob run lays out glyphs
        // using only the wrapped font's own hmtx — it has no idea Tc
        // or Tw exist. When either is non-zero the glyph run drawn
        // here silently drifts from the PDF-intended (and
        // cursor-tracked) positions: each glyph after a space ends up
        // further right than the PDF asked for, and by the end of a
        // long/justified line the drift is large enough for the
        // final glyph to visually collide with whatever is drawn
        // next (e.g. issue #652 — Tw=-0.588 over 10 spaces shifted
        // "movement"'s trailing "t" ~6pt right of where the
        // following em-dash correctly starts, visually merging the
        // two). Fix: when Tc/Tw actually apply, position each glyph
        // explicitly using cumulative /Widths advances plus Tc/Tw,
        // matching the PDF-spec formula used to track the cursor
        // (SumPdfWidths below): tx = (w0/1000 * Tfs) + Tc + Tw — Tc
        // and Tw are ALREADY in unscaled text-space units and must
        // NOT be multiplied by font size again (unlike w0, which is
        // in thousandths of an em and does need the Tfs scale).
        // "cursor" here lives in the pre-xyRatio canvas frame (the
        // canvas's own Scale(xyRatio, …) converts it to device
        // space), so Tc/Tw are scaled by the text matrix's own
        // Y-axis scale (yScale) rather than by effectiveSize, to
        // land in that same frame — consistent with how effectiveSize
        // itself is fontSize*yScale.
        float tcSpacing = _textState.CharSpacing;
        float twSpacing = _textState.WordSpacing;
        bool needsExplicitSpacing =
            (tcSpacing != 0f || twSpacing != 0f) &&
            currentFont.Widths != null;

        SKPoint[]? positions = null;
        if (needsExplicitSpacing)
        {
            var yScale = ComputeTextMatrixYScale();

            positions = new SKPoint[glyphCount];
            float cursor = 0f;
            for (int i = 0; i < sourceBytes.Length; i++)
            {
                positions[i] = new SKPoint(cursor, 0);
                int idx = sourceBytes[i] - currentFont.FirstChar;
                float w = idx >= 0 && idx < currentFont.Widths!.Length
                    ? currentFont.Widths[idx]
                    : currentFont.MissingWidth;
                float spacing = (tcSpacing + (sourceBytes[i] == 0x20 ? twSpacing : 0f)) * yScale;
                cursor += (w / 1000f) * effectiveSize + spacing * sizeSign;
            }
        }

        return positions;
    }

    // Called AFTER painting, as before: state reads and font/resource lifetimes are unchanged.
    private void AdvanceSimpleTextMatrix(string text, byte[]? sourceBytes, ResolvedRenderFont currentFont,
        SKFont font, SKPaint measurePaint, SimpleTextLayoutFrame frame)
    {
        var effectiveSize = frame.EffectiveSize;
        var sizeSign = frame.SizeSign;
        var xyRatio = frame.XYRatio;
        // Advance the cursor by what the PDF *intended*, which is not
        // always what Skia just drew.
        //   - PDF supplies /Widths → trust the PDF's explicit widths,
        //     embedded or not (#584). PDF /Widths is authoritative per ISO
        //     32000 9.2.4 regardless of what's baked into the font program;
        //     for a substituted system typeface this also avoids per-glyph
        //     drift into visible mid-word gaps (the birth-cert form is the
        //     canary — that fixture has no embedded program, so this branch
        //     already covered it before #584 and still does).
        //     For an *embedded* CFF program specifically, this branch used
        //     to be skipped entirely (only the substituted-typeface case
        //     trusted /Widths) on the assumption that Skia's own MeasureText
        //     against the real embedded outlines is always right. It isn't:
        //     the CFF→OpenType wrapper's hmtx is built from /Widths keyed by
        //     CFF glyph INDEX (CffToOpenType.BuildHmtx), but a subsetted font
        //     can have the same glyph index reachable from more than one PDF
        //     character code with different declared widths, and a glyph
        //     whose code falls outside every alias's /Widths coverage gets a
        //     hardcoded stub (500) instead of its real width — hmtx and the
        //     PDF's own /Widths can disagree. Going straight to /Widths
        //     (SumPdfWidths, indexed by PDF code, not glyph index) removes
        //     that disagreement instead of trusting whichever one hmtx
        //     happened to end up with. Confirmed via #584: this makes the
        //     computed advance for a specific real-world em-dash glyph
        //     exactly the intended full em (was previously reachable only
        //     through the buggy hmtx path) — though the glyph OUTLINE for
        //     that same font still renders at the wrong scale/baseline, a
        //     separate, unresolved defect in the wrapper's synthesized
        //     metrics tables (see #584's follow-up notes).
        //   - Otherwise (no /Widths at all) fall back to Skia's MeasureText.
        float widthInFontUnits;
        bool advanceFromPdfWidths =
            currentFont.Widths != null &&
            sourceBytes != null;

        if (advanceFromPdfWidths)
        {
            widthInFontUnits = SumPdfWidths(sourceBytes!) * effectiveSize;
        }
        else if (currentFont.ByteToGlyph != null && sourceBytes != null)
        {
            // Same byte-coded glyph-ID path as the draw branch above —
            // SkiaSharp 3 moved MeasureText off SKPaint, the glyph-id
            // overload now lives on SKFont.
            var gids = BuildGlyphIds(sourceBytes, currentFont.ByteToGlyph);
            widthInFontUnits = font.MeasureText(new ReadOnlySpan<ushort>(gids), measurePaint);
        }
        else
        {
            widthInFontUnits = font.MeasureText(text, measurePaint);
        }

        // BACK TO THE TEXT-MATRIX FRAME. §9.4.4:
        //     tx = ((w0 − Tj/1000)·Tfs + Tc + Tw)·Th
        // Only the glyph-width term is multiplied by Tfs, so only it carries
        // the sign of a negative font size; Tc and Tw are added afterwards and
        // keep pushing the nominal way. sizeSign is +1 for every ordinary
        // document, so this line is a no-op there. #970
        var width = widthInFontUnits * xyRatio * sizeSign;
        var charCount = sourceBytes?.Length ?? text.Length;
        var spaceCount = sourceBytes != null
            ? sourceBytes.Count(b => b == 0x20)
            : text.Count(c => c == ' ');

        // PDF spec 9.4.4: Tc and Tw are in UNSCALED text space units. Scale by
        // the text matrix's X-scale before adding to device-space advance,
        // otherwise Tw-heavy layouts overlap themselves (birth-cert form).
        var tmA = _textState.TextMatrixA;
        var tmB = _textState.TextMatrixB;
        var xScale = (float)Math.Sqrt(tmA * tmA + tmB * tmB);
        if (xScale < 1e-6f) xScale = 1f;
        width += charCount * _textState.CharSpacing * xScale;
        width += spaceCount * _textState.WordSpacing * xScale;
        width *= _textState.HorizontalScale / 100.0f;

        AdvanceTextMatrixX(width);
    }
}
