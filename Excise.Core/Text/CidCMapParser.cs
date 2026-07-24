using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// Parses Type 0 font Encoding CMaps into character-code to CID mappings.
/// </summary>
internal sealed class CidCMap
{
    private readonly Dictionary<int, int> _codeToCid;
    private readonly List<CodespaceRange> _codespaces;

    private CidCMap(Dictionary<int, int> codeToCid, List<CodespaceRange> codespaces, int wMode)
    {
        _codeToCid = codeToCid;
        _codespaces = codespaces;
        WMode = wMode;
    }

    internal readonly record struct CodespaceRange(int Low, int High, int Bytes);

    public IReadOnlyDictionary<int, int> Mapping => _codeToCid;

    public IReadOnlyList<CodespaceRange> CodespaceRanges => _codespaces;

    /// <summary>
    /// The CMap's <c>/WMode</c> (PDF §9.7.5.2): 0 = horizontal (default),
    /// 1 = vertical writing. Read from the CMap's own <c>/WMode n def</c>
    /// entry; not inherited through <c>usecmap</c> (each CMap declares its
    /// own writing mode — the -V variants carry their own WMode 1). #515
    /// </summary>
    public int WMode { get; }

    public static CidCMap Parse(byte[] cmapData, Func<string, CidCMap?>? usecmapResolver = null)
        => Parse(Encoding.UTF8.GetString(cmapData), usecmapResolver);

    public static CidCMap Parse(string cmapContent, Func<string, CidCMap?>? usecmapResolver = null)
    {
        var parser = new Parser(cmapContent, usecmapResolver);
        parser.Parse();
        return new CidCMap(parser.Mapping, parser.Codespaces, parser.WMode);
    }

    public int[] Decode(byte[] bytes)
        => DecodeDetailed(bytes).Select(d => d.Cid).ToArray();

    /// <summary>
    /// Decodes <paramref name="bytes"/> against this CMap's codespace ranges,
    /// returning for each decoded source code the mapped CID and the number of
    /// bytes the code occupied. Mixed-width codespaces (e.g. 90ms-RKSJ-H's
    /// 1-byte + 2-byte ranges) yield per-code byte lengths. Codes with no
    /// explicit mapping pass through as their own CID (Identity behavior). #515
    /// </summary>
    public IReadOnlyList<(int Code, int Cid, int ByteLength)> DecodeDetailed(byte[] bytes)
    {
        if (bytes.Length == 0)
            return Array.Empty<(int, int, int)>();

        var codespaces = _codespaces.Count == 0
            ? [new CodespaceRange(0, 0xffff, 2)]
            : _codespaces
                .OrderByDescending(r => r.Bytes)
                .ThenBy(r => r.Low)
                .ToArray();

        var result = new List<(int Code, int Cid, int ByteLength)>(bytes.Length / 2);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var matched = false;
            foreach (var codespace in codespaces)
            {
                if (codespace.Bytes <= 0 || offset + codespace.Bytes > bytes.Length)
                    continue;

                var code = ReadBigEndian(bytes, offset, codespace.Bytes);
                if (code < codespace.Low || code > codespace.High)
                    continue;

                result.Add((code, _codeToCid.TryGetValue(code, out var mappedCid) ? mappedCid : code, codespace.Bytes));
                offset += codespace.Bytes;
                matched = true;
                break;
            }

            if (matched)
                continue;

            var remaining = bytes.Length - offset;
            var fallbackBytes = remaining >= 2 ? 2 : 1;
            var fallbackCode = ReadBigEndian(bytes, offset, fallbackBytes);
            result.Add((fallbackCode, _codeToCid.TryGetValue(fallbackCode, out var fallbackCid) ? fallbackCid : fallbackCode, fallbackBytes));
            offset += fallbackBytes;
        }

