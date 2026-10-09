using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

            // Shown text (#2034): a term split across a kerned TJ array, or one
            // glyph per Tj, is in no single string. Only stream bodies are
            // content streams; the raw-file region is the whole file.
            if (region.IsStream)
                foreach (var run in ShownTextRuns(region.Data, region.From, region.To))
                {
                    if (run.Operands.Count < 2) continue; // one operand is ScanString's job
                    if (RunMatches(run.Bytes) && !run.Operands.Any(RunMatches))
                    {
                        var hit = $"{region.Where}: shown text split across {run.Operands.Count} string operands";
                        if (!hits.Contains(hit)) hits.Add(hit);
                    }
                }
        }

        return hits;

        bool RunMatches(byte[] value) =>
            (pdfDocBytes != null && ContainsBytes(value, pdfDocBytes))
            || ContainsBytes(value, utf16Bytes)
            || ContainsBytes(value, utf8Bytes);
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
        foreach (var span in StreamSpans(saved))
        {
            var raw = saved[span.Start..span.End];
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
    /// Every stream body in the file: from after the <c>stream</c> keyword's
    /// end-of-line to the next <c>endstream</c>, never by <c>/Length</c>, which
    /// is the file describing itself.
    /// </summary>
    private static IEnumerable<Syntax> StreamSpans(byte[] saved) =>
        Walk(saved, 0, saved.Length).Where(s => s.String == null);

    /// <param name="ScanBytes">False for a stored stream body: the raw-file
    /// byte search has already covered its bytes.</param>
    /// <param name="IsStream">A stream body (a candidate content or appearance stream), not the raw file.</param>
    private readonly record struct Region(string Where, byte[] Data, int From, int To, bool ScanBytes, bool IsStream = false);

    /// <summary>The raw file, then every stream body, inflated where possible.</summary>
    private static IEnumerable<Region> Regions(byte[] saved)
    {
        yield return new Region("raw file", saved, 0, saved.Length, ScanBytes: true);
        var streamIndex = 0;
        foreach (var span in StreamSpans(saved))
        {
            var where = $"stream #{streamIndex}" + (span.ByteSearch ? " (byte search: the syntax walk lost sync)" : "");
            var inflated = TryInflate(saved[span.Start..span.End]);
            yield return inflated != null
                ? new Region($"inflated {where}", inflated, 0, inflated.Length, ScanBytes: true, IsStream: true)
                : new Region(where, saved, span.Start, span.End, ScanBytes: false, IsStream: true);
            streamIndex++;
        }
    }

    /// <summary>The value of every literal and hex string (§7.3.4) in <paramref name="data"/>[<paramref name="from"/>..<paramref name="to"/>).</summary>
    private static IEnumerable<byte[]> StringObjects(byte[] data, int from, int to) =>
        Walk(data, from, to).Where(s => s.String != null).Select(s => s.String!);

    /// <summary>A string object's value, or, when <see cref="String"/> is null, a stream body's byte range.</summary>
    /// <param name="ByteSearch">The body was found by <see cref="ByteSearchSpans"/>, not by the walk.</param>
    private readonly record struct Syntax(byte[]? String, int Start, int End, bool ByteSearch = false);

    /// <summary>
    /// Every string (§7.3.4) and stream body (§7.3.8) in
    /// <paramref name="data"/>[<paramref name="from"/>..<paramref name="to"/>).
    /// Self-contained on purpose: a scanner built on excise's own lexer would
    /// share every blind spot it exists to catch.
    ///
    /// <para>Comments and stream bodies are skipped: binary stream data is not
    /// syntax, and a stray <c>(</c> in it would open a literal that swallows
    /// every real string up to some later <c>)</c>. Each body is tokenized as
    /// its own region instead, so such a desync stays inside that body.</para>
    ///
    /// <para>Only a token is the <c>stream</c> keyword: the word in a string,
    /// comment or name before it would start the body there and hide the real
    /// one (#1855). Where the walk loses sync, at a literal that never closes or
    /// an <c>endstream</c> no keyword opened, the bodies after the last one it
    /// found come from the byte search instead.</para>
    /// </summary>
    private static IEnumerable<Syntax> Walk(byte[] data, int from, int to, List<(int Start, int End)>? idRanges = null)
    {
        var i = from;
        var found = from; // every body before this was found
        var lost = false;
        while (i < to)
        {
            var c = data[i];
            if (c == '%')
            {
                while (i < to && data[i] != '\r' && data[i] != '\n') i++;
            }
            else if (c == '(')
            {
                var (value, next, closed) = LiteralString(data, i + 1, to);
                i = next;
                yield return new Syntax(value, 0, 0);
                if (!closed && !lost)
                {
                    lost = true;
                    foreach (var span in ByteSearchSpans(data, found, to)) yield return span;
                }
            }
            else if (c == '<' && i + 1 < to && data[i + 1] == '<')
            {
                i += 2;
            }
            else if (c == '<')
            {
                var (value, next) = HexString(data, i + 1, to);
                i = next;
                if (value != null) yield return new Syntax(value, 0, 0);
            }
            else if (c == '/')
            {
                // A name, so "/stream" is not the keyword, and "/ID" is the
                // identifier key only here, never inside a string (#1858).
                var name = ++i;
                while (i < to && IsRegularCharacter(data[i])) i++;
                if (idRanges != null && data.AsSpan(name, i - name).SequenceEqual("ID"u8))
                    i = FileIdentifier(data, i, to, idRanges);
            }
            else if (IsRegularCharacter(c))
            {
                var start = i;
                while (i < to && IsRegularCharacter(data[i])) i++;
                // A writer that leaves out the end-of-line runs the keyword into its data.
                if (data.AsSpan(start, i - start).StartsWith("stream"u8))
                {
                    var body = BodyStart(data, start + "stream".Length, to);
                    var end = data.AsSpan(body, to - body).IndexOf("endstream"u8);
                    if (end < 0)
                    {
                        // A truncated file: the body runs to its end.
                        i = to;
                        if (!lost) yield return new Syntax(null, body, to);
                        continue;
                    }
                    i = found = body + end + "endstream".Length;
                    if (!lost) yield return new Syntax(null, body, body + end);
                }
                else if (!lost && data.AsSpan(start, i - start).SequenceEqual("endstream"u8))
                {
                    foreach (var span in ByteSearchSpans(data, found, i)) yield return span;
                    found = i;
                }
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// The byte search the walk falls back on: each <c>endstream</c> in
    /// <paramref name="data"/>[<paramref name="from"/>..<paramref name="to"/>)
    /// ends a body that starts after the NEAREST <c>stream</c> before it, since
    /// a string ahead of the keyword can hold the word. A body that holds the
    /// word itself loses its head here; only the walk reads that whole.
    /// </summary>
    private static IEnumerable<Syntax> ByteSearchSpans(byte[] data, int from, int to)
    {
        while (true)
        {
            var end = data.AsSpan(from, to - from).IndexOf("endstream"u8);
            if (end < 0) yield break;
            end += from;
            var keyword = data.AsSpan(from, end - from).LastIndexOf("stream"u8);
            if (keyword >= 0)
                yield return new Syntax(null, BodyStart(data, from + keyword + "stream".Length, end), end, ByteSearch: true);
            from = end + "endstream".Length;
        }
    }

    /// <summary>After the keyword's end-of-line: CR LF, LF, or a bare CR.</summary>
    private static int BodyStart(byte[] data, int body, int to)
    {
        if (body < to && data[body] == '\r') body++;
        if (body < to && data[body] == '\n') body++;
        return body;
    }

    /// <summary>§7.3.4.2, from just after the opening parenthesis; <c>Closed</c> is false when the string ran out first.</summary>
    private static (byte[] Value, int Next, bool Closed) LiteralString(byte[] data, int i, int to)
    {
        var value = new List<byte>();
        var depth = 1;
        while (i < to)
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
                else if (c == ')' && --depth == 0) return (value.ToArray(), i, true);
                value.Add(c);
            }
        }
        return (value.ToArray(), i, false);
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
        while (i < to && data[i] != '>')
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
    /// short-term absence assertion intermittently red (#1295, #771, #800).</para>
    ///
    /// <para><b>Scope is deliberately narrow.</b> Only strings <i>inside an
    /// <c>/ID [ … ]</c> array</i> are blanked, and only where <c>/ID</c> is a
    /// name token in the <see cref="Walk"/>: the same characters inside a
    /// string, comment or stream body are content, and blanking them made the
    /// scanner blind to exactly the text it exists to find (#1858). Bytes are
    /// overwritten IN PLACE with NUL at the same length, so every file offset
    /// and all stream boundaries stay valid.</para>
    /// </summary>
    internal static byte[] MaskFileIdentifier(byte[] saved)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var _ in Walk(saved, 0, saved.Length, ranges)) { }
        if (ranges.Count == 0) return saved;

        var masked = (byte[])saved.Clone();
        foreach (var (start, end) in ranges)
            masked.AsSpan(start, end - start).Clear();
        return masked;
    }

    /// <summary>
    /// After an <c>/ID</c> name at <paramref name="i"/>: when an array follows,
    /// records the content range of each string in it and returns the index
    /// after the array. Anything else named /ID is not the file identifier.
    /// </summary>
    private static int FileIdentifier(byte[] data, int i, int to, List<(int Start, int End)> ranges)
    {
        var p = SkipWhitespace(data, i);
        if (p >= to || data[p] != (byte)'[') return i;
        p++;
        while (p < to && data[p] != (byte)']')
        {
            if (data[p] == (byte)'<')
            {
                var start = ++p;
                while (p < to && data[p] != (byte)'>') p++;
                ranges.Add((start, p));
            }
            else if (data[p] == (byte)'(')
            {
                var (_, next, closed) = LiteralString(data, p + 1, to);
                ranges.Add((p + 1, closed ? next - 1 : next));
                p = next;
                continue;
            }
            p++;
        }
        return p;
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
            using var output = new MemoryStream();
            try
            {
                using var input = new MemoryStream(raw);
                using Stream decoder = zlib
                    ? new ZLibStream(input, CompressionMode.Decompress)
                    : new DeflateStream(input, CompressionMode.Decompress);
                decoder.CopyTo(output);
            }
            catch (InvalidDataException) { /* not this encoding, or truncated: keep what inflated */ }
            catch (NotSupportedException) { }
            if (output.Length > 0) return output.ToArray();
        }

        return null;
    }

    /// <summary>One run of shown text: the string operands and their concatenated bytes.</summary>
    internal readonly record struct ShownRun(byte[] Bytes, IReadOnlyList<byte[]> Operands);

    /// <summary>A TJ adjustment at or beyond this (thousandths of an em, leftwards = negative) is a word gap.</summary>
    private const double WordGapAdjustment = -250;

    /// <summary>Shown-text runs of one decoded stream body; see <see cref="ShownTextRuns(byte[], int, int)"/>.</summary>
    internal static IReadOnlyList<ShownRun> ShownTextRuns(byte[] body) => ShownTextRuns(body, 0, body.Length);

    /// <summary>
    /// What a content or appearance stream DRAWS, as runs of consecutive shown
    /// strings (#2034). Text split as <c>[(ALP) -20 (HA) 10 (OLD)] TJ</c> or
    /// one glyph per <c>Tj</c> is in no single string object, so the per-string
    /// search cannot see it while the file still draws it.
    ///
    /// <para><b>The rule.</b> A run is the string operands of <c>Tj</c> and
    /// <c>TJ</c> (kerning numbers ignored) shown one after another with nothing
    /// between them that moves the text to a different place. The run ENDS at:
    /// <c>BT</c>/<c>ET</c>; <c>T*</c>; <c>'</c> and <c>"</c> (they start a new
    /// line, and begin the next run); <c>Td</c>/<c>TD</c> with a non-zero y
    /// offset; <c>Tm</c> unless it repeats the previous <c>Tm</c>'s
    /// a b c d f (same line, same orientation); <c>cm</c>, <c>q</c>, <c>Q</c>,
    /// <c>Do</c>; and a <c>TJ</c> adjustment of -250 or more to the left, which
    /// is a word gap rather than kerning. Strings are never joined across
    /// streams, which keeps a field's appearance apart from the page.</para>
    ///
    /// <para><b>False-positive boundary.</b> Not a break: <c>Tf</c> (a word may
    /// change font), a horizontal-only <c>Td</c>/<c>Tm</c> (per-glyph
    /// positioning is the case to catch), <c>Tc</c>/<c>Tw</c>/<c>Tz</c> and
    /// kerning tighter than a word gap. So two strings on one baseline that a
    /// horizontal move separates by a column are joined, and a term that spans
    /// them without a space is reported. Bytes are joined as shown, so a
    /// two-byte-code font joins as UTF-16BE and a term is searched in the
    /// encodings <see cref="FindTerm"/> uses for string objects. A glyph a
    /// font maps to other characters is out of reach, as it is for every byte
    /// search here; mutool is the oracle for that.</para>
    ///
    /// <para>Self-contained on purpose, like <see cref="Walk"/>: the gate must
    /// not share excise's content-stream lexer.</para>
    /// </summary>
    internal static IReadOnlyList<ShownRun> ShownTextRuns(byte[] data, int from, int to)
    {
        var runs = new List<ShownRun>();
        var current = new List<byte[]>();
        double[]? lineMatrix = null;

        void End()
        {
            if (current.Count > 0)
            {
                runs.Add(new ShownRun(current.SelectMany(o => o).ToArray(), current.ToArray()));
                current = new List<byte[]>();
            }
            lineMatrix = null;
        }

        var operands = new List<object>();
        var arrays = new Stack<List<object>>();
        void Push(object o) => (arrays.Count > 0 ? arrays.Peek() : operands).Add(o);

        void Operator(string op)
        {
            switch (op)
            {
                case "BT": case "ET": case "T*": case "cm": case "q": case "Q": case "Do":
                    End();
                    break;
                case "Tj":
                    if (operands.Count > 0 && operands[^1] is byte[] shown) current.Add(shown);
                    break;
                case "TJ":
                    if (operands.Count > 0 && operands[^1] is List<object> array)
                        foreach (var element in array)
                        {
                            if (element is byte[] part) current.Add(part);
                            else if (element is double adjustment && adjustment <= WordGapAdjustment) End();
                        }
                    break;
                case "'": case "\"":
                    End();
                    if (operands.Count > 0 && operands[^1] is byte[] line) current.Add(line);
                    break;
                case "Td": case "TD":
                    if (operands.Count < 2 || operands[^1] is not double ty || ty != 0) End();
                    break;
                case "Tm":
                    var m = operands.OfType<double>().TakeLast(6).ToArray();
                    if (m.Length != 6 || lineMatrix == null
                        || m[0] != lineMatrix[0] || m[1] != lineMatrix[1] || m[2] != lineMatrix[2]
                        || m[3] != lineMatrix[3] || m[5] != lineMatrix[5])
                        End();
                    lineMatrix = m.Length == 6 ? m : null;
                    break;
            }
        }

        var i = from;
        while (i < to)
        {
            var c = data[i];
            if (IsWhitespace(c)) { i++; continue; }
            switch (c)
            {
                case (byte)'%':
                    while (i < to && data[i] != '\r' && data[i] != '\n') i++;
                    break;
                case (byte)'(':
                {
                    var (value, next, _) = LiteralString(data, i + 1, to);
                    i = next;
                    Push(value);
                    break;
                }
                case (byte)'<' when i + 1 < to && data[i + 1] == '<':
                case (byte)'>' when i + 1 < to && data[i + 1] == '>':
                    i += 2;
                    break;
                case (byte)'<':
                {
                    var (value, next) = HexString(data, i + 1, to);
                    if (value != null) { Push(value); i = next; } else i++;
                    break;
                }
                case (byte)'[':
                    arrays.Push(new List<object>());
                    i++;
                    break;
                case (byte)']':
                    i++;
                    if (arrays.Count > 0)
                    {
                        var array = arrays.Pop();
                        Push(array);
                    }
                    break;
                case (byte)'/':
                    i++;
                    while (i < to && IsRegularCharacter(data[i])) i++;
                    Push("/");
                    break;
                default:
                {
                    if (!IsRegularCharacter(c)) { i++; break; }
                    var start = i;
                    while (i < to && IsRegularCharacter(data[i])) i++;
                    var word = Encoding.Latin1.GetString(data, start, i - start);
                    if (double.TryParse(word, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var number))
                    {
                        Push(number);
                        break;
                    }
                    arrays.Clear();
                    if (word == "ID") i = SkipInlineImage(data, i, to);
                    else Operator(word);
                    operands.Clear();
                    break;
                }
            }
        }
        End();
        return runs;
    }

    /// <summary>After an <c>ID</c> operator: the index after the <c>EI</c> that ends the image data.</summary>
    private static int SkipInlineImage(byte[] data, int i, int to)
    {
        for (var p = i; p + 1 < to; p++)
            if (data[p] == 'E' && data[p + 1] == 'I'
                && IsWhitespace(data[p - 1])
                && (p + 2 >= to || IsWhitespace(data[p + 2])))
                return p + 2;
        return to;
    }

    /// <summary>
    /// Byte-exact substring search. Size-safe on multi-hundred-MB haystacks
    /// where decoding to a string would overflow (see <see cref="FindTerm"/>).
    /// An empty needle never matches — a term with no bytes is not a leak.
    /// </summary>
    private static bool ContainsBytes(byte[] haystack, byte[] needle)
        => needle.Length > 0 && haystack.AsSpan().IndexOf(needle) >= 0;
}
