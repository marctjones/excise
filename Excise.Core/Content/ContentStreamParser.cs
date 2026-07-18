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
    private Dictionary<int, double>? _cidWidths;
    private double _textLeading;
    private double _charSpacing;
    private double _wordSpacing;
    private double _horizontalScaling = 100;
    private double _textRise;
    private int _textRenderMode;

    // Text matrix
    private double _tm_a = 1, _tm_b, _tm_c, _tm_d = 1, _tm_e, _tm_f;
    private double _tlm_e, _tlm_f;

    // Current path for bounds calculation
    private double _pathMinX, _pathMinY, _pathMaxX, _pathMaxY;
    private bool _pathStarted;
    private double _currentX, _currentY;

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
        ExecuteOperator(name, operands, op);

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
                if (operands.Count >= 1)
                    _textRenderMode = (int)GetNumber(operands[0]);
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
                    var (text, bounds) = ProcessTextArray(arr);
                    op.TextContent = text;
                    op.BoundingBox = bounds;
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

        _tlm_e += tx;
        _tlm_f += ty;
        _tm_e = _tlm_e;
        _tm_f = _tlm_f;
    }

    private void MoveToNextTextLine()
    {
        _tlm_f -= _textLeading;
        _tm_e = _tlm_e;
        _tm_f = _tlm_f;
    }

    private void SetTextOperatorResult(PdfObject textObject, ContentOperator op)
    {
        var (text, bounds) = ProcessTextString(textObject);
        op.TextContent = text;
        op.BoundingBox = bounds;
    }

    #region Text Processing

    private (string text, PdfRectangle? bounds) ProcessTextString(PdfObject obj)
    {
        byte[] bytes;
        if (obj is PdfString ps)
            bytes = Encoding.Latin1.GetBytes(ps.Value);
        else
            return ("", null);

        return ProcessTextBytes(bytes);
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
                var bytes = Encoding.Latin1.GetBytes(ps.Value);
                var (text, bounds) = ProcessTextBytes(bytes);
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
                // Adjust position (negative = move right)
                var adj = pi.Value;
                _tm_e -= (adj / 1000.0) * _fontSize * (_horizontalScaling / 100.0);
            }
            else if (item is PdfReal pr)
            {
                var adj = pr.Value;
                _tm_e -= (adj / 1000.0) * _fontSize * (_horizontalScaling / 100.0);
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

        int stride = _is2ByteFont ? 2 : 1;
        for (int i = 0; i + stride <= bytes.Length; i += stride)
        {
            int charCode = _is2ByteFont
                ? (bytes[i] << 8) | bytes[i + 1]
                : bytes[i];

            var unicode = DecodeCharacter(charCode);
            var charWidth = GetCharWidth(charCode);

            // Transform position — text rise shifts the baseline vertically (§9.3.7)
            var (x, y) = TransformTextPoint(_tm_e, _tm_f + _textRise);

            // Calculate glyph dimensions
            var glyphWidth = charWidth * _fontSize * (_horizontalScaling / 100.0) / 1000.0;
            var glyphHeight = _fontSize;

            // Update bounds
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + glyphWidth);
            maxY = Math.Max(maxY, y + glyphHeight);

            sb.Append(unicode);

            // Advance text position
            var tx = (charWidth / 1000.0) * _fontSize * (_horizontalScaling / 100.0);
            tx += _charSpacing;
            if (charCode == 32) tx += _wordSpacing;

            _tm_e += tx * _tm_a;
            _tm_f += tx * _tm_b;
        }

        if (bytes.Length == 0)
            return (sb.ToString(), null);

        return (sb.ToString(), new PdfRectangle(minX, minY, maxX, maxY));
    }

    private (double x, double y) TransformTextPoint(double tx, double ty)
    {
        // Apply CTM to text position
        var x = tx * _state.Ctm_a + ty * _state.Ctm_c + _state.Ctm_e;
        var y = tx * _state.Ctm_b + ty * _state.Ctm_d + _state.Ctm_f;
        return (x, y);
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
        // mapped through the CTM, i.e. the four corners (0,0),(1,0),(1,1),(0,1))
        var b = TransformBounds(0, 0, 1, 1);
        var op = new ContentOperator("BI", new PdfObject[] { imageParams });
        op.BoundingBox = b;
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
        if (gsDict.ContainsKey("Tr") && gsDict.GetOptional("Tr") is { } trObj)
            _textRenderMode = (int)GetNumber(trObj);

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
        _cidWidths = null;

        if (_currentFont != null)
        {
            // Detect Type0 (composite) fonts
            var subtype = _currentFont.GetNameOrNull("Subtype");
            _is2ByteFont = subtype == "Type0";

            // A Type0 font isn't guaranteed to use Identity-H/V's 2-byte
            // codes — a font can wrap an embedded CMap declaring a narrower
            // codespace (#659). Only switch to 1-byte on an EXPLICITLY
            // UNIFORM 1-byte codespace; keep the safe 2-byte default for
            // Identity-H/V and any 2-byte/mixed-width codespace. Mirrors the
            // identical fix in TextExtractor.LoadFontGeometry — this parser
            // feeds redaction's glyph removal, and it must decode the same
            // bytes the same way TextExtractor does or redaction can target
            // the wrong glyphs.
            if (_is2ByteFont)
            {
                var encObj = _page.Document.Resolve(_currentFont.GetOptional("Encoding") ?? PdfNull.Instance);
                if (encObj is PdfStream encStream)
                {
                    var detail = Text.ToUnicodeCMapParser.ParseDetailed(encStream.DecodedData);
                    if (detail.CodespaceRanges.Count > 0 && detail.MaxCodeBytes == 1)
                        _is2ByteFont = false;
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
        }
    }

    private void ParseCidWidths()
    {
        if (_cidFontDict == null) return;

        _cidWidths = new Dictionary<int, double>();
        var widthsObj = _cidFontDict.GetOptional("W");

        if (widthsObj is PdfArray widths)
        {
            int i = 0;
            while (i < widths.Count)
            {
                var first = widths[i];
                if (first is not (PdfInteger or PdfReal)) { i++; continue; }

                var firstCid = (int)GetNumber(first);
                i++;

                if (i >= widths.Count) break;

                var second = widths[i];

                if (second is PdfArray cidWidthArray)
                {
                    // Format: c [w1 w2 w3 ...]
                    for (int j = 0; j < cidWidthArray.Count; j++)
                    {
                        _cidWidths[firstCid + j] = GetNumber(cidWidthArray[j]);
                    }
                    i++;
                }
                else if (second is PdfInteger or PdfReal)
                {
                    // Format: c1 c2 w
                    var lastCid = (int)GetNumber(second);
                    i++;

                    if (i >= widths.Count) break;

                    var width = GetNumber(widths[i]);
                    for (int cid = firstCid; cid <= lastCid; cid++)
                    {
                        _cidWidths[cid] = width;
                    }
                    i++;
                }
                else
                {
                    i++;
                }
            }
        }
    }

    private string DecodeCharacter(int charCode)
    {
        if (_toUnicodeMap != null && _toUnicodeMap.TryGetValue(charCode, out var unicode))
            return unicode;

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

    private double GetCharWidth(int charCode)
    {
        // Check CID width table for Type0 fonts first
        if (_cidWidths != null && _cidWidths.TryGetValue(charCode, out var cidWidth))
            return cidWidth;

        if (_cidFontDict != null)
            return _cidFontDict.GetNumber("DW", 1000);

        if (_currentFont != null)
        {
            var widthsObj = _currentFont.GetOptional("Widths");
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
        _currentX = x;
        _currentY = y;
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
        _currentX = x;
        _currentY = y;
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
        var sb = new StringBuilder();
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
                    case (byte)'n': sb.Append('\n'); break;
                    case (byte)'r': sb.Append('\r'); break;
                    case (byte)'t': sb.Append('\t'); break;
                    case (byte)'b': sb.Append('\b'); break;
                    case (byte)'f': sb.Append('\f'); break;
                    case (byte)'(': sb.Append('('); break;
                    case (byte)')': sb.Append(')'); break;
                    case (byte)'\\': sb.Append('\\'); break;
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
                            var octal = new StringBuilder();
                            octal.Append((char)escaped);
                            while (octal.Length < 3 && _pos + 1 < _content.Length &&
                                   _content[_pos + 1] >= '0' && _content[_pos + 1] <= '7')
                            {
                                _pos++;
                                octal.Append((char)_content[_pos]);
                            }
                            var code = Convert.ToInt32(octal.ToString(), 8);
                            sb.Append((char)code);
                        }
                        else
                        {
                            sb.Append((char)escaped);
                        }
                        break;
                }
            }
            else if (c == '(')
            {
                depth++;
                sb.Append((char)c);
            }
            else if (c == ')')
            {
                depth--;
                if (depth > 0) sb.Append((char)c);
            }
            else
            {
                sb.Append((char)c);
            }
            _pos++;
        }

        return new PdfString(sb.ToString());
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

    private void SkipDictionary()
    {
        int depth = 1;
        while (_pos < _content.Length && depth > 0)
        {
            if (_pos + 1 < _content.Length)
            {
                if (_content[_pos] == '<' && _content[_pos + 1] == '<')
                {
                    depth++;
                    _pos += 2;
                    continue;
                }
                if (_content[_pos] == '>' && _content[_pos + 1] == '>')
                {
                    depth--;
                    _pos += 2;
                    continue;
                }
            }
            _pos++;
        }
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
