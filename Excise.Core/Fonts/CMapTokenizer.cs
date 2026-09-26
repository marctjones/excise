using System.Text;

namespace Excise.Core.Text;

/// <summary>One entry from a CMap <c>codespacerange</c> block.</summary>
internal readonly record struct CodespaceRange(int Low, int High, int Bytes);

/// <summary>
/// The single CMap lexer, shared by <see cref="CidCMap"/> and
/// <see cref="ToUnicodeCMapParser"/> (#1831). Comments and string literals are
/// skipped; hex strings keep only their hex digits.
/// </summary>
internal static class CMapTokenizer
{
    internal enum TokenType { Keyword, Number, HexString, Name, ArrayStart, ArrayEnd }

    internal readonly record struct Token(TokenType Type, string Text);

    internal static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ' ' or '\t' or '\r' or '\n' or '\f') { i++; continue; }

            if (c == '%')
            {
                while (i < s.Length && s[i] != '\n' && s[i] != '\r')
                    i++;
                continue;
            }

            if (c == '<')
            {
                i++;
                var sb = new StringBuilder();
                for (; i < s.Length && s[i] != '>'; i++)
                {
                    if (char.IsAsciiHexDigit(s[i]))
                        sb.Append(s[i]);
                }

                if (i < s.Length)
                    i++;
                tokens.Add(new Token(TokenType.HexString, sb.ToString()));
                continue;
            }

            if (c == '[') { tokens.Add(new Token(TokenType.ArrayStart, "[")); i++; continue; }
            if (c == ']') { tokens.Add(new Token(TokenType.ArrayEnd, "]")); i++; continue; }

            if (c == '(')
            {
                i++;
                for (var depth = 1; i < s.Length && depth > 0; i++)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                        i++;
                    else if (s[i] == '(')
                        depth++;
                    else if (s[i] == ')')
                        depth--;
                }

                continue;
            }

            var isName = c == '/';
            var start = isName ? ++i : i;
            while (i < s.Length && !IsDelimiter(s[i]))
                i++;
            if (isName)
                tokens.Add(new Token(TokenType.Name, s.Substring(start, i - start)));
            else if (char.IsAsciiDigit(c) || c is '-' or '+')
                tokens.Add(new Token(TokenType.Number, s.Substring(start, i - start)));
            else if (i > start)
                tokens.Add(new Token(TokenType.Keyword, s.Substring(start, i - start)));
            else
                i++;
        }

        return tokens;
    }

    /// <summary>
    /// Reads the <c>&lt;lo&gt; &lt;hi&gt;</c> pairs of a <c>begincodespacerange</c>
    /// block that starts at <paramref name="i"/> into <paramref name="ranges"/>
    /// and returns the index past <c>endcodespacerange</c>.
    /// </summary>
    internal static int ParseCodespace(List<Token> tokens, int i, List<CodespaceRange> ranges)
    {
        while (i + 1 < tokens.Count
               && tokens[i].Type == TokenType.HexString
               && tokens[i + 1].Type == TokenType.HexString)
        {
            // Codes are at most 4 bytes per the CMap spec; clamp so a
            // malformed over-long hex bound cannot declare a code width
            // wider than the int the code is read into. #515
            var bytes = Math.Min(4, Math.Max(1, (tokens[i].Text.Length + 1) / 2));
            ranges.Add(new CodespaceRange(HexToInt(tokens[i].Text), HexToInt(tokens[i + 1].Text), bytes));
            i += 2;
        }

        return SkipPast(tokens, i, "endcodespacerange");
    }

    /// <summary>The index just past the first <paramref name="keyword"/> at or after <paramref name="i"/>.</summary>
    internal static int SkipPast(List<Token> tokens, int i, string keyword)
    {
        while (i < tokens.Count && !(tokens[i].Type == TokenType.Keyword && tokens[i].Text == keyword))
            i++;
        return i < tokens.Count ? i + 1 : i;
    }

    internal static int HexToInt(string hex)
    {
        if (hex.Length == 0)
            return 0;

        if ((hex.Length & 1) != 0)
            hex = "0" + hex;

        // Codes are at most 4 bytes (8 hex digits) per the CMap spec — a
        // malformed longer string must not keep shifting bytes out: keep the
        // leading 4 bytes only. The raw 32-bit BIT PATTERN is preserved (a
        // 4-byte code like UniGB-UTF16-H's <D800DC00> wraps negative as an
        // int) because ReadBigEndian produces the identical wrap for decoded
        // codes, and the byte-wise codespace comparison is bit-pattern-based
        // — clamping would destroy the per-byte bounds. #515
        var digits = Math.Min(hex.Length, 8);
        var value = 0;
        for (var i = 0; i < digits; i++)
            value = (value << 4) | HexDigit(hex[i]);
        return value;
    }

    internal static int HexDigit(char c)
        => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => 0
        };

    private static bool IsDelimiter(char c)
        => c is ' ' or '\t' or '\r' or '\n' or '\f' or '<' or '>' or '[' or ']' or '/' or '(' or ')' or '%';
}
