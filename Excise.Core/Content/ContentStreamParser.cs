using System;
using System.Text;
using System.Threading;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Content;

/// <summary>
/// Parses PDF content stream bytes into a sequence of operators.
/// ISO 32000-2:2020 Section 7.8.2.
///
/// <para>This class owns no state machine. It is a SINK over
/// <see cref="ContentStreamWalker"/> — the single tokenizer + graphics/text
/// state machine + glyph walk — and its whole job is to aggregate what the
/// walker reports into per-operator metadata: the glyph cells of a
/// text-showing operator into its <see cref="ContentOperator.BoundingBox"/>,
/// the decoded glyphs into its <see cref="ContentOperator.TextContent"/>, and
/// path/inline-image extents into the same box for everything else. #992.</para>
/// </summary>
public class ContentStreamParser
{
    private readonly ContentStreamWalker _walker;

    /// <summary>Max nesting depth for content-stream arrays before bailing.</summary>
    public int MaxNestingDepth
    {
        get => _walker.MaxNestingDepth;
        set => _walker.MaxNestingDepth = value;
    }

    /// <summary>
    /// When false, the parser skips the state-tracking pass that annotates
    /// each operator with <see cref="ContentOperator.BoundingBox"/> and the
    /// decoded <see cref="ContentOperator.TextContent"/> — the font
    /// resolution, ToUnicode CMap parsing, glyph-width advances, graphics
    /// state cloning and path-bounds accumulation that exist only to compute
    /// that metadata. Operator names, operands, and inline-image data are
    /// byte-for-byte identical either way. Intended for callers that
    /// re-execute the operators under their own full state machine and never
    /// read the metadata (Excise.Rendering does this — see #598).
    /// ⚠️ Redaction and text extraction rely on the metadata pass and MUST
    /// keep the default (true): LetterFinder/GlyphRemover match on the
    /// decoded operator text and bounds this pass produces.
    /// Internal — a parser tuning option, not a public SemVer commitment;
    /// Excise.Rendering reaches it via InternalsVisibleTo (see #598).
    /// </summary>
    internal bool ComputeOperatorMetadata
    {
        get => _walker.TrackState;
        set => _walker.TrackState = value;
    }

    // Accumulators. All of this is per-operator metadata derived from what the
    // walker reports; none of it is state that survives q/Q, which is why none
    // of it lives here twice.
    private readonly List<ContentOperator> _operators = new();
    private ContentOperator? _current;

    // Current path for bounds calculation
    private double _pathMinX, _pathMinY, _pathMaxX, _pathMaxY;
    private bool _pathStarted;

    // Text-showing accumulation: the decoded glyphs and the union of their
    // cells, per shown string and then per operator.
    private readonly StringBuilder _textContent = new();
    private double _strMinX, _strMinY, _strMaxX, _strMaxY;
    private double _opMinX, _opMinY, _opMaxX, _opMaxY;
    private bool _opHasBounds;

    // Per-text-showing-op pen advance in TJ-adjustment units (#758) —
    // reset before each Tj/TJ/'/" string is processed, accumulated per glyph,
    // then stored on the operator so redaction can replay a removed op's
    // advance as a numeric TJ adjustment.
    private double _opAdvanceThousandths;
    private bool _opAdvanceExpressible;

    /// <summary>
    /// Create a parser for the given content bytes.
    /// </summary>
    /// <param name="content">Raw content stream bytes.</param>
    /// <param name="page">Optional page reference for font resolution.</param>
    public ContentStreamParser(byte[] content, PdfPage? page = null)
    {
        _walker = new ContentStreamWalker(content, page);
    }

    /// <summary>
    /// Parse the content stream into a ContentStream object.
    /// </summary>
    /// <param name="cancellationToken">Cooperatively cancels a runaway parse of
    /// hostile/huge input (#346).</param>
    public ContentStream Parse(CancellationToken cancellationToken = default)
    {
        _operators.Clear();
        _current = null;

        var sink = new OperatorSink(this);
        _walker.Walk(ref sink, cancellationToken);

        return new ContentStream(new List<ContentOperator>(_operators));
    }

