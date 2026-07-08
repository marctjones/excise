using System.Text;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Text;

/// <summary>
/// Extracts text and letter information from PDF pages.
/// </summary>
public class TextExtractor
{
    private readonly PdfPage _page;
    private readonly byte[] _contentStream;

    // Text state
    private double _fontSize = 12;
    private string _fontName = "";
    private PdfDictionary? _currentFont;
    private Dictionary<int, string>? _toUnicodeMap;
    private double _textLeading = 0;
    private double _charSpacing = 0;
    private double _wordSpacing = 0;
    private double _horizontalScaling = 100;
    // Type 0 / CID font state (§9.7)
    private bool _is2ByteFont;
    private bool _isVerticalWriting;
    private Dictionary<int, double>? _cidWidths;
    private double _cidDefaultWidth = 1000;

    // Text matrix (position + transformation)
    private double _tm_a = 1, _tm_b = 0, _tm_c = 0, _tm_d = 1, _tm_e = 0, _tm_f = 0;

    // Line matrix (start of line position)
    private double _tlm_e = 0, _tlm_f = 0;

    // Graphics state stack
    private readonly Stack<GraphicsState> _stateStack = new();
    private double _ctm_a = 1, _ctm_b = 0, _ctm_c = 0, _ctm_d = 1, _ctm_e = 0, _ctm_f = 0;
    private readonly Stack<PdfDictionary> _resourcesStack = new();
    private readonly HashSet<PdfStream> _formXObjectStack = new();
    private readonly Stack<bool> _optionalContentHiddenStack = new();
    private int _formXObjectDepth;
    private const int MaxFormXObjectDepth = 64;

    private readonly List<Letter> _letters = new();

    public TextExtractor(PdfPage page)
    {
        _page = page;
        _contentStream = page.GetContentStreamBytes();
        if (page.Resources != null)
            _resourcesStack.Push(page.Resources);
    }

    /// <summary>
    /// When true, AcroForm field values whose widget is on this page are
    /// emitted as synthetic Letters in addition to the content-stream text.
    /// This makes form-fill values visible to search, text extraction, and
    /// — critically — redaction. Defaults to true; the only reason to turn
    /// it off is to inspect raw content-stream output for diagnostics.
    /// </summary>
    public bool IncludeFormFieldValues { get; set; } = true;

    /// <summary>
    /// Extract all letters from the page.
    /// </summary>
    public IReadOnlyList<Letter> ExtractLetters()
    {
        _letters.Clear();
        ParseContentStream();
        if (IncludeFormFieldValues)
            EmitFormFieldLetters();
        return _letters.AsReadOnly();
    }

    /// <summary>
    /// Walk the page's AcroForm fields and emit synthetic Letters for any with
    /// a string value (/V) and a widget rectangle. Positions are an estimate
    /// based on the widget rect — the real glyph layout would require parsing
    /// the field's appearance stream, which the read-only AcroForm slice
    /// deliberately defers. For search and redaction the rect-based positions
    /// are precise enough: the redaction rectangle still encloses the value,
    /// and search just needs the text content present.
    /// </summary>
    private void EmitFormFieldLetters()
    {
        IReadOnlyList<PdfField> fields;
        try { fields = _page.GetFormFields(); }
        catch (Exception __ex) when (__ex is not OutOfMemoryException) { return; }

        foreach (var field in fields)
        {
            if (field.Rect == null) continue;
            var value = field.Value ?? field.DefaultValue;
            if (string.IsNullOrEmpty(value)) continue;
            // Buttons are off/on/checked/unchecked names — not human-readable text.
            if (field.FieldType == PdfFieldType.Button) continue;
            // Signatures don't render text in the field; skip.
            if (field.FieldType == PdfFieldType.Signature) continue;

            EmitLettersInRect(value, field.Rect.Value, $"AcroForm:{field.FieldType}");
        }
    }

    private void EmitLettersInRect(string text, PdfRectangle rect, string fontName)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var fontSize = Math.Min(rect.Height * 0.85, 12.0);
        if (fontSize <= 0) return;

        // Approximation: assume average glyph advance of 0.55em. Real PDFs
        // vary, but for search/redaction we just need to land letters within
        // the widget's rect.
        var advance = fontSize * 0.55;
        var maxChars = (int)Math.Floor(rect.Width / advance);
        if (maxChars <= 0) return;

        // Truncate so we never paint outside the widget rect.
        if (text.Length > maxChars) text = text.Substring(0, maxChars);

        var x = rect.Left;
        var baselineY = rect.Bottom + (rect.Height - fontSize) * 0.5;

