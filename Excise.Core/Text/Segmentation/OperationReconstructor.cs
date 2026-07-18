using System;
using System.Collections.Generic;
using Excise.Core.Content;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Rebuilds a text block from the <see cref="TextSegment"/>s that a redaction
/// operation has decided to keep. Emits a self-contained BT/ET sequence:
/// <c>BT /Font 1 Tf [Tc Tw Tz Tr Ts TL] (Tm Tj)* ET</c>. Each kept segment
/// gets its own explicit Tm so positioning doesn't drift across the removed
/// runs; glyph sizes travel in the Tm matrix's a/d components (the common
/// "1 Tf + sized Tm" idiom the renderer understands).
/// </summary>
/// <remarks>
/// Ported from Excise.App.Redaction.GlyphLevel.OperationReconstructor. The
/// rotation-correction branch (content-stream vs visual coordinates) is
/// deferred — callers that redact rotated pages should pre-transform
/// <see cref="TextSegment.StartX"/>/<see cref="TextSegment.StartY"/> into
/// content-stream space before invoking.
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
    {
        var ops = new List<ContentOperator>();
        if (segments.Count == 0) return ops;

        var fontName = string.IsNullOrEmpty(context.FontName) ? "F1" : context.FontName;
        var fontSize = (context.FontSize > 0 && context.FontSize < 1000) ? context.FontSize : 12.0;

        ops.Add(ContentOperator.BeginText());

        // Use Tf with size 1 — the actual size goes in each segment's Tm. Matches
        // the idiom many PDF producers use (`/F1 1 Tf` + `s 0 0 s x y Tm`) and
        // keeps the renderer's Y-scale math consistent.
        ops.Add(new ContentOperator("Tf", new PdfObject[]
        {
            new PdfName(fontName),
            new PdfReal(1.0),
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

        foreach (var segment in segments)
        {
            // Tm places the cursor and encodes the effective font size. Splitting
            // per segment (instead of Td-stepping) isolates each kept run so
            // removed runs can't drift their neighbors.
            ops.Add(ContentOperator.TextMatrix(
                fontSize, 0,
                0, fontSize,
                segment.StartX, segment.StartY));

            // CID / ToUnicode fonts round-trip via raw bytes — Unicode text
            // can't be re-encoded without the original code mapping. When the
            // segment carries raw bytes we emit them as a hex string; otherwise
            // the plain Tj string path handles simple fonts.
            var rawBytes = segment.GetRawBytes();
            bool useRawBytes = rawBytes.Length > 0 &&
                               (segment.IsCidFont || segment.HasToUnicode);

            PdfObject operand = useRawBytes
                ? new PdfString(rawBytes)
                : new PdfString(segment.Text);

            ops.Add(new ContentOperator("Tj", new PdfObject[] { operand }));
        }

        ops.Add(ContentOperator.EndText());
        return ops;
    }
}
