using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Orchestrates glyph-level redaction over a content-stream's operator list.
/// Walks BT…ET text blocks and, for each text-showing operator inside a block
/// that overlaps the redaction area, uses <see cref="LetterFinder"/> +
/// <see cref="TextSegmenter"/> + <see cref="OperationReconstructor"/> to emit
/// a new BT…ET block containing only the kept runs. Operators outside any
/// affected block pass through untouched.
/// </summary>
/// <remarks>
/// <para>
/// This is the third piece of the glyph-redaction pipeline being ported from
/// Excise.App.Redaction.GlyphLevel.GlyphRemover. The original used byte-offset
/// <c>StreamPosition</c> values to locate operators; Excise.Core operates on a
/// flat <c>IReadOnlyList&lt;ContentOperator&gt;</c> so we use list indices
/// directly, which simplifies the bookkeeping.
/// </para>
/// <para>
/// Processing rule for each BT/ET block:
/// <list type="bullet">
///   <item>No text-op has letters intersecting the area → block is copied
///     verbatim.</item>
///   <item>Every text-op intersects → block is replaced wholesale by the
///     reconstructed sequence (no surviving original state ops).</item>
///   <item>Mixed (some text-ops intersect, some don't) → original block
///     structure is preserved with intersecting text-ops stripped out,
///     and the reconstructed runs are appended as a new BT…ET block
///     immediately after the original ET. This matches the behavior of
///     the Excise.App.Redaction original and avoids nested BT/ET.</item>
/// </list>
/// </para>
/// <para>
/// Deferred (tracked under #313 / #281): nested BT in malformed streams,
/// Form-XObject traversal, partial glyph rasterization (#278), and page
/// rotation. Callers redacting rotated pages should pre-transform the
/// redaction rectangle into content-stream coordinates.
/// </para>
/// </remarks>
public class GlyphRemover
{
    private readonly LetterFinder _letterFinder;
    private readonly TextSegmenter _textSegmenter;
    private readonly OperationReconstructor _reconstructor;

    public GlyphRemover()
        : this(new LetterFinder(), new TextSegmenter(), new OperationReconstructor()) { }

    public GlyphRemover(
        LetterFinder letterFinder,
        TextSegmenter textSegmenter,
        OperationReconstructor reconstructor)
    {
        _letterFinder = letterFinder;
        _textSegmenter = textSegmenter;
        _reconstructor = reconstructor;
    }

    /// <summary>
    /// Rewrite <paramref name="operations"/> so that any glyph whose bounding
    /// box overlaps <paramref name="redactionArea"/> is removed from the
    /// content stream.
    /// </summary>
    /// <param name="operations">Parsed operators from a content stream.</param>
    /// <param name="letters">Page-level letters from <c>PdfPage.Letters</c>.</param>
    /// <param name="redactionArea">Target area in content-stream coordinates.</param>
    /// <param name="strategy">Which overlap rule decides glyph removal.</param>
    public List<ContentOperator> ProcessOperations(
        IReadOnlyList<ContentOperator> operations,
        IReadOnlyList<Letter> letters,
        PdfRectangle redactionArea,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap)
    {
        var blocks = IdentifyTextBlocks(operations);
        var result = new List<ContentOperator>(operations.Count);

        int i = 0;
        while (i < operations.Count)
        {
            var block = FindBlockStartingAt(blocks, i);
            if (block == null)
            {
                // Not the start of a BT — copy through and advance. Operators
                // inside a BT we've already processed are skipped via the
                // jump at the end of the block branch.
                result.Add(operations[i]);
                i++;
                continue;
            }

            ProcessBlock(operations, block, letters, redactionArea, strategy, result);
            i = block.EtIndex + 1;
        }

        return result;
    }

    private static BlockInfo? FindBlockStartingAt(List<BlockInfo> blocks, int index)
    {
        foreach (var b in blocks)
            if (b.BtIndex == index) return b;
        return null;
    }

