using System;
using System.Text;
using System.Threading;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Parsing;

namespace Excise.Core.Content;

/// <summary>
/// Parses PDF content stream bytes into a sequence of operators.
/// ISO 32000-2:2020 Section 7.8.2.
/// </summary>
public class ContentStreamParser
{
    private readonly byte[] _content;
    private readonly PdfPage? _page;
    private int _pos;

    // Hostile-input guards (#346): bound recursion (nested arrays) and offer a
    // cooperative cancellation point so a pathological stream can't spin or
    // overflow the stack past the caller's timeout.
    private int _nestingDepth;
    private CancellationToken _cancellationToken;

    /// <summary>Max nesting depth for content-stream arrays before bailing.</summary>
    public int MaxNestingDepth { get; set; } = 256;

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
    internal bool ComputeOperatorMetadata { get; set; } = true;

    /// <summary>
    /// Upper bound on an inline image's data scan when no <c>/L</c> length is
    /// declared (#347). Inline images are meant to be small (§8.9.7); this is
    /// far larger than any legitimate one and just bounds malicious input.
    /// </summary>
    private const int MaxInlineImageScanBytes = 64 * 1024 * 1024;

    // Graphics state tracking
    private readonly Stack<GraphicsState> _stateStack = new();
    private GraphicsState _state = new();

    // Text state tracking
    private double _fontSize = 12;
    private string _fontName = "";
    private PdfDictionary? _currentFont;
    private Dictionary<int, string>? _toUnicodeMap;
    private bool _is2ByteFont = false;
    private PdfDictionary? _cidFontDict;
    private Fonts.CidFontWidths? _cidMetrics;
    private bool _isVerticalWriting;
    // Registered (predefined) CJK CMap support (#515), mirroring
    // TextExtractor exactly — this parser feeds redaction's glyph removal, and
    // its operator text must decode the same way TextExtractor decodes page
    // letters or LetterFinder cannot match them and glyph-level redaction
    // silently degrades to whole-operator removal.
    private Text.CidCMap? _registeredEncodingCMap;
    private IReadOnlyDictionary<int, string>? _registeredCidToUnicode;
    private double _textLeading;
    private double _charSpacing;
    private double _wordSpacing;
    private double _horizontalScaling = 100;
    private double _textRise;

    // Text matrix
    private double _tm_a = 1, _tm_b, _tm_c, _tm_d = 1, _tm_e, _tm_f;
    private double _tlm_e, _tlm_f;

    // Per-text-showing-op pen advance in TJ-adjustment units (#758) —
    // reset before each Tj/TJ/'/" string is processed, accumulated by
    // EmitGlyph/ApplyTjAdjustment, then stored on the operator so redaction
    // can replay a removed op's advance as a numeric TJ adjustment.
    private double _opAdvanceThousandths;
    private bool _opAdvanceExpressible;

    // Current path for bounds calculation
    private double _pathMinX, _pathMinY, _pathMaxX, _pathMaxY;
    private bool _pathStarted;