    /// <summary>
    /// The walker's consumer. A STRUCT, dispatched through the walker's generic
    /// constraint, so the per-glyph callback allocates nothing and devirtualizes
    /// (#966) — it holds one reference and forwards; every accumulator lives on
    /// the parser.
    /// </summary>
    private readonly struct OperatorSink(ContentStreamParser owner) : IContentStreamSink
    {
        public void OnOperator(string name, List<PdfObject> operands) =>
            owner.BeginOperator(name, operands);

        public void OnInlineImage(PdfDictionary imageParams, byte[] imageData) =>
            owner.AddInlineImage(imageParams, imageData);

        public void OnTextShowBegin() => owner.BeginTextShow();

        public void OnStringBegin() => owner.BeginShownString();

        public void OnGlyph(in WalkedGlyph glyph) => owner.AddGlyph(in glyph);

        public void OnStringEnd(int byteCount) => owner.EndShownString(byteCount);

        public void OnTextShowEnd() => owner.EndTextShow();

        public void OnTjAdjustment(double adjustment) =>
            owner._opAdvanceThousandths -= adjustment;
    }

    #region Operator accumulation

    private void BeginOperator(string name, List<PdfObject> operands)
    {
        var op = new ContentOperator(name, operands.ToList());
        _operators.Add(op);
        _current = op;

        if (!ComputeOperatorMetadata)
            return;

        if (AccumulatePathConstruction(name, operands)) return;
        if (AccumulatePathPainting(name, op)) return;
        AccumulateType3GlyphMetrics(name, operands, op);
    }

    private void AddInlineImage(PdfDictionary imageParams, byte[] imageData)
    {
        // Inline image bounds: the unit square mapped through the CTM, i.e. the
        // four corners (0,0),(1,0),(1,1),(0,1). Skipped in metadata-free mode —
        // the CTM is not tracked there, so a computed box would be wrong rather
        // than merely absent.
        var op = new ContentOperator("BI", new PdfObject[] { imageParams });
        if (ComputeOperatorMetadata)
            op.BoundingBox = _walker.TransformBounds(0, 0, 1, 1);
        op.InlineImageData = imageData;
        _operators.Add(op);
        _current = op;
    }

    private bool AccumulatePathConstruction(string name, List<PdfObject> operands)
    {
        switch (name)
        {
            case "m":
                if (operands.Count >= 2)
                    StartPath(GetNumber(operands[0]), GetNumber(operands[1]));
                return true;

            case "l":
                if (operands.Count >= 2)
                    ExtendPath(GetNumber(operands[0]), GetNumber(operands[1]));
                return true;

            case "c":
                if (operands.Count >= 6)
                {
                    ExtendPath(GetNumber(operands[0]), GetNumber(operands[1]));
                    ExtendPath(GetNumber(operands[2]), GetNumber(operands[3]));
                    ExtendPath(GetNumber(operands[4]), GetNumber(operands[5]));
                }
                return true;

            case "v":
            case "y":
                if (operands.Count >= 4)
                {
                    ExtendPath(GetNumber(operands[0]), GetNumber(operands[1]));
                    ExtendPath(GetNumber(operands[2]), GetNumber(operands[3]));
                }
                return true;

            case "re":
                if (operands.Count >= 4)
                {
                    var x = GetNumber(operands[0]);
                    var y = GetNumber(operands[1]);
                    var w = GetNumber(operands[2]);
                    var h = GetNumber(operands[3]);
                    StartPath(x, y);
                    ExtendPath(x + w, y);
                    ExtendPath(x + w, y + h);
                    ExtendPath(x, y + h);
                }
                return true;

            case "h":
                return true;

            default:
                return false;
        }
    }

    private bool AccumulatePathPainting(string name, ContentOperator op)
    {
        switch (name)
        {
            case "S":
            case "s":
            case "f":
            case "F":
            case "f*":
            case "B":
            case "B*":
            case "b":
            case "b*":
                if (_pathStarted)
                    op.BoundingBox = _walker.TransformBounds(_pathMinX, _pathMinY, _pathMaxX, _pathMaxY);

                EndPath();
                return true;

            case "n":
                EndPath();
                return true;

            default:
                return false;
        }
    }

    private static void AccumulateType3GlyphMetrics(string name, List<PdfObject> operands, ContentOperator op)
    {
        if (name == "d1" && operands.Count >= 6)
        {
            op.BoundingBox = new PdfRectangle(
                GetNumber(operands[2]),
                GetNumber(operands[3]),
                GetNumber(operands[4]),
                GetNumber(operands[5]));
        }
    }

    #endregion

    #region Text accumulation