    private void ProcessBlock(
        IReadOnlyList<ContentOperator> operations,
        BlockInfo block,
        IReadOnlyList<Letter> letters,
        PdfRectangle redactionArea,
        GlyphRemovalStrategy strategy,
        List<ContentOperator> output)
    {
        // Classify each text-showing operator in the block: either its
        // letters intersect the redaction area (→ reconstruct) or they
        // don't (→ keep as-is). State operators (Tf/Tc/Tm/etc.) always
        // pass through; we use the running state to parameterize the
        // reconstructed block if one gets emitted.
        var state = new TextStateTracker();
        var intersectingTextOpIndices = new HashSet<int>();
        var reconstructionJobs = new List<ReconstructionJob>();

        for (int idx = block.BtIndex; idx <= block.EtIndex; idx++)
        {
            var op = operations[idx];
            state.Apply(op);

            if (op.Category != OperatorCategory.TextShowing)
                continue;

            var text = op.TextContent ?? ExtractTextFromOperands(op);
            if (string.IsNullOrEmpty(text)) continue;

            var matches = _letterFinder.FindOperationLetters(text, letters);
            if (matches.Count == 0) continue;

            bool anyIntersects = matches.Any(m =>
                ShouldRemoveLetter(m.Letter, redactionArea, strategy));
            if (!anyIntersects) continue;

            intersectingTextOpIndices.Add(idx);
            reconstructionJobs.Add(new ReconstructionJob
            {
                Text = text,
                Matches = matches,
                FontName = state.FontName,
                FontSize = state.FontSize,
                CharacterSpacing = state.CharacterSpacing,
                WordSpacing = state.WordSpacing,
                HorizontalScaling = state.HorizontalScaling,
                TextRenderingMode = state.TextRenderingMode,
                TextRise = state.TextRise,
                TextLeading = state.TextLeading,
            });
        }

        if (reconstructionJobs.Count == 0)
        {
            // No glyphs affected — copy the block verbatim.
            for (int idx = block.BtIndex; idx <= block.EtIndex; idx++)
                output.Add(operations[idx]);
            return;
        }

        // Build the reconstructed BT…ET block(s) that will go AFTER the
        // (possibly trimmed) original block.
        var reconstructed = BuildReconstructedOps(reconstructionJobs, redactionArea, strategy);

        // Which surviving text-ops are left in the original block? If none,
        // the whole original block vanishes — otherwise we preserve its
        // state operators and the unaffected text-ops.
        bool anySurvivors = false;
        for (int idx = block.BtIndex; idx <= block.EtIndex; idx++)
        {
            if (operations[idx].Category == OperatorCategory.TextShowing &&
                !intersectingTextOpIndices.Contains(idx))
            {
                anySurvivors = true;
                break;
            }
        }

        if (anySurvivors)
        {
            // Keep original block minus intersecting text-ops; state ops
            // survive so the kept-as-is text-ops keep their positioning.
            for (int idx = block.BtIndex; idx <= block.EtIndex; idx++)
            {
                if (intersectingTextOpIndices.Contains(idx)) continue;
                output.Add(operations[idx]);
            }
        }
        // else: whole original block is dropped; reconstructed ops replace it.

        output.AddRange(reconstructed);
    }

    private List<ContentOperator> BuildReconstructedOps(
        List<ReconstructionJob> jobs,
        PdfRectangle redactionArea,
        GlyphRemovalStrategy strategy)
    {
        var result = new List<ContentOperator>();
        foreach (var job in jobs)
        {
            var bounds = ComputeBoundsFromMatches(job.Matches);
            var segments = _textSegmenter.BuildSegments(
                job.Text, bounds, job.Matches, redactionArea, strategy);

            if (segments.Count == 0) continue; // entire op fully redacted

            var ctx = new OperationReconstructor.Context
            {
                FontName = job.FontName,
                FontSize = job.FontSize,
                CharacterSpacing = job.CharacterSpacing,
                WordSpacing = job.WordSpacing,
                HorizontalScaling = job.HorizontalScaling,
                TextRenderingMode = job.TextRenderingMode,
                TextRise = job.TextRise,
                TextLeading = job.TextLeading,
            };
            result.AddRange(_reconstructor.ReconstructWithPositioning(segments, ctx));
        }
        return result;
    }

    private static PdfRectangle ComputeBoundsFromMatches(List<LetterMatch> matches)
    {
        if (matches.Count == 0) return new PdfRectangle(0, 0, 0, 0);
        double left = double.MaxValue, bottom = double.MaxValue;
        double right = double.MinValue, top = double.MinValue;
        foreach (var m in matches)
        {
            var r = m.Letter.GlyphRectangle;
            if (r.Left < left) left = r.Left;
            if (r.Bottom < bottom) bottom = r.Bottom;
            if (r.Right > right) right = r.Right;
            if (r.Top > top) top = r.Top;
        }
        return new PdfRectangle(left, bottom, right, top);
    }

