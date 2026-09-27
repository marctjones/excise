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
/// Still deferred: nested BT in malformed streams.
/// </para>
/// <para>
/// Partial glyph rasterization (#278) is DECLINED, not pending. It asks that a
/// glyph the redaction rectangle only partly covers be rasterized so the
/// uncovered sliver survives. That is the exact property
/// <see cref="GlyphRemovalStrategy.AnyOverlap"/> — the default — exists to
/// prevent: its own summary says it "prevents partial glyph exposure". Callers
/// who want the other trade-off already have
/// <see cref="GlyphRemovalStrategy.FullyContained"/> and
/// <see cref="GlyphRemovalStrategy.CenterPoint"/>, which reach it by removing
/// less rather than by re-drawing pixels of redacted text. Separately,
/// rasterizing needs a renderer, and Excise.Core has no project references by
/// design (Excise.Rendering depends on Core, never the reverse).
/// </para>
/// <para>
/// No longer deferred — do not re-add these: Form-XObject traversal
/// (FormXObjectFlattener) and page rotation (/Rotate is honoured end-to-end via
/// PdfPage.ToContentStreamCoordinates, covered by RotatedPageRedactionTests).
/// The old wording also pointed at #313/#281 as the tracking issues; both are
/// closed and were about CJK/CID font support, not this list.
/// </para>
/// </remarks>
internal class GlyphRemover
{
    /// <summary>
    /// #1044 SPIKE FLAG. Off by default: this changes how redaction removes
    /// text, and it is being MEASURED before it is trusted. Tests turn it on to
    /// compare collateral against the restructuring path.
    ///
    /// <para>Not an env var deliberately — a redaction tool whose removal
    /// mechanism depends on ambient configuration is a tool whose output you
    /// cannot reason about from the file alone.</para>
    /// </summary>
    internal static bool BlankInPlace
    {
        get => _blankInPlace.Value;
        set => _blankInPlace.Value = value;
    }

    // Per async context, not process-wide: the #1044 spike tests switch this on, and a process-wide
    // flag leaked into every redaction test that happened to run in parallel with them (t1 chunk 21).
    // AsyncLocal still reaches the worker tasks a redaction spawns.
    private static readonly AsyncLocal<bool> _blankInPlace = new();

    /// <summary>
    /// #1145 — WIDTH-CLOSING mode, per-instance and off by default. When set,
    /// reconstructed runs collapse the oversized gap a removed run leaves, so
    /// the advance-width residue channel (#1116) is destroyed. Opt-in: the
    /// width-preserving default is unchanged unless a caller sets this.
    /// </summary>
    public bool CloseWidth { get; set; }

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
        => ProcessOperations(operations, letters, new[] { redactionArea }, strategy);

    internal List<ContentOperator> ProcessOperations(
        IReadOnlyList<ContentOperator> operations,
        IReadOnlyList<Letter> letters,
        IReadOnlyList<PdfRectangle> redactionAreas,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap)
    {
        var blocks = IdentifyTextBlocks(operations);

        // Every text-showing operator with glyphs to remove, found before any
        // is rewritten: a width-closing policy needs the whole page's removals
        // to know how far each line's remaining runs move (#1751).
        var removals = new Dictionary<int, WidthClosureLedger.Removal>();
        foreach (var block in blocks)
            FindRemovals(operations, block, letters, redactionAreas, strategy, removals);

        WidthClosureLedger? ledger = null;
        if (CloseWidth && removals.Count > 0)
        {
            ledger = WidthClosureLedger.Build(operations, letters, removals.Values.ToList());
            WidthNotes.AddRange(ledger.Notes);
        }

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

            ProcessBlock(operations, block, removals, ledger, redactionAreas, strategy, result);
            i = block.EtIndex + 1;
        }