    /// <summary>
    /// Create a parser for the given content bytes.
    /// </summary>
    /// <param name="content">Raw content stream bytes.</param>
    /// <param name="page">Optional page reference for font resolution.</param>
    public ContentStreamParser(byte[] content, PdfPage? page = null)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _page = page;
    }

    /// <summary>
    /// Parse the content stream into a ContentStream object.
    /// </summary>
    /// <param name="cancellationToken">Cooperatively cancels a runaway parse of
    /// hostile/huge input (#346).</param>
    public ContentStream Parse(CancellationToken cancellationToken = default)
    {
        _cancellationToken = cancellationToken;
        _nestingDepth = 0;
        _pos = 0;
        var operators = new List<ContentOperator>();
        var operands = new List<PdfObject>();

        while (_pos < _content.Length)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            SkipWhitespaceAndComments();
            if (_pos >= _content.Length) break;

            var token = ParseToken();
            if (token == null) continue;

            if (token is string op && IsOperator(op))
            {
                if (op == "BI")
                {
                    // Inline image — parse the image dict + binary data in one shot
                    // so that the raw pixel bytes never enter the general token stream
                    var contentOp = ParseInlineImage();
                    if (contentOp != null)
                        operators.Add(contentOp);
                    operands.Clear();
                }
                else if (op is "ID" or "EI")
                {
                    // Should only appear inside BI handling above; skip if stray
                    operands.Clear();
                }
                else
                {
                    var contentOp = CreateOperator(op, operands);
                    if (contentOp != null)
                        operators.Add(contentOp);
                    operands.Clear();
                }
            }
            else if (token is PdfObject pdfObj)
            {
                operands.Add(pdfObj);
            }
            else if (token is string keyword)
            {
                // §7.8.2: `true`/`false`/`null` are operand literals; anything
                // else here is an operator this parser does not implement, and
                // an unimplemented operator still TERMINATES its operands —
                // leaving them queued would let the next real operator read
                // them as its own. Twin of the same rule in
                // TextExtractor.ParseContentBytes (#980).
                if (keyword == "true") operands.Add(PdfBoolean.True);
                else if (keyword == "false") operands.Add(PdfBoolean.False);
                else if (keyword == "null") operands.Add(PdfNull.Instance);
                else operands.Clear();
            }
        }

        return new ContentStream(operators);
    }

    /// <summary>
    /// Create a ContentOperator and calculate its bounds/properties.
    /// </summary>
    private ContentOperator? CreateOperator(string name, List<PdfObject> operands)
    {
        var op = new ContentOperator(name, operands.ToList());

        // Execute operator to update state and calculate bounds
        if (ComputeOperatorMetadata)
        {
            ExecuteOperator(name, operands, op);
        }

        return op;
    }

    /// <summary>
    /// Execute an operator to track state and calculate bounds.
    /// </summary>
    private void ExecuteOperator(string name, List<PdfObject> operands, ContentOperator op)
    {
        if (ExecuteGraphicsStateOperator(name, operands)) return;
        if (ExecutePathConstructionOperator(name, operands)) return;
        if (ExecutePathPaintingOperator(name, op)) return;
        if (ExecuteTextObjectOperator(name)) return;
        if (ExecuteTextStateOperator(name, operands)) return;
        if (ExecuteTextPositioningOperator(name, operands)) return;
        if (ExecuteTextShowingOperator(name, operands, op)) return;
        if (ExecuteClippingOperator(name)) return;
        if (ExecuteType3GlyphOperator(name, operands, op)) return;
        if (ExecuteColorOperator(name, operands)) return;
        ExecuteAcceptedNoOpOperator(name);
    }

    private bool ExecuteGraphicsStateOperator(string name, List<PdfObject> operands)
    {
        switch (name)
        {
            case "q":
                _stateStack.Push(_state.Clone());
                return true;

            case "Q":
                if (_stateStack.Count > 0)
                    _state = _stateStack.Pop();
                return true;

            case "cm":
                if (operands.Count >= 6)
                {
                    var a = GetNumber(operands[0]);
                    var b = GetNumber(operands[1]);
                    var c = GetNumber(operands[2]);
                    var d = GetNumber(operands[3]);
                    var e = GetNumber(operands[4]);
                    var f = GetNumber(operands[5]);
                    _state.MultiplyCtm(a, b, c, d, e, f);
                }
                return true;

            case "w":
                if (operands.Count >= 1)
                    _state.LineWidth = GetNumber(operands[0]);
                return true;

            case "J":
                if (operands.Count >= 1)
                    _state.LineCap = (int)GetNumber(operands[0]);
                return true;

            case "j":
                if (operands.Count >= 1)
                    _state.LineJoin = (int)GetNumber(operands[0]);
                return true;

            case "M":
                if (operands.Count >= 1)
                    _state.MiterLimit = GetNumber(operands[0]);
                return true;

            case "gs":
                if (operands.Count >= 1 && operands[0] is PdfName gsName && _page != null)
                    ApplyExtGState(gsName.Value);
                return true;

            default:
                return false;
        }
    }

    private bool ExecutePathConstructionOperator(string name, List<PdfObject> operands)
    {
        switch (name)
        {
            case "m":
                if (operands.Count >= 2)
                {
                    var x = GetNumber(operands[0]);
                    var y = GetNumber(operands[1]);
                    StartPath(x, y);
                }
                return true;

            case "l":
                if (operands.Count >= 2)
                {
                    var x = GetNumber(operands[0]);
                    var y = GetNumber(operands[1]);
                    ExtendPath(x, y);
                }
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

    private bool ExecutePathPaintingOperator(string name, ContentOperator op)
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
                    op.BoundingBox = TransformBounds(_pathMinX, _pathMinY, _pathMaxX, _pathMaxY);

                _state.PendingClip = null;
                EndPath();
                return true;

            case "n":
                _state.PendingClip = null;
                EndPath();
                return true;

            default:
                return false;
        }
    }

    private bool ExecuteTextObjectOperator(string name)
    {
        switch (name)
        {
            case "BT":
                _tm_a = 1; _tm_b = 0; _tm_c = 0; _tm_d = 1; _tm_e = 0; _tm_f = 0;
                _tlm_e = 0; _tlm_f = 0;
                return true;

            case "ET":
                return true;

            default:
                return false;
        }
    }

    private bool ExecuteTextStateOperator(string name, List<PdfObject> operands)
    {
        switch (name)
        {
            case "Tf":
                if (operands.Count >= 2)
                {
                    _fontName = operands[0] is PdfName n ? n.Value : "";
                    _fontSize = GetNumber(operands[1]);
                    LoadFont();
                }
                return true;

            case "TL":
                if (operands.Count >= 1)
                    _textLeading = GetNumber(operands[0]);
                return true;

            case "Tc":
                if (operands.Count >= 1)
                    _charSpacing = GetNumber(operands[0]);
                return true;

            case "Tw":
                if (operands.Count >= 1)
                    _wordSpacing = GetNumber(operands[0]);
                return true;

            case "Tz":
                if (operands.Count >= 1)
                    _horizontalScaling = GetNumber(operands[0]);
                return true;

            case "Tr":
                // Recognized (must not fall to the unknown-operator path);
                // render mode itself was write-only state — IDE0051/#911.
                return true;

            case "Ts":
                if (operands.Count >= 1)
                    _textRise = GetNumber(operands[0]);
                return true;

            default:
                return false;
        }
    }

    private bool ExecuteTextPositioningOperator(string name, List<PdfObject> operands)
    {
        switch (name)
        {
            case "Td":
                MoveTextPosition(operands, setLeading: false);
                return true;

            case "TD":
                MoveTextPosition(operands, setLeading: true);
                return true;

            case "Tm":
                if (operands.Count >= 6)
                {
                    _tm_a = GetNumber(operands[0]);
                    _tm_b = GetNumber(operands[1]);
                    _tm_c = GetNumber(operands[2]);
                    _tm_d = GetNumber(operands[3]);
                    _tm_e = GetNumber(operands[4]);
                    _tm_f = GetNumber(operands[5]);
                    _tlm_e = _tm_e;
                    _tlm_f = _tm_f;
                }
                return true;

            case "T*":
                MoveToNextTextLine();
                return true;

            default:
                return false;
        }
    }

    private bool ExecuteTextShowingOperator(string name, List<PdfObject> operands, ContentOperator op)
    {
        switch (name)
        {
            case "Tj":
                if (operands.Count >= 1)
                    SetTextOperatorResult(operands[0], op);
                return true;

            case "TJ":
                if (operands.Count >= 1 && operands[0] is PdfArray arr)
                {
                    CaptureTextOperatorTransforms(op);
                    BeginOpAdvanceTracking();
                    var (text, bounds) = ProcessTextArray(arr);
                    op.TextContent = text;
                    op.BoundingBox = bounds;
                    EndOpAdvanceTracking(op);
                }
                return true;

            case "'":
                MoveToNextTextLine();
                if (operands.Count >= 1)
                    SetTextOperatorResult(operands[0], op);
                return true;

            case "\"":
                if (operands.Count >= 3)
                {
                    _wordSpacing = GetNumber(operands[0]);
                    _charSpacing = GetNumber(operands[1]);
                    MoveToNextTextLine();
                    SetTextOperatorResult(operands[2], op);
                }
                return true;

            default:
                return false;
        }
    }

    private bool ExecuteClippingOperator(string name)
    {
        switch (name)
        {
            case "W":
                _state.PendingClip = "W";
                return true;

            case "W*":
                _state.PendingClip = "W*";
                return true;

            default:
                return false;
        }
    }

    private bool ExecuteType3GlyphOperator(string name, List<PdfObject> operands, ContentOperator op)
    {
        switch (name)
        {
            case "d0":
                return true;

            case "d1":
                if (operands.Count >= 6)
                {
                    op.BoundingBox = new PdfRectangle(
                        GetNumber(operands[2]),
                        GetNumber(operands[3]),
                        GetNumber(operands[4]),
                        GetNumber(operands[5]));
                }
                return true;

            default:
                return false;
        }
    }

    private bool ExecuteColorOperator(string name, List<PdfObject> operands)
    {
        switch (name)
        {
            case "CS":
                if (operands.Count >= 1 && operands[0] is PdfName csNameStroke)
                    _state.StrokeColorSpace = csNameStroke.Value;
                return true;

            case "cs":
                if (operands.Count >= 1 && operands[0] is PdfName csNameFill)
                    _state.FillColorSpace = csNameFill.Value;
                return true;

            case "G":
                _state.StrokeColorSpace = "DeviceGray";
                return true;

            case "g":
                _state.FillColorSpace = "DeviceGray";
                return true;

            case "RG":
                _state.StrokeColorSpace = "DeviceRGB";
                return true;

            case "rg":
                _state.FillColorSpace = "DeviceRGB";
                return true;

            case "K":
                _state.StrokeColorSpace = "DeviceCMYK";
                return true;

            case "k":
                _state.FillColorSpace = "DeviceCMYK";
                return true;

            case "SC":
            case "SCN":
            case "sc":
            case "scn":
                return true;

            default:
                return false;
        }
    }

    private bool ExecuteAcceptedNoOpOperator(string name)
    {
        return name is
            "d" or "ri" or "i" or
            "Do" or "sh" or
            "MP" or "DP" or "BMC" or "BDC" or "EMC" or
            "BX" or "EX";
    }

    private void MoveTextPosition(List<PdfObject> operands, bool setLeading)
    {
        if (operands.Count < 2)
            return;

        var tx = GetNumber(operands[0]);
        var ty = GetNumber(operands[1]);
        if (setLeading)
            _textLeading = -ty;

        // §9.4.2: Tlm' = [1 0 0 1 tx ty] × Tlm — text-space offset composed
        // through the matrix's linear part. The raw add stacked every line of
        // a `1 Tf` + scaled-`Tm` document onto the first, so this parser's
        // operator bboxes overlapped lines they weren't on — and the fallback
        // removal paths trust those bboxes (#942/#899). Twin of the same fix
        // in TextExtractor.ExecuteOperator.
        _tlm_e += tx * _tm_a + ty * _tm_c;
        _tlm_f += tx * _tm_b + ty * _tm_d;
        _tm_e = _tlm_e;
        _tm_f = _tlm_f;
    }

    private void MoveToNextTextLine()
    {
        // §9.4.2: T* ≡ `0 -TL Td` — composed like MoveTextPosition above.
        _tlm_e += -_textLeading * _tm_c;
        _tlm_f += -_textLeading * _tm_d;
        _tm_e = _tlm_e;
        _tm_f = _tlm_f;
    }

    private void SetTextOperatorResult(PdfObject textObject, ContentOperator op)
    {
        CaptureTextOperatorTransforms(op);
        BeginOpAdvanceTracking();
        var (text, bounds) = ProcessTextString(textObject);
        op.TextContent = text;
        op.BoundingBox = bounds;
        EndOpAdvanceTracking(op);
    }

    private void CaptureTextOperatorTransforms(ContentOperator op)
    {
        op.GraphicsTransform = new ContentTransform(
            _state.Ctm_a, _state.Ctm_b, _state.Ctm_c,
            _state.Ctm_d, _state.Ctm_e, _state.Ctm_f);
        op.TextTransform = new ContentTransform(
            _tm_a, _tm_b, _tm_c, _tm_d, _tm_e, _tm_f);
    }

    /// <summary>
    /// Start accumulating the pen advance of one text-showing operator
    /// (#758). Called after any implicit T*/Tw/Tc side effects of '/" have
    /// been applied — those are line-matrix/state moves, not pen advance.
    /// </summary>
    private void BeginOpAdvanceTracking()
    {
        _opAdvanceThousandths = 0;
        _opAdvanceExpressible = true;
    }

    /// <summary>
    /// Store the accumulated pen advance on the operator, in TJ-adjustment
    /// units (see <see cref="ContentOperator.TextAdvanceThousandths"/>).
    /// </summary>
    private void EndOpAdvanceTracking(ContentOperator op)
    {
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
    /// caller applies to the text matrix, with the shared Th factor cancelled.
    /// </summary>
    private void AccumulateOpAdvance(double displacementThousandths, double spacing)
    {
        if (spacing != 0)
        {
            if (Math.Abs(_fontSize) < 1e-9)
            {
                // tx = spacing·Th with Tfs = 0 — no TJ number can reproduce
                // it (the number is multiplied by Tfs). Mark inexpressible.
                _opAdvanceExpressible = false;
                return;
            }
            displacementThousandths += spacing * 1000.0 / _fontSize;
        }
        _opAdvanceThousandths += displacementThousandths;
    }

    #region Text Processing

    private (string text, PdfRectangle? bounds) ProcessTextString(PdfObject obj)
    {
        // Use the RAW string bytes. Text-showing operands are font-encoded
        // byte codes, not PDFDoc/UTF-16 text — round-tripping through
        // PdfString.Value's document-string decode heuristics and Latin-1
        // mangles any byte the heuristic maps above U+00FF (Latin-1 clamps it
        // to '?'), which garbled every multi-byte CJK code (#515).
        if (obj is PdfString ps)
            return ProcessTextBytes(ps.Bytes);

        return ("", null);
    }

    private (string text, PdfRectangle? bounds) ProcessTextArray(PdfArray arr)
    {
        var sb = new StringBuilder();
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool hasBounds = false;

        foreach (var item in arr)
        {
            if (item is PdfString ps)
            {
                // Raw font-encoded bytes — see ProcessTextString (#515).
                var (text, bounds) = ProcessTextBytes(ps.Bytes);
                sb.Append(text);

                if (bounds.HasValue)
                {
                    var b = bounds.Value;
                    minX = Math.Min(minX, b.Left);
                    minY = Math.Min(minY, b.Bottom);
                    maxX = Math.Max(maxX, b.Right);
                    maxY = Math.Max(maxY, b.Top);
                    hasBounds = true;
                }
            }
            else if (item is PdfInteger pi)
            {
                ApplyTjAdjustment(pi.Value);
            }
            else if (item is PdfReal pr)
            {
                ApplyTjAdjustment(pr.Value);
            }
        }

        var result = hasBounds ? new PdfRectangle(minX, minY, maxX, maxY) : (PdfRectangle?)null;
        return (sb.ToString(), result);
    }

    private (string text, PdfRectangle? bounds) ProcessTextBytes(byte[] bytes)
    {
        var sb = new StringBuilder();
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        // Emits one glyph for a decoded source code. cid == charCode except
        // under a registered encoding CMap (#515); widths are CID-keyed.
        void EmitGlyph(int charCode, int cid, int byteLength)
        {
            var unicode = DecodeCharacter(charCode, cid);
            var charWidth = GetCharWidth(cid);

            // Transform position. §9.4.4 puts the rise in the text rendering
            // matrix as the translation (0, Ts) INSIDE Tm, so it is a text-space
            // offset and must be composed through the matrix's linear part —
            // the same §9.4.2 arithmetic MoveTextPosition does. Adding it raw to
            // _tm_f put a `6 Ts` under a `12 0 0 12 Tm` matrix 6 units up
            // instead of 72 (#980). Twin of TextExtractor.ShowGlyph.
            var (x, y) = TransformTextPoint(
                _tm_e + _textRise * _tm_c,
                _tm_f + _textRise * _tm_d);

            // Glyph advance and ascent are TEXT-space DISPLACEMENTS. Th
            // (horizontal scaling) applies only in horizontal writing
            // (§9.2.4/§9.4.4).
            var advanceTextSpace = _isVerticalWriting
                ? charWidth * _fontSize / 1000.0
                : charWidth * _fontSize * (_horizontalScaling / 100.0) / 1000.0;
            var ascentTextSpace = _fontSize;

            // Map both displacements through the text-matrix × CTM linear parts
            // before they touch the user-space pen position — adding the raw
            // text-space scalars onto (x, y) drops the matrix scale, which under
            // the ubiquitous `1 Tf` + `s 0 0 s Tm` producer idiom yielded a box
            // s times too small in BOTH axes while the pen advance (which DOES
            // apply the matrix) kept positions correct. Redaction's fallback
            // removal paths trust these boxes. This is #833's fix, which landed
            // in TextExtractor.ShowGlyph only; the two must agree on where the
            // same glyph's cell is (#980).
            var (wx, wy) = TransformTextVector(advanceTextSpace, 0);
            var (hx, hy) = TransformTextVector(0, ascentTextSpace);
            var glyphWidth = Math.Sqrt(wx * wx + wy * wy);

            // Update bounds. Vertical writing (§9.7.4.3): the pen is the
            // VERTICAL origin — the cell is centered on it via the /W2
            // position vector (default vx = w0/2) and spans DOWN by the
            // vertical displacement w1y. Mirrors TextExtractor.ShowGlyph so
            // redaction area-intersection sees the same glyph cells.
            if (_isVerticalWriting)
            {
                var vm = GetVerticalMetrics(cid);
                var (vxx, _) = TransformTextVector(vm.Vx * _fontSize / 1000.0, 0);
                var (chx, chy) = TransformTextVector(0, Math.Abs(vm.W1Y) * _fontSize / 1000.0);
                var cellHeight = Math.Sqrt(chx * chx + chy * chy);
                if (cellHeight <= 0) cellHeight = Math.Abs(hy) > 0 ? Math.Abs(hy) : glyphWidth;
                minX = Math.Min(minX, x - vxx);
                minY = Math.Min(minY, y - cellHeight);
                maxX = Math.Max(maxX, x - vxx + glyphWidth);
                maxY = Math.Max(maxY, y);
            }
            else
            {
                var cell = AxisAlignedBox(x, y, wx, wy, hx, hy);
                minX = Math.Min(minX, cell.Left);
                minY = Math.Min(minY, cell.Bottom);
                maxX = Math.Max(maxX, cell.Right);
                maxY = Math.Max(maxY, cell.Top);
            }

            sb.Append(unicode);

            // Advance text position (§9.4.4). Word spacing fires only on the
            // SINGLE-BYTE code 32 (§9.3.3). Vertical: ty = w1y·Tfs + Tc + Tw
            // from /W2 (else /DW2, default −1000 → down the page), no Th.
            var spacing = _charSpacing;
            if (byteLength == 1 && charCode == 32) spacing += _wordSpacing;

            if (_isVerticalWriting)
            {
                var vm = GetVerticalMetrics(cid);
                var ty = (vm.W1Y / 1000.0) * _fontSize + spacing;
                _tm_e += ty * _tm_c;
                _tm_f += ty * _tm_d;
                AccumulateOpAdvance(vm.W1Y, spacing);
            }
            else
            {
                // tx = (w0·Tfs + Tc + Tw)·Th — the spacing terms sit INSIDE
                // the horizontal-scaling factor (§9.4.4). Mirrors
                // TextExtractor.ShowGlyph exactly so redaction bounds stay in
                // lock-step with extracted letters. #734
                var tx = ((charWidth / 1000.0) * _fontSize + spacing) * (_horizontalScaling / 100.0);
                _tm_e += tx * _tm_a;
                _tm_f += tx * _tm_b;
                AccumulateOpAdvance(charWidth, spacing);
            }
        }

        if (_registeredEncodingCMap != null)
        {
            // Registered CMap codespaces drive segmentation — mixed 1/2-byte
            // codes (90ms-RKSJ-H) would be garbled by a fixed stride (#515).
            foreach (var (code, cid, byteLength) in _registeredEncodingCMap.DecodeDetailed(bytes))
                EmitGlyph(code, cid, byteLength);
        }
        else
        {
            int stride = _is2ByteFont ? 2 : 1;
            for (int i = 0; i + stride <= bytes.Length; i += stride)
            {
                int charCode = _is2ByteFont
                    ? (bytes[i] << 8) | bytes[i + 1]
                    : bytes[i];
                EmitGlyph(charCode, charCode, stride);
            }
        }

        if (bytes.Length == 0)
            return (sb.ToString(), null);

        return (sb.ToString(), new PdfRectangle(minX, minY, maxX, maxY));
    }

    /// <summary>
    /// TJ position adjustment, subtracted from the coordinate of the WRITING
    /// direction (§9.4.3): horizontal → tx (scaled by Th), vertical → ty
    /// (no Th). Mirrors TextExtractor. #515
    /// </summary>
    private void ApplyTjAdjustment(double adj)
    {
        if (_isVerticalWriting)
        {
            var ty = -(adj / 1000.0) * _fontSize;
            _tm_e += ty * _tm_c;
            _tm_f += ty * _tm_d;
        }
        else
        {
            _tm_e -= (adj / 1000.0) * _fontSize * (_horizontalScaling / 100.0);
        }

        // A TJ number already IS a TJ-adjustment-unit displacement: it moves
        // the pen by −adj thousandths along the writing direction. #758
        _opAdvanceThousandths -= adj;
    }

    /// <summary>
    /// Vertical metrics for <paramref name="cid"/> — /W2, else the §9.7.4.3
    /// defaults (w1y from /DW2, v = (w0∕2, DW2[0])). Mirrors TextExtractor.
    /// </summary>
    private Fonts.CidVerticalMetrics GetVerticalMetrics(int cid)
        => _cidMetrics?.GetVerticalMetrics(cid)
            ?? new Fonts.CidVerticalMetrics(
                Fonts.CidFontWidths.SpecDefaultVerticalDisplacement,
                GetCharWidth(cid) / 2,
                Fonts.CidFontWidths.SpecDefaultVerticalOriginY);

    private (double x, double y) TransformTextPoint(double tx, double ty)
    {
        // Apply CTM to text position
        var x = tx * _state.Ctm_a + ty * _state.Ctm_c + _state.Ctm_e;
        var y = tx * _state.Ctm_b + ty * _state.Ctm_d + _state.Ctm_f;
        return (x, y);
    }

    /// <summary>
    /// Map a text-space DISPLACEMENT vector through the LINEAR parts of the
    /// text matrix and then the CTM (no translation). Glyph width/height are
    /// displacements, not points. Twin of TextExtractor.TransformVector (#833,
    /// #980).
    /// </summary>
    private (double dx, double dy) TransformTextVector(double vx, double vy)
    {
        var tx = vx * _tm_a + vy * _tm_c;
        var ty = vx * _tm_b + vy * _tm_d;
        return (tx * _state.Ctm_a + ty * _state.Ctm_c,
                tx * _state.Ctm_b + ty * _state.Ctm_d);
    }

    /// <summary>
    /// Axis-aligned bounding box of the parallelogram spanned from origin
    /// (<paramref name="ox"/>, <paramref name="oy"/>) by the width vector
    /// (wx, wy) and height vector (hx, hy). Twin of
    /// TextExtractor.AxisAlignedBox (#833, #980).
    /// </summary>
    private static PdfRectangle AxisAlignedBox(
        double ox, double oy, double wx, double wy, double hx, double hy)
    {
        double x2 = ox + wx, x3 = ox + hx, x4 = ox + wx + hx;
        double y2 = oy + wy, y3 = oy + hy, y4 = oy + wy + hy;
        double left = Math.Min(Math.Min(ox, x2), Math.Min(x3, x4));
        double right = Math.Max(Math.Max(ox, x2), Math.Max(x3, x4));
        double bottom = Math.Min(Math.Min(oy, y2), Math.Min(y3, y4));
        double top = Math.Max(Math.Max(oy, y2), Math.Max(y3, y4));
        return new PdfRectangle(left, bottom, right, top);
    }

    /// <summary>
    /// PDF spec ISO 32000-2:2020, Table 91 — inline image dict
    /// abbreviations. PDF allows either spelling on every key, but when
    /// BOTH appear in the same inline-image dict the spec was silent
    /// from PDF 1.0 (1993) until 2020. The PDF Association resolved
    /// (pdf-association/pdf-issues#3) that <b>the abbreviated key shall
    /// take precedence</b>. Without this, parsers that pick the wrong
    /// key get out of sync with the content stream — most viewers
    /// (Acrobat, Firefox, Chrome/PDFium, mutool) all needed fixes;
    /// see the SafeDocs test fixture <c>issue14256.pdf</c> which is
    /// specifically designed to exercise the eight semantic
    /// collisions in Table 91.
    /// </summary>
    private static readonly Dictionary<string, string> InlineImageFullToAbbrev = new()
    {
        ["BitsPerComponent"] = "BPC",
        ["ColorSpace"]       = "CS",
        ["Decode"]           = "D",
        ["DecodeParms"]      = "DP",
        ["Filter"]           = "F",
        ["Height"]           = "H",
        ["ImageMask"]        = "IM",
        ["Interpolate"]      = "I",
        ["Length"]           = "L",
        ["Width"]            = "W",
    };

    /// <summary>
    /// The set of abbreviated forms (RHS of <see cref="InlineImageFullToAbbrev"/>)
    /// for fast O(1) "is this an abbreviated key?" lookups during parse.
    /// </summary>
    private static readonly HashSet<string> InlineImageAbbreviatedKeys =
        new(InlineImageFullToAbbrev.Values);

    /// <summary>
    /// Parse an inline image (§8.9.7).
    /// Called immediately after the BI token is consumed.
    /// Reads the image-parameter key-value pairs, skips past ID, and
    /// consumes the binary image data up to (and including) EI.
    /// Returns a BI operator whose first operand is the image-parameter dict.
    ///
    /// Key normalization: full-form keys (e.g. <c>/Width</c>) are
    /// stored under their abbreviated equivalent (<c>/W</c>) so
    /// downstream code only sees one spelling. When both forms appear
    /// in the same dict, the abbreviated wins regardless of source
    /// order — implements the PDF Association's pdf-issues#3 ruling.
    /// </summary>
    private ContentOperator? ParseInlineImage()
    {
        // --- 1. Parse abbreviated image parameters until 'ID' ---
        var imageParams = new PdfDictionary();
        // Tracks which abbreviated keys were *explicitly* set by the
        // source (i.e. an entry like `/W 10`, not `/Width 10` mapped).
        // When an explicit abbreviated key is present, later full-form
        // entries for the same semantic are ignored — abbreviated wins.
        var explicitlyAbbreviated = new HashSet<string>();

        while (_pos < _content.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _content.Length) break;

            // Peek: is this 'ID'?
            if (_content[_pos] == 'I' && _pos + 1 < _content.Length && _content[_pos + 1] == 'D' &&
                (_pos + 2 >= _content.Length || IsWhitespaceByte(_content[_pos + 2])))
            {
                _pos += 2; // consume 'ID'
                // Consume exactly one whitespace char that separates ID from data (per spec)
                if (_pos < _content.Length && IsWhitespaceByte(_content[_pos]))
                    _pos++;
                break;
            }

            var keyToken = ParseToken();
            if (keyToken is not PdfName keyName) continue;
            var rawKey = keyName.Value;

            // Determine the canonical (abbreviated) storage key and
            // whether this key would be ignored under the precedence
            // rule. A full-form key is ignored iff its abbreviated
            // counterpart was explicitly set earlier.
            string canonicalKey = rawKey;
            bool ignore = false;
            if (InlineImageFullToAbbrev.TryGetValue(rawKey, out var ab))
            {
                canonicalKey = ab;
                if (explicitlyAbbreviated.Contains(ab)) ignore = true;
            }
            else if (InlineImageAbbreviatedKeys.Contains(rawKey))
            {
                explicitlyAbbreviated.Add(rawKey);
            }

            SkipWhitespaceAndComments();
            var valToken = ParseToken();
            if (ignore) continue;                   // abbreviated already won

            // ParseToken returns a `string` for keywords (because that's
            // how operator names propagate back to the main loop). For
            // inline-image dict values the only legal keywords are
            // booleans — promote those into proper PdfBoolean so
            // dict.GetBool("IM") works downstream.
            PdfObject? valObj = valToken switch
            {
                PdfObject obj => obj,
                "true"  => PdfBoolean.True,
                "false" => PdfBoolean.False,
                _ => null,
            };
            if (valObj != null)
                imageParams[canonicalKey] = valObj;
        }

        // --- 2. Locate the end of the image data (past 'EI') ---
        // dataStart is the first byte of binary image data (the byte after
        // the single whitespace following ID, already consumed above).
        // dataEnd is the exclusive end of that data; the slice in between is
        // captured verbatim so the round-trip (rewrite) is lossless. (#354)
        //
        // §8.9.7 says ID is followed by EXACTLY ONE whitespace byte, and the
        // byte after it is data. Producers write CRLF anyway. pdf.js
        // bug1065245.pdf ends every one of its three inline images' ID with
        // `0d 0a` before the JPEG SOI:
        //
        //     ... I D 0d 0a ff d8 ff e0 ...
        //
        // Consuming only the `\r` left the data starting on `\n`, so the JPEG
        // decoder never found ffd8 at offset 0, returned null, and the page
        // rendered blank — while mutool and pdftocairo both draw it. Treat a
        // CRLF PAIR as the single separator, which is what they do.
        //
        // Deliberately narrow: only the exact `\r\n` pair, never a general
        // "skip all whitespace". For unfiltered inline image data the first
        // real byte can legitimately BE 0x0A or 0x20, and skipping those would
        // shift every sample by one — corrupting the image instead of failing
        // to draw it, which is the worse outcome.
        if (_pos > 0 && _pos < _content.Length &&
            _content[_pos - 1] == (byte)'\r' && _content[_pos] == (byte)'\n')
        {
            _pos++;
        }

        int dataStart = _pos;
        int dataEnd;
        bool consumed = false;

        // 2a. Trust an explicit data length when present (/L — PDF 2.0 §8.9.7;
        // or full-form /Length mapped to the canonical "L" key). Skip exactly
        // that many bytes and confirm EI follows. This avoids false-positive
        // 'EI' matches inside binary image data, which the byte-scan below
        // cannot reliably distinguish. (Issue #347)
        dataEnd = _content.Length;
        if (imageParams.GetOptional("L") is PdfInteger lenObj &&
            lenObj.Value > 0 && lenObj.Value <= int.MaxValue)
        {
            int afterData = _pos + (int)lenObj.Value;
            if (afterData <= _content.Length)
            {
                int probe = afterData;
                while (probe < _content.Length && IsWhitespaceByte(_content[probe])) probe++;
                if (probe + 1 < _content.Length &&
                    _content[probe] == 'E' && _content[probe + 1] == 'I' &&
                    (probe + 2 >= _content.Length || IsWordBoundaryByte(_content[probe + 2])))
                {
                    dataEnd = afterData; // data is exactly /L bytes
                    _pos = probe + 2;    // consume 'EI'
                    consumed = true;
                }
                // length present but EI didn't line up → fall back to scanning
            }
        }

        // 2b. No usable length: scan for 'EI' at a word boundary, consuming raw
        // image data. Each iteration advances _pos by at least one byte and
        // does a constant-time boundary check, so this is O(n) in the data size
        // and always terminates. A safety bound bails on absurd input — an
        // inline image with no /L that exceeds MaxInlineImageScanBytes without a
        // boundary EI is treated as malformed rather than scanned to the end of
        // a huge stream. (#347)
        if (!consumed)
        {
            dataEnd = _content.Length;
            while (_pos < _content.Length)
            {
                if (_pos - dataStart > MaxInlineImageScanBytes)
                    throw new PdfParseException(
                        $"Inline image (no /L) exceeded {MaxInlineImageScanBytes} bytes without an EI marker");
                if (IsWhitespaceByte(_content[_pos]) || _pos == dataStart)
                {
                    // Consume the whitespace, then check for 'EI'
                    int wsPos = _pos;
                    if (_pos != dataStart) _pos++; // skip whitespace byte

                    if (_pos + 1 < _content.Length &&
                        _content[_pos] == 'E' && _content[_pos + 1] == 'I' &&
                        (_pos + 2 >= _content.Length || IsWordBoundaryByte(_content[_pos + 2])))
                    {
                        // Data ends at the delimiter whitespace before EI
                        // (wsPos); the empty-data case (_pos == dataStart on
                        // the first iteration) leaves dataEnd == dataStart.
                        dataEnd = wsPos;
                        _pos += 2; // consume 'EI'
                        break;
                    }
                    // Not EI — roll back to whitespace position and advance one
                    _pos = wsPos + 1;
                }
                else
                {
                    _pos++;
                }
            }
        }

        // Capture the raw image data verbatim so ContentStreamWriter can
        // re-emit BI…ID<data>EI on round-trip. Clamp defensively in case a
        // malformed stream left the indices crossed or out of range.
        byte[] imageData = Array.Empty<byte>();
        if (dataEnd > dataStart && dataStart >= 0 && dataEnd <= _content.Length)
        {
            imageData = new byte[dataEnd - dataStart];
            Array.Copy(_content, dataStart, imageData, 0, dataEnd - dataStart);
        }

        // Compute operator bounds from current CTM (inline image fills the unit square
        // mapped through the CTM, i.e. the four corners (0,0),(1,0),(1,1),(0,1)).
        // Skipped in metadata-free mode — the CTM is not tracked there, so a
        // computed box would be wrong rather than merely absent.
        var op = new ContentOperator("BI", new PdfObject[] { imageParams });
        if (ComputeOperatorMetadata)
            op.BoundingBox = TransformBounds(0, 0, 1, 1);
        op.InlineImageData = imageData;
        return op;
    }

    private static bool IsWhitespaceByte(byte b) =>
        b == 0x20 || b == 0x09 || b == 0x0A || b == 0x0D || b == 0x0C || b == 0x00;

    private static bool IsWordBoundaryByte(byte b) =>
        IsWhitespaceByte(b) || b == '/' || b == '(' || b == ')' || b == '[' || b == ']';

    private void ApplyExtGState(string gsName)
    {
        var gsDict = _page?.GetExtGState(gsName);
        if (gsDict == null) return;

        if (gsDict.ContainsKey("LW")) _state.LineWidth = gsDict.GetNumber("LW", _state.LineWidth);
        if (gsDict.ContainsKey("LC")) _state.LineCap   = gsDict.GetInt("LC", _state.LineCap);
        if (gsDict.ContainsKey("LJ")) _state.LineJoin  = gsDict.GetInt("LJ", _state.LineJoin);
        if (gsDict.ContainsKey("ML")) _state.MiterLimit = gsDict.GetNumber("ML", _state.MiterLimit);
        // Transparency parameters (§11.6.4)
        if (gsDict.ContainsKey("ca")) _state.FillAlpha   = gsDict.GetNumber("ca", _state.FillAlpha);
        if (gsDict.ContainsKey("CA")) _state.StrokeAlpha = gsDict.GetNumber("CA", _state.StrokeAlpha);
        if (gsDict.ContainsKey("BM"))
        {
            var bmObj = gsDict.GetOptional("BM");
            if (bmObj is PdfName bmName) _state.BlendMode = bmName.Value;
            else if (bmObj is PdfArray bmArr && bmArr.Count > 0 && bmArr[0] is PdfName firstBm)
                _state.BlendMode = firstBm.Value;
        }
        if (gsDict.ContainsKey("SMask"))
        {
            // SMask=/None disables; otherwise a soft mask dictionary is referenced.
            var smaskObj = gsDict.GetOptional("SMask");
            _state.HasSoftMask = !(smaskObj is PdfName smaskName && smaskName.Value == "None");
        }
        if (gsDict.ContainsKey("AIS")) _state.AlphaIsShape = gsDict.GetBool("AIS", _state.AlphaIsShape);
        if (gsDict.ContainsKey("SA"))  _state.StrokeAdjustment = gsDict.GetBool("SA", _state.StrokeAdjustment);
    }

    private void LoadFont()
    {
        if (_page == null) return;

        _currentFont = _page.GetFont(_fontName);
        _toUnicodeMap = null;
        _is2ByteFont = false;
        _cidFontDict = null;
        _cidMetrics = null;
        _isVerticalWriting = false;
        _registeredEncodingCMap = null;
        _registeredCidToUnicode = null;

        if (_currentFont != null)
        {
            // Detect Type0 (composite) fonts
            var subtype = _currentFont.GetNameOrNull("Subtype");
            _is2ByteFont = subtype == "Type0";

            // Embedded /Encoding CMap streams and registered CMap names both
            // drive byte segmentation through the parsed CMap's codespace
            // ranges (mixed 1/2-byte widths, per-byte-range matched) and map
            // code→CID for width lookup. Mirrors TextExtractor.LoadFontGeometry
            // EXACTLY — this parser feeds redaction's glyph removal, and it
            // must decode the same bytes the same way TextExtractor does or
            // redaction can target the wrong glyphs. #515 #659
            if (_is2ByteFont)
            {
                var encObj = _page.Document.Resolve(_currentFont.GetOptional("Encoding") ?? PdfNull.Instance);
                if (encObj is PdfStream encStream)
                {
                    try
                    {
                        var embedded = Text.CidCMap.Parse(encStream.DecodedData,
                            static name => Text.PredefinedCMapProvider.TryGetEncodingCMap(name));

                        // /WMode 1 in the embedded CMap means vertical
                        // writing (§9.7.5.2). #515
                        if (embedded.WMode == 1)
                            _isVerticalWriting = true;
                        if (embedded.CodespaceRanges.Count > 0 || embedded.Mapping.Count > 0)
                            _registeredEncodingCMap = embedded;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Best-effort: unreadable CMap keeps identity defaults.
                    }

                    // Stride fallback for streams the CMap parser could not
                    // read (#659): an EXPLICITLY UNIFORM 1-byte codespace
                    // decodes one byte at a time; anything else keeps the
                    // safe 2-byte default.
                    if (_registeredEncodingCMap == null)
                    {
                        var detail = Text.ToUnicodeCMapParser.ParseDetailed(encStream.DecodedData);
                        if (detail.CodespaceRanges.Count > 0 && detail.MaxCodeBytes == 1)
                            _is2ByteFont = false;
                    }
                }

                // Identity-V / registered -V CMap names mean vertical
                // writing mode, matching TextExtractor. #515
                if (encObj is PdfName encName
                    && (encName.Value == "Identity-V"
                        || Text.PredefinedCMapProvider.IsVertical(encName.Value)))
                {
                    _isVerticalWriting = true;
                }

                // Registered (predefined) CMap NAME as /Encoding (#515):
                // its codespaces drive byte segmentation (90ms-RKSJ-H mixes
                // 1- and 2-byte codes) and its mapping gives the CID for
                // width lookup and CID→Unicode decoding.
                if (encObj is PdfName registeredName
                    && registeredName.Value is not ("Identity-H" or "Identity-V")
                    && Text.PredefinedCMapProvider.TryGetEncodingCMap(registeredName.Value) is { } registeredCMap)
                {
                    _registeredEncodingCMap = registeredCMap;
                }
            }

            if (_is2ByteFont)
            {
                // Type0 font: load descendant CID font
                var descendantFontsObj = _currentFont.GetOptional("DescendantFonts");
                if (descendantFontsObj is PdfArray descendantFonts && descendantFonts.Count > 0)
                {
                    var descendantRef = descendantFonts[0];
                    var descendantResolved = _page.Document.Resolve(descendantRef);
                    if (descendantResolved is PdfDictionary cidFont)
                    {
                        _cidFontDict = cidFont;
                        ParseCidWidths();
                    }
                }
            }

            var toUnicodeObj = _currentFont.GetOptional("ToUnicode");
            if (toUnicodeObj != null)
            {
                var resolved = _page.Document.Resolve(toUnicodeObj);
                if (resolved is PdfStream stream)
                {
                    try
                    {
                        _toUnicodeMap = Text.ToUnicodeCMapParser.Parse(stream.DecodedData);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // ToUnicode CMaps are optional best-effort metadata used
                        // for text extraction; tolerate malformed ones rather
                        // than failing the whole content-stream parse. We still
                        // let fatal resource exhaustion (OOM) propagate instead
                        // of silently swallowing it. See issue #345.
                    }
                }
            }

            // Registered CID→Unicode via the descendant's /CIDSystemInfo
            // ordering (#515), mirroring TextExtractor.LoadFontGeometry: a
            // /ToUnicode STREAM wins outright; /ToUnicode /Identity-H|V means
            // code == Unicode (which the WinAnsi fallback reproduces for the
            // codes CJK text uses); a registered-CMap-name /ToUnicode (#715)
            // contributes its ordering. First signal with a shipped map wins.
            if (_is2ByteFont && _toUnicodeMap == null && !ToUnicodeIsIdentityName())
            {
                _registeredCidToUnicode =
                    TryLoadOrderingMap(GetCidSystemInfoOrdering())
                    ?? TryLoadOrderingMap(GetEncodingNameOrdering())
                    ?? TryLoadOrderingMap(GetToUnicodeRegisteredOrdering());
            }
        }
    }

    private bool ToUnicodeIsIdentityName()
    {
        if (_currentFont == null || _page == null) return false;
        var toUnicode = _page.Document.Resolve(_currentFont.GetOptional("ToUnicode") ?? PdfNull.Instance);
        return toUnicode is PdfName name && (name.Value == "Identity-H" || name.Value == "Identity-V");
    }

    private string? GetCidSystemInfoOrdering()
    {
        if (_cidFontDict == null || _page == null) return null;
        if (_page.Document.Resolve(_cidFontDict.GetOptional("CIDSystemInfo") ?? PdfNull.Instance)
            is not PdfDictionary systemInfo)
            return null;
        return _page.Document.Resolve(systemInfo.GetOptional("Ordering") ?? PdfNull.Instance)
            is PdfString ordering ? ordering.Value : null;
    }

    private string? GetEncodingNameOrdering()
    {
        if (_currentFont == null || _page == null) return null;
        return _page.Document.Resolve(_currentFont.GetOptional("Encoding") ?? PdfNull.Instance)
            is PdfName encName ? Text.PredefinedCMapProvider.GetOrderingForEncodingCMap(encName.Value) : null;
    }

    private string? GetToUnicodeRegisteredOrdering()
    {
        if (_currentFont == null || _page == null) return null;
        return _page.Document.Resolve(_currentFont.GetOptional("ToUnicode") ?? PdfNull.Instance)
            is PdfName name ? Text.PredefinedCMapProvider.GetOrderingForEncodingCMap(name.Value) : null;
    }

    private static IReadOnlyDictionary<int, string>? TryLoadOrderingMap(string? ordering)
        => ordering == null ? null : Text.PredefinedCMapProvider.TryGetCidToUnicodeMap(ordering);

    private void ParseCidWidths()
    {
        if (_cidFontDict == null) return;

        // /DW, /W, /DW2, /W2 via the shared hardened parser (§9.7.4.3, #515)
        // — the same tables TextExtractor loads, so redaction bounds and
        // extraction letters stay in the same geometry.
        _cidMetrics = Fonts.CidFontWidths.Parse(
            _cidFontDict,
            _page != null ? _page.Document.Resolve : null);
    }

    private string DecodeCharacter(int charCode, int cid)
    {
        if (_toUnicodeMap != null && _toUnicodeMap.TryGetValue(charCode, out var unicode))
            return unicode;

        // Registered CID→Unicode (#515): the Adobe-<Ordering>-UCS2 CMap for
        // the font's /CIDSystemInfo ordering, keyed by CID. Mirrors
        // TextExtractor.DecodeCharacter so operator text matches page letters.
        if (_registeredCidToUnicode != null && _registeredCidToUnicode.TryGetValue(cid, out var orderingUnicode))
            return orderingUnicode;

        // Fall back to WinAnsi encoding
        if (charCode < 128 || charCode >= 160)
            return ((char)charCode).ToString();

        return charCode switch
        {
            128 => "\u20AC", 130 => "\u201A", 131 => "\u0192", 132 => "\u201E",
            133 => "\u2026", 134 => "\u2020", 135 => "\u2021", 136 => "\u02C6",
            137 => "\u2030", 138 => "\u0160", 139 => "\u2039", 140 => "\u0152",
            142 => "\u017D", 145 => "\u2018", 146 => "\u2019", 147 => "\u201C",
            148 => "\u201D", 149 => "\u2022", 150 => "\u2013", 151 => "\u2014",
            152 => "\u02DC", 153 => "\u2122", 154 => "\u0161", 155 => "\u203A",
            156 => "\u0153", 158 => "\u017E", 159 => "\u0178",
            _ => ((char)charCode).ToString()
        };
    }

    /// <summary>
    /// Glyph width in 1000ths of an em. Mirrors TextExtractor.GetCharWidth
    /// step for step — a width disagreement between the two moves the glyph
    /// cells redaction matches against, cumulatively along a string. Until
    /// #980 this copy stopped after /Widths, so a Type0 font with no metrics,
    /// a /MissingWidth, and every non-embedded standard-14 font fell to the
    /// flat 600 default here while TextExtractor used real metrics.
    /// </summary>
    private double GetCharWidth(int charCode)
    {
        // Type 0 / CIDFont: /W with the /DW fallback (§9.7.4.3).
        if (_is2ByteFont)
            return _cidMetrics?.GetWidth(charCode) ?? Fonts.CidFontWidths.SpecDefaultWidth;

        if (_currentFont != null)
        {
            // /Widths is an indirect reference in TeX/dvips PDFs — resolve it,
            // or the cast fails and glyph widths (which feed redaction bounding
            // boxes on this path) collapse to the 600 default (#843).
            var widthsObj = _page is { } page
                ? page.Document.Resolve(_currentFont.GetOptional("Widths") ?? PdfNull.Instance)
                : _currentFont.GetOptional("Widths");
            if (widthsObj is PdfArray widths)
            {
                var firstChar = _currentFont.GetInt("FirstChar", 0);
                var lastChar = _currentFont.GetInt("LastChar", 255);

                if (charCode >= firstChar && charCode <= lastChar)
                {
                    var index = charCode - firstChar;
                    if (index < widths.Count)
                        return widths.GetNumber(index);
                }
            }

            var fontDescriptor = _currentFont.GetDictionaryOrNull("FontDescriptor");
            if (fontDescriptor != null)
            {
                var missingWidth = fontDescriptor.GetNumber("MissingWidth", 0);
                if (missingWidth > 0)
                    return missingWidth;
            }

            var baseFont = _currentFont.GetNameOrNull("BaseFont");
            if (baseFont != null)
                return Text.TextExtractor.GetStandardFontWidth(baseFont, charCode);
        }

        return 600; // Default width
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

    private PdfRectangle TransformBounds(double minX, double minY, double maxX, double maxY)
    {
        // Transform all four corners through CTM
        var corners = new[]
        {
            TransformPoint(minX, minY),
            TransformPoint(maxX, minY),
            TransformPoint(minX, maxY),
            TransformPoint(maxX, maxY)
        };

        return new PdfRectangle(
            corners.Min(p => p.x),
            corners.Min(p => p.y),
            corners.Max(p => p.x),
            corners.Max(p => p.y)
        );
    }

    private (double x, double y) TransformPoint(double x, double y)
    {
        var tx = x * _state.Ctm_a + y * _state.Ctm_c + _state.Ctm_e;
        var ty = x * _state.Ctm_b + y * _state.Ctm_d + _state.Ctm_f;
        return (tx, ty);
    }

    #endregion

    #region Tokenization

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _content.Length)
        {
            var c = _content[_pos];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == 0)
            {
                _pos++;
            }
            else if (c == '%')
            {
                // Skip comment to end of line
                while (_pos < _content.Length && _content[_pos] != '\n' && _content[_pos] != '\r')
                    _pos++;
            }
            else
            {
                break;
            }
        }
    }

    private object? ParseToken()
    {
        if (_pos >= _content.Length) return null;

        var c = _content[_pos];

        // String literal
        if (c == '(')
            return ParseStringLiteral();

        // Hex string
        if (c == '<')
        {
            if (_pos + 1 < _content.Length && _content[_pos + 1] == '<')
                return ParseDictionary();
            return ParseHexString();
        }

        // Array
        if (c == '[')
            return ParseArray();

        // Name
        if (c == '/')
            return ParseName();

        // Number or operator
        if (char.IsDigit((char)c) || c == '-' || c == '+' || c == '.')
            return ParseNumber();

        // Keyword/operator
        if (char.IsLetter((char)c) || c == '\'' || c == '"' || c == '*')
            return ParseKeyword();

        _pos++;
        return null;
    }

    private PdfString ParseStringLiteral()
    {
        // Accumulate BYTES, not chars. Building a .NET string and handing it to
        // PdfString(string) re-encoded the WHOLE operand as UTF-16BE-with-BOM
        // the moment any one char exceeded U+00FF — which a `\777` escape did,
        // because the octal value was not truncated to a byte. `(\777) Tj`
        // parsed to the four bytes FE FF 01 FF (four glyphs) here while
        // TextExtractor produced the single byte FF. §7.3.4.2: "high-order
        // overflow shall be ignored". #980
        var bytes = new List<byte>();
        _pos++; // Skip '('
        int depth = 1;

        while (_pos < _content.Length && depth > 0)
        {
            var c = _content[_pos];

            if (c == '\\' && _pos + 1 < _content.Length)
            {
                _pos++;
                var escaped = _content[_pos];
                switch (escaped)
                {
                    case (byte)'n': bytes.Add((byte)'\n'); break;
                    case (byte)'r': bytes.Add((byte)'\r'); break;
                    case (byte)'t': bytes.Add((byte)'\t'); break;
                    case (byte)'b': bytes.Add((byte)'\b'); break;
                    case (byte)'f': bytes.Add((byte)'\f'); break;
                    case (byte)'(': bytes.Add((byte)'('); break;
                    case (byte)')': bytes.Add((byte)')'); break;
                    case (byte)'\\': bytes.Add((byte)'\\'); break;
                    // REVERSE SOLIDUS followed by an end-of-line marker is a
                    // line-continuation: it produces NO character (PDF32000-1
                    // §7.3.4.2 Table 3). CRLF is one marker, not two — consume
                    // both bytes. Must match TextExtractor.ParseStringLiteral's
                    // handling of the same escape (#637) — this parser feeds
                    // the glyph-removal rewrite pipeline, so a mismatch here
                    // risks a matched-but-not-actually-excised redaction leak.
                    case (byte)'\r':
                        if (_pos + 1 < _content.Length && _content[_pos + 1] == '\n') _pos++;
                        break;
                    case (byte)'\n':
                        break;
                    default:
                        if (escaped >= '0' && escaped <= '7')
                        {
                            int value = escaped - '0';
                            int digits = 1;
                            while (digits < 3 && _pos + 1 < _content.Length &&
                                   _content[_pos + 1] >= '0' && _content[_pos + 1] <= '7')
                            {
                                _pos++;
                                value = value * 8 + (_content[_pos] - '0');
                                digits++;
                            }
                            // Truncated to a byte, exactly as TextExtractor does.
                            bytes.Add(unchecked((byte)value));
                        }
                        else
                        {
                            bytes.Add(escaped);
                        }
                        break;
                }
            }
            else if (c == '(')
            {
                depth++;
                bytes.Add(c);
            }
            else if (c == ')')
            {
                depth--;
                if (depth > 0) bytes.Add(c);
            }
            else
            {
                bytes.Add(c);
            }
            _pos++;
        }

        return new PdfString(bytes.ToArray());
    }

    private PdfString ParseHexString()
    {
        _pos++; // Skip '<'
        var hex = new StringBuilder();

        while (_pos < _content.Length && _content[_pos] != '>')
        {
            var c = (char)_content[_pos];
            // Per §7.3.4.3 a hex string holds only hex digits (whitespace is
            // ignored). Collect ONLY hex digits — letters G–Z would otherwise
            // reach Convert.ToInt32(.,16) and throw FormatException on hostile
            // input. (#352)
            if (Uri.IsHexDigit(c))
                hex.Append(c);
            _pos++;
        }
        _pos++; // Skip '>'

        var hexStr = hex.ToString();
        if (hexStr.Length % 2 == 1)
            hexStr += "0";

        var sb = new StringBuilder();
        for (int i = 0; i < hexStr.Length; i += 2)
        {
            var code = Convert.ToInt32(hexStr.Substring(i, 2), 16);
            sb.Append((char)code);
        }

        return new PdfString(sb.ToString());
    }

    private PdfArray ParseArray()
    {
        // Bound recursion: ParseArray -> ParseToken -> ParseArray for nested
        // arrays. A deeply nested array in hostile input would otherwise drive
        // a StackOverflow. (#346)
        if (++_nestingDepth > MaxNestingDepth)
        {
            _nestingDepth--;
            throw new PdfParseException(
                $"Maximum nesting depth ({MaxNestingDepth}) exceeded while parsing content-stream array");
        }
        try
        {
            var result = new PdfArray();
            _pos++; // Skip '['

            while (_pos < _content.Length)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                SkipWhitespaceAndComments();
                if (_pos >= _content.Length || _content[_pos] == ']')
                {
                    _pos++;
                    break;
                }

                var item = ParseToken();
                if (item is PdfObject pdfObj)
                    result.Add(pdfObj);
            }

            return result;
        }
        finally { _nestingDepth--; }
    }

    private PdfDictionary ParseDictionary()
    {
        if (++_nestingDepth > MaxNestingDepth)
        {
            _nestingDepth--;
            throw new PdfParseException(
                $"Maximum nesting depth ({MaxNestingDepth}) exceeded while parsing content-stream dictionary");
        }

        try
        {
            var result = new PdfDictionary();
            _pos += 2; // Skip '<<'

            while (_pos < _content.Length)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                SkipWhitespaceAndComments();
                if (_pos + 1 < _content.Length && _content[_pos] == '>' && _content[_pos + 1] == '>')
                {
                    _pos += 2;
                    break;
                }

                var keyToken = ParseToken();
                if (keyToken is not PdfName key)
                    continue;

                SkipWhitespaceAndComments();
                if (_pos + 1 < _content.Length && _content[_pos] == '>' && _content[_pos + 1] == '>')
                {
                    result[key] = PdfNull.Instance;
                    _pos += 2;
                    break;
                }

                var valueToken = ParseToken();
                var value = valueToken switch
                {
                    PdfObject obj => obj,
                    "true" => PdfBoolean.True,
                    "false" => PdfBoolean.False,
                    "null" => PdfNull.Instance,
                    _ => null,
                };
                if (value != null)
                    result[key] = value;
            }

            return result;
        }
        finally { _nestingDepth--; }
    }

    private PdfName ParseName()
    {
        _pos++; // Skip '/'
        var sb = new StringBuilder();

        while (_pos < _content.Length)
        {
            var c = _content[_pos];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '/' ||
                c == '[' || c == ']' || c == '<' || c == '>' || c == '(' || c == ')')
                break;

            if (c == '#' && _pos + 2 < _content.Length)
            {
                var hex = Encoding.ASCII.GetString(_content, _pos + 1, 2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                {
                    sb.Append((char)code);
                    _pos += 3;
                    continue;
                }
            }

            sb.Append((char)c);
            _pos++;
        }

        return new PdfName(sb.ToString());
    }

    private PdfObject ParseNumber()
    {
        var sb = new StringBuilder();

        while (_pos < _content.Length)
        {
            var c = _content[_pos];
            if (char.IsDigit((char)c) || c == '-' || c == '+' || c == '.')
            {
                sb.Append((char)c);
                _pos++;
            }
            else
            {
                break;
            }
        }

        var str = sb.ToString();
        if (str.Contains('.'))
        {
            if (double.TryParse(str, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                return new PdfReal(d);
        }
        else
        {
            if (int.TryParse(str, out var i))
                return new PdfInteger(i);
        }

        return new PdfInteger(0);
    }

    private string ParseKeyword()
    {
        var sb = new StringBuilder();

        while (_pos < _content.Length)
        {
            var c = _content[_pos];
            if (char.IsLetterOrDigit((char)c) || c == '\'' || c == '"' || c == '*')
            {
                sb.Append((char)c);
                _pos++;
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }



    private static double GetNumber(PdfObject obj)
    {
        return obj switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            _ => 0
        };
    }

    private static readonly HashSet<string> Operators = new()
    {
        // Graphics state
        "q", "Q", "cm", "w", "J", "j", "M", "d", "ri", "i", "gs",
        // Path construction
        "m", "l", "c", "v", "y", "h", "re",
        // Path painting
        "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n",
        // Clipping
        "W", "W*",
        // Text
        "BT", "ET", "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts",
        "Td", "TD", "Tm", "T*", "Tj", "TJ", "'", "\"",
        // Color
        "CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg", "K", "k",
        // Shading
        "sh",
        // XObject/Images
        "Do", "BI", "ID", "EI",
        // Type 3 font character widths
        "d0", "d1",
        // Marked content + compatibility
        "MP", "DP", "BMC", "BDC", "EMC", "BX", "EX"
    };

    private static bool IsOperator(string token) => Operators.Contains(token);

    #endregion

    /// <summary>
    /// Internal graphics state for tracking transformations.
    /// </summary>
    private class GraphicsState
    {
        public double Ctm_a = 1, Ctm_b, Ctm_c, Ctm_d = 1, Ctm_e, Ctm_f;

        // Line state (§8.4.3 table 57)
        public double LineWidth = 1;
        public int    LineCap;
        public int    LineJoin;
        public double MiterLimit = 10;

        // Transparency (§11.3 table 128)
        public double FillAlpha = 1.0;
        public double StrokeAlpha = 1.0;
        public string BlendMode = "Normal";
        public bool   HasSoftMask;
        public bool   AlphaIsShape;
        public bool   StrokeAdjustment;

        // Color state — name only; fully resolving colors requires the resource dict.
        public string FillColorSpace = "DeviceGray";
        public string StrokeColorSpace = "DeviceGray";

        // Pending clipping operator queued by W / W*; consumed at the next path-painting op.
        public string? PendingClip;

        public void MultiplyCtm(double a, double b, double c, double d, double e, double f)
        {
            var na = a * Ctm_a + b * Ctm_c;
            var nb = a * Ctm_b + b * Ctm_d;
            var nc = c * Ctm_a + d * Ctm_c;
            var nd = c * Ctm_b + d * Ctm_d;
            var ne = e * Ctm_a + f * Ctm_c + Ctm_e;
            var nf = e * Ctm_b + f * Ctm_d + Ctm_f;

            Ctm_a = na; Ctm_b = nb; Ctm_c = nc;
            Ctm_d = nd; Ctm_e = ne; Ctm_f = nf;
        }

        public GraphicsState Clone() => new()
        {
            Ctm_a = Ctm_a, Ctm_b = Ctm_b, Ctm_c = Ctm_c,
            Ctm_d = Ctm_d, Ctm_e = Ctm_e, Ctm_f = Ctm_f,
            LineWidth = LineWidth, LineCap = LineCap,
            LineJoin  = LineJoin,  MiterLimit = MiterLimit,
            FillAlpha = FillAlpha, StrokeAlpha = StrokeAlpha,
            BlendMode = BlendMode, HasSoftMask = HasSoftMask,
            AlphaIsShape = AlphaIsShape, StrokeAdjustment = StrokeAdjustment,
            FillColorSpace = FillColorSpace,
            StrokeColorSpace = StrokeColorSpace,
            PendingClip = PendingClip
        };
    }
}