    private static bool ShouldRemoveLetter(
        Letter letter, PdfRectangle redactionArea, GlyphRemovalStrategy strategy)
    {
        var g = letter.GlyphRectangle.Normalize();
        var r = redactionArea.Normalize();

        bool intersects = g.IntersectsWith(r);
        if (!intersects) return false;

        bool fullyContained =
            r.Contains(g.Left, g.Bottom) && r.Contains(g.Right, g.Top) &&
            r.Contains(g.Left, g.Top) && r.Contains(g.Right, g.Bottom);

        return strategy switch
        {
            GlyphRemovalStrategy.FullyContained => fullyContained,
            GlyphRemovalStrategy.CenterPoint => r.Contains(
                (g.Left + g.Right) * 0.5,
                (g.Bottom + g.Top) * 0.5),
            _ => true, // AnyOverlap
        };
    }

    // Fallback used when the parser didn't populate TextContent. Extracts
    // text from Tj/TJ operands directly; good enough for simple fonts where
    // bytes map to characters 1:1.
    private static string ExtractTextFromOperands(ContentOperator op)
    {
        if (op.Operands.Count == 0) return "";

        // Tj, ', " — first operand is the string.
        if (op.Name == "Tj" || op.Name == "'" || op.Name == "\"")
        {
            if (op.Operands[^1] is PdfString s) return s.Value;
        }
        // TJ — operand is an array of strings and numbers. Concatenate strings.
        if (op.Name == "TJ" && op.Operands[0] is PdfArray arr)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var item in arr)
                if (item is PdfString ps) sb.Append(ps.Value);
            return sb.ToString();
        }
        return "";
    }

    private static List<BlockInfo> IdentifyTextBlocks(IReadOnlyList<ContentOperator> ops)
    {
        var blocks = new List<BlockInfo>();
        int? openBt = null;
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i].Name == "BT") { openBt = i; }
            else if (ops[i].Name == "ET" && openBt.HasValue)
            {
                blocks.Add(new BlockInfo { BtIndex = openBt.Value, EtIndex = i });
                openBt = null;
            }
        }
        return blocks;
    }

    /// <summary>Closed index range of a BT…ET block in the operator list.</summary>
    private sealed class BlockInfo
    {
        public required int BtIndex { get; init; }
        public required int EtIndex { get; init; }
    }

    /// <summary>A text-op classified as needing reconstruction, with its
    /// ambient text state captured.</summary>
    private sealed class ReconstructionJob
    {
        public required string Text { get; init; }
        public required List<LetterMatch> Matches { get; init; }
        public required string FontName { get; init; }
        public required double FontSize { get; init; }
        public required double CharacterSpacing { get; init; }
        public required double WordSpacing { get; init; }
        public required double HorizontalScaling { get; init; }
        public required int TextRenderingMode { get; init; }
        public required double TextRise { get; init; }
        public required double TextLeading { get; init; }
    }

    /// <summary>
    /// Keeps a running snapshot of text-state operator parameters so
    /// reconstructed text-ops can be reconstructed under the same state the
    /// original was drawn in. Covers the state-affecting operators we emit
    /// back out: Tf, Tc, Tw, Tz, Tr, Ts, TL.
    /// </summary>
    private sealed class TextStateTracker
    {
        public string FontName = "F1";
        public double FontSize = 12;
        public double CharacterSpacing;
        public double WordSpacing;
        public double HorizontalScaling = 100;
        public int TextRenderingMode;
        public double TextRise;
        public double TextLeading;

        public void Apply(ContentOperator op)
        {
            switch (op.Name)
            {
                case "Tf":
                    if (op.Operands.Count >= 2)
                    {
                        FontName = op.GetName(0);
                        FontSize = op.GetNumber(1);
                    }
                    break;
                case "Tc": CharacterSpacing = op.GetNumber(0); break;
                case "Tw": WordSpacing = op.GetNumber(0); break;
                case "Tz": HorizontalScaling = op.GetNumber(0); break;
                case "Tr": TextRenderingMode = (int)op.GetNumber(0); break;
                case "Ts": TextRise = op.GetNumber(0); break;
                case "TL": TextLeading = op.GetNumber(0); break;
            }
        }
    }
}