        return result;
    }

    private static int ReadBigEndian(byte[] bytes, int offset, int length)
    {
        var value = 0;
        for (var i = 0; i < length; i++)
            value = (value << 8) | bytes[offset + i];
        return value;
    }

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly Func<string, CidCMap?>? _usecmapResolver;

        public Parser(string content, Func<string, CidCMap?>? usecmapResolver = null)
        {
            _tokens = Tokenize(content);
            _usecmapResolver = usecmapResolver;
        }

        public Dictionary<int, int> Mapping { get; } = new();

        public List<CodespaceRange> Codespaces { get; } = new();

        public int WMode { get; private set; }

        public void Parse()
        {
            var i = 0;
            while (i < _tokens.Count)
            {
                var token = _tokens[i];
                if (token.Type == TokenType.Keyword)
                {
                    switch (token.Text)
                    {
                        case "begincodespacerange":
                            i = ParseCodespace(i + 1);
                            continue;
                        case "begincidchar":
                            i = ParseCharMappings(i + 1, "endcidchar");
                            continue;
                        case "beginbfchar":
                            i = ParseCharMappings(i + 1, "endbfchar");
                            continue;
                        case "begincidrange":
                            i = ParseCidRanges(i + 1);
                            continue;
                        case "beginbfrange":
                            i = ParseBfRanges(i + 1);
                            continue;
                        case "usecmap":
                            if (i > 0 && _tokens[i - 1].Type == TokenType.Name)
                                AddPredefinedCMap(_tokens[i - 1].Text);
                            i++;
                            continue;
                    }
                }
                else if (token.Type == TokenType.Name && token.Text == "WMode"
                         && i + 1 < _tokens.Count
                         && _tokens[i + 1].Type == TokenType.Number
                         && int.TryParse(_tokens[i + 1].Text, out var wMode))
                {
                    // /WMode 1 def — vertical writing (§9.7.5.2). Any value
                    // other than 1 is treated as horizontal.
                    WMode = wMode == 1 ? 1 : 0;
                    i += 2;
                    continue;
                }

                i++;
            }
        }

        private int ParseCodespace(int i)
        {
            while (i < _tokens.Count && _tokens[i].Type != TokenType.Keyword)
            {
                if (i + 1 >= _tokens.Count)
                    break;

                var low = _tokens[i];
                var high = _tokens[i + 1];
                if (low.Type != TokenType.HexString || high.Type != TokenType.HexString)
                    break;

                var bytes = Math.Max(1, (low.Text.Length + 1) / 2);
                Codespaces.Add(new CodespaceRange(HexToInt(low.Text), HexToInt(high.Text), bytes));
                i += 2;
            }

            return SkipPast(i, "endcodespacerange");
        }

        private int ParseCharMappings(int i, string endKeyword)
        {
            while (i < _tokens.Count && _tokens[i].Type != TokenType.Keyword)
            {
                if (i + 1 >= _tokens.Count)
                    break;

                var source = _tokens[i];
                var destination = _tokens[i + 1];
                if (source.Type != TokenType.HexString)
                    break;

                if (TryGetCid(destination, out var cid))
                    Mapping[HexToInt(source.Text)] = cid;

                i += 2;
            }

            return SkipPast(i, endKeyword);
        }

        private int ParseCidRanges(int i)
        {
            while (i < _tokens.Count && _tokens[i].Type != TokenType.Keyword)
            {
                if (i + 2 >= _tokens.Count)
                    break;

                var low = _tokens[i];
                var high = _tokens[i + 1];
                var destination = _tokens[i + 2];
                if (low.Type != TokenType.HexString ||
                    high.Type != TokenType.HexString ||
                    !TryGetCid(destination, out var firstCid))
                    break;

                AddIncrementingRange(HexToInt(low.Text), HexToInt(high.Text), firstCid);
                i += 3;
            }

            return SkipPast(i, "endcidrange");
        }

        private int ParseBfRanges(int i)
        {
            while (i < _tokens.Count && _tokens[i].Type != TokenType.Keyword)
            {
                if (i + 2 >= _tokens.Count)
                    break;

                var low = _tokens[i];
                var high = _tokens[i + 1];
                var destination = _tokens[i + 2];
                if (low.Type != TokenType.HexString || high.Type != TokenType.HexString)
                    break;

                var lowCode = HexToInt(low.Text);
                var highCode = HexToInt(high.Text);
                if (destination.Type == TokenType.ArrayStart)
                {
                    var code = lowCode;
                    var j = i + 3;
                    while (j < _tokens.Count && _tokens[j].Type != TokenType.ArrayEnd)
                    {
                        if (code <= highCode && TryGetCid(_tokens[j], out var cid))
                            Mapping[code++] = cid;
                        j++;
                    }

                    i = j < _tokens.Count ? j + 1 : j;
                }
                else if (TryGetCid(destination, out var firstCid))
                {
                    AddIncrementingRange(lowCode, highCode, firstCid);
                    i += 3;
                }
                else
                {
                    break;
                }
            }

            return SkipPast(i, "endbfrange");
        }

        private void AddIncrementingRange(int lowCode, int highCode, int firstCid)
        {
            if (highCode < lowCode)
                return;

            for (var code = lowCode; code <= highCode; code++)
                Mapping[code] = firstCid + code - lowCode;
        }

        private void AddPredefinedCMap(string name)
        {
            if (name is "Identity-H" or "Identity-V")
            {
                AddCodespaceIfMissing(new CodespaceRange(0x0000, 0xffff, 2));
                return;
            }

            // usecmap of a non-Identity CMap: inherit the referenced CMap's
            // codespaces and mappings (per PostScript CMap semantics the using
            // CMap's own entries, which appear after usecmap, override). The
            // resolver — when provided — loads registered CMaps such as
            // /UniJIS-UCS2-H or /90ms-RKSJ-H by name (#515); cycle protection
            // lives in the resolver (PredefinedCMapProvider).
            var parent = _usecmapResolver?.Invoke(name);
            if (parent == null)
                return;

            foreach (var range in parent.CodespaceRanges)
                AddCodespaceIfMissing(range);
            foreach (var (code, cid) in parent.Mapping)
                Mapping.TryAdd(code, cid);
        }

        private void AddCodespaceIfMissing(CodespaceRange range)
        {
            if (!Codespaces.Contains(range))
                Codespaces.Add(range);
        }

        private int SkipPast(int i, string keyword)
        {
            while (i < _tokens.Count &&
                   !(_tokens[i].Type == TokenType.Keyword && _tokens[i].Text == keyword))
            {
                i++;
            }

            return i < _tokens.Count ? i + 1 : i;
        }

        private static bool TryGetCid(Token token, out int cid)
        {
            switch (token.Type)
            {
                case TokenType.HexString:
                    cid = HexToInt(token.Text);
                    return true;
                case TokenType.Number:
                    return int.TryParse(token.Text, out cid);
                default:
                    cid = 0;
                    return false;
            }
        }
    }

    private enum TokenType { Keyword, Number, HexString, Name, ArrayStart, ArrayEnd }

    private readonly record struct Token(TokenType Type, string Text);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                i++;
                continue;
            }

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
                while (i < s.Length && s[i] != '>')
                {
                    if (IsHex(s[i]))
                        sb.Append(s[i]);
                    i++;
                }

                if (i < s.Length)
                    i++;

                tokens.Add(new Token(TokenType.HexString, sb.ToString()));
                continue;
            }

            if (c == '[')
            {
                tokens.Add(new Token(TokenType.ArrayStart, "["));
                i++;
                continue;
            }

            if (c == ']')
            {
                tokens.Add(new Token(TokenType.ArrayEnd, "]"));
                i++;
                continue;
            }

            if (c == '/')
            {
                var start = ++i;
                while (i < s.Length && !IsDelimiter(s[i]))
                    i++;
                tokens.Add(new Token(TokenType.Name, s.Substring(start, i - start)));
                continue;
            }

            if (char.IsAsciiDigit(c) || c is '-' or '+')
            {
                var start = i++;
                while (i < s.Length && !IsDelimiter(s[i]))
                    i++;
                tokens.Add(new Token(TokenType.Number, s.Substring(start, i - start)));
                continue;
            }

            if (c == '(')
            {
                i++;
                var depth = 1;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i += 2;
                        continue;
                    }

                    if (s[i] == '(')
                        depth++;
                    else if (s[i] == ')')
                        depth--;
                    i++;
                }

                continue;
            }

            {
                var start = i;
                while (i < s.Length && !IsDelimiter(s[i]))
                    i++;
                if (i > start)
                    tokens.Add(new Token(TokenType.Keyword, s.Substring(start, i - start)));
                else
                    i++;
            }
        }

        return tokens;
    }

    private static bool IsDelimiter(char c)
        => c is ' ' or '\t' or '\r' or '\n' or '\f' or '<' or '>' or '[' or ']' or '/' or '(' or ')' or '%';

    private static bool IsHex(char c)
        => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static int HexToInt(string hex)
    {
        if (hex.Length == 0)
            return 0;

        if ((hex.Length & 1) != 0)
            hex = "0" + hex;

        var value = 0;
        foreach (var c in hex)
            value = (value << 4) | HexDigit(c);
        return value;
    }

    private static int HexDigit(char c)
        => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => 0
        };
}
