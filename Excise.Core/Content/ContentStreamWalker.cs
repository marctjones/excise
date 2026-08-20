using System;
using System.Text;
using System.Threading;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Parsing;

namespace Excise.Core.Content;

/// <summary>
/// One glyph, as the walker resolved it: the decoded Unicode, the pen origin
/// and cell in USER space, and the raw §9.4.4 displacement terms a sink needs
/// to reproduce the advance in its own units.
///
/// <para>Passed by <c>in</c> reference to <see cref="IContentStreamSink.OnGlyph"/>
/// so a per-glyph callback costs no allocation (#966) — the walker computes
/// each field once and every sink reads the same numbers, which is the whole
/// point of #992: two machines can no longer disagree about where a glyph is.</para>
/// </summary>
/// <param name="CharCode">The source character code, as segmented from the string bytes.</param>
/// <param name="Cid">The CID the code maps to (== <paramref name="CharCode"/> outside a registered CMap).</param>
/// <param name="ByteLength">Content-stream bytes this code occupied (1, 2, or per-code under a mixed CMap).</param>
/// <param name="Unicode">The decoded Unicode text for this code.</param>
/// <param name="X">Pen origin in user space, with the §9.4.4 text rise applied.</param>
/// <param name="Y">Pen origin in user space, with the §9.4.4 text rise applied.</param>
/// <param name="Cell">The glyph cell in user space (§9.4.4 advance × ascent, matrix-mapped).</param>
/// <param name="Width">Magnitude of the user-space advance vector.</param>
/// <param name="FontSize">Active font size (Tfs) at this glyph.</param>
/// <param name="FontName">Active font resource name at this glyph.</param>
/// <param name="DisplacementThousandths">
/// Glyph-space displacement along the writing direction, in 1000ths of an em:
/// w0 horizontally, w1y vertically. #758's advance accounting needs the raw
/// term, not the composed matrix delta.
/// </param>
/// <param name="Spacing">The Tc (+Tw where §9.3.3 allows) contribution, in text space.</param>
/// <param name="IsVerticalWriting">True when the active font is in vertical writing mode (§9.7.4.3).</param>
/// <param name="IsCidFont">True when the active font is a composite (Type0/CID) font (§9.7).</param>
internal readonly record struct WalkedGlyph(
    int CharCode,
    int Cid,
    int ByteLength,
    string Unicode,
    double X,
    double Y,
    PdfRectangle Cell,
    double Width,
    double FontSize,
    string FontName,
    double DisplacementThousandths,
    double Spacing,
    bool IsVerticalWriting,
    bool IsCidFont);

/// <summary>
/// What a <see cref="ContentStreamWalker"/> consumer implements. Implemented by
/// a STRUCT and dispatched through a generic constraint, never through an
/// interface-typed field: the JIT specializes and devirtualizes each sink, so
/// the per-glyph callback on the extraction hot path allocates nothing and
/// costs no virtual dispatch (#966/#600).
/// </summary>
internal interface IContentStreamSink
{
    /// <summary>
    /// Every operator the walker recognises, in stream order, BEFORE the walker
    /// executes it. Inline images arrive through
    /// <see cref="OnInlineImage"/> instead; <c>ID</c>/<c>EI</c> never arrive at
    /// all.
    /// </summary>
    void OnOperator(string name, List<PdfObject> operands);

    /// <summary>An inline image (§8.9.7): its normalized parameter dict and raw sample bytes.</summary>
    void OnInlineImage(PdfDictionary imageParams, byte[] imageData);

    /// <summary>
    /// A text-showing operator is about to show glyphs, after any implicit
    /// line/spacing side effects of <c>'</c> and <c>"</c> have been applied.
    /// </summary>
    void OnTextShowBegin();

    /// <summary>One shown string is about to be decoded (a <c>TJ</c> array holds several).</summary>
    void OnStringBegin();

    /// <summary>One glyph, fully resolved. See <see cref="WalkedGlyph"/>.</summary>
    void OnGlyph(in WalkedGlyph glyph);

    /// <summary>The shown string is finished; <paramref name="byteCount"/> is its source byte length.</summary>
    void OnStringEnd(int byteCount);

    /// <summary>The text-showing operator is finished.</summary>
    void OnTextShowEnd();

    /// <summary>A numeric <c>TJ</c> array element, in TJ-adjustment units (§9.4.3).</summary>
    void OnTjAdjustment(double adjustment);
}

/// <summary>
/// THE content-stream state machine: tokenizer, graphics state (§8.4.1 Table
/// 52, including the text parameters <c>q</c>/<c>Q</c> save and restore), text
/// state, §9.4.2 line stepping, §9.4.4 glyph advance, the glyph cell transform,
/// font resolution and the shared <see cref="Text.GlyphUnicodeDecoder"/>
/// cascade. It walks the bytes once and hands each glyph to a sink.
///
/// <para><b>One walk, many sinks — a new consumer adds a sink, never a
/// parser.</b> This class exists because the walk used to exist twice
/// (<see cref="ContentStreamParser"/> aggregating glyph cells into operator
/// bounding boxes, <see cref="Text.TextExtractor"/> emitting letters) and every
/// RC1 defect lived in the divergence between the copies: §9.4.2 line stepping
/// (#942/#899), the advance terms inside horizontal scaling (#734), the glyph
/// cell (#833/#980), the array nesting bound (#971), the hex-digit skip (#974),
/// <c>sh</c>/<c>d0</c>/<c>d1</c> and inline images (#980), the decode cascade
/// (#981), cancellation (#982) and the Table 52 text state (#983). A second
/// implementation of any of this re-opens all of them. See #992.</para>
/// </summary>
internal sealed class ContentStreamWalker
{
    // Not readonly: RunNested swaps in a form XObject's bytes and swaps them
    // back, so one walker (one state machine) covers the whole nested walk.
    private byte[] _content;
    private readonly PdfPage? _page;
    private int _pos;

    // Hostile-input guards (#346): bound recursion (nested arrays and
    // dictionaries) and offer a cooperative cancellation point so a
    // pathological stream can't spin or overflow the stack past the caller's
    // timeout.
    private int _nestingDepth;
    private CancellationToken _cancellationToken;

    /// <summary>Max nesting depth for content-stream arrays/dictionaries before bailing (#346/#971).</summary>
    public int MaxNestingDepth { get; set; } = 256;

