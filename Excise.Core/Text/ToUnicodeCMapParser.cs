using System.Globalization;
using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// Parses ToUnicode CMap streams (ISO 32000-2 §9.10.3) into character-code →
/// Unicode-string mappings.
///
/// CMaps are emitted by every modern PDF producer for composite fonts. The
/// shape we have to handle:
///
///   <pre>
///   N begincodespacerange  &lt;LO&gt; &lt;HI&gt; ... endcodespacerange
///   N beginbfchar          &lt;src&gt; &lt;dst&gt; ... endbfchar
///   N beginbfrange         &lt;srcLo&gt; &lt;srcHi&gt; &lt;dst&gt;       ... endbfrange
///   N beginbfrange         &lt;srcLo&gt; &lt;srcHi&gt; [&lt;d1&gt; &lt;d2&gt;] ... endbfrange
///   </pre>
///
/// We tokenise the stream rather than regex it: the regex approach mis-matches
/// `<srcLo><srcHi>[<d1><d2>]` triples as if they were `<srcLo><srcHi><dstLo>`
/// simple ranges, double-emitting every array-form entry.
///
/// Source codes can be 1-, 2-, or even 4-byte; the codespace ranges declare
/// the lengths actually in use. Destination strings can be multi-character
/// (ligatures, ﬂag → "fl", emoji surrogate pairs, etc.) — we always store
/// them as System.String UTF-16.
/// </summary>
public class ToUnicodeCMapParser
{
    private readonly Dictionary<int, string> _mapping = new();
    private readonly List<CodespaceRange> _codespaces = new();
    private int _maxCodeBytes = 1;

    /// <summary>One entry from a /codespacerange/ block.</summary>
    public readonly record struct CodespaceRange(int Low, int High, int Bytes);

    /// <summary>Parse a CMap byte stream. Returns code → Unicode string.</summary>
    public static Dictionary<int, string> Parse(byte[] cmapData)
    {
        var parser = new ToUnicodeCMapParser();
        parser.ParseInternal(Encoding.UTF8.GetString(cmapData));
        return parser._mapping;
    }

    /// <summary>Parse a CMap source string. Returns code → Unicode string.</summary>
    public static Dictionary<int, string> Parse(string cmapContent)
    {
        var parser = new ToUnicodeCMapParser();
        parser.ParseInternal(cmapContent);
        return parser._mapping;
    }

    /// <summary>Parse and return the parser instance for codespace introspection.</summary>
    public static ToUnicodeCMapParser ParseDetailed(byte[] cmapData)
    {
        var parser = new ToUnicodeCMapParser();
        parser.ParseInternal(Encoding.UTF8.GetString(cmapData));
        return parser;
    }

    /// <summary>Final code → Unicode mapping.</summary>
    public IReadOnlyDictionary<int, string> Mapping => _mapping;

    /// <summary>Declared codespace ranges (informs how many bytes per source code).</summary>
    public IReadOnlyList<CodespaceRange> CodespaceRanges => _codespaces;

    /// <summary>Maximum source-code length declared by codespacerange (1, 2, 3, or 4).</summary>
    public int MaxCodeBytes => _maxCodeBytes;

    private void ParseInternal(string content)
    {
        var tokens = Tokenize(content);
        int i = 0;

        while (i < tokens.Count)
        {
            var t = tokens[i];

            // Look for `N keyword` openers.
            if (t.Type == TokenType.Keyword)
            {
                switch (t.Text)
                {
                    case "begincodespacerange":
                        i = ParseCodespace(tokens, i + 1);
                        continue;
                    case "beginbfchar":
                        i = ParseBfChar(tokens, i + 1);
                        continue;
                    case "beginbfrange":
                        i = ParseBfRange(tokens, i + 1);
                        continue;
                }
            }

            i++;
        }
    }

    private int ParseCodespace(List<Token> tokens, int i)
    {
        while (i < tokens.Count && tokens[i].Type != TokenType.Keyword)
        {
            // Consume <lo> <hi> pairs.
            if (i + 1 >= tokens.Count) break;
            var loTok = tokens[i];
            var hiTok = tokens[i + 1];
            if (loTok.Type != TokenType.HexString || hiTok.Type != TokenType.HexString) break;

            int bytes = (loTok.Text.Length + 1) / 2;
            if (bytes > _maxCodeBytes) _maxCodeBytes = bytes;
            int lo = HexToInt(loTok.Text);
            int hi = HexToInt(hiTok.Text);
            _codespaces.Add(new CodespaceRange(lo, hi, bytes));
            i += 2;
        }

        // Skip the closing `endcodespacerange` keyword.
        while (i < tokens.Count && !(tokens[i].Type == TokenType.Keyword && tokens[i].Text == "endcodespacerange"))
            i++;
        return i + 1;
    }

    private int ParseBfChar(List<Token> tokens, int i)
    {
        while (i < tokens.Count && tokens[i].Type != TokenType.Keyword)
        {
            if (i + 1 >= tokens.Count) break;
            var src = tokens[i];
            var dst = tokens[i + 1];
            if (src.Type != TokenType.HexString) break;
            // dst can be either a hex string (the common case) or a /name (rare).
            if (dst.Type == TokenType.HexString)
            {
                int code = HexToInt(src.Text);
                _mapping[code] = HexToUnicodeString(dst.Text);
            }
            i += 2;
        }
        while (i < tokens.Count && !(tokens[i].Type == TokenType.Keyword && tokens[i].Text == "endbfchar"))
            i++;
        return i + 1;
    }