        return result;
    }

    /// <summary>
    /// What the width policy could not do on this page (#1751), one row per
    /// line. Reported by the caller, never dropped.
    /// </summary>
    internal List<string> WidthNotes { get; } = new();

    private static BlockInfo? FindBlockStartingAt(List<BlockInfo> blocks, int index)
    {
        foreach (var b in blocks)
            if (b.BtIndex == index) return b;
        return null;
    }

    /// <summary>
    /// Classify each text-showing operator in the block: its letters intersect
    /// a redaction area (→ a <see cref="WidthClosureLedger.Removal"/>) or they
    /// don't (→ kept as-is).
    /// </summary>
    private void FindRemovals(
        IReadOnlyList<ContentOperator> operations,
        BlockInfo block,
        IReadOnlyList<Letter> letters,
        IReadOnlyList<PdfRectangle> redactionAreas,
        GlyphRemovalStrategy strategy,
        Dictionary<int, WidthClosureLedger.Removal> removals)
    {
        for (int idx = block.BtIndex; idx <= block.EtIndex; idx++)
        {
            var op = operations[idx];
            if (op.Category != OperatorCategory.TextShowing)
                continue;

            var text = op.TextContent ?? op.RawTextOperand;
            if (string.IsNullOrEmpty(text)) continue;

            var matches = _letterFinder.FindOperationLetters(text, letters, op.BoundingBox);
            if (matches.Count == 0) continue;

            var matchesToRemove = matches
                .Where(m => redactionAreas.Any(area => strategy.Selects(m.Letter.GlyphRectangle, area)))
                .ToList();
            if (matchesToRemove.Count == 0) continue;

            removals[idx] = new WidthClosureLedger.Removal(idx, op, text, matches, matchesToRemove);
        }
    }

    private void ProcessBlock(
        IReadOnlyList<ContentOperator> operations,
        BlockInfo block,
        Dictionary<int, WidthClosureLedger.Removal> removals,
        WidthClosureLedger? ledger,
        IReadOnlyList<PdfRectangle> redactionAreas,
        GlyphRemovalStrategy strategy,
        List<ContentOperator> output)
    {
        // State operators (Tf/Tc/Tm/etc.) always pass through; the
        // reconstructed block, if one gets emitted, is parameterized by the
        // text state the parser stamped on the op.
        var intersectingTextOpIndices = new HashSet<int>();
        var blankedOperators = new Dictionary<int, ContentOperator>();
        var reconstructionJobs = new List<ReconstructionJob>();

        for (int idx = block.BtIndex; idx <= block.EtIndex; idx++)
        {
            if (!removals.TryGetValue(idx, out var removal)) continue;
            var op = removal.Op;

            // #1091: operand-level TJ-split — the PRIMARY removal path. It
            // byte-splices the matched glyphs out of the operator's own operand
            // and replaces each removed run with ONE advance adjustment (#1045),
            // WITHOUT touching the operator's place in the stream, its BT/ET, or
            // its Tf state. Restructuring — where every known collateral defect
            // lives (#1038's 5-36% loss, #1039, Pitfall 2) — is confined to the
            // reported fallback below. Correct for Type0/CID via the #1092 byte
            // offset. Returns null (falls back) where it can't safely split.
            // A malformed (implicitly-ended, no ET) block goes to reconstruction,
            // which REPAIRS it (§9.4 forbids an unterminated BT); the split keeps
            // operators in place and would leave the input's invalidity intact.
            var splitOp = block.ImplicitEnd
                ? null : OperandGlyphSplitter.TrySplit(op, removal.ToRemove, CloseWidth);
            if (splitOp != null)
            {
                blankedOperators[idx] = splitOp;
                continue;
            }

            // #1044 SPIKE (now secondary to the split): blank the matched codes
            // in place. Refused (null) for anything where a decoded index is not
            // a byte offset; see GlyphBlanker. Blanking keeps the advance, so it
            // never runs under a width-closing ledger.
            if (BlankInPlace && ledger == null)
            {
                var blankedOp = GlyphBlanker.TryBlank(op, removal.ToRemove);
                if (blankedOp != null)
                {
                    blankedOperators[idx] = blankedOp;
                    continue;
                }
            }

            intersectingTextOpIndices.Add(idx);

            // #942: the EFFECTIVE size, not the Tf operand. Producers routinely
            // write `/F1 1 Tf` and carry the real scale in Tm (the W-9's fonts
            // are all size 1 by Tf, 7-12 by matrix). The stamped FontSize is the
            // Tf operand, so reconstructing with it drew kept text at 1pt — the
            // matched letters' transformed glyph heights are the ground truth
            // for what size this run actually renders at; 0 means none, and the
            // reconstructor falls back to the stamped size.
            var effectiveSize = MedianGlyphHeight(removal.Matches);
            reconstructionJobs.Add(new ReconstructionJob
            {
                Source = op,
                Text = removal.Text,
                Matches = removal.Matches,
                EffectiveFontSize = effectiveSize > 0.01 ? effectiveSize : 0,
            });
        }

        if (reconstructionJobs.Count == 0 && ledger == null)
        {
            // No RESTRUCTURING needed. Copy the block through, substituting any
            // operator whose codes were blanked in place (#1044).
            for (int idx = block.BtIndex; idx <= block.EtIndex; idx++)
                output.Add(blankedOperators.TryGetValue(idx, out var b) ? b : operations[idx]);
            return;
        }

        // Build the reconstructed BT…ET block(s) that will go AFTER the
        // (possibly trimmed) original block.
        var reconstructed = BuildReconstructedOps(reconstructionJobs, redactionAreas, strategy, ledger);

        // Keep the original block minus intersecting text. Even when every text
        // op was removed, its Tf/Tc/Tw/Tz state operators must survive: text
        // state persists across BT/ET and later blocks may inherit it (#942).
        // Empty BT/ET plus positioning operators draw nothing and retain no
        // secret bytes, while preserving those downstream semantics.
        //
        // #1751: under a width-closing ledger, `chain` is the page-space shift
        // the current pen chain already carries. It restarts at every operator
        // that sets the pen from the line matrix; a text-showing operator whose
        // target shift differs from it gets a numeric-only TJ in front.
        double chain = 0;
        for (int idx = block.BtIndex; idx <= block.EtIndex; idx++)
        {
            var op = operations[idx];
            if (op.Name is "BT" or "Td" or "TD" or "Tm" or "T*") chain = 0;

            if (intersectingTextOpIndices.Contains(idx))
            {
                // #758: a removed text-op's pen advance (and, for '/",
                // its line-move/spacing side effects) must still be
                // consumed, or every later run in this block that relies
                // on the accumulated §9.4.4 pen position collapses back
                // to the last explicit positioning operator. Emits ONLY
                // geometry/state operators — never text.
                chain = EmitRemovedOperatorCompensation(operations, block, idx, output, ledger, chain);
                continue;
            }

            var emitted = blankedOperators.TryGetValue(idx, out var blanked) ? blanked : op;
            if (ledger != null && op.Category == OperatorCategory.TextShowing)
            {
                chain = EmitShifted(idx, op, emitted, ledger, chain, output);
                continue;
            }
            output.Add(emitted);
        }

        if (reconstructionJobs.Count == 0) return;

        // #1039: the source block ran to end-of-content with no ET. Close it
        // before appending the reconstruction — text objects cannot nest
        // (§9.4), so emitting BT with one still open would make the output
        // less valid than the input we were handed.
        if (block.ImplicitEnd)
            output.Add(ContentOperator.EndText());

        output.AddRange(reconstructed);
    }

    /// <summary>
    /// #1751 — emit a kept (or split) text-showing operator so its glyphs land
    /// at the ledger's target: when the pen chain it inherits carries a
    /// different shift, a numeric-only TJ goes first. <c>'</c> and <c>"</c>
    /// reset the pen themselves (their implicit T* would discard a TJ placed
    /// before them), so they are written out as their §9.4.3 equivalents with
    /// the TJ after the T*. Returns the shift the chain carries past the operator.
    /// </summary>
    private static double EmitShifted(
        int index, ContentOperator source, ContentOperator emitted,
        WidthClosureLedger ledger, double chain, List<ContentOperator> output)
    {
        var quote = source.Name is "'" or "\"";
        if (quote) chain = 0;

        var target = ledger.TargetOf(source);
        var shift = target is double t && !double.IsNaN(t) ? t - chain : 0;
        if (Math.Abs(shift) < 1e-3)
        {
            output.Add(emitted);
            return chain + ledger.InternalShift(index);
        }

        var move = WidthClosureLedger.ShiftOperator(source, shift);
        if (quote)
        {
            var ops = source.Operands;
            if (source.Name == "\"")
            {
                output.Add(new ContentOperator("Tw", new[] { ops[0] }));
                output.Add(new ContentOperator("Tc", new[] { ops[1] }));
            }
            output.Add(new ContentOperator("T*"));
            output.Add(move);
            output.Add(new ContentOperator("Tj", new[] { ops[^1] }));
        }
        else
        {
            output.Add(move);
            output.Add(emitted);
        }
        return chain + shift + ledger.InternalShift(index);
    }

    /// <summary>
    /// Replace a removed text-showing operator with the non-text side effects
    /// it had on the block (#758): the pen advance of the glyphs it drew, and
    /// for <c>'</c>/<c>"</c> the implicit next-line move (and <c>"</c>'s
    /// Tw/Tc settings) that later operators in the block depend on. The
    /// advance is replayed as a numeric-only TJ adjustment — under the same
    /// ambient Tf/Tz state (which the keep-as-is branch preserves) a TJ
    /// number of −advance reproduces the removed op's §9.4.4 displacement
    /// exactly, moves only the text matrix (not the line matrix), and draws
    /// nothing. The removed text itself is NEVER re-emitted in any form.
    /// </summary>
    /// <remarks>
    /// #1751: under a width-closing ledger the replayed advance leaves out the
    /// removed runs' own advance, as the operand split does. Replaying all of
    /// it kept every later run of the block where it was, so the gap the
    /// reconstructed runs had just closed reopened in front of them.
    /// </remarks>
    private static double EmitRemovedOperatorCompensation(
        IReadOnlyList<ContentOperator> operations,
        BlockInfo block,
        int removedIndex,
        List<ContentOperator> output,
        WidthClosureLedger? ledger,
        double chain)
    {
        var removed = operations[removedIndex];

        // " sets word spacing and character spacing before showing text
        // (§9.4.3) — text state that persists past the operator.
        if (removed.Name == "\"" && removed.Operands.Count >= 3)
        {
            output.Add(new ContentOperator("Tw", new[] { removed.Operands[0] }));
            output.Add(new ContentOperator("Tc", new[] { removed.Operands[1] }));
        }

        // ' and " both move to the next line (equivalent to T*) before
        // showing text — a line-matrix move every subsequent operator in the
        // block builds on.
        if (removed.Name == "'" || removed.Name == "\"")
        {
            output.Add(new ContentOperator("T*"));
            chain = 0;
        }

        // Pen-advance compensation. Null means the parser couldn't express
        // the advance (metadata pass off, synthetic op, degenerate state) —
        // in that case fall back to today's behavior rather than guessing.
        if (removed.TextAdvanceThousandths is not double advance) return chain;
        var number = -advance;
        if (ledger != null && WidthClosureLedger.UnitOf(removed) is double unit)
        {
            var shift = ledger.TargetOf(removed) is double t && !double.IsNaN(t) ? t - chain : 0;
            number += ledger.ClosedThousandths(removedIndex, removed) - shift / unit;
            chain += shift + ledger.InternalShift(removedIndex);
        }
        if (Math.Abs(number) < 1e-6) return chain;
        if (!PenPositionMattersAfter(operations, block, removedIndex)) return chain;

        output.Add(new ContentOperator("TJ", new PdfObject[]
        {
            new PdfArray(new PdfObject[] { new PdfReal(number) }),
        }));
        return chain;
    }

    /// <summary>
    /// True when some Tj/TJ after <paramref name="removedIndex"/> in the
    /// block still depends on the accumulated pen position — i.e. appears
    /// before any operator that resets the pen from the line matrix
    /// (Td/TD/Tm/T*, or the implicit T* of '/").
    /// </summary>
    private static bool PenPositionMattersAfter(
        IReadOnlyList<ContentOperator> operations, BlockInfo block, int removedIndex)
    {
        for (int i = removedIndex + 1; i <= block.EtIndex; i++)
        {
            var op = operations[i];
            if (op.Name == "Tj" || op.Name == "TJ") return true;
            if (op.Category == OperatorCategory.TextPositioning) return false;
            if (op.Name == "'" || op.Name == "\"") return false;
        }
        return false;
    }

    private List<ContentOperator> BuildReconstructedOps(
        List<ReconstructionJob> jobs,
        IReadOnlyList<PdfRectangle> redactionAreas,
        GlyphRemovalStrategy strategy,
        WidthClosureLedger? ledger)
    {
        var result = new List<ContentOperator>();
        foreach (var job in jobs)
        {
            var bounds = ComputeBoundsFromMatches(job.Matches);
            var segments = _textSegmenter.BuildSegments(
                job.Text, bounds, job.Matches, redactionAreas, strategy);

            if (segments.Count == 0) continue; // entire op fully redacted

            var reconstructed = _reconstructor.ReconstructWithPositioning(
                segments, ReconstructionContext(job.Source.TextState, ledger),
                job.Source.GraphicsTransform, job.Source.TextTransform, job.EffectiveFontSize);
            if (reconstructed.Count == 0) continue;
            result.AddRange(reconstructed);
        }
        return result;
    }

    /// <summary>
    /// The text state the parser stamped on the source operator (#1830). A
    /// synthetic operator carries none, so no text-state operator is emitted and
    /// the rebuilt run draws under the ambient state.
    /// </summary>
    private static OperationReconstructor.Context ReconstructionContext(
        ContentStreamWalker.TextStateSnapshot? s, WidthClosureLedger? ledger) =>
        s is null
            ? new() { FontName = "", FontSize = 0, Shift = ledger == null ? null : ledger.ShiftAt }
            : new()
            {
                FontName = s.FontName,
                FontExtGState = s.FontExtGState,
                FontSize = s.FontSize,
                CharacterSpacing = s.CharSpacing,
                WordSpacing = s.WordSpacing,
                HorizontalScaling = s.HorizontalScaling,
                TextRenderingMode = s.TextRenderMode,
                TextRise = s.TextRise,
                TextLeading = s.TextLeading,
                Shift = ledger == null ? null : ledger.ShiftAt,   // #1751
            };

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

    /// <summary>
    /// The run's effective rendered size, from the transformed glyph geometry.
    /// Median, so one clipped or superscripted glyph cannot skew the run.
    /// </summary>
    private static double MedianGlyphHeight(List<LetterMatch> matches)
    {
        if (matches.Count == 0) return 0;
        var heights = matches.Select(m => m.Letter.GlyphRectangle.Normalize().Height)
                             .Where(h => h > 0)
                             .OrderBy(h => h)
                             .ToList();
        return heights.Count == 0 ? 0 : heights[heights.Count / 2];
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

        // #1039: a BT that is never closed used to record NO block at all, so
        // every glyph it drew was invisible to reconstruction — the match was
        // found, removal did nothing, and the resulting stall handed the page
        // to the whole-operator fallback, which destroyed the line (#1038).
        // Every real viewer treats end-of-content as an implicit ET (§9.4), so
        // the last operator closes the block here too.
        if (openBt.HasValue && openBt.Value < ops.Count - 1)
            blocks.Add(new BlockInfo
            {
                BtIndex = openBt.Value,
                EtIndex = ops.Count - 1,
                ImplicitEnd = true,
            });

        return blocks;
    }

    /// <summary>Closed index range of a BT…ET block in the operator list.</summary>
    private sealed class BlockInfo
    {
        public required int BtIndex { get; init; }
        public required int EtIndex { get; init; }

        /// <summary>
        /// True when the block was closed by end-of-content rather than by an
        /// <c>ET</c> operator (#1039). The block's operators are copied
        /// unchanged either way, but a reconstructed block appended after this
        /// one must be preceded by an explicit <c>ET</c> — text objects cannot
        /// nest (§9.4), and emitting <c>BT</c> while one is still open would
        /// leave the output less valid than the input.
        /// </summary>
        public bool ImplicitEnd { get; init; }
    }

    /// <summary>A text-op classified as needing reconstruction.</summary>
    private sealed class ReconstructionJob
    {
        public required ContentOperator Source { get; init; }
        public required string Text { get; init; }
        public required List<LetterMatch> Matches { get; init; }
        public required double EffectiveFontSize { get; init; }
    }
}