    /// <summary>
    /// When false the walker only TOKENIZES: operators and their operands are
    /// delivered verbatim, but no graphics/text state is tracked, no font is
    /// resolved and no glyph is emitted. This is the mode a caller uses when it
    /// re-executes the operators under its own state machine and never reads
    /// the derived metadata (Excise.Rendering — see #598 and
    /// <see cref="ContentStreamParser.ComputeOperatorMetadata"/>).
    /// </summary>
    public bool TrackState { get; set; } = true;

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
    // The shared code→Unicode cascade (#981) — /ToUnicode, /Differences, the
    // embedded reverse cmap, the Mac glyph order and the symbol cmap all live
    // in this object.
    private Text.GlyphUnicodeDecoder _decoder = Text.GlyphUnicodeDecoder.None;
    private readonly Dictionary<PdfDictionary, Text.GlyphUnicodeDecoder> _decoderCache =
        new(ReferenceEqualityComparer.Instance);
    private bool _is2ByteFont;
    // Composite (Type0/CID) font, per §9.7. Distinct from _is2ByteFont, which
    // #659's explicitly-uniform-1-byte codespace can turn off on a font that is
    // still CID-keyed.
    private bool _isCidFont;
    private PdfDictionary? _cidFontDict;
    private Fonts.CidFontWidths? _cidMetrics;
    private bool _isVerticalWriting;
    // Registered (predefined) CJK CMap support (#515): the CMap's codespaces
    // drive byte segmentation and its mapping gives the CID for width lookup
    // and CID→Unicode decoding.
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

    // Scratch byte buffer reused by ParseStringLiteral/ParseHexString across
    // calls (#600's trick, ported here because this tokenizer is now the one on
    // the extraction hot path): each call resets the length, appends its decoded
    // bytes and copies them into an exact-size result array before returning, so
    // nothing aliases the scratch. String/hex token parses never nest inside one
    // another, and a walker instance is single-threaded by construction.
    private byte[] _stringScratch = new byte[128];
    private int _stringScratchLen;

    private void ScratchAdd(byte b)
    {
        if (_stringScratchLen == _stringScratch.Length)
            Array.Resize(ref _stringScratch, _stringScratch.Length * 2);
        _stringScratch[_stringScratchLen++] = b;
    }

    // Cached PdfInteger instances for the small values content streams are made
    // of — TJ kern adjustments, Td/Tm/cm coordinates (#600 measured the boxing
    // of exactly these as a large share of extraction allocation). PdfInteger is
    // immutable and only ever read back through Value, so sharing instances is
    // observably identical to allocating one each time.
    private const int SmallIntMin = -1024;
    private const int SmallIntMax = 1024;
    private static readonly PdfInteger[] SmallIntegers = CreateSmallIntegers();

    private static PdfInteger[] CreateSmallIntegers()
    {
        var values = new PdfInteger[SmallIntMax - SmallIntMin + 1];
        for (int i = 0; i < values.Length; i++)
            values[i] = new PdfInteger(SmallIntMin + i);
        return values;
    }

    private static PdfInteger IntegerFor(int value) =>
        value >= SmallIntMin && value <= SmallIntMax
            ? SmallIntegers[value - SmallIntMin]
            : new PdfInteger(value);

    // Resource dictionaries in scope, innermost first (§8.10.1: a form XObject's
    // /Resources shadow the page's for the duration of its content). EMPTY by
    // default, which is exactly the page-only lookup ContentStreamParser has
    // always done; only a sink that walks INTO a form pushes onto it.
    private readonly Stack<PdfDictionary> _resourcesStack = new();

