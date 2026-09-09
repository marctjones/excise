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
    /// searching raw bytes and inflated stream bodies, in ASCII and UTF-16BE.
    /// Returns a human-readable location per hit — empty means clean.
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
            // UTF-16BE is how a PDF text string carries non-Latin-1 content;
            // /Info, /Contents and outline titles all use it routinely.
            if (ContainsBytes(haystack, utf16Bytes))
                hits.Add($"{where}: UTF-16BE");
            // UTF-8 is what an XMP /Metadata stream carries (§14.3.2), and it
            // differs from Latin-1 for exactly the non-ASCII terms these tests
            // exist for — Arabic, CJK, accented Latin.
            if (ContainsBytes(haystack, utf8Bytes))
                hits.Add($"{where}: UTF-8");
        }

        Scan(saved, "raw file");

        var i = 0;
        var streamIndex = 0;
        while (true)
        {
            var start = IndexOf(saved, "stream", i);
            if (start < 0) break;

            var body = start + "stream".Length;
            if (body < saved.Length && saved[body] == (byte)'\r') body++;
            if (body < saved.Length && saved[body] == (byte)'\n') body++;

            var end = IndexOf(saved, "endstream", body);
            if (end < 0) break;

            var raw = new byte[end - body];
            Array.Copy(saved, body, raw, 0, raw.Length);

            var inflated = TryInflate(raw);
            if (inflated != null)
                Scan(inflated, $"inflated stream #{streamIndex}");

            streamIndex++;
            i = end + "endstream".Length;
        }

        return hits;
    }

    /// <summary>
    /// The saved file rendered as searchable text across every carrier — raw
    /// bytes and inflated stream bodies, in Latin-1 and UTF-16BE.
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
        var i = 0;
        while (true)
        {
            var start = IndexOf(saved, "stream", i);
            if (start < 0) break;

            var body = start + "stream".Length;
            if (body < saved.Length && saved[body] == (byte)'\r') body++;
            if (body < saved.Length && saved[body] == (byte)'\n') body++;

            var end = IndexOf(saved, "endstream", body);
            if (end < 0) break;

            var raw = new byte[end - body];
            Array.Copy(saved, body, raw, 0, raw.Length);
            var decoded = TryInflate(raw) ?? raw;
            // Decoding a >1GB body to a string overflows Latin1 GetString. A
            // stream that large is a ballooned-output pathology, not real text
            // to scan; skip it rather than crash. Small-fixture callers never
            // trip this.
            if (decoded.LongLength <= 256L * 1024 * 1024)
                bodies.Add(Encoding.Latin1.GetString(decoded));

            i = end + "endstream".Length;
        }
        return bodies;
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
        while (i < b.Length && (b[i] == ' ' || b[i] == '\r' || b[i] == '\n'
                                || b[i] == '\t' || b[i] == '\f' || b[i] == 0)) i++;
        return i;
    }

    /// <summary>§7.2.2: anything that is not whitespace or a delimiter.</summary>
    private static bool IsRegularCharacter(byte c)
        => !(c == ' ' || c == '\r' || c == '\n' || c == '\t' || c == '\f' || c == 0
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
    {
        if (needle.Length == 0) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    private static int IndexOf(byte[] haystack, string needle, int from)
    {
        var pat = Encoding.ASCII.GetBytes(needle);
        for (var i = Math.Max(0, from); i <= haystack.Length - pat.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < pat.Length; j++)
                if (haystack[i + j] != pat[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
