using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Excise.TestSupport;

/// <summary>
/// Search a SAVED PDF for a term in every carrier, <b>including inside
/// compressed streams</b>.
///
/// <para>⚠️ The scan CLAUDE.md prescribes —
/// <c>Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved)</c>
/// over the raw bytes — is blind to anything inside a <c>/FlateDecode</c>
/// stream, and excise's writer compresses on save. It is presented as the
/// carrier-agnostic backstop that catches what the extractor misses, and on a
/// compressed file it catches nothing.</para>
///
/// <para>This is not hypothetical. #1040's leaking output scanned <b>clean</b>
/// that way — 0 ASCII, 0 UTF-16BE — while mutool read the name straight out of
/// it. Decompressing first found it immediately.</para>
///
/// <para>Inflation goes through <see cref="ZLibStream"/>, not excise's own
/// filter code, deliberately: a decoder bug that hid bytes from excise must not
/// also hide them from the gate that checks excise.</para>
/// </summary>
internal static class SavedPdfLeakScanner
{
    /// <summary>
    /// Every occurrence of <paramref name="term"/> in <paramref name="saved"/>,
    /// searching raw bytes and inflated stream bodies in ASCII, UTF-16BE and
    /// UTF-8, and every string object (§7.3.4) in them decoded as a reader
    /// decodes it. Returns a human-readable location per hit — empty means clean.
    /// </summary>
    public static IReadOnlyList<string> FindTerm(byte[] saved, string term)
    {
        var hits = new List<string>();
        saved = MaskFileIdentifier(saved);

        // Search the ENCODED BYTES of the term, not a decoded string of the whole
        // haystack. Decoding a >1GB stream to a string overflows
        // Latin1Encoding.GetString (a real crash the benchmark hit on a large
        // corpus file); a byte-level substring search is size-safe AND avoids
        // three full-array allocations per scan. Ordinal string.Contains and a
        // byte-exact IndexOf are equivalent for these fixed-encoding patterns.
        var latin1Bytes = Encoding.Latin1.GetBytes(term);
        var utf16Bytes = Encoding.BigEndianUnicode.GetBytes(term);
        var utf8Bytes = Encoding.UTF8.GetBytes(term);

        void Scan(byte[] haystack, string where)
        {
            if (ContainsBytes(haystack, latin1Bytes))
                hits.Add($"{where}: ASCII");
            if (ContainsBytes(haystack, utf16Bytes))
                hits.Add($"{where}: UTF-16BE");
            // UTF-8 is what an XMP /Metadata stream carries (§14.3.2), and it
            // differs from Latin-1 for exactly the non-ASCII terms these tests
            // exist for — Arabic, CJK, accented Latin.
            if (ContainsBytes(haystack, utf8Bytes))
                hits.Add($"{where}: UTF-8");
        }

        // A text string's bytes are rarely the term's bytes: excise writes a
        // UTF-16BE string as hex or with octal escapes (#1846), so the search
        // runs on each string's value. An unmarked value is searched as bytes,
        // which needs no decoded copy of every string in the file.
        var pdfDocBytes = PdfDocEncode(term);

        void ScanString(byte[] value, string where)
        {
            void Hit(string encoding)
            {
                var hit = $"{where}: decoded text string ({encoding})";
                if (!hits.Contains(hit)) hits.Add(hit);
            }

            if (MarkedTextString(value) is { } marked)
            {
                if (marked.Text.Contains(term, StringComparison.Ordinal)) Hit(marked.Encoding);
                return;
            }
            if (pdfDocBytes != null && ContainsBytes(value, pdfDocBytes)) Hit("PDFDocEncoding");
            if (ContainsBytes(value, utf16Bytes)) Hit("unmarked UTF-16BE");
            if (ContainsBytes(value, utf8Bytes)) Hit("unmarked UTF-8");
        }

        foreach (var region in Regions(saved))
        {
            if (region.ScanBytes)
                Scan(region.Data, region.Where);
            foreach (var value in StringObjects(region.Data, region.From, region.To))
                ScanString(value, region.Where);
        }

        return hits;
    }

