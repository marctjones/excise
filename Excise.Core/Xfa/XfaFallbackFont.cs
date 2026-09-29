using System.Buffers.Binary;
using System.Text;
using Excise.Core.Graphics;

namespace Excise.Core.Xfa;

/// <summary>
/// A system font for XFA text that base-14 fonts cannot draw (CJK, Cyrillic, Greek...),
/// #1577. Excise ships no font: the first installed font from a list of known wide-coverage
/// Unicode fonts is used, read from the platform's font directory by our own sfnt code (no
/// native font API), and embedded as a subset through <see cref="PdfFont.FromTrueType(byte[], double)"/>.
/// Nothing here runs unless a laid-out form has such text, so other documents pay nothing.
/// </summary>
internal static class XfaFallbackFont
{
    /// <summary>Font files larger than this are not read.</summary>
    private const long MaxFontBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The font bytes plus a size-1 <see cref="PdfFont"/> for measuring, held together so a
    /// reload re-derives both from one file read.
    /// </summary>
    private sealed class Cached
    {
        public readonly byte[] Bytes;
        public readonly PdfFont Metrics; // Size 1: widths scale linearly. Never adds glyphs to a subset.
        public Cached(byte[] bytes)
        {
            Bytes = bytes;
            Metrics = PdfFont.FromTrueType(bytes, 1);
        }
    }

    private static readonly object Sync = new();

    // #1921: the resolved path (and the fact that no candidate was found) are cheap and never
    // change while the process runs, so they are cached strongly. The font bytes themselves
    // (tens of MB, e.g. Arial Unicode) are cached only weakly: reused across documents opened
    // back-to-back in the same session as long as something keeps them reachable, but
    // reclaimable under memory pressure or once nothing has needed them for a while, instead
    // of being held for the life of the process (#1921).
    private static string? _resolvedPath;
    private static bool _discoveryFailed;
    private static WeakReference<Cached>? _cache;

    private static Cached? GetOrLoad()
    {
        lock (Sync)
        {
            if (_cache != null && _cache.TryGetTarget(out var cached))
                return cached;

            var bytes = _resolvedPath != null ? LoadFromPath(_resolvedPath) : null;
            if (bytes == null)
            {
                if (_discoveryFailed)
                    return null;
                var found = DiscoverPath();
                if (found == null)
                {
                    _discoveryFailed = true;
                    return null;
                }
                _resolvedPath = found;
                bytes = LoadFromPath(found);
                if (bytes == null)
                {
                    _discoveryFailed = true;
                    return null;
                }
            }

            var created = new Cached(bytes);
            _cache = new WeakReference<Cached>(created);
            return created;
        }
    }

    /// <summary>A new embeddable instance for one document, or null when no font was found.</summary>
    public static PdfFont? ForDocument(double size) => GetOrLoad() is { } cached ? PdfFont.FromTrueType(cached.Bytes, size) : null;