    private void BeginTextShow()
    {
        if (_current is { } op)
        {
            op.GraphicsTransform = new ContentTransform(
                _walker.Ctm_a, _walker.Ctm_b, _walker.Ctm_c,
                _walker.Ctm_d, _walker.Ctm_e, _walker.Ctm_f);
            op.TextTransform = new ContentTransform(
                _walker.Tm_a, _walker.Tm_b, _walker.Tm_c,
                _walker.Tm_d, _walker.Tm_e, _walker.Tm_f);
        }

        _textContent.Clear();
        _opMinX = double.MaxValue; _opMinY = double.MaxValue;
        _opMaxX = double.MinValue; _opMaxY = double.MinValue;
        _opHasBounds = false;

        // Start accumulating the pen advance of one text-showing operator
        // (#758). Reached after any implicit T*/Tw/Tc side effects of '/" have
        // been applied — those are line-matrix/state moves, not pen advance.
        _opAdvanceThousandths = 0;
        _opAdvanceExpressible = true;
    }

    private void BeginShownString()
    {
        _strMinX = double.MaxValue; _strMinY = double.MaxValue;
        _strMaxX = double.MinValue; _strMaxY = double.MinValue;
    }

    private void AddGlyph(in WalkedGlyph glyph)
    {
        var cell = glyph.Cell;
        _strMinX = Math.Min(_strMinX, cell.Left);
        _strMinY = Math.Min(_strMinY, cell.Bottom);
        _strMaxX = Math.Max(_strMaxX, cell.Right);
        _strMaxY = Math.Max(_strMaxY, cell.Top);

        _textContent.Append(glyph.Unicode);

        AccumulateOpAdvance(glyph.DisplacementThousandths, glyph.Spacing, glyph.FontSize);
    }

    private void EndShownString(int byteCount)
    {
        // An empty operand contributes no box at all — the distinction between
        // "no bounds" and "a degenerate box" is what tells a caller whether the
        // operator drew anything.
        if (byteCount == 0)
            return;

        _opMinX = Math.Min(_opMinX, _strMinX);
        _opMinY = Math.Min(_opMinY, _strMinY);
        _opMaxX = Math.Max(_opMaxX, _strMaxX);
        _opMaxY = Math.Max(_opMaxY, _strMaxY);
        _opHasBounds = true;
    }

    private void EndTextShow()
    {
        if (_current is not { } op)
            return;

        op.TextContent = _textContent.ToString();
        op.BoundingBox = _opHasBounds
            ? new PdfRectangle(_opMinX, _opMinY, _opMaxX, _opMaxY)
            : null;

        // Store the accumulated pen advance on the operator, in TJ-adjustment
        // units (see ContentOperator.TextAdvanceThousandths).
        op.TextAdvanceThousandths =
            _opAdvanceExpressible ? _opAdvanceThousandths : null;
    }

    /// <summary>
    /// Accumulate one glyph's pen displacement into the current op's advance
    /// (#758). <paramref name="displacementThousandths"/> is the glyph-space
    /// displacement along the writing direction (horizontal: w0; vertical:
    /// w1y) and <paramref name="spacing"/> the active Tc(+Tw) contribution in
    /// text-space units, converted here into the same thousandths-of-Tfs
    /// units a TJ number uses — mirroring exactly the §9.4.4 advance the
    /// walker applies to the text matrix, with the shared Th factor cancelled.
    /// </summary>
    private void AccumulateOpAdvance(double displacementThousandths, double spacing, double fontSize)
    {
        if (spacing != 0)
        {
            if (Math.Abs(fontSize) < 1e-9)
            {
                // tx = spacing·Th with Tfs = 0 — no TJ number can reproduce
                // it (the number is multiplied by Tfs). Mark inexpressible.
                _opAdvanceExpressible = false;
                return;
            }
            displacementThousandths += spacing * 1000.0 / fontSize;
        }
        _opAdvanceThousandths += displacementThousandths;
    }

    #endregion

    #region Path Tracking

    private void StartPath(double x, double y)
    {
        _pathStarted = true;
        _pathMinX = _pathMaxX = x;
        _pathMinY = _pathMaxY = y;
    }

    private void ExtendPath(double x, double y)
    {
        if (!_pathStarted)
        {
            StartPath(x, y);
            return;
        }

        _pathMinX = Math.Min(_pathMinX, x);
        _pathMinY = Math.Min(_pathMinY, y);
        _pathMaxX = Math.Max(_pathMaxX, x);
        _pathMaxY = Math.Max(_pathMaxY, y);
    }

    private void EndPath()
    {
        _pathStarted = false;
    }

    #endregion

    private static double GetNumber(PdfObject obj)
    {
        return obj switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            _ => 0
        };
    }
}