    private int ParseBfRange(List<Token> tokens, int i)
    {
        while (i < tokens.Count && tokens[i].Type != TokenType.Keyword)
        {
            if (i + 2 >= tokens.Count) break;
            var lo = tokens[i];
            var hi = tokens[i + 1];
            var dst = tokens[i + 2];

            if (lo.Type != TokenType.HexString || hi.Type != TokenType.HexString)
                break;

            int srcLo = HexToInt(lo.Text);
            int srcHi = HexToInt(hi.Text);

            if (dst.Type == TokenType.HexString)
            {
                // <lo> <hi> <dstLo> — incrementing destination.
                // For multi-character destinations only the *last* code point increments.
                var dstStr = HexToUnicodeString(dst.Text);
                for (int code = srcLo; code <= srcHi; code++)
                {
                    if (dstStr.Length == 0) continue;
                    int offset = code - srcLo;
                    if (offset == 0)
                    {
                        _mapping[code] = dstStr;
                    }
                    else
                    {
                        // Increment the last code point by `offset`.
                        var lastIdx = dstStr.Length;
                        // Walk back one code point
                        if (char.IsLowSurrogate(dstStr[lastIdx - 1]) && lastIdx >= 2)
                            lastIdx -= 2;
                        else
                            lastIdx -= 1;

                        var prefix = dstStr.Substring(0, lastIdx);
                        int lastCp = char.ConvertToUtf32(dstStr, lastIdx);
                        _mapping[code] = prefix + char.ConvertFromUtf32(lastCp + offset);
                    }
                }
                i += 3;
            }
            else if (dst.Type == TokenType.ArrayStart)
            {
                // <lo> <hi> [ <d1> <d2> ... ] — explicit list, one entry per code.
                int j = i + 3;
                int code = srcLo;
                while (j < tokens.Count && tokens[j].Type != TokenType.ArrayEnd)
                {
                    if (tokens[j].Type == TokenType.HexString && code <= srcHi)
                    {
                        _mapping[code] = HexToUnicodeString(tokens[j].Text);
                        code++;
                    }
                    j++;
                }
                i = j + 1; // skip past ArrayEnd
            }
            else
            {
                break;
            }
        }
        while (i < tokens.Count && !(tokens[i].Type == TokenType.Keyword && tokens[i].Text == "endbfrange"))
            i++;
        return i + 1;
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    private enum TokenType { Keyword, Number, HexString, Name, ArrayStart, ArrayEnd }

    private readonly record struct Token(TokenType Type, string Text);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];

            // Whitespace + line endings
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f') { i++; continue; }

            // Comment to end of line
            if (c == '%') { while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++; continue; }

            // Hex string <...>
            if (c == '<')
            {
                int start = ++i;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '>')
                {
                    char ch = s[i];
                    if (IsHex(ch)) sb.Append(ch);
                    i++;
                }
                if (i < s.Length) i++; // skip '>'
                tokens.Add(new Token(TokenType.HexString, sb.ToString()));
                continue;
            }

            // Array delimiters
            if (c == '[') { tokens.Add(new Token(TokenType.ArrayStart, "[")); i++; continue; }
            if (c == ']') { tokens.Add(new Token(TokenType.ArrayEnd,   "]")); i++; continue; }

            // /Name
            if (c == '/')
            {
                int start = ++i;
                while (i < s.Length && !IsDelim(s[i])) i++;
                tokens.Add(new Token(TokenType.Name, s.Substring(start, i - start)));
                continue;
            }

            // Numbers (may be negative or decimal — used by usefont, codespacerange counts)
            if ((c >= '0' && c <= '9') || c == '-' || c == '+')
            {
                int start = i++;
                while (i < s.Length && !IsDelim(s[i])) i++;
                tokens.Add(new Token(TokenType.Number, s.Substring(start, i - start)));
                continue;
            }

            // String literal (rare in CMaps but valid PDF)
            if (c == '(')
            {
                int depth = 1; i++;
                var sb = new StringBuilder();
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i]); sb.Append(s[i + 1]); i += 2; continue; }
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')') { depth--; if (depth == 0) break; }
                    sb.Append(s[i]);
                    i++;
                }
                if (i < s.Length) i++;
                continue; // CMap doesn't use string literals semantically
            }

            // Otherwise a keyword (begin*, end*, def, dict, etc.)
            {
                int start = i;
                while (i < s.Length && !IsDelim(s[i])) i++;
                if (i > start)
                    tokens.Add(new Token(TokenType.Keyword, s.Substring(start, i - start)));
                else
                    i++; // never advance zero
            }
        }
        return tokens;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    private static bool IsDelim(char c) =>
        c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' ||
        c == '<' || c == '>' || c == '[' || c == ']' || c == '/' || c == '(' || c == ')' || c == '%';

    private static int HexToInt(string hex)
    {
        if (hex.Length == 0) return 0;
        if ((hex.Length & 1) != 0) hex = "0" + hex; // odd-length → left-pad
        int v = 0;
        foreach (char c in hex)
        {
            v = (v << 4) | HexDigit(c);
        }
        return v;
    }

    private static int HexDigit(char c) =>
        c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => 0
        };

    private static string HexToUnicodeString(string hex)
    {
        if (hex.Length == 0) return string.Empty;
        if ((hex.Length & 1) != 0) hex = "0" + hex;

        int byteCount = hex.Length / 2;
        var bytes = new byte[byteCount];
        for (int k = 0; k < byteCount; k++)
            bytes[k] = (byte)((HexDigit(hex[2 * k]) << 4) | HexDigit(hex[2 * k + 1]));

        // Per spec the destination is UTF-16BE. A 1-byte hex string (which would
        // be illegal as UTF-16BE because it needs an even byte count) means a
        // single 8-bit code; treat it as Latin-1.
        if (byteCount == 1)
            return ((char)bytes[0]).ToString(CultureInfo.InvariantCulture);

        return Encoding.BigEndianUnicode.GetString(bytes);
    }
}