    /// <summary>
    /// Split <paramref name="text"/> into runs drawn with <paramref name="base14"/> and runs
    /// drawn with the fallback font. A character goes to the fallback only when base-14
    /// cannot encode it and the fallback has a glyph for it; anything else stays base-14
    /// and is drawn as '?' (still reported).
    /// </summary>
    public static List<(string Text, bool Fallback)> Segments(string text, PdfFont base14)
    {
        var fallback = GetOrLoad()?.Metrics;
        var segments = new List<(string, bool)>();
        var current = new StringBuilder();
        bool currentFallback = false;
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            var ch = char.IsSurrogatePair(text, i) ? text.Substring(i, 2) : text[i].ToString();
            bool useFallback = fallback != null && !base14.CanEncodeFully(ch) && fallback.CanEncodeFully(ch);
            if (current.Length > 0 && useFallback != currentFallback)
            {
                segments.Add((current.ToString(), currentFallback));
                current.Clear();
            }
            currentFallback = useFallback;
            current.Append(ch);
        }
        if (current.Length > 0)
            segments.Add((current.ToString(), currentFallback));
        return segments;
    }

    /// <summary>The width of <paramref name="text"/> in the fallback font at <paramref name="size"/> points.</summary>
    public static double Width(string text, double size) => (GetOrLoad()?.Metrics.MeasureWidth(text) ?? 0) * size;

    /// <summary>Scans <see cref="Candidates"/> for the first font that passes the coverage check.</summary>
    private static string? DiscoverPath()
    {
        foreach (var path in Candidates())
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxFontBytes)
                    continue;
                var bytes = LoadFromPath(path);
                if (bytes != null && PdfFont.FromTrueType(bytes, 1).CanEncodeFully("中Ж"))
                    return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException or IndexOutOfRangeException
                or InvalidOperationException or OverflowException)
            {
                // An unreadable or malformed file is skipped; the next candidate is tried.
            }
        }
        return null;
    }

    /// <summary>Reads a font file already known to be a suitable candidate, unwrapping a collection.</summary>
    private static byte[]? LoadFromPath(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return IsCollection(bytes) ? FirstFontOfCollection(bytes) : bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or IndexOutOfRangeException
            or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Known fonts that cover CJK and the common non-Latin scripts, per platform, most
    /// complete first. A font that lacks '中' or 'Ж' is passed over.
    /// </summary>
    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts/Supplemental/Arial Unicode.ttf";
            yield return "/Library/Fonts/Arial Unicode.ttf";
            yield return "/System/Library/Fonts/Hiragino Sans GB.ttc";
            yield return "/System/Library/Fonts/Supplemental/Songti.ttc";
            yield return "/System/Library/Fonts/STHeiti Light.ttc";
        }
        else if (OperatingSystem.IsWindows())
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var name in new[] { "arialuni.ttf", "msyh.ttc", "msgothic.ttc", "simsun.ttc", "YuGothR.ttc", "malgun.ttf" })
                yield return Path.Combine(fonts, name);
        }
        else
        {
            foreach (var path in new[]
            {
                "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
                "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
                "/usr/share/fonts/google-noto-cjk/NotoSansCJK-Regular.ttc",
                "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
                "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc",
                "/usr/share/fonts/wqy-zenhei/wqy-zenhei.ttc",
                "/usr/share/fonts/truetype/arphic/uming.ttc",
            })
            {
                yield return path;
            }
        }
    }

    private static bool IsCollection(byte[] b) => b.Length >= 12 && b[0] == 't' && b[1] == 't' && b[2] == 'c' && b[3] == 'f';

    /// <summary>
    /// The first font of a TrueType collection as a standalone sfnt: its table directory
    /// with every table copied after it and the offsets rewritten (a collection's offsets
    /// are from the start of the whole file).
    /// </summary>
    internal static byte[]? FirstFontOfCollection(byte[] ttc)
    {
        if (!IsCollection(ttc) || BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(8)) == 0 || ttc.Length < 16)
            return null;
        int dir = checked((int)BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(12)));
        if (dir < 0 || dir + 12 > ttc.Length)
            return null;
        int tables = BinaryPrimitives.ReadUInt16BigEndian(ttc.AsSpan(dir + 4));
        int header = 12 + 16 * tables;
        if (tables == 0 || dir + header > ttc.Length)
            return null;

        long total = header;
        for (int i = 0; i < tables; i++)
        {
            int record = dir + 12 + 16 * i;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(record + 8));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(record + 12));
            if ((long)offset + length > ttc.Length)
                return null;
            total += (length + 3) & ~3L;
        }

        var sfnt = new byte[total];
        ttc.AsSpan(dir, header).CopyTo(sfnt);
        int at = header;
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + 16 * i;
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(record + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(record + 12));
            ttc.AsSpan(offset, length).CopyTo(sfnt.AsSpan(at));
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(record + 8), (uint)at);
            at += (length + 3) & ~3;
        }
        return sfnt;
    }
}
