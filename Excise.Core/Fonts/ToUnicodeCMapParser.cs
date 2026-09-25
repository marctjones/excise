using System.Globalization;
using System.Text;
using static Excise.Core.Text.CMapTokenizer;

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
    internal IReadOnlyList<CodespaceRange> CodespaceRanges => _codespaces;

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

            // Codes are at most 4 bytes per the CMap spec; clamp malformed
            // over-long bounds so MaxCodeBytes stays meaningful. #515
            int bytes = Math.Min(4, Math.Max(1, (loTok.Text.Length + 1) / 2));
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

            // Cap the incrementing expansion: a malformed range like
            // <0000> <7FFFFFFF> must not hang the parser or exhaust memory.
            // Spec-conforming bfranges vary only in the last byte (≤ 256
            // codes); a 64K cap keeps even a full 2-byte range intact. #515
            if ((long)srcHi - srcLo > 0xFFFF)
                srcHi = srcLo + 0xFFFF;

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
                        int nextCp = lastCp + offset;

                        // Incrementing past the Unicode range (or into the
                        // surrogate block) can only happen in a malformed
                        // range — stop rather than throw away the whole
                        // CMap on ConvertFromUtf32's exception. #515
                        if (nextCp > 0x10FFFF || (nextCp >= 0xD800 && nextCp <= 0xDFFF))
                            break;

                        _mapping[code] = prefix + char.ConvertFromUtf32(nextCp);
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
