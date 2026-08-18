using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Excise.Core.Tests.Text.Segmentation;

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

        void Scan(byte[] haystack, string where)
        {
            var text = Encoding.Latin1.GetString(haystack);
            if (text.Contains(term, StringComparison.Ordinal))
                hits.Add($"{where}: ASCII");

            // UTF-16BE is how a PDF text string carries non-Latin-1 content;
            // /Info, /Contents and outline titles all use it routinely.
            var utf16 = Encoding.BigEndianUnicode.GetString(haystack);
            if (utf16.Contains(term, StringComparison.Ordinal))
                hits.Add($"{where}: UTF-16BE");
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
            bodies.Add(Encoding.Latin1.GetString(TryInflate(raw) ?? raw));

            i = end + "endstream".Length;
        }
        return bodies;
    }

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
