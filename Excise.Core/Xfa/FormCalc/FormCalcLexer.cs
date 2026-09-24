using System.Globalization;
using System.Text;

namespace Excise.Core.Xfa.FormCalc;

internal enum FormCalcTokenKind
{
    Eof,
    Number,
    String,
    Identifier,
    // operators and punctuation
    And, Or, Not, Plus, Minus, Times, Divide,
    Eq, Ne, Lt, Le, Gt, Ge, Assign,
    LeftParen, RightParen, LeftBracket, RightBracket, Comma,
    Dot, DotDot, DotHash, DotStar,
    // keywords
    Break, Continue, Do, Downto, Else, ElseIf, End, EndFor, EndFunc, EndIf, EndWhile,
    Exit, For, ForEach, Func, If, In, Null, Return, Step, Then, This, Throw, Upto, Var, While,
}

internal readonly record struct FormCalcToken(FormCalcTokenKind Kind, string Text, double Number, int Position);

/// <summary>Raised for a script that is not FormCalc, or exceeds a bound. Never escapes the interpreter's caller.</summary>
internal sealed class FormCalcSyntaxException : Exception
{
    public FormCalcSyntaxException(string message, int position) : base($"{message} (at {position})") { Position = position; }
    public int Position { get; }
}

/// <summary>
/// The FormCalc tokenizer (XFA 3.3, "FormCalc"). Newlines are not significant; a
/// <c>;</c> or <c>//</c> starts a comment that runs to the end of the line.
/// Identifiers may start with a letter, <c>_</c>, <c>$</c> or <c>!</c> (the SOM roots),
/// and keywords are case-insensitive.
/// </summary>
internal sealed class FormCalcLexer
{
    private static readonly Dictionary<string, FormCalcTokenKind> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["and"] = FormCalcTokenKind.And, ["break"] = FormCalcTokenKind.Break, ["continue"] = FormCalcTokenKind.Continue,
        ["do"] = FormCalcTokenKind.Do, ["downto"] = FormCalcTokenKind.Downto, ["else"] = FormCalcTokenKind.Else,
        ["elseif"] = FormCalcTokenKind.ElseIf, ["end"] = FormCalcTokenKind.End, ["endfor"] = FormCalcTokenKind.EndFor,
        ["endfunc"] = FormCalcTokenKind.EndFunc, ["endif"] = FormCalcTokenKind.EndIf, ["endwhile"] = FormCalcTokenKind.EndWhile,
        ["eq"] = FormCalcTokenKind.Eq, ["exit"] = FormCalcTokenKind.Exit, ["for"] = FormCalcTokenKind.For,
        ["foreach"] = FormCalcTokenKind.ForEach, ["func"] = FormCalcTokenKind.Func, ["ge"] = FormCalcTokenKind.Ge,
        ["gt"] = FormCalcTokenKind.Gt, ["if"] = FormCalcTokenKind.If, ["in"] = FormCalcTokenKind.In,
        ["le"] = FormCalcTokenKind.Le, ["lt"] = FormCalcTokenKind.Lt, ["ne"] = FormCalcTokenKind.Ne,
        ["not"] = FormCalcTokenKind.Not, ["null"] = FormCalcTokenKind.Null, ["or"] = FormCalcTokenKind.Or,
        ["return"] = FormCalcTokenKind.Return, ["step"] = FormCalcTokenKind.Step, ["then"] = FormCalcTokenKind.Then,
        ["this"] = FormCalcTokenKind.This, ["throw"] = FormCalcTokenKind.Throw, ["upto"] = FormCalcTokenKind.Upto,
        ["var"] = FormCalcTokenKind.Var, ["while"] = FormCalcTokenKind.While,
    };

    /// <summary>Longest script accepted, in characters.</summary>
    internal const int MaxScriptLength = 256 * 1024;

    private readonly string _text;
    private int _pos;

    public FormCalcLexer(string text)
    {
        if (text.Length > MaxScriptLength)
            throw new FormCalcSyntaxException($"The script is longer than {MaxScriptLength} characters", 0);
        _text = text;
    }

    public FormCalcToken Next()
    {
        while (_pos < _text.Length)
        {
            var start = _pos;
            var c = _text[_pos++];
            switch (c)
            {
                case ' ': case '\t': case '\n': case '\v': case '\f': case '\r':
                    continue;
                case ';':
                    SkipLine();
                    continue;
                case '/':
                    if (Peek() == '/') { SkipLine(); continue; }
                    return Simple(FormCalcTokenKind.Divide, "/", start);
                case '"': return ReadString(start);
                case '&': return Simple(FormCalcTokenKind.And, "&", start);
                case '|': return Simple(FormCalcTokenKind.Or, "|", start);
                case '(': return Simple(FormCalcTokenKind.LeftParen, "(", start);
                case ')': return Simple(FormCalcTokenKind.RightParen, ")", start);
                case '[': return Simple(FormCalcTokenKind.LeftBracket, "[", start);
                case ']': return Simple(FormCalcTokenKind.RightBracket, "]", start);
                case ',': return Simple(FormCalcTokenKind.Comma, ",", start);
                case '*': return Simple(FormCalcTokenKind.Times, "*", start);
                case '+': return Simple(FormCalcTokenKind.Plus, "+", start);
                case '-': return Simple(FormCalcTokenKind.Minus, "-", start);
                case '=':
                    if (Peek() == '=') { _pos++; return Simple(FormCalcTokenKind.Eq, "==", start); }
                    return Simple(FormCalcTokenKind.Assign, "=", start);
                case '>':
                    if (Peek() == '=') { _pos++; return Simple(FormCalcTokenKind.Ge, ">=", start); }
                    return Simple(FormCalcTokenKind.Gt, ">", start);
                case '<':
                    if (Peek() == '=') { _pos++; return Simple(FormCalcTokenKind.Le, "<=", start); }
                    if (Peek() == '>') { _pos++; return Simple(FormCalcTokenKind.Ne, "<>", start); }
                    return Simple(FormCalcTokenKind.Lt, "<", start);
                case '.':
                    if (Peek() == '.') { _pos++; return Simple(FormCalcTokenKind.DotDot, "..", start); }
                    if (Peek() == '*') { _pos++; return Simple(FormCalcTokenKind.DotStar, ".*", start); }
                    if (Peek() == '#') { _pos++; return Simple(FormCalcTokenKind.DotHash, ".#", start); }
                    if (char.IsAsciiDigit(Peek())) { _pos = start; return ReadNumber(start); }
                    return Simple(FormCalcTokenKind.Dot, ".", start);
                default:
                    if (char.IsAsciiDigit(c)) { _pos = start; return ReadNumber(start); }
                    _pos = start;
                    return ReadIdentifier(start);
            }
        }
        return new FormCalcToken(FormCalcTokenKind.Eof, "", 0, _text.Length);
    }

    private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

    private static FormCalcToken Simple(FormCalcTokenKind kind, string text, int position) => new(kind, text, 0, position);

    private void SkipLine()
    {
        while (_pos < _text.Length && _text[_pos] != '\n' && _text[_pos] != '\r') _pos++;
    }

    private FormCalcToken ReadNumber(int start)
    {
        var i = _pos;
        while (i < _text.Length && char.IsAsciiDigit(_text[i])) i++;
        if (i < _text.Length && _text[i] == '.')
        {
            i++;
            while (i < _text.Length && char.IsAsciiDigit(_text[i])) i++;
        }
        if (i < _text.Length && (_text[i] == 'e' || _text[i] == 'E'))
        {
            var j = i + 1;
            if (j < _text.Length && (_text[j] == '+' || _text[j] == '-')) j++;
            if (j < _text.Length && char.IsAsciiDigit(_text[j]))
            {
                while (j < _text.Length && char.IsAsciiDigit(_text[j])) j++;
                i = j;
            }
        }
        var raw = _text[_pos..i];
        _pos = i;
        // "12." and "2.e3" and ".7": give the parser a form it accepts.
        var norm = raw.StartsWith('.') ? "0" + raw : raw;
        norm = norm.Replace(".e", ".0e", StringComparison.OrdinalIgnoreCase);
        if (norm.EndsWith('.')) norm += "0";
        var value = double.Parse(norm, NumberStyles.Float, CultureInfo.InvariantCulture); // overflow gives Infinity
        return new FormCalcToken(FormCalcTokenKind.Number, raw, value, start);
    }

    private FormCalcToken ReadString(int start)
    {
        var sb = new StringBuilder();
        while (_pos < _text.Length)
        {
            var c = _text[_pos++];
            if (c == '"')
            {
                if (Peek() == '"') { sb.Append('"'); _pos++; continue; }
                return new FormCalcToken(FormCalcTokenKind.String, sb.ToString(), 0, start);
            }
            if (c == '\\' && (Peek() == 'u' || Peek() == 'U'))
            {
                // \u then 4 to 8 hex digits: exactly 8 is one code unit, 4 to 7 use the first four
                // and leave the rest as text. Anything else keeps the backslash literally.
                var n = 0;
                while (n < 8 && _pos + 1 + n < _text.Length && Uri.IsHexDigit(_text[_pos + 1 + n])) n++;
                if (n >= 4)
                {
                    var take = n == 8 ? 8 : 4;
                    var code = Convert.ToUInt32(_text.Substring(_pos + 1, take), 16);
                    sb.Append((char)(code & 0xFFFF));
                    _pos += 1 + take;
                    continue;
                }
            }
            sb.Append(c);
        }
        // Unterminated: the rest is the string, as other engines do.
        return new FormCalcToken(FormCalcTokenKind.String, sb.ToString(), 0, start);
    }

    private FormCalcToken ReadIdentifier(int start)
    {
        var i = _pos;
        var first = _text[i];
        if (!(char.IsLetter(first) || first == '_' || first == '$' || first == '!'))
            throw new FormCalcSyntaxException($"Unexpected character '{first}'", start);
        i++;
        while (i < _text.Length && (char.IsLetterOrDigit(_text[i]) || _text[i] == '_' || _text[i] == '$')) i++;
        var text = _text[_pos..i];
        _pos = i;
        if (text.Equals("nan", StringComparison.OrdinalIgnoreCase))
            return new FormCalcToken(FormCalcTokenKind.Number, text, double.NaN, start);
        if (text.Equals("infinity", StringComparison.OrdinalIgnoreCase))
            return new FormCalcToken(FormCalcTokenKind.Number, text, double.PositiveInfinity, start);
        return Keywords.TryGetValue(text, out var kw)
            ? new FormCalcToken(kw, text, 0, start)
            : new FormCalcToken(FormCalcTokenKind.Identifier, text, 0, start);
    }
}