    /// <summary>
    /// Create a walker over the given content bytes.
    /// </summary>
    /// <param name="content">Raw content stream bytes.</param>
    /// <param name="page">Optional page reference for font/ExtGState resolution.</param>
    public ContentStreamWalker(byte[] content, PdfPage? page = null)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _page = page;
    }

    /// <summary>
    /// Bring a resource dictionary into scope for name lookups (fonts,
    /// XObjects, /Properties). The page's own resources are NOT pushed
    /// automatically — a sink that wants them says so.
    /// </summary>
    public void PushResources(PdfDictionary resources) => _resourcesStack.Push(resources);

    /// <summary>The resource dictionaries in scope, innermost first.</summary>
    public IEnumerable<PdfDictionary> ActiveResources => _resourcesStack;

    /// <summary>
    /// Resolve a named XObject through the resources in scope, falling back to
    /// the page's own — the §8.10.1 lookup a <c>Do</c> operand needs.
    /// </summary>
    public PdfObject? ResolveXObject(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            var xObjectsObj = resources.GetOptional("XObject");
            if (xObjectsObj == null) continue;
            if (_page?.Document.Resolve(xObjectsObj) is not PdfDictionary xObjects)
                continue;
            var xObject = xObjects.GetOptional(name);
            if (xObject == null) continue;
            return _page.Document.Resolve(xObject);
        }

        return _page?.GetXObject(name);
    }

    private PdfDictionary? ResolveFont(string fontName)
    {
        foreach (var resources in _resourcesStack)
        {
            var fontsObj = resources.GetOptional("Font");
            if (fontsObj == null) continue;
            if (_page?.Document.Resolve(fontsObj) is not PdfDictionary fonts)
                continue;
            var fontObj = fonts.GetOptional(fontName);
            if (fontObj == null) continue;
            return _page.Document.Resolve(fontObj) as PdfDictionary;
        }

        return _page?.GetFont(fontName);
    }

    #region State accessors

    /// <summary>Current transformation matrix, a.</summary>
    public double Ctm_a => _state.Ctm_a;
    /// <summary>Current transformation matrix, b.</summary>
    public double Ctm_b => _state.Ctm_b;
    /// <summary>Current transformation matrix, c.</summary>
    public double Ctm_c => _state.Ctm_c;
    /// <summary>Current transformation matrix, d.</summary>
    public double Ctm_d => _state.Ctm_d;
    /// <summary>Current transformation matrix, e.</summary>
    public double Ctm_e => _state.Ctm_e;
    /// <summary>Current transformation matrix, f.</summary>
    public double Ctm_f => _state.Ctm_f;

    /// <summary>Current text matrix, a.</summary>
    public double Tm_a => _tm_a;
    /// <summary>Current text matrix, b.</summary>
    public double Tm_b => _tm_b;
    /// <summary>Current text matrix, c.</summary>
    public double Tm_c => _tm_c;
    /// <summary>Current text matrix, d.</summary>
    public double Tm_d => _tm_d;
    /// <summary>Current text matrix, e.</summary>
    public double Tm_e => _tm_e;
    /// <summary>Current text matrix, f.</summary>
    public double Tm_f => _tm_f;

    /// <summary>Transform a point from user space through the CTM.</summary>
    public (double x, double y) TransformPoint(double x, double y)
    {
        var tx = x * _state.Ctm_a + y * _state.Ctm_c + _state.Ctm_e;
        var ty = x * _state.Ctm_b + y * _state.Ctm_d + _state.Ctm_f;
        return (tx, ty);
    }

    /// <summary>
    /// Axis-aligned extent of a rectangle's four corners transformed through
    /// the CTM.
    /// </summary>
    public PdfRectangle TransformBounds(double minX, double minY, double maxX, double maxY)
    {
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

    #endregion

    /// <summary>
    /// Walk the whole content stream, driving <paramref name="sink"/>.
    /// </summary>
    /// <param name="sink">The consumer, by reference so a struct sink keeps its state.</param>
    /// <param name="cancellationToken">Cooperatively cancels a runaway walk of
    /// hostile/huge input (#346/#982).</param>
    public void Walk<TSink>(ref TSink sink, CancellationToken cancellationToken = default)
        where TSink : struct, IContentStreamSink
    {
        _cancellationToken = cancellationToken;
        _nestingDepth = 0;
        _pos = 0;
        WalkOperators(ref sink);
    }

    /// <summary>
    /// Walk a NESTED content stream — a form XObject invoked by <c>Do</c>, or an
    /// annotation's <c>/AP</c> appearance — under §8.10.1 semantics: the form's
    /// <c>/Matrix</c> is concatenated onto the CTM, its <c>/Resources</c> come
    /// into scope, and every graphics and text parameter is restored afterwards
    /// as if the whole invocation were bracketed in <c>q … Q</c>.
    ///
    /// <para>This lives in the walker rather than in a sink because it is state
    /// machinery, and a second copy of state machinery is what #992 exists to
    /// remove. Nesting DEPTH and cycle detection stay with the caller: whether
    /// to descend at all is a policy decision, not a parsing one.</para>
    /// </summary>
    /// <param name="content">The nested stream's decoded bytes.</param>
    /// <param name="resources">The nested stream's <c>/Resources</c>, if any.</param>
    /// <param name="matrix">The nested stream's <c>/Matrix</c>, or null for identity.</param>
    /// <param name="fromIdentityCtm">
    /// Start the nested walk from an IDENTITY CTM rather than the current one.
    /// For an appearance stream reached outside the page's content stream, the
    /// leftover page CTM is unrelated to it and would only corrupt the positions.
    /// </param>
    /// <param name="sink">The consumer, by reference.</param>
    public void RunNested<TSink>(
        byte[] content,
        PdfDictionary? resources,
        double[]? matrix,
        bool fromIdentityCtm,
        ref TSink sink)
        where TSink : struct, IContentStreamSink
    {
        var savedContent = _content;
        var savedPos = _pos;
        var savedNesting = _nestingDepth;
        var savedState = _state;
        var savedStackDepth = _stateStack.Count;
        var savedTextState = CaptureTextState();
        var savedTm = (_tm_a, _tm_b, _tm_c, _tm_d, _tm_e, _tm_f, _tlm_e, _tlm_f);
        var pushedResources = false;

        try
        {
            _state = _state.Clone();
            if (fromIdentityCtm)
            {
                _state.Ctm_a = 1; _state.Ctm_b = 0; _state.Ctm_c = 0;
                _state.Ctm_d = 1; _state.Ctm_e = 0; _state.Ctm_f = 0;
            }
            if (matrix is { Length: >= 6 })
                _state.MultiplyCtm(matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]);

            if (resources != null)
            {
                _resourcesStack.Push(resources);
                pushedResources = true;
            }

            _content = content;
            _pos = 0;
            _nestingDepth = 0;
            WalkOperators(ref sink);
        }
        finally
        {
            if (pushedResources)
                _resourcesStack.Pop();

            // An unbalanced `q` inside the form must not leak saved states out
            // of it (§8.10.1 brackets the invocation).
            while (_stateStack.Count > savedStackDepth)
                _stateStack.Pop();

            _content = savedContent;
            _pos = savedPos;
            _nestingDepth = savedNesting;
            _state = savedState;
            RestoreTextState(savedTextState);
            (_tm_a, _tm_b, _tm_c, _tm_d, _tm_e, _tm_f, _tlm_e, _tlm_f) = savedTm;
        }
    }

    private void WalkOperators<TSink>(ref TSink sink)
        where TSink : struct, IContentStreamSink
    {
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
                    ParseInlineImage(ref sink);
                    operands.Clear();
                }
                else if (op is "ID" or "EI")
                {
                    // Should only appear inside BI handling above; skip if stray
                    operands.Clear();
                }
                else
                {
                    sink.OnOperator(op, operands);
                    if (TrackState)
                        ExecuteOperator(op, Arguments(op, operands), ref sink);
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
                // else here is an operator this walker does not implement, and
                // an unimplemented operator still TERMINATES its operands —
                // leaving them queued would let the next real operator read
                // them as its own (#980).
                if (keyword == "true") operands.Add(PdfBoolean.True);
                else if (keyword == "false") operands.Add(PdfBoolean.False);
                else if (keyword == "null") operands.Add(PdfNull.Instance);
                else operands.Clear();
            }
        }
    }

    #region Operator execution

    /// <summary>
    /// A window onto the tail of the accumulated operand list — the operands an
    /// operator actually takes.
    ///
    /// <para>§7.8.2: an operator's operands are the objects that IMMEDIATELY
    /// precede it. When a stream leaves extra objects on the list (malformed,
    /// but it happens), the operator's own operands are the LAST n, not the
    /// first n. Reading from the front instead silently mis-executes:
    /// <c>&lt;&lt;/K /V&gt;&gt; (Text) Tj</c> showed the DICTIONARY and dropped
    /// the string. That was a real divergence — ContentStreamParser lost the
    /// text and TextExtractor did not, because the extractor's tokenizer
    /// discarded dictionaries outright — and #980's gate documented it as
    /// untested rather than pinning it. Consolidating the walk forced the
    /// question; this answers it in the direction that keeps the text.</para>
    ///
    /// <para>The view is EXECUTION-only. The sink still receives the complete
    /// operand list, so <see cref="ContentStreamWriter"/> round-trips every
    /// token that was there — trimming what gets recorded would turn a
    /// mis-execution into data loss on rewrite, which for redaction output is
    /// the worse failure.</para>
    /// </summary>
    private readonly struct OperandView(List<PdfObject> items, int offset)
    {
        public int Count => items.Count - offset;
        public PdfObject this[int index] => items[offset + index];
    }

    /// <summary>
    /// Operand counts for the operators whose arity ISO 32000-2 fixes. Absent =
    /// variable (<c>SC</c>/<c>SCN</c>/<c>sc</c>/<c>scn</c> take as many colour
    /// components as the space has) and left untrimmed.
    /// </summary>
    private static readonly Dictionary<string, int> OperatorArity = new()
    {
        ["q"] = 0, ["Q"] = 0, ["BT"] = 0, ["ET"] = 0, ["T*"] = 0,
        ["n"] = 0, ["h"] = 0, ["W"] = 0, ["W*"] = 0,
        ["S"] = 0, ["s"] = 0, ["f"] = 0, ["F"] = 0, ["f*"] = 0,
        ["B"] = 0, ["B*"] = 0, ["b"] = 0, ["b*"] = 0,
        ["EMC"] = 0, ["BX"] = 0, ["EX"] = 0,

        ["Tj"] = 1, ["TJ"] = 1, ["'"] = 1,
        ["Tc"] = 1, ["Tw"] = 1, ["Tz"] = 1, ["TL"] = 1, ["Ts"] = 1, ["Tr"] = 1,
        ["w"] = 1, ["J"] = 1, ["j"] = 1, ["M"] = 1, ["i"] = 1, ["ri"] = 1,
        ["gs"] = 1, ["Do"] = 1, ["sh"] = 1, ["CS"] = 1, ["cs"] = 1,
        ["G"] = 1, ["g"] = 1, ["BMC"] = 1, ["MP"] = 1,

        ["Td"] = 2, ["TD"] = 2, ["m"] = 2, ["l"] = 2, ["Tf"] = 2,
        ["d"] = 2, ["d0"] = 2, ["BDC"] = 2, ["DP"] = 2,

        ["\""] = 3, ["rg"] = 3, ["RG"] = 3,

        ["re"] = 4, ["v"] = 4, ["y"] = 4, ["k"] = 4, ["K"] = 4,

        ["c"] = 6, ["cm"] = 6, ["Tm"] = 6, ["d1"] = 6,
    };

    private static OperandView Arguments(string name, List<PdfObject> operands) =>
        OperatorArity.TryGetValue(name, out var arity) && operands.Count > arity
            ? new OperandView(operands, operands.Count - arity)
            : new OperandView(operands, 0);

    private void ExecuteOperator<TSink>(string name, OperandView operands, ref TSink sink)
        where TSink : struct, IContentStreamSink
    {
        if (ExecuteGraphicsStateOperator(name, operands)) return;
        if (ExecuteTextObjectOperator(name)) return;
        if (ExecuteTextStateOperator(name, operands)) return;
        if (ExecuteTextPositioningOperator(name, operands)) return;
        if (ExecuteTextShowingOperator(name, operands, ref sink)) return;
        if (ExecuteClippingOperator(name)) return;
        ExecuteColorOperator(name, operands);
    }

    private bool ExecuteGraphicsStateOperator(string name, OperandView operands)
    {
        switch (name)
        {
            case "q":
                {
                    // §8.4.1 Table 52 puts the TEXT state parameters (font +
                    // size, Tc, Tw, Tz, TL, Ts, Tr) in the GRAPHICS state, so
                    // `q` saves them and `Q` restores them. Neither content
                    // parser did until #983: a producer that brackets a
                    // differently-styled run in q/Q left the font size, spacing
                    // and leading of that run applied to everything after the
                    // `Q`. GlyphRemover.TextStateTracker has always done it, so
                    // the redaction pipeline disagreed with itself. mutool
                    // corroborates the spec reading (its stext reports the
                    // pre-`q` size after `Q`).
                    var saved = _state.Clone();
                    saved.SavedTextState = CaptureTextState();
                    _stateStack.Push(saved);
                }
                return true;

            case "Q":
                if (_stateStack.Count > 0)
                {
                    _state = _stateStack.Pop();
                    if (_state.SavedTextState is { } restored)
                        RestoreTextState(restored);
                }
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

            // Path PAINTING consumes the pending clip (§8.5.4). The path's own
            // bounds are a sink concern — the walker owns only what survives
            // q/Q.
            case "S":
            case "s":
            case "f":
            case "F":
            case "f*":
            case "B":
            case "B*":
            case "b":
            case "b*":
            case "n":
                _state.PendingClip = null;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The §8.4.1 Table 52 text-state parameters, plus the per-font state
    /// <c>Tf</c> derives from the font dictionary. Restoring the NAME and SIZE
    /// alone would leave a walker that reports "F1 @ 12" while decoding through
    /// the bracketed font's ToUnicode/CID maps, which is a worse failure than
    /// not restoring at all. The derived members are all
    /// immutable-per-font references, so this is a handful of pointer copies —
    /// no <see cref="LoadFont"/> re-parse on restore.
    ///
    /// The text matrix is deliberately ABSENT: Table 52 does not list it, it is
    /// reset by <c>BT</c>, and q/Q may not appear inside a text object (§8.2).
    /// #983.
    /// </summary>
    private readonly record struct TextStateSnapshot(
        double FontSize,
        string FontName,
        PdfDictionary? CurrentFont,
        Text.GlyphUnicodeDecoder Decoder,
        bool Is2ByteFont,
        PdfDictionary? CidFontDict,
        Fonts.CidFontWidths? CidMetrics,
        bool IsVerticalWriting,
        Text.CidCMap? RegisteredEncodingCMap,
        IReadOnlyDictionary<int, string>? RegisteredCidToUnicode,
        double TextLeading,
        double CharSpacing,
        double WordSpacing,
        double HorizontalScaling,
        double TextRise);

    private TextStateSnapshot CaptureTextState() => new(
        _fontSize, _fontName, _currentFont, _decoder, _is2ByteFont,
        _cidFontDict, _cidMetrics, _isVerticalWriting, _registeredEncodingCMap,
        _registeredCidToUnicode, _textLeading, _charSpacing, _wordSpacing,
        _horizontalScaling, _textRise);

    private void RestoreTextState(in TextStateSnapshot s)
    {
        _fontSize = s.FontSize;
        _fontName = s.FontName;
        _currentFont = s.CurrentFont;
        _decoder = s.Decoder;
        _is2ByteFont = s.Is2ByteFont;
        _cidFontDict = s.CidFontDict;
        _cidMetrics = s.CidMetrics;
        _isVerticalWriting = s.IsVerticalWriting;
        _registeredEncodingCMap = s.RegisteredEncodingCMap;
        _registeredCidToUnicode = s.RegisteredCidToUnicode;
        _textLeading = s.TextLeading;
        _charSpacing = s.CharSpacing;
        _wordSpacing = s.WordSpacing;
        _horizontalScaling = s.HorizontalScaling;
        _textRise = s.TextRise;
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

    private bool ExecuteTextStateOperator(string name, OperandView operands)
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

    private bool ExecuteTextPositioningOperator(string name, OperandView operands)
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

    private bool ExecuteTextShowingOperator<TSink>(string name, OperandView operands, ref TSink sink)
        where TSink : struct, IContentStreamSink
    {
        switch (name)
        {
            case "Tj":
                if (operands.Count >= 1)
                {
                    sink.OnTextShowBegin();
                    ShowTextObject(operands[0], ref sink);
                    sink.OnTextShowEnd();
                }
                return true;

            case "TJ":
                if (operands.Count >= 1 && operands[0] is PdfArray arr)
                {
                    sink.OnTextShowBegin();
                    ShowTextArray(arr, ref sink);
                    sink.OnTextShowEnd();
                }
                return true;

            case "'":
                MoveToNextTextLine();
                if (operands.Count >= 1)
                {
                    sink.OnTextShowBegin();
                    ShowTextObject(operands[0], ref sink);
                    sink.OnTextShowEnd();
                }
                return true;

            case "\"":
                if (operands.Count >= 3)
                {
                    _wordSpacing = GetNumber(operands[0]);
                    _charSpacing = GetNumber(operands[1]);
                    MoveToNextTextLine();
                    sink.OnTextShowBegin();
                    ShowTextObject(operands[2], ref sink);
                    sink.OnTextShowEnd();
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

    private bool ExecuteColorOperator(string name, OperandView operands)
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

            default:
                return false;
        }
    }

    private void MoveTextPosition(OperandView operands, bool setLeading)
    {
        if (operands.Count < 2)
            return;

        var tx = GetNumber(operands[0]);
        var ty = GetNumber(operands[1]);
        if (setLeading)
            _textLeading = -ty;

        // §9.4.2: Tlm' = [1 0 0 1 tx ty] × Tlm — text-space offset composed
        // through the matrix's linear part. The raw add stacked every line of
        // a `1 Tf` + scaled-`Tm` document onto the first, which both made
        // operator bboxes overlap lines they weren't on and marched extracted
        // letters off-page under flipped matrices (#942/#899).
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

    #endregion

    #region Glyph walk

    private void ShowTextObject<TSink>(PdfObject obj, ref TSink sink)
        where TSink : struct, IContentStreamSink
    {
        // Use the RAW string bytes. Text-showing operands are font-encoded
        // byte codes, not PDFDoc/UTF-16 text — round-tripping through
        // PdfString.Value's document-string decode heuristics and Latin-1
        // mangles any byte the heuristic maps above U+00FF (Latin-1 clamps it
        // to '?'), which garbled every multi-byte CJK code (#515).
        if (obj is PdfString ps)
            ShowTextBytes(ps.Bytes, ref sink);
    }

    private void ShowTextArray<TSink>(PdfArray arr, ref TSink sink)
        where TSink : struct, IContentStreamSink
    {
        foreach (var item in arr)
        {
            if (item is PdfString ps)
            {
                // Raw font-encoded bytes — see ShowTextObject (#515).
                ShowTextBytes(ps.Bytes, ref sink);
            }
            else if (item is PdfInteger pi)
            {
                ApplyTjAdjustment(pi.Value, ref sink);
            }
            else if (item is PdfReal pr)
            {
                ApplyTjAdjustment(pr.Value, ref sink);
            }
        }
    }

    private void ShowTextBytes<TSink>(byte[] bytes, ref TSink sink)
        where TSink : struct, IContentStreamSink
    {
        sink.OnStringBegin();

        if (_registeredEncodingCMap != null)
        {
            // Registered CMap codespaces drive segmentation — mixed 1/2-byte
            // codes (90ms-RKSJ-H) would be garbled by a fixed stride (#515).
            foreach (var (code, cid, byteLength) in _registeredEncodingCMap.DecodeDetailed(bytes))
                EmitGlyph(code, cid, byteLength, ref sink);
        }
        else
        {
            int stride = _is2ByteFont ? 2 : 1;
            for (int i = 0; i + stride <= bytes.Length; i += stride)
            {
                int charCode = _is2ByteFont
                    ? (bytes[i] << 8) | bytes[i + 1]
                    : bytes[i];
                EmitGlyph(charCode, charCode, stride, ref sink);
            }
        }

        sink.OnStringEnd(bytes.Length);
    }

    /// <summary>
    /// Resolve one glyph for a decoded source code and advance the pen.
    /// <paramref name="cid"/> equals <paramref name="charCode"/> except under a
    /// registered encoding CMap (#515); widths are CID-keyed (§9.7.4.3).
    /// </summary>
    private void EmitGlyph<TSink>(int charCode, int cid, int byteLength, ref TSink sink)
        where TSink : struct, IContentStreamSink
    {
        var unicode = _decoder.Decode(charCode, cid, _registeredCidToUnicode);
        var charWidth = GetCharWidth(cid);

        // Transform position. §9.4.4 puts the rise in the text rendering
        // matrix as the translation (0, Ts) INSIDE Tm, so it is a text-space
        // offset and must be composed through the matrix's linear part —
        // the same §9.4.2 arithmetic MoveTextPosition does. Adding it raw to
        // _tm_f put a `6 Ts` under a `12 0 0 12 Tm` matrix 6 units up
        // instead of 72 (#980).
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
        // apply the matrix) kept positions correct (#833/#980).
        var (wx, wy) = TransformTextVector(advanceTextSpace, 0);
        var (hx, hy) = TransformTextVector(0, ascentTextSpace);
        var glyphWidth = Math.Sqrt(wx * wx + wy * wy);

        // Word spacing fires only on the SINGLE-BYTE code 32 (§9.3.3).
        var spacing = _charSpacing;
        if (byteLength == 1 && charCode == 32) spacing += _wordSpacing;

        PdfRectangle cell;
        double displacementThousandths;
        double ty = 0;

        if (_isVerticalWriting)
        {
            // Vertical writing (§9.7.4.3): the pen is the VERTICAL origin —
            // the cell is centered on it via the /W2 position vector (default
            // vx = w0/2) and spans DOWN by the vertical displacement w1y.
            var vm = GetVerticalMetrics(cid);
            var (vxx, _) = TransformTextVector(vm.Vx * _fontSize / 1000.0, 0);
            var (chx, chy) = TransformTextVector(0, Math.Abs(vm.W1Y) * _fontSize / 1000.0);
            var cellHeight = Math.Sqrt(chx * chx + chy * chy);
            if (cellHeight <= 0) cellHeight = Math.Abs(hy) > 0 ? Math.Abs(hy) : glyphWidth;
            cell = new PdfRectangle(x - vxx, y - cellHeight, x - vxx + glyphWidth, y);

            // ty = w1y·Tfs + Tc + Tw from /W2 (else /DW2, default −1000 → down
            // the page), no Th (§9.4.4).
            displacementThousandths = vm.W1Y;
            ty = (vm.W1Y / 1000.0) * _fontSize + spacing;
        }
        else
        {
            cell = AxisAlignedBox(x, y, wx, wy, hx, hy);
            displacementThousandths = charWidth;
        }

        var glyph = new WalkedGlyph(
            charCode, cid, byteLength, unicode,
            x, y, cell, glyphWidth,
            _fontSize, _fontName,
            displacementThousandths, spacing, _isVerticalWriting, _isCidFont);
        sink.OnGlyph(in glyph);

        // Advance the text position (§9.4.4).
        if (_isVerticalWriting)
        {
            _tm_e += ty * _tm_c;
            _tm_f += ty * _tm_d;
        }
        else
        {
            // tx = (w0·Tfs + Tc + Tw)·Th — the spacing terms sit INSIDE
            // the horizontal-scaling factor (§9.4.4). #734
            var tx = ((charWidth / 1000.0) * _fontSize + spacing) * (_horizontalScaling / 100.0);
            _tm_e += tx * _tm_a;
            _tm_f += tx * _tm_b;
        }
    }

    /// <summary>
    /// TJ position adjustment, subtracted from the coordinate of the WRITING
    /// direction (§9.4.3): horizontal → tx (scaled by Th), vertical → ty
    /// (no Th). #515
    /// </summary>
    private void ApplyTjAdjustment<TSink>(double adj, ref TSink sink)
        where TSink : struct, IContentStreamSink
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

        sink.OnTjAdjustment(adj);
    }

    /// <summary>
    /// Vertical metrics for <paramref name="cid"/> — /W2, else the §9.7.4.3
    /// defaults (w1y from /DW2, v = (w0∕2, DW2[0])).
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
    /// displacements, not points (#833, #980).
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
    /// (wx, wy) and height vector (hx, hy) (#833, #980).
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

    #endregion

    #region Inline images

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
    ///
    /// Key normalization: full-form keys (e.g. <c>/Width</c>) are
    /// stored under their abbreviated equivalent (<c>/W</c>) so
    /// downstream code only sees one spelling. When both forms appear
    /// in the same dict, the abbreviated wins regardless of source
    /// order — implements the PDF Association's pdf-issues#3 ruling.
    /// </summary>
    private void ParseInlineImage<TSink>(ref TSink sink)
        where TSink : struct, IContentStreamSink
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

        sink.OnInlineImage(imageParams, imageData);
    }

    private static bool IsWhitespaceByte(byte b) =>
        b == 0x20 || b == 0x09 || b == 0x0A || b == 0x0D || b == 0x0C || b == 0x00;

    private static bool IsWordBoundaryByte(byte b) =>
        IsWhitespaceByte(b) || b == '/' || b == '(' || b == ')' || b == '[' || b == ']';

    #endregion

    #region Font resolution

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

        // §8.4.5 Table 58: /Font [ <font-ref> <size> ] sets exactly the two text
        // state parameters `Tf` sets. SkiaRenderer implemented it and NEITHER
        // content parser did (#990), so for a producer that selects fonts
        // through `gs` the renderer drew one font while the text model measured
        // glyph widths and decoded codes through the previous one — wrong glyph
        // cells (the geometry redaction removes on) and the wrong /ToUnicode,
        // with no differential able to see it because both parsers were wrong
        // the same way. Implemented once, here, because there is now one place
        // for it to live.
        if (gsDict.ContainsKey("Font") && _page != null &&
            _page.Document.Resolve(gsDict.GetOptional("Font") ?? PdfNull.Instance)
                is PdfArray fontEntry && fontEntry.Count >= 2 &&
            _page.Document.Resolve(fontEntry[0]) is PdfDictionary gsFont)
        {
            // The resource NAME is deliberately left alone: this font was
            // reached through the ExtGState, not through the /Font resource
            // dictionary, so it has no name to report.
            _fontSize = GetNumber(_page.Document.Resolve(fontEntry[1]));
            SelectFont(gsFont);
        }
    }

    /// <summary>
    /// Everything <c>Tf</c> derives from the resolved font dictionary. All of it
    /// is a pure function of that dictionary, but <c>Tf</c> recurs constantly —
    /// every text block re-issues it — so recomputing it per call (re-parsing
    /// the /ToUnicode and CMap streams and the /W width table) was a large share
    /// of extraction cost (#600). Cached by font-dict reference:
    /// <c>PdfDocument.Resolve</c> returns a stable instance per object number,
    /// so the same font hits.
    /// </summary>
    private readonly record struct FontState(
        Text.GlyphUnicodeDecoder Decoder,
        bool Is2ByteFont,
        bool IsCidFont,
        PdfDictionary? CidFontDict,
        Fonts.CidFontWidths? CidMetrics,
        bool IsVerticalWriting,
        Text.CidCMap? RegisteredEncodingCMap,
        IReadOnlyDictionary<int, string>? RegisteredCidToUnicode);

    private readonly Dictionary<PdfDictionary, FontState> _fontStateCache =
        new(ReferenceEqualityComparer.Instance);

    private FontState CaptureFontState() => new(
        _decoder, _is2ByteFont, _isCidFont, _cidFontDict, _cidMetrics,
        _isVerticalWriting, _registeredEncodingCMap, _registeredCidToUnicode);

    private void ApplyFontState(in FontState s)
    {
        _decoder = s.Decoder;
        _is2ByteFont = s.Is2ByteFont;
        _isCidFont = s.IsCidFont;
        _cidFontDict = s.CidFontDict;
        _cidMetrics = s.CidMetrics;
        _isVerticalWriting = s.IsVerticalWriting;
        _registeredEncodingCMap = s.RegisteredEncodingCMap;
        _registeredCidToUnicode = s.RegisteredCidToUnicode;
    }

    private void LoadFont()
    {
        if (_page == null) return;
        SelectFont(ResolveFont(_fontName));
    }

    /// <summary>
    /// Make <paramref name="font"/> the active font and derive everything that
    /// depends on it. Reached from <c>Tf</c> (by resource name) and from an
    /// ExtGState <c>/Font</c> entry (by direct reference, §8.4.5 Table 58) —
    /// two spellings of the same operation, sharing one implementation.
    /// </summary>
    private void SelectFont(PdfDictionary? font)
    {
        _currentFont = font;

        if (_currentFont != null && _fontStateCache.TryGetValue(_currentFont, out var cached))
        {
            ApplyFontState(cached);
            return;
        }

        LoadFontDerivedState();

        if (_currentFont != null)
            _fontStateCache[_currentFont] = CaptureFontState();
    }

    private void LoadFontDerivedState()
    {
        if (_page == null) return;

        _decoder = Text.GlyphUnicodeDecoder.None;
        _is2ByteFont = false;
        _isCidFont = false;
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
            _isCidFont = _is2ByteFont;

            // Embedded /Encoding CMap streams and registered CMap names both
            // drive byte segmentation through the parsed CMap's codespace
            // ranges (mixed 1/2-byte widths, per-byte-range matched) and map
            // code→CID for width lookup. #515 #659
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
                // writing mode. #515
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
                // Type0 font: load descendant CID font. /DescendantFonts is an
                // INDIRECT REFERENCE in real producers' output, so it must be
                // resolved — the bare `is PdfArray` cast failed there, leaving
                // _cidMetrics null and every CID glyph on a default width
                // (#843/#980).
                var descendantFontsObj = _page.Document.Resolve(
                    _currentFont.GetOptional("DescendantFonts") ?? PdfNull.Instance);
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

            // The whole code→Unicode cascade (#981), cached per font
            // dictionary. It swallows a malformed /ToUnicode rather than
            // failing the content-stream parse (#345).
            _decoder = GetDecoder(_currentFont);

            // Registered CID→Unicode via the descendant's /CIDSystemInfo
            // ordering (#515): a /ToUnicode STREAM wins outright; /ToUnicode
            // /Identity-H|V means code == Unicode (which the WinAnsi fallback
            // reproduces for the codes CJK text uses); a registered-CMap-name
            // /ToUnicode (#715) contributes its ordering. First signal with a
            // shipped map wins.
            if (_is2ByteFont && !_decoder.HasToUnicodeStreamMap && !_decoder.ToUnicodeIsIdentityName)
            {
                _registeredCidToUnicode =
                    TryLoadOrderingMap(GetCidSystemInfoOrdering())
                    ?? TryLoadOrderingMap(GetEncodingNameOrdering())
                    ?? TryLoadOrderingMap(GetToUnicodeRegisteredOrdering());
            }
        }
    }

    /// <summary>
    /// The shared decode cascade for one font dictionary (#981), cached so a
    /// stream that re-selects the same /Font on every text block does not
    /// re-parse its CMaps and embedded program each time.
    /// </summary>
    private Text.GlyphUnicodeDecoder GetDecoder(PdfDictionary? font)
    {
        if (font == null || _page == null)
            return Text.GlyphUnicodeDecoder.None;
        if (_decoderCache.TryGetValue(font, out var cached))
            return cached;
        var decoder = Text.GlyphUnicodeDecoder.Build(_page.Document, font);
        _decoderCache[font] = decoder;
        return decoder;
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

        // /DW, /W, /DW2, /W2 via the shared hardened parser (§9.7.4.3, #515).
        _cidMetrics = Fonts.CidFontWidths.Parse(
            _cidFontDict,
            _page != null ? _page.Document.Resolve : null);
    }

    /// <summary>
    /// Glyph width in 1000ths of an em. Until #980 the parser copy of this
    /// stopped after /Widths, so a Type0 font with no metrics, a
    /// /MissingWidth, and every non-embedded standard-14 font fell to a flat
    /// 600 default while the extractor used real metrics.
    /// </summary>
    private double GetCharWidth(int charCode)
    {
        // Type 0 / CIDFont: /W with the /DW fallback (§9.7.4.3).
        if (_is2ByteFont)
            return _cidMetrics?.GetWidth(charCode) ?? Fonts.CidFontWidths.SpecDefaultWidth;

        if (_currentFont != null)
        {
            // /Widths is an indirect reference in TeX/dvips PDFs — resolve it,
            // or the cast fails and glyph widths collapse to the 600 default
            // (#843).
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

            // #1050: RESOLVE. /FontDescriptor is almost ALWAYS an indirect
            // reference in real files -- it is a shared object by design -- so
            // the non-resolving read returned null essentially always, and
            // /MissingWidth was never applied. Every undefined glyph advanced
            // by 0 instead of the font's stated default width, which shifts
            // every subsequent glyph on the line and therefore shifts the
            // geometry redaction matches against.
            var fontDescriptor = _page != null
                ? _currentFont.ResolveDictionary(_page.Document, "FontDescriptor")
                : _currentFont.GetDirectDictionaryOrNull("FontDescriptor");
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
        // because the octal value was not truncated to a byte. §7.3.4.2:
        // "high-order overflow shall be ignored". #980
        _stringScratchLen = 0; // reuse the scratch buffer across calls (#600)
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
                    case (byte)'n': ScratchAdd((byte)'\n'); break;
                    case (byte)'r': ScratchAdd((byte)'\r'); break;
                    case (byte)'t': ScratchAdd((byte)'\t'); break;
                    case (byte)'b': ScratchAdd((byte)'\b'); break;
                    case (byte)'f': ScratchAdd((byte)'\f'); break;
                    case (byte)'(': ScratchAdd((byte)'('); break;
                    case (byte)')': ScratchAdd((byte)')'); break;
                    case (byte)'\\': ScratchAdd((byte)'\\'); break;
                    // REVERSE SOLIDUS followed by an end-of-line marker is a
                    // line-continuation: it produces NO character (PDF32000-1
                    // §7.3.4.2 Table 3). CRLF is one marker, not two — consume
                    // both bytes. A mismatch here risks a matched-but-not-
                    // actually-excised redaction leak (#637).
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
                            // Truncated to a byte (§7.3.4.2).
                            ScratchAdd(unchecked((byte)value));
                        }
                        else
                        {
                            ScratchAdd(escaped);
                        }
                        break;
                }
            }
            else if (c == '(')
            {
                depth++;
                ScratchAdd(c);
            }
            else if (c == ')')
            {
                depth--;
                if (depth > 0) ScratchAdd(c);
            }
            else
            {
                ScratchAdd(c);
            }
            _pos++;
        }

        return new PdfString(_stringScratch.AsSpan(0, _stringScratchLen).ToArray());
    }

    private PdfString ParseHexString()
    {
        _pos++; // Skip '<'
        _stringScratchLen = 0;
        int pendingNibble = -1;

        while (_pos < _content.Length && _content[_pos] != '>')
        {
            var c = (char)_content[_pos];
            // Per §7.3.4.3 a hex string holds only hex digits (whitespace is
            // ignored). Consume ONLY hex digits — letters G–Z would otherwise
            // reach a numeric conversion and throw FormatException on hostile
            // input. (#352)
            if (Uri.IsHexDigit(c))
            {
                int nibble = HexDigitValue(c);
                if (pendingNibble < 0)
                {
                    pendingNibble = nibble;
                }
                else
                {
                    ScratchAdd((byte)((pendingNibble << 4) | nibble));
                    pendingNibble = -1;
                }
            }
            _pos++;
        }
        _pos++; // Skip '>'

        // An odd digit count is padded with a trailing 0 (§7.3.4.3), which is
        // exactly a lone high nibble.
        if (pendingNibble >= 0)
            ScratchAdd((byte)(pendingNibble << 4));

        // Byte-identical to the previous build-a-string-then-PdfString(string)
        // form: every code is ≤ 0xFF, and that constructor Latin-1-encodes any
        // string with no char above U+00FF.
        return new PdfString(_stringScratch.AsSpan(0, _stringScratchLen).ToArray());
    }

    private static int HexDigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    private PdfArray ParseArray()
    {
        // Bound recursion: ParseArray -> ParseToken -> ParseArray for nested
        // arrays. A deeply nested array in hostile input would otherwise drive
        // a StackOverflow, which .NET cannot catch. (#346/#971)
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
        int segStart = _pos;
        StringBuilder? sb = null; // only needed when a #XX escape occurs (rare)

        while (_pos < _content.Length)
        {
            var c = _content[_pos];
            // §7.2.3 Table 2: the delimiters are ()<>[]{}/% and every
            // whitespace byte. FORM FEED and the two BRACES were missing from
            // this copy and present in the extractor's, so `/Name{` parsed as
            // the name "Name{" in one machine and "Name" in the other — a
            // resource lookup that resolves in one and not the other (#980).
            if (IsWhitespaceByte(c) || c == '/' ||
                c == '[' || c == ']' || c == '<' || c == '>' ||
                c == '(' || c == ')' || c == '{' || c == '}')
                break;

            if (c == '#' && _pos + 2 < _content.Length)
            {
                var hex = Encoding.ASCII.GetString(_content, _pos + 1, 2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                {
                    sb ??= new StringBuilder();
                    sb.Append(Latin1(segStart, _pos - segStart));
                    sb.Append((char)code);
                    _pos += 3;
                    segStart = _pos;
                    continue;
                }
            }

            _pos++;
        }

        // The overwhelmingly common name has no #XX escape and needs no builder
        // at all — one Latin-1 decode of the byte run, which is exactly what
        // appending `(char)b` per byte produced.
        if (sb == null)
            return new PdfName(Latin1(segStart, _pos - segStart));

        sb.Append(Latin1(segStart, _pos - segStart));
        return new PdfName(sb.ToString());
    }

    /// <summary>Latin-1 decode of a content byte run — the `(char)b` cast, in bulk.</summary>
    private string Latin1(int start, int length) =>
        length <= 0 ? string.Empty : Encoding.Latin1.GetString(_content, start, length);

    private PdfObject ParseNumber()
    {
        int start = _pos;
        bool hasDot = false;

        while (_pos < _content.Length)
        {
            var c = _content[_pos];
            if (char.IsDigit((char)c) || c == '-' || c == '+' || c == '.')
            {
                if (c == '.') hasDot = true;
                _pos++;
            }
            else
            {
                break;
            }
        }

        int length = _pos - start;

        // Numeric operands are the bulk of a content stream (TJ kerns, Td/Tm/cm
        // coordinates). A StringBuilder plus its char[] plus the finished string
        // PER NUMBER is what #600 measured and removed on the extraction side;
        // this tokenizer is now that hot path, so it parses off a stack span
        // instead. Bit-identical: the same TryParse overloads see the same
        // characters, and a parse failure still yields integer 0.
        if (length <= MaxStackNumberChars)
        {
            Span<char> buffer = stackalloc char[MaxStackNumberChars];
            for (int i = 0; i < length; i++)
                buffer[i] = (char)_content[start + i];
            return ParseNumber(buffer[..length], hasDot);
        }

        return ParseNumber(Latin1(start, length).AsSpan(), hasDot);
    }

    private const int MaxStackNumberChars = 64;

    private static PdfObject ParseNumber(ReadOnlySpan<char> text, bool hasDot)
    {
        if (hasDot)
        {
            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                return new PdfReal(d);
        }
        else
        {
            if (int.TryParse(text, out var i))
                return IntegerFor(i);
        }

        return IntegerFor(0);
    }

    private string ParseKeyword()
    {
        int start = _pos;

        while (_pos < _content.Length)
        {
            var c = _content[_pos];
            if (char.IsLetterOrDigit((char)c) || c == '\'' || c == '"' || c == '*')
                _pos++;
            else
                break;
        }

        int length = _pos - start;

        // Return the cached instance for a known token (every operator, plus the
        // three literal keywords) — value-identical to the substring, and
        // allocation-free for the case that is essentially all of them (#600).
        if (length is > 0 and <= MaxKnownKeywordChars)
        {
            Span<char> buffer = stackalloc char[MaxKnownKeywordChars];
            for (int i = 0; i < length; i++)
                buffer[i] = (char)_content[start + i];
            if (KnownKeywordLookup.TryGetValue(buffer[..length], out var known))
                return known;
        }

        return Latin1(start, length);
    }

    private const int MaxKnownKeywordChars = 8;

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

    // Span-keyed lookup returning the cached string instance for a known
    // content-stream token, so ParseKeyword does not allocate a fresh string per
    // operator (#600). Covers every operator plus the non-operator keywords that
    // legally appear as operands.
    private static readonly HashSet<string> KnownKeywords =
        new(Operators.Concat(new[] { "true", "false", "null" }), StringComparer.Ordinal);

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> KnownKeywordLookup =
        KnownKeywords.GetAlternateLookup<ReadOnlySpan<char>>();

    #endregion

    /// <summary>
    /// Graphics state (§8.4.1 Table 52), including the text parameters that
    /// <c>q</c>/<c>Q</c> save and restore (#983).
    /// </summary>
    private sealed class GraphicsState
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

        // The §8.4.1 Table 52 text-state parameters as of the `q` that pushed
        // this state; null on the live state, which never needs one. #983.
        public TextStateSnapshot? SavedTextState;

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
