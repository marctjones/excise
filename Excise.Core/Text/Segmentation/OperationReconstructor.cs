using System;
using System.Collections.Generic;
using Excise.Core.Content;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Rebuilds a text block from the <see cref="TextSegment"/>s that a redaction
/// operation has decided to keep. Emits a graphics-state-isolated text sequence:
/// <c>q BT /Font 1 Tf [Tc Tw Tz Tr Ts TL] (Tm Tj)* ET Q</c>. Each kept segment
/// gets explicit positioning so removed runs cannot shift their neighbours.
/// </summary>
/// <remarks>
/// Source-aware callers provide the graphics and text matrices captured by the
/// parser; public synthetic callers use normalized page-space placement.
/// </remarks>
public class OperationReconstructor
{
    /// <summary>
    /// Context needed to rebuild a text block: the font resource name and
    /// size, plus any non-default text-state parameters that were active
    /// when the original operation was parsed. Defaults match PDF spec.
    /// </summary>
    public sealed class Context
    {
        /// <summary>Font resource name (e.g. "F1", "TT0"). Leading slash omitted.</summary>
        public required string FontName { get; init; }
        /// <summary>Font size in points, in the original text matrix's units.</summary>
        public required double FontSize { get; init; }
        public double CharacterSpacing { get; init; } = 0;
        public double WordSpacing { get; init; } = 0;
        public double HorizontalScaling { get; init; } = 100;
        public int TextRenderingMode { get; init; } = 0;
        public double TextRise { get; init; } = 0;
        public double TextLeading { get; init; } = 0;
    }

    /// <summary>
    /// Emit a complete, self-contained text block for <paramref name="segments"/>.
    /// Returns an empty list when there's nothing to keep.
    /// </summary>
    public List<ContentOperator> ReconstructWithPositioning(
        List<TextSegment> segments,
        Context context)
        => ReconstructWithPositioning(
            segments, context, graphicsTransform: null, textTransform: null,
            effectiveFontSize: context.FontSize);

    internal List<ContentOperator> ReconstructWithPositioning(
        List<TextSegment> segments,
        Context context,
        ContentTransform? graphicsTransform,
        ContentTransform? textTransform,
        double effectiveFontSize)
    {
        var ops = new List<ContentOperator>();
        if (segments.Count == 0) return ops;

        var fontName = string.IsNullOrEmpty(context.FontName) ? "F1" : context.FontName;
        var sourceFontSize = (context.FontSize > 0 && context.FontSize < 1000) ? context.FontSize : 12.0;
        var normalizedFontSize = (effectiveFontSize > 0 && effectiveFontSize < 1000)
            ? effectiveFontSize
            : sourceFontSize;
        var textMatrix = textTransform.GetValueOrDefault();
        var pageToLocal = default(ContentTransform);
        var preserveSourceMatrix = graphicsTransform is { } graphics &&
                                   textTransform.HasValue &&
                                   graphics.TryInvert(out pageToLocal);
        var fontSize = preserveSourceMatrix ? sourceFontSize : normalizedFontSize;

        // Text-state parameters persist across BT/ET. Isolate reconstruction so
        // its Tf/Tc/Tw/Tz settings cannot alter untouched source blocks that
        // rely on inherited text state later in the stream (#942).
        ops.Add(ContentOperator.SaveState());
        ops.Add(ContentOperator.BeginText());

        // Source-aware reconstruction retains the original Tf/Tm scale. The
        // synthetic fallback puts effective size in Tf and uses a unit Tm so
        // text advances are not composed through the size twice (#942).
        ops.Add(new ContentOperator("Tf", new PdfObject[]
        {
            new PdfName(fontName),
            new PdfReal(fontSize),
        }));

        // Emit text-state operators only when they differ from PDF defaults,
        // mirroring the original renderer's behavior and keeping streams terse.
        if (Math.Abs(context.CharacterSpacing) > 0.001)
            ops.Add(new ContentOperator("Tc", new PdfObject[] { new PdfReal(context.CharacterSpacing) }));
        if (Math.Abs(context.WordSpacing) > 0.001)
            ops.Add(new ContentOperator("Tw", new PdfObject[] { new PdfReal(context.WordSpacing) }));
        if (Math.Abs(context.HorizontalScaling - 100.0) > 0.001)
            ops.Add(new ContentOperator("Tz", new PdfObject[] { new PdfReal(context.HorizontalScaling) }));
        if (context.TextRenderingMode != 0)
            ops.Add(new ContentOperator("Tr", new PdfObject[] { new PdfInteger(context.TextRenderingMode) }));
        if (Math.Abs(context.TextRise) > 0.001)
            ops.Add(new ContentOperator("Ts", new PdfObject[] { new PdfReal(context.TextRise) }));
        if (Math.Abs(context.TextLeading) > 0.001)
            ops.Add(new ContentOperator("TL", new PdfObject[] { new PdfReal(context.TextLeading) }));

        void AddPosition(double pageX, double pageY)
        {
            if (preserveSourceMatrix)
            {
                var local = pageToLocal.TransformPoint(pageX, pageY);
                ops.Add(ContentOperator.TextMatrix(
                    textMatrix.A, textMatrix.B, textMatrix.C, textMatrix.D,
                    local.X, local.Y));
            }
            else
            {
                // No source matrices (synthetic callers): normalized page-space
                // placement is the conservative fallback.
                ops.Add(ContentOperator.TextMatrix(
                    1, 0, 0, 1, pageX, pageY));
            }
        }

        foreach (var segment in segments)
        {
            // Producers commonly use custom encodings and TJ adjustments between
            // glyphs. Re-encoding decoded Unicode can turn a simple-font code
            // into UTF-16 bytes, while collapsing a CID run to one Tj discards
            // its positioning. Fully matched simple-font runs retain their bytes;
            // CID runs also replay each extracted baseline exactly (#942).
            var glyphs = segment.LetterMatches;
            var hasCompleteSourceBytes = glyphs.Count == segment.EndIndex - segment.StartIndex &&
                                         glyphs.All(m => m.RawBytes is { Length: > 0 });
            var canPositionGlyphs = segment.IsCidFont && hasCompleteSourceBytes;
            if (canPositionGlyphs)
            {
                foreach (var match in glyphs)
                {
                    AddPosition(match.Letter.StartX, match.Letter.StartY);
                    ops.Add(new ContentOperator("Tj", new PdfObject[]
                    {
                        new PdfString(match.RawBytes!),
                    }));
                }
                continue;
            }

            AddPosition(segment.StartX, segment.StartY);

            // CID / ToUnicode fonts round-trip via raw bytes — Unicode text
            // can't be re-encoded without the original code mapping. When the
            // segment carries raw bytes we emit them as a hex string; otherwise
            // the plain Tj string path handles simple fonts.
            var rawBytes = segment.GetRawBytes();
            bool useRawBytes = rawBytes.Length > 0 &&
                               (hasCompleteSourceBytes || segment.IsCidFont || segment.HasToUnicode);

            PdfObject operand = useRawBytes
                ? new PdfString(rawBytes)
                : new PdfString(segment.Text);

            ops.Add(new ContentOperator("Tj", new PdfObject[] { operand }));
        }

        ops.Add(ContentOperator.EndText());
        ops.Add(ContentOperator.RestoreState());
        return ops;
    }
}