        foreach (var ch in text)
        {
            var bbox = new PdfRectangle(x, baselineY, x + advance, baselineY + fontSize);
            _letters.Add(new Letter(
                ch.ToString(),
                bbox,
                fontSize,
                fontName,
                x,
                baselineY,
                advance,
                ch));
            x += advance;
        }
    }

    /// <summary>
    /// Extract plain text from the page.
    /// </summary>
    public string ExtractText()
    {
        var letters = ExtractLetters();
        var sb = new StringBuilder();
        foreach (var letter in letters)
        {
            sb.Append(letter.Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extract words from the page. Words are sequences of letters
    /// separated by whitespace or large gaps.
    /// </summary>
    public IReadOnlyList<Word> ExtractWords()
    {
        var letters = ExtractLetters();
        return BuildWords(letters);
    }

    internal static IReadOnlyList<Word> BuildWords(IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0)
            return Array.Empty<Word>();

        var words = new List<Word>();
        var currentWordLetters = new List<Letter>();

        // Threshold for word separation (in points)
        // Typical space width is ~3-4 points at 12pt font
        const double wordGapThreshold = 3.0;
        const double lineGapThreshold = 5.0;

        Letter? prevLetter = null;

        foreach (var letter in letters)
        {
            bool startNewWord = false;

            if (prevLetter != null)
            {
                // Check for line break
                var yDiff = Math.Abs(letter.StartY - prevLetter.StartY);
                if (yDiff > lineGapThreshold)
                {
                    startNewWord = true;
                }
                else
                {
                    // Check for horizontal gap
                    var gap = letter.GlyphRectangle.Left - prevLetter.GlyphRectangle.Right;
                    if (gap > wordGapThreshold)
                    {
                        startNewWord = true;
                    }
                }
            }

            // Check if letter is whitespace
            if (letter.Value.Length == 1 && char.IsWhiteSpace(letter.Value[0]))
            {
                // Don't add whitespace to words, but end current word
                if (currentWordLetters.Count > 0)
                {
                    words.Add(new Word(currentWordLetters.ToArray()));
                    currentWordLetters.Clear();
                }
                prevLetter = letter;
                continue;
            }

            if (startNewWord && currentWordLetters.Count > 0)
            {
                words.Add(new Word(currentWordLetters.ToArray()));
                currentWordLetters.Clear();
            }

            currentWordLetters.Add(letter);
            prevLetter = letter;
        }

        // Don't forget the last word
        if (currentWordLetters.Count > 0)
        {
            words.Add(new Word(currentWordLetters.ToArray()));
        }

        return words;
    }

    private void ParseContentStream()
    {
        ParseContentBytes(_contentStream);
    }

    private void ParseContentBytes(byte[] contentBytes)
    {
        var content = Encoding.Latin1.GetString(contentBytes);
        var pos = 0;
        var operands = new List<object>();

        while (pos < content.Length)
        {
            SkipWhitespaceAndComments(content, ref pos);
            if (pos >= content.Length) break;

            // Try to parse a token
            var token = ParseToken(content, ref pos);
            if (token == null) continue; // Skip null tokens (like dictionaries) but keep parsing

            if (token is string op && IsOperator(op))
            {
                ExecuteOperator(op, operands);
                operands.Clear();
            }
            else
            {
                operands.Add(token);
            }
        }
    }

    private void SkipWhitespaceAndComments(string content, ref int pos)
    {
        while (pos < content.Length)
        {
            var c = content[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
            }
            else if (c == '%')
            {
                // Skip comment to end of line
                while (pos < content.Length && content[pos] != '\n' && content[pos] != '\r')
                    pos++;
            }
            else
            {
                break;
            }
        }
    }

    private object? ParseToken(string content, ref int pos)
    {
        if (pos >= content.Length) return null;

        var c = content[pos];

        // String literal
        if (c == '(')
        {
            return ParseStringLiteral(content, ref pos);
        }

        // Hex string
        if (c == '<')
        {
            if (pos + 1 < content.Length && content[pos + 1] == '<')
            {
                // Dictionary - skip for now
                pos += 2;
                SkipDictionary(content, ref pos);
                return null;
            }
            return ParseHexString(content, ref pos);
        }

        // Array
        if (c == '[')
        {
            return ParseArray(content, ref pos);
        }

        // Name
        if (c == '/')
        {
            return ParseName(content, ref pos);
        }

        // Number or operator
        if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
        {
            return ParseNumber(content, ref pos);
        }

        // Keyword/operator
        if (char.IsLetter(c) || c == '\'' || c == '"' || c == '*')
        {
            return ParseKeyword(content, ref pos);
        }

        // Skip unknown
        pos++;
        return null;
    }

    private byte[] ParseStringLiteral(string content, ref int pos)
    {
        var bytes = new List<byte>();
        pos++; // Skip opening '('
        int parenDepth = 1;

        while (pos < content.Length && parenDepth > 0)
        {
            var c = content[pos];

            if (c == '\\' && pos + 1 < content.Length)
            {
                pos++;
                var escaped = content[pos];
                switch (escaped)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add((byte)'\b'); break;
                    case 'f': bytes.Add((byte)'\f'); break;
                    case '(': bytes.Add((byte)'('); break;
                    case ')': bytes.Add((byte)')'); break;
                    case '\\': bytes.Add((byte)'\\'); break;
                    default:
                        // Octal escape
                        if (escaped >= '0' && escaped <= '7')
                        {
                            var octal = new StringBuilder();
                            octal.Append(escaped);
                            while (octal.Length < 3 && pos + 1 < content.Length &&
                                   content[pos + 1] >= '0' && content[pos + 1] <= '7')
                            {
                                pos++;
                                octal.Append(content[pos]);
                            }
                            bytes.Add((byte)Convert.ToInt32(octal.ToString(), 8));
                        }
                        else
                        {
                            bytes.Add((byte)escaped);
                        }
                        break;
                }
            }
            else if (c == '(')
            {
                parenDepth++;
                bytes.Add((byte)c);
            }
            else if (c == ')')
            {
                parenDepth--;
                if (parenDepth > 0)
                    bytes.Add((byte)c);
            }
            else
            {
                bytes.Add((byte)c);
            }
            pos++;
        }

        return bytes.ToArray();
    }

    private byte[] ParseHexString(string content, ref int pos)
    {
        pos++; // Skip '<'
        var hex = new StringBuilder();

        while (pos < content.Length && content[pos] != '>')
        {
            var c = content[pos];
            if (char.IsLetterOrDigit(c))
                hex.Append(c);
            pos++;
        }
        pos++; // Skip '>'

        var hexStr = hex.ToString();
        if (hexStr.Length % 2 == 1)
            hexStr += "0"; // Pad with 0 if odd length

        var bytes = new byte[hexStr.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hexStr.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    private List<object> ParseArray(string content, ref int pos)
    {
        var result = new List<object>();
        pos++; // Skip '['

        while (pos < content.Length)
        {
            SkipWhitespaceAndComments(content, ref pos);
            if (pos >= content.Length || content[pos] == ']')
            {
                pos++;
                break;
            }

            var item = ParseToken(content, ref pos);
            if (item != null)
                result.Add(item);
        }

        return result;
    }

    private string ParseName(string content, ref int pos)
    {
        pos++; // Skip '/'
        var sb = new StringBuilder();

        while (pos < content.Length)
        {
            var c = content[pos];
            if (char.IsWhiteSpace(c) || c == '/' || c == '[' || c == ']' ||
                c == '<' || c == '>' || c == '(' || c == ')' || c == '{' || c == '}')
                break;

            // Handle #XX hex escape
            if (c == '#' && pos + 2 < content.Length)
            {
                var hex = content.Substring(pos + 1, 2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                {
                    sb.Append((char)code);
                    pos += 3;
                    continue;
                }
            }

            sb.Append(c);
            pos++;
        }

        return "/" + sb.ToString();
    }

    private object ParseNumber(string content, ref int pos)
    {
        var sb = new StringBuilder();

        while (pos < content.Length)
        {
            var c = content[pos];
            if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
            {
                sb.Append(c);
                pos++;
            }
            else
            {
                break;
            }
        }

        var str = sb.ToString();
        if (str.Contains('.'))
        {
            return double.TryParse(str, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        }
        return int.TryParse(str, out var i) ? i : 0;
    }

    private string ParseKeyword(string content, ref int pos)
    {
        var sb = new StringBuilder();

        while (pos < content.Length)
        {
            var c = content[pos];
            if (char.IsLetterOrDigit(c) || c == '\'' || c == '"' || c == '*')
            {
                sb.Append(c);
                pos++;
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }

    private void SkipDictionary(string content, ref int pos)
    {
        int depth = 1;
        while (pos < content.Length && depth > 0)
        {
            if (pos + 1 < content.Length)
            {
                if (content[pos] == '<' && content[pos + 1] == '<')
                {
                    depth++;
                    pos += 2;
                    continue;
                }
                if (content[pos] == '>' && content[pos + 1] == '>')
                {
                    depth--;
                    pos += 2;
                    continue;
                }
            }
            pos++;
        }
    }

    private static readonly HashSet<string> Operators = new()
    {
        // Text state
        "BT", "ET", "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts",
        // Text positioning
        "Td", "TD", "Tm", "T*",
        // Text showing
        "Tj", "TJ", "'", "\"",
        // Graphics state
        "q", "Q", "cm",
        // Path and other
        "m", "l", "c", "v", "y", "h", "re",
        "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n", "W", "W*",
        "Do", "BI", "ID", "EI",
        "gs", "CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg", "K", "k",
        "d", "i", "j", "J", "M", "ri", "w",
        "BDC", "BMC", "EMC", "BX", "EX", "DP", "MP"
    };

    private static bool IsOperator(string token)
    {
        return Operators.Contains(token);
    }

    private void ExecuteOperator(string op, List<object> operands)
    {
        switch (op)
        {
            case "BT": // Begin text
                _tm_a = 1; _tm_b = 0; _tm_c = 0; _tm_d = 1; _tm_e = 0; _tm_f = 0;
                _tlm_e = 0; _tlm_f = 0;
                break;

            case "ET": // End text
                break;

            case "Tf": // Set font and size: fontName fontSize Tf
                if (operands.Count >= 2)
                {
                    _fontName = operands[0] is string name ? name.TrimStart('/') : "";
                    _fontSize = ToDouble(operands[1]);
                    _currentFont = ResolveFontFromActiveResources(_fontName);
                    _toUnicodeMap = LoadToUnicodeMap(_currentFont);
                    LoadFontGeometry();
                }
                break;

            case "Td": // Move to next line: tx ty Td
                if (operands.Count >= 2)
                {
                    var tx = ToDouble(operands[0]);
                    var ty = ToDouble(operands[1]);
                    _tlm_e += tx;
                    _tlm_f += ty;
                    _tm_e = _tlm_e;
                    _tm_f = _tlm_f;
                }
                break;

            case "TD": // Move to next line and set leading: tx ty TD
                if (operands.Count >= 2)
                {
                    var tx = ToDouble(operands[0]);
                    var ty = ToDouble(operands[1]);
                    _textLeading = -ty;
                    _tlm_e += tx;
                    _tlm_f += ty;
                    _tm_e = _tlm_e;
                    _tm_f = _tlm_f;
                }
                break;

            case "Tm": // Set text matrix: a b c d e f Tm
                if (operands.Count >= 6)
                {
                    _tm_a = ToDouble(operands[0]);
                    _tm_b = ToDouble(operands[1]);
                    _tm_c = ToDouble(operands[2]);
                    _tm_d = ToDouble(operands[3]);
                    _tm_e = ToDouble(operands[4]);
                    _tm_f = ToDouble(operands[5]);
                    _tlm_e = _tm_e;
                    _tlm_f = _tm_f;
                }
                break;

            case "T*": // Move to start of next line
                _tlm_f -= _textLeading;
                _tm_e = _tlm_e;
                _tm_f = _tlm_f;
                break;

            case "TL": // Set text leading
                if (operands.Count >= 1)
                    _textLeading = ToDouble(operands[0]);
                break;

            case "Tc": // Set character spacing
                if (operands.Count >= 1)
                    _charSpacing = ToDouble(operands[0]);
                break;

            case "Tw": // Set word spacing
                if (operands.Count >= 1)
                    _wordSpacing = ToDouble(operands[0]);
                break;

            case "Tz": // Set horizontal scaling
                if (operands.Count >= 1)
                    _horizontalScaling = ToDouble(operands[0]);
                break;

            case "Tj": // Show text string
                if (operands.Count >= 1)
                {
                    if (operands[0] is string str)
                        ShowText(str);
                    else if (operands[0] is byte[] bytes)
                        ShowText(bytes);
                }
                break;

            case "TJ": // Show text with positioning
                if (operands.Count >= 1 && operands[0] is List<object> array)
                {
                    foreach (var item in array)
                    {
                        if (item is string str)
                            ShowText(str);
                        else if (item is byte[] bytes)
                            ShowText(bytes);
                        else if (item is int or double)
                        {
                            // Adjust position: negative = move right, positive = move left
                            var adj = ToDouble(item);
                            _tm_e -= (adj / 1000.0) * _fontSize * (_horizontalScaling / 100.0);
                        }
                    }
                }
                break;

            case "'": // Move to next line and show text
                _tlm_f -= _textLeading;
                _tm_e = _tlm_e;
                _tm_f = _tlm_f;
                if (operands.Count >= 1)
                {
                    if (operands[0] is string str)
                        ShowText(str);
                    else if (operands[0] is byte[] bytes)
                        ShowText(bytes);
                }
                break;

            case "\"": // Set word and char spacing, move to next line, show text
                if (operands.Count >= 3)
                {
                    _wordSpacing = ToDouble(operands[0]);
                    _charSpacing = ToDouble(operands[1]);
                    _tlm_f -= _textLeading;
                    _tm_e = _tlm_e;
                    _tm_f = _tlm_f;
                    if (operands[2] is string str)
                        ShowText(str);
                    else if (operands[2] is byte[] bytes)
                        ShowText(bytes);
                }
                break;

            case "q": // Save graphics state
                _stateStack.Push(new GraphicsState
                {
                    ctm_a = _ctm_a, ctm_b = _ctm_b, ctm_c = _ctm_c, ctm_d = _ctm_d, ctm_e = _ctm_e, ctm_f = _ctm_f
                });
                break;

            case "Q": // Restore graphics state
                if (_stateStack.Count > 0)
                {
                    var state = _stateStack.Pop();
                    _ctm_a = state.ctm_a; _ctm_b = state.ctm_b; _ctm_c = state.ctm_c;
                    _ctm_d = state.ctm_d; _ctm_e = state.ctm_e; _ctm_f = state.ctm_f;
                }
                break;

            case "cm": // Modify current transformation matrix
                if (operands.Count >= 6)
                {
                    ConcatenateCtm(
                        ToDouble(operands[0]),
                        ToDouble(operands[1]),
                        ToDouble(operands[2]),
                        ToDouble(operands[3]),
                        ToDouble(operands[4]),
                        ToDouble(operands[5]));
                }
                break;

            case "Do":
                if (operands.Count >= 1 && operands[0] is string xObjectName)
                    ExtractFormXObjectText(xObjectName.TrimStart('/'));
                break;

            case "BDC":
                _optionalContentHiddenStack.Push(IsHiddenOptionalContentSpan(operands));
                break;

            case "BMC":
                _optionalContentHiddenStack.Push(false);
                break;

            case "EMC":
                if (_optionalContentHiddenStack.Count > 0)
                    _optionalContentHiddenStack.Pop();
                break;
        }
    }

    private bool IsHiddenOptionalContentSpan(List<object> operands)
    {
        if (operands.Count < 2)
            return false;

        if (operands[0] is not string tag || tag != "/OC")
            return false;

        if (operands[1] is not string propertyName)
            return false;

        return IsHiddenOptionalContentProperty(propertyName.TrimStart('/'));
    }

    private bool IsHiddenOptionalContentProperty(string propertyName)
    {
        foreach (var resources in _resourcesStack)
        {
            var propertiesObj = resources.GetOptional("Properties");
            if (propertiesObj == null)
                continue;

            if (_page.Document.Resolve(propertiesObj) is not PdfDictionary properties)
                continue;

            var propertyObj = properties.GetOptional(propertyName);
            if (propertyObj == null)
                continue;

            if (IsHiddenOptionalContentObject(propertyObj))
                return true;
        }

        return false;
    }

    private bool IsHiddenOptionalContentObject(PdfObject obj)
    {
        PdfObject resolved;
        try { resolved = _page.Document.Resolve(obj); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }

        if (resolved is PdfDictionary dict && dict.GetNameOrNull("Type") == "OCG")
        {
            var name = dict.GetOptional("Name") switch
            {
                PdfString s => s.Value,
                PdfName n => n.Value,
                _ => null
            };

            return !string.IsNullOrEmpty(name) &&
                   _page.Document.GetOptionalContentGroupConfig().OffByDefault.Contains(name);
        }

        if (resolved is PdfName directName)
            return _page.Document.GetOptionalContentGroupConfig().OffByDefault.Contains(directName.Value);

        return false;
    }

    private void ConcatenateCtm(double a, double b, double c, double d, double e, double f)
    {
        // Multiply: CTM = new_matrix * CTM
        var na = a * _ctm_a + b * _ctm_c;
        var nb = a * _ctm_b + b * _ctm_d;
        var nc = c * _ctm_a + d * _ctm_c;
        var nd = c * _ctm_b + d * _ctm_d;
        var ne = e * _ctm_a + f * _ctm_c + _ctm_e;
        var nf = e * _ctm_b + f * _ctm_d + _ctm_f;

        _ctm_a = na; _ctm_b = nb; _ctm_c = nc;
        _ctm_d = nd; _ctm_e = ne; _ctm_f = nf;
    }

    private void ExtractFormXObjectText(string name)
    {
        if (_formXObjectDepth >= MaxFormXObjectDepth)
            return;

        var xObject = ResolveXObjectFromActiveResources(name);
        if (xObject is not PdfStream stream || stream.GetNameOrNull("Subtype") != "Form")
            return;

        if (!_formXObjectStack.Add(stream))
            return;

        _formXObjectDepth++;
        var savedCtm = (_ctm_a, _ctm_b, _ctm_c, _ctm_d, _ctm_e, _ctm_f);
        var savedTextState = (
            _fontSize,
            _fontName,
            _currentFont,
            _toUnicodeMap,
            _textLeading,
            _charSpacing,
            _wordSpacing,
            _horizontalScaling,
            _is2ByteFont,
            _isVerticalWriting,
            _cidWidths,
            _cidDefaultWidth,
            _tm_a,
            _tm_b,
            _tm_c,
            _tm_d,
            _tm_e,
            _tm_f,
            _tlm_e,
            _tlm_f);
        var pushedResources = false;

        try
        {
            if (TryReadMatrix(stream.GetOptional("Matrix"), out var matrix))
                ConcatenateCtm(matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f);

            var resourcesObj = stream.GetOptional("Resources");
            if (resourcesObj != null &&
                _page.Document.Resolve(resourcesObj) is PdfDictionary resources)
            {
                _resourcesStack.Push(resources);
                pushedResources = true;
            }

            ParseContentBytes(stream.DecodedData);
        }
        finally
        {
            if (pushedResources)
                _resourcesStack.Pop();
            (_ctm_a, _ctm_b, _ctm_c, _ctm_d, _ctm_e, _ctm_f) = savedCtm;
            (
                _fontSize,
                _fontName,
                _currentFont,
                _toUnicodeMap,
                _textLeading,
                _charSpacing,
                _wordSpacing,
                _horizontalScaling,
                _is2ByteFont,
                _isVerticalWriting,
                _cidWidths,
                _cidDefaultWidth,
                _tm_a,
                _tm_b,
                _tm_c,
                _tm_d,
                _tm_e,
                _tm_f,
                _tlm_e,
                _tlm_f) = savedTextState;
            _formXObjectDepth--;
            _formXObjectStack.Remove(stream);
        }
    }

    private PdfDictionary? ResolveFontFromActiveResources(string fontName)
    {
        foreach (var resources in _resourcesStack)
        {
            var fontsObj = resources.GetOptional("Font");
            if (fontsObj == null) continue;
            if (_page.Document.Resolve(fontsObj) is not PdfDictionary fonts)
                continue;
            var fontObj = fonts.GetOptional(fontName);
            if (fontObj == null) continue;
            return _page.Document.Resolve(fontObj) as PdfDictionary;
        }

        return _page.GetFont(fontName);
    }

    private PdfObject? ResolveXObjectFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            var xObjectsObj = resources.GetOptional("XObject");
            if (xObjectsObj == null) continue;
            if (_page.Document.Resolve(xObjectsObj) is not PdfDictionary xObjects)
                continue;
            var xObject = xObjects.GetOptional(name);
            if (xObject == null) continue;
            return _page.Document.Resolve(xObject);
        }

        return _page.GetXObject(name);
    }

    private static bool TryReadMatrix(PdfObject? matrixObj, out (double a, double b, double c, double d, double e, double f) matrix)
    {
        matrix = (1, 0, 0, 1, 0, 0);
        if (matrixObj is not PdfArray array || array.Count < 6)
            return false;

        if (!TryNumber(array[0], out var a) ||
            !TryNumber(array[1], out var b) ||
            !TryNumber(array[2], out var c) ||
            !TryNumber(array[3], out var d) ||
            !TryNumber(array[4], out var e) ||
            !TryNumber(array[5], out var f))
        {
            return false;
        }

        matrix = (a, b, c, d, e, f);
        return true;
    }

    private void ShowText(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        ShowText(bytes);
    }

    private void ShowText(byte[] bytes)
    {
        // Type 0 / composite fonts use multi-byte source codes. The descendant
        // CIDFont's encoding (or the outer ToUnicode CMap) tells us how many
        // bytes per code; in practice every modern producer uses 2 bytes for
        // Identity-H/V, so we treat the font as 2-byte if the Tf-loaded font
        // has Subtype /Type0. Simple fonts (Type1/TrueType not wrapped in a
        // Type 0 / CIDFont) stay 1-byte.
        int stride = _is2ByteFont ? 2 : 1;

        for (int i = 0; i + stride <= bytes.Length; i += stride)
        {
            int charCode = stride == 2
                ? (bytes[i] << 8) | bytes[i + 1]
                : bytes[i];

            var unicode = DecodeCharacter(charCode);
            var charWidth = GetCharWidth(charCode);

            // Calculate position in user space
            var (x, y) = TransformPoint(_tm_e, _tm_f);

            // Estimate glyph dimensions
            var glyphWidth = charWidth * _fontSize * (_horizontalScaling / 100.0) / 1000.0;
            var glyphHeight = _fontSize;

            // Create bounding box
            var bbox = new PdfRectangle(x, y, x + glyphWidth, y + glyphHeight);

            var letter = new Letter(
                unicode,
                bbox,
                _fontSize,
                _fontName,
                x,
                y,
                glyphWidth,
                charCode,
                stride // 1 for simple fonts, 2 for Type0/CID (#353)
            )
            {
                IsInHiddenOptionalContent = _optionalContentHiddenStack.Any(hidden => hidden)
            };
            _letters.Add(letter);

            // Advance text position. For vertical writing (WMode 1) the
            // displacement is along the y-axis; horizontal is along x. We don't
            // model glyph-specific vertical metrics yet, so vertical advance
            // uses the same width-as-displacement assumption.
            var displacement = (charWidth / 1000.0) * _fontSize * (_horizontalScaling / 100.0);
            displacement += _charSpacing;
            if (charCode == 32) // Space — also fire wordSpacing per §9.3.3
                displacement += _wordSpacing;

            if (_isVerticalWriting)
            {
                _tm_e += displacement * _tm_c;
                _tm_f += displacement * _tm_d;
            }
            else
            {
                _tm_e += displacement * _tm_a;
                _tm_f += displacement * _tm_b;
            }
        }
    }

    /// <summary>
    /// After Tf loads a font, update Type 0 / CID-specific state: 2-byte stride,
    /// vertical writing mode, default CID width, and per-CID width table.
    /// </summary>
    private void LoadFontGeometry()
    {
        _is2ByteFont = false;
        _isVerticalWriting = false;
        _cidWidths = null;
        _cidDefaultWidth = 1000;

        if (_currentFont == null) return;

        var subtype = _currentFont.GetNameOrNull("Subtype");
        if (subtype != "Type0") return;

        _is2ByteFont = true;

        // /Encoding can be a name (Identity-H/V) or a CMap stream. Identity-V
        // and any /WMode 1 in a custom CMap means vertical writing.
        var encObj = _currentFont.GetOptional("Encoding");
        if (encObj is PdfName encName && encName.Value == "Identity-V")
            _isVerticalWriting = true;

        // Resolve descendant CIDFont (always exactly one entry, per §9.7.6.1).
        var descendantsObj = _currentFont.GetOptional("DescendantFonts");
        if (descendantsObj == null) return;
        var descendantsResolved = _page.Document.Resolve(descendantsObj);
        if (descendantsResolved is not PdfArray descendants || descendants.Count == 0) return;

        var firstResolved = _page.Document.Resolve(descendants[0]);
        if (firstResolved is not PdfDictionary cidFont) return;

        // Default width (DW) — applied to any CID not in the W table (§9.7.4.3).
        if (cidFont.GetOptional("DW") is { } dwObj && TryNumber(dwObj, out var dwVal))
            _cidDefaultWidth = dwVal;

        // /W = [c [w1 w2 …]] | [c1 c2 w] mixed array
        if (cidFont.GetOptional("W") is { } wObj &&
            _page.Document.Resolve(wObj) is PdfArray w)
        {
            _cidWidths = new Dictionary<int, double>();
            int idx = 0;
            while (idx < w.Count)
            {
                if (!TryNumber(w[idx], out var firstNum)) { idx++; continue; }
                int firstCid = (int)firstNum;

                if (idx + 1 < w.Count && _page.Document.Resolve(w[idx + 1]) is PdfArray inner)
                {
                    for (int k = 0; k < inner.Count; k++)
                        if (TryNumber(inner[k], out var width))
                            _cidWidths[firstCid + k] = width;
                    idx += 2;
                }
                else if (idx + 2 < w.Count
                         && TryNumber(w[idx + 1], out var lastNum)
                         && TryNumber(w[idx + 2], out var widthAll))
                {
                    int lastCid = (int)lastNum;
                    for (int c = firstCid; c <= lastCid; c++)
                        _cidWidths[c] = widthAll;
                    idx += 3;
                }
                else
                {
                    idx++;
                }
            }
        }
    }

    private static bool TryNumber(PdfObject? obj, out double v)
    {
        switch (obj)
        {
            case PdfInteger i: v = i.Value; return true;
            case PdfReal r:    v = r.Value; return true;
            default:           v = 0; return false;
        }
    }

    private (double x, double y) TransformPoint(double tx, double ty)
    {
        // Apply text matrix
        var x1 = tx;
        var y1 = ty;

        // Apply CTM
        var x2 = x1 * _ctm_a + y1 * _ctm_c + _ctm_e;
        var y2 = x1 * _ctm_b + y1 * _ctm_d + _ctm_f;

        return (x2, y2);
    }

    private Dictionary<int, string>? LoadToUnicodeMap(PdfDictionary? font)
    {
        if (font == null)
            return null;

        var toUnicodeObj = font.GetOptional("ToUnicode");
        if (toUnicodeObj == null)
            return null;

        // Resolve the reference
        var resolved = _page.Document.Resolve(toUnicodeObj);
        if (resolved is not PdfStream stream)
            return null;

        try
        {
            return ToUnicodeCMapParser.Parse(stream.DecodedData);
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            // If CMap parsing fails, fall back to encoding
            return null;
        }
    }

    private string DecodeCharacter(int charCode)
    {
        // First, check ToUnicode map (highest priority)
        if (_toUnicodeMap != null && _toUnicodeMap.TryGetValue(charCode, out var unicode))
        {
            return unicode;
        }

        // Check for encoding in font dictionary
        if (_currentFont != null)
        {
            var encoding = _currentFont.GetOptional("Encoding");
            if (encoding is PdfName encName)
            {
                var encNameStr = encName.Value;
                if (encNameStr == "WinAnsiEncoding")
                    return DecodeWinAnsi(charCode);
                if (encNameStr == "MacRomanEncoding")
                    return DecodeMacRoman(charCode);
            }
        }

        // Default: assume WinAnsiEncoding for Type1 fonts with standard base fonts
        return DecodeWinAnsi(charCode);
    }

    private static string DecodeWinAnsi(int charCode)
    {
        // WinAnsiEncoding (Windows Code Page 1252)
        // Most chars map directly, special handling for 128-159
        if (charCode < 128 || charCode >= 160)
            return ((char)charCode).ToString();

        // Special mappings for 128-159
        return charCode switch
        {
            128 => "\u20AC", // Euro sign
            130 => "\u201A", // Single low-9 quotation mark
            131 => "\u0192", // Latin small letter f with hook
            132 => "\u201E", // Double low-9 quotation mark
            133 => "\u2026", // Horizontal ellipsis
            134 => "\u2020", // Dagger
            135 => "\u2021", // Double dagger
            136 => "\u02C6", // Modifier letter circumflex accent
            137 => "\u2030", // Per mille sign
            138 => "\u0160", // Latin capital letter S with caron
            139 => "\u2039", // Single left-pointing angle quotation mark
            140 => "\u0152", // Latin capital ligature OE
            142 => "\u017D", // Latin capital letter Z with caron
            145 => "\u2018", // Left single quotation mark
            146 => "\u2019", // Right single quotation mark
            147 => "\u201C", // Left double quotation mark
            148 => "\u201D", // Right double quotation mark
            149 => "\u2022", // Bullet
            150 => "\u2013", // En dash
            151 => "\u2014", // Em dash
            152 => "\u02DC", // Small tilde
            153 => "\u2122", // Trade mark sign
            154 => "\u0161", // Latin small letter s with caron
            155 => "\u203A", // Single right-pointing angle quotation mark
            156 => "\u0153", // Latin small ligature oe
            158 => "\u017E", // Latin small letter z with caron
            159 => "\u0178", // Latin capital letter Y with diaeresis
            _ => ((char)charCode).ToString()
        };
    }

    private static string DecodeMacRoman(int charCode)
    {
        // MacRomanEncoding - simplified, handle special chars 128-255
        if (charCode < 128)
            return ((char)charCode).ToString();

        // Mac Roman special characters (subset)
        return charCode switch
        {
            128 => "\u00C4", // Ä
            129 => "\u00C5", // Å
            130 => "\u00C7", // Ç
            131 => "\u00C9", // É
            132 => "\u00D1", // Ñ
            133 => "\u00D6", // Ö
            134 => "\u00DC", // Ü
            135 => "\u00E1", // á
            136 => "\u00E0", // à
            137 => "\u00E2", // â
            138 => "\u00E4", // ä
            139 => "\u00E3", // ã
            140 => "\u00E5", // å
            141 => "\u00E7", // ç
            142 => "\u00E9", // é
            143 => "\u00E8", // è
            144 => "\u00EA", // ê
            145 => "\u00EB", // ë
            146 => "\u00ED", // í
            147 => "\u00EC", // ì
            148 => "\u00EE", // î
            149 => "\u00EF", // ï
            150 => "\u00F1", // ñ
            151 => "\u00F3", // ó
            152 => "\u00F2", // ò
            153 => "\u00F4", // ô
            154 => "\u00F6", // ö
            155 => "\u00F5", // õ
            156 => "\u00FA", // ú
            157 => "\u00F9", // ù
            158 => "\u00FB", // û
            159 => "\u00FC", // ü
            _ => ((char)charCode).ToString()
        };
    }

    private double GetCharWidth(int charCode)
    {
        // Type 0 / CIDFont: width comes from the /W table on the descendant font,
        // falling back to /DW when the CID is unlisted (§9.7.4.3).
        if (_is2ByteFont)
        {
            if (_cidWidths != null && _cidWidths.TryGetValue(charCode, out var w))
                return w;
            return _cidDefaultWidth;
        }

        // Try to get width from font dictionary
        if (_currentFont != null)
        {
            // Check if font has Widths array
            var widthsObj = _currentFont.GetOptional("Widths");
            if (widthsObj is PdfArray widths)
            {
                var firstChar = _currentFont.GetInt("FirstChar", 0);
                var lastChar = _currentFont.GetInt("LastChar", 255);

                if (charCode >= firstChar && charCode <= lastChar)
                {
                    var index = charCode - firstChar;
                    if (index < widths.Count)
                    {
                        return widths.GetNumber(index);
                    }
                }
            }

            // Check for MissingWidth in FontDescriptor
            var fontDescriptor = _currentFont.GetDictionaryOrNull("FontDescriptor");
            if (fontDescriptor != null)
            {
                var missingWidth = fontDescriptor.GetNumber("MissingWidth", 0);
                if (missingWidth > 0)
                    return missingWidth;
            }

            // For standard Type1 fonts without Widths, use built-in metrics
            var baseFont = _currentFont.GetNameOrNull("BaseFont");
            if (baseFont != null)
            {
                return GetStandardFontWidth(baseFont, charCode);
            }
        }

        // Default width for standard fonts
        return 600; // Approximate average width
    }

    /// <summary>
    /// Get character width for standard Type1 fonts (Helvetica, Times, Courier, etc.)
    /// </summary>
    private static double GetStandardFontWidth(string baseFont, int charCode)
    {
        // Standard 14 fonts have fixed-width or variable-width glyphs
        // For Courier (monospace), all glyphs are 600 units wide
        if (baseFont.StartsWith("Courier"))
            return 600;

        // For Helvetica and Times, widths vary
        // These are approximate averages
        if (baseFont.StartsWith("Helvetica"))
        {
            return charCode switch
            {
                32 => 278,  // space
                65 => 667,  // A
                66 => 667,  // B
                67 => 722,  // C
                68 => 722,  // D
                69 => 667,  // E
                70 => 611,  // F
                71 => 778,  // G
                72 => 722,  // H
                73 => 278,  // I
                74 => 500,  // J
                75 => 667,  // K
                76 => 556,  // L
                77 => 833,  // M
                78 => 722,  // N
                79 => 778,  // O
                80 => 667,  // P
                81 => 778,  // Q
                82 => 722,  // R
                83 => 667,  // S
                84 => 611,  // T
                85 => 722,  // U
                86 => 667,  // V
                87 => 944,  // W
                88 => 667,  // X
                89 => 667,  // Y
                90 => 611,  // Z
                97 => 556,  // a
                98 => 556,  // b
                99 => 500,  // c
                100 => 556, // d
                101 => 556, // e
                102 => 278, // f
                103 => 556, // g
                104 => 556, // h
                105 => 222, // i
                106 => 222, // j
                107 => 500, // k
                108 => 222, // l
                109 => 833, // m
                110 => 556, // n
                111 => 556, // o
                112 => 556, // p
                113 => 556, // q
                114 => 333, // r
                115 => 500, // s
                116 => 278, // t
                117 => 556, // u
                118 => 500, // v
                119 => 722, // w
                120 => 500, // x
                121 => 500, // y
                122 => 500, // z
                _ => 556    // average
            };
        }

        // Default: average width
        return 600;
    }

    private static double ToDouble(object obj)
    {
        return obj switch
        {
            int i => i,
            double d => d,
            long l => l,
            float f => f,
            _ => 0
        };
    }

    private struct GraphicsState
    {
        public double ctm_a, ctm_b, ctm_c, ctm_d, ctm_e, ctm_f;
    }
}