    /// <summary>
    /// The saved file rendered as searchable text across every carrier — raw
    /// bytes and inflated stream bodies in Latin-1, UTF-16BE and UTF-8, and
    /// every string object in them decoded as <see cref="FindTerm"/> decodes it.
    ///
    /// <para>For POSITIVE assertions ("this must still be present"). Absence
    /// assertions should use <see cref="FindTerm"/> instead: when one fails it
    /// names the carrier the term survived in, and a leak you cannot locate is
    /// a leak you cannot triage.</para>
    /// </summary>
    public static string AllCarriersText(byte[] saved)
    {
        saved = MaskFileIdentifier(saved);
        var sb = new StringBuilder();
        sb.Append(Encoding.Latin1.GetString(saved)).Append('\n');
        sb.Append(Encoding.BigEndianUnicode.GetString(saved)).Append('\n');
        sb.Append(Encoding.UTF8.GetString(saved)).Append('\n');
        foreach (var body in StreamBodies(saved))
            sb.Append(body).Append('\n');
        foreach (var region in Regions(saved))
            foreach (var value in StringObjects(region.Data, region.From, region.To))
                foreach (var text in DecodeTextString(value))
                    sb.Append(text).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Every stream body in <paramref name="saved"/>, inflated where possible,
    /// as Latin-1 text. For assertions about content-stream STRUCTURE — how
    /// many text-showing operators survive — which is the only instrument left
    /// when a term is never contiguous in the bytes (one glyph per <c>Tj</c>,
    /// as #1047's document and fixture emit).
    /// </summary>
    public static IReadOnlyList<string> StreamBodies(byte[] saved)
    {
        var bodies = new List<string>();
        foreach (var (start, end) in StreamSpans(saved))
        {
            var raw = saved[start..end];
            var decoded = TryInflate(raw) ?? raw;
            // Decoding a >1GB body to a string overflows Latin1 GetString. A
            // stream that large is a ballooned-output pathology, not real text
            // to scan; skip it rather than crash. Small-fixture callers never
            // trip this.
            if (decoded.LongLength <= 256L * 1024 * 1024)
                bodies.Add(Encoding.Latin1.GetString(decoded));
        }
        return bodies;
    }

    /// <summary>
    /// The byte range of every stream body: from after the <c>stream</c>
    /// keyword's end-of-line to the next <c>endstream</c>.
    /// </summary>
    private static IEnumerable<(int Start, int End)> StreamSpans(byte[] saved)
    {
        var i = 0;
        while (true)
        {
            var start = IndexOf(saved, "stream", i);
            if (start < 0) yield break;

            var body = start + "stream".Length;
            if (body < saved.Length && saved[body] == (byte)'\r') body++;
            if (body < saved.Length && saved[body] == (byte)'\n') body++;

            var end = IndexOf(saved, "endstream", body);
            if (end < 0) yield break;

            yield return (body, end);
            i = end + "endstream".Length;
        }
    }

    /// <param name="ScanBytes">False for a stored stream body: the raw-file
    /// byte search has already covered its bytes.</param>
    private readonly record struct Region(string Where, byte[] Data, int From, int To, bool ScanBytes);

    /// <summary>The raw file, then every stream body, inflated where possible.</summary>
    private static IEnumerable<Region> Regions(byte[] saved)
    {
        yield return new Region("raw file", saved, 0, saved.Length, ScanBytes: true);
        var streamIndex = 0;
        foreach (var (start, end) in StreamSpans(saved))
        {
            var inflated = TryInflate(saved[start..end]);
            yield return inflated != null
                ? new Region($"inflated stream #{streamIndex}", inflated, 0, inflated.Length, ScanBytes: true)
                : new Region($"stream #{streamIndex}", saved, start, end, ScanBytes: false);
            streamIndex++;
        }
    }

    /// <summary>A string longer than this is binary data a stray delimiter opened, not text.</summary>
    private const int MaxStringBytes = 1 << 20;

    /// <summary>
    /// The value of every literal and hex string (§7.3.4) in
    /// <paramref name="data"/>[<paramref name="from"/>..<paramref name="to"/>).
    /// Self-contained on purpose: a scanner built on excise's own lexer would
    /// share every blind spot it exists to catch.
    ///
    /// <para>Comments and stream bodies are skipped: binary stream data is not
    /// syntax, and a stray <c>(</c> in it would open a literal that swallows
    /// every real string up to some later <c>)</c>. Each body is tokenized as
    /// its own region instead, so such a desync stays inside that body.</para>
    /// </summary>
    private static IEnumerable<byte[]> StringObjects(byte[] data, int from, int to)
    {
        var i = from;
        while (i < to)
        {
            var c = data[i];
            if (c == '%')
            {
                while (i < to && data[i] != '\r' && data[i] != '\n') i++;
            }
            else if (c == '(')
            {
                var (value, next) = LiteralString(data, i + 1, to);
                i = next;
                yield return value;
            }
            else if (c == '<' && i + 1 < to && data[i + 1] == '<')
            {
                i += 2;
            }
            else if (c == '<')
            {
                var (value, next) = HexString(data, i + 1, to);
                i = next;
                if (value != null) yield return value;
            }
            else if (c == '/')
            {
                // A name, so "/stream" is not the keyword.
                i++;
                while (i < to && IsRegularCharacter(data[i])) i++;
            }
            else if (IsRegularCharacter(c))
            {
                var start = i;
                while (i < to && IsRegularCharacter(data[i])) i++;
                if (data.AsSpan(start, i - start).SequenceEqual("stream"u8))
                {
                    var end = data.AsSpan(i, to - i).IndexOf("endstream"u8);
                    i = end < 0 ? to : i + end + "endstream".Length;
                }
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>§7.3.4.2, from just after the opening parenthesis.</summary>
    private static (byte[] Value, int Next) LiteralString(byte[] data, int i, int to)
    {
        var value = new List<byte>();
        var depth = 1;
        while (i < to && value.Count < MaxStringBytes)
        {
            var c = data[i++];
            if (c == '\\' && i < to)
            {
                var e = data[i++];
                if (e is >= (byte)'0' and <= (byte)'7')
                {
                    var code = e - '0';
                    for (var digits = 1; digits < 3 && i < to && data[i] is >= (byte)'0' and <= (byte)'7'; digits++)
                        code = code * 8 + data[i++] - '0';
                    value.Add((byte)code); // high-order overflow is ignored
                }
                else if (e == '\r')
                {
                    if (i < to && data[i] == '\n') i++; // line continuation
                }
                else if (e != '\n')
                {
                    // \( \) \\ stand for themselves, and so does any other
                    // character: the backslash is ignored.
                    value.Add(e switch
                    {
                        (byte)'n' => (byte)'\n',
                        (byte)'r' => (byte)'\r',
                        (byte)'t' => (byte)'\t',
                        (byte)'b' => (byte)'\b',
                        (byte)'f' => (byte)'\f',
                        _ => e,
                    });
                }
            }
            else if (c == '\r')
            {
                // An unescaped end-of-line of any form reads as a single LF.
                if (i < to && data[i] == '\n') i++;
                value.Add((byte)'\n');
            }
            else
            {
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) break;
                value.Add(c);
            }
        }
        return (value.ToArray(), i);
    }

    /// <summary>
    /// §7.3.4.3, from just after the opening angle bracket, or null when what
    /// follows is not hex digits and white space up to a <c>&gt;</c>: a stray
    /// <c>&lt;</c> in binary data or XML is not a string. NUL is white space,
    /// so the masked <c>/ID</c> reads as empty.
    /// </summary>
    private static (byte[]? Value, int Next) HexString(byte[] data, int i, int to)
    {
        var start = i;
        var value = new List<byte>();
        var high = -1;
        while (i < to && data[i] != '>' && value.Count < MaxStringBytes)
        {
            var c = data[i++];
            if (IsWhitespace(c)) continue;
            var nibble = HexValue(c);
            if (nibble < 0) return (null, start);
            if (high < 0) high = nibble;
            else { value.Add((byte)(high << 4 | nibble)); high = -1; }
        }
        if (i == to) return (null, start);
        if (high >= 0) value.Add((byte)(high << 4)); // an odd final digit is followed by 0
        return (value.ToArray(), data[i] == '>' ? i + 1 : i);
    }

    private static int HexValue(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        _ => -1,
    };

    /// <summary>A text string's value by its byte order mark (§7.9.2.2), or null when it has none.</summary>
    private static (string Encoding, string Text)? MarkedTextString(byte[] s) =>
        s is [0xFE, 0xFF, ..] ? ("UTF-16BE", Encoding.BigEndianUnicode.GetString(s, 2, s.Length - 2))
        : s is [0xFF, 0xFE, ..] ? ("UTF-16LE", Encoding.Unicode.GetString(s, 2, s.Length - 2))
        : s is [0xEF, 0xBB, 0xBF, ..] ? ("UTF-8", Encoding.UTF8.GetString(s, 3, s.Length - 3))
        : null;

    /// <summary>
    /// A string's text as a reader decodes it: by its byte order mark, else as
    /// PDFDocEncoding. An unmarked string is also read as UTF-16BE and UTF-8,
    /// the encodings the byte search looks for, so a hex glyph string or
    /// escaped UTF-8 is not missed.
    /// </summary>
    private static IEnumerable<string> DecodeTextString(byte[] s)
    {
        if (MarkedTextString(s) is { } marked)
        {
            yield return marked.Text;
            yield break;
        }
        yield return string.Create(s.Length, s, (chars, bytes) =>
        {
            for (var i = 0; i < bytes.Length; i++) chars[i] = PdfDocChars[bytes[i]];
        });
        yield return Encoding.BigEndianUnicode.GetString(s);
        yield return Encoding.UTF8.GetString(s);
    }

    /// <summary>
    /// Annex D, Table D.2, indexed by byte. PDFDocEncoding differs from Latin-1
    /// only at 0x18–0x1F and 0x80–0xA0; the undefined codes (0x7F, 0x9F, 0xAD)
    /// keep their Latin-1 reading.
    /// </summary>
    private static readonly string PdfDocChars = string.Create(256, 0, (chars, _) =>
    {
        const string low = "\u02D8\u02C7\u02C6\u02D9\u02DD\u02DB\u02DA\u02DC";
        const string high =
            "\u2022\u2020\u2021\u2026\u2014\u2013\u0192\u2044\u2039\u203A\u2212\u2030\u201E\u201C\u201D\u2018" +
            "\u2019\u201A\u2122\uFB01\uFB02\u0141\u0152\u0160\u0178\u017D\u0131\u0142\u0153\u0161\u017E\u009F\u20AC";
        for (var b = 0; b < 256; b++)
            chars[b] = b switch
            {
                >= 0x18 and <= 0x1F => low[b - 0x18],
                >= 0x80 and <= 0xA0 => high[b - 0x80],
                _ => (char)b,
            };
    });

    /// <summary><paramref name="term"/> in PDFDocEncoding, or null when it has a character the encoding cannot carry.</summary>
    private static byte[]? PdfDocEncode(string term)
    {
        var bytes = new byte[term.Length];
        for (var i = 0; i < term.Length; i++)
        {
            var b = PdfDocChars.IndexOf(term[i]);
            if (b < 0) return null;
            bytes[i] = (byte)b;
        }
        return bytes;
    }

    /// <summary>
    /// Blank the STRING VALUES of the trailer/xref-stream <c>/ID</c> array
    /// (§14.4) so a short needle cannot collide with them.
    ///
    /// <para><b>Why this exclusion exists.</b> <c>/ID</c> is a random 16-byte
    /// file identifier written as uppercase hex. It is content-INDEPENDENT
    /// serialization metadata: no page text, no carrier, nothing a redaction
    /// could leak into. But a 3-character ASCII needle has a ~1-in-a-few-dozen
    /// chance of appearing in 32 random hex digits, so scanning it makes every
    /// short-term absence assertion intermittently red. Observed at least four
    /// times on <c>FullwidthFormsRedactionTests</c> alone — <c>/ID</c>
    /// <c>479CCFE4C1FA...DABC6B61...</c> matched needle <c>ABC</c>, and
    /// <c>F37F6B37EA4F42180EF0F2141237C322</c> matched <c>123</c>, both times
    /// with a provably clean redacted page (#1295, #771, #800).</para>
    ///
    /// <para><b>Why it is here and not in each test.</b> The exclusion used to
    /// live in a hand-rolled per-test searchable view; #1049's migration to
    /// this shared scanner dropped it and reintroduced the flake. One shared
    /// policy is the fix — see <c>SavedPdfLeakScannerTests</c>, which pins both
    /// directions on a forced <c>/ID</c>.</para>
    ///
    /// <para><b>Scope is deliberately narrow.</b> Only strings <i>inside an
    /// <c>/ID [ … ]</c> array</i> are blanked — not hex strings generally, since
    /// a <c>&lt;…&gt;</c> in a content stream is a real text carrier and
    /// blanking those would make the scanner blind to exactly the leaks it
    /// exists to catch. Bytes are overwritten IN PLACE with NUL at the same
    /// length, so every file offset, the raw/UTF-16BE/UTF-8 views, and all
    /// stream boundaries stay valid.</para>
    /// </summary>
    internal static byte[] MaskFileIdentifier(byte[] saved)
    {
        byte[]? masked = null;
        var i = 0;

        while (true)
        {
            var at = IndexOf(saved, "/ID", i);
            if (at < 0) break;
            i = at + 3;

            // "/IDS" is a different name; the identifier key ends here.
            if (i < saved.Length && IsRegularCharacter(saved[i])) continue;

            var p = SkipWhitespace(saved, i);
            // The file identifier is always an ARRAY of one or two strings.
            // Anything else that happens to be named /ID is not it.
            if (p >= saved.Length || saved[p] != (byte)'[') continue;
            p++;

            masked ??= (byte[])saved.Clone();

            while (p < saved.Length && saved[p] != (byte)']')
            {
                if (saved[p] == (byte)'<')
                {
                    p++;
                    while (p < saved.Length && saved[p] != (byte)'>') masked[p++] = 0;
                }
                else if (saved[p] == (byte)'(')
                {
                    // §7.3.4.2 literal-string form, legal though rarely emitted.
                    p++;
                    for (var depth = 1; p < saved.Length && depth > 0; p++)
                    {
                        if (saved[p] == (byte)'\\') { masked[p] = 0; p++; if (p < saved.Length) masked[p] = 0; continue; }
                        if (saved[p] == (byte)'(') depth++;
                        else if (saved[p] == (byte)')' && --depth == 0) break;
                        masked[p] = 0;
                    }
                }
                p++;
            }

            i = p;
        }

        return masked ?? saved;
    }

    private static int SkipWhitespace(byte[] b, int from)
    {
        var i = from;
        while (i < b.Length && IsWhitespace(b[i])) i++;
        return i;
    }

    /// <summary>§7.2.3, Table 1.</summary>
    private static bool IsWhitespace(byte c)
        => c == ' ' || c == '\r' || c == '\n' || c == '\t' || c == '\f' || c == 0;

    /// <summary>§7.2.2: anything that is not whitespace or a delimiter.</summary>
    private static bool IsRegularCharacter(byte c)
        => !(IsWhitespace(c)
             || c == '(' || c == ')' || c == '<' || c == '>' || c == '['
             || c == ']' || c == '{' || c == '}' || c == '/' || c == '%');

    /// <summary>
    /// Inflate a zlib/deflate stream body, or null when it is not compressed
    /// (already scanned as part of the raw file) or cannot be inflated.
    /// </summary>
    private static byte[]? TryInflate(byte[] raw)
    {
        if (raw.Length < 2) return null;

        foreach (var zlib in new[] { true, false })
        {
            try
            {
                using var input = new MemoryStream(raw);
                using Stream decoder = zlib
                    ? new ZLibStream(input, CompressionMode.Decompress)
                    : new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                decoder.CopyTo(output);
                if (output.Length > 0) return output.ToArray();
            }
            catch (InvalidDataException) { /* not this encoding — try the other */ }
            catch (NotSupportedException) { }
        }

        return null;
    }

    /// <summary>
    /// Byte-exact substring search. Size-safe on multi-hundred-MB haystacks
    /// where decoding to a string would overflow (see <see cref="FindTerm"/>).
    /// An empty needle never matches — a term with no bytes is not a leak.
    /// </summary>
    private static bool ContainsBytes(byte[] haystack, byte[] needle)
        => needle.Length > 0 && haystack.AsSpan().IndexOf(needle) >= 0;

    private static int IndexOf(byte[] haystack, string needle, int from)
    {
        from = Math.Max(0, from);
        if (from >= haystack.Length) return -1;
        var at = haystack.AsSpan(from).IndexOf(Encoding.ASCII.GetBytes(needle));
        return at < 0 ? -1 : from + at;
    }
}
