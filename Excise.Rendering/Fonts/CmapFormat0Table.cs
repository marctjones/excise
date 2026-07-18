using SkiaSharp;

namespace Excise.Rendering.Fonts;

/// <summary>
/// Reads the format-0 (byte-encoding) subtable from a TrueType <c>cmap</c>
/// when the typeface has no Unicode-mapped subtable Skia's text shaper can
/// use.
///
/// Why this exists.
/// Subset TrueType fonts produced by veraPDF Test Builder, LibreOffice's PDF
/// export and Office often ship with a single <c>platformID=1, encodingID=0</c>
/// (Macintosh Roman) cmap whose entries map the original PDF byte codes
/// (0x01..0x05 etc.) directly to internal glyph IDs. Skia's
/// <see cref="SKFont.GetGlyph"/> and shaper require a Unicode-mapped subtable
/// (platform=0 or platform=3 encoding=1, format 4 or 12), so when only a
/// format-0 subtable is present every glyph resolves to <c>.notdef</c> and
/// pages render blank — even though excise's parser extracts the text correctly
/// via <c>/ToUnicode</c>.
///
/// To draw such fonts excise parses the format-0 subtable here and dispatches
/// through <see cref="SKTextEncoding.GlyphId"/>, sidestepping cmap selection
/// entirely.
/// </summary>
internal static class CmapFormat0Table
{
    // Big-endian 4-byte table tag for "cmap" — SkiaSharp's
    // SKTypeface.GetTableData takes the tag as a uint.
    private const uint CmapTableTag =
        ((uint)'c' << 24) | ((uint)'m' << 16) | ((uint)'a' << 8) | (uint)'p';

    /// <summary>
    /// Returns a 256-entry byte→glyphId map taken from any format-0 cmap
    /// subtable in <paramref name="typeface"/>, or null if no format-0
    /// subtable is present, the table is malformed, or every entry is
    /// <c>.notdef</c> (glyph 0). Indices with no glyph mapping are 0.
    /// </summary>
    public static ushort[]? TryRead(SKTypeface typeface)
    {
        byte[]? data;
        try { data = typeface.GetTableData(CmapTableTag); }
        catch { return null; }
        if (data == null || data.Length < 4) return null;

        // cmap header: uint16 version, uint16 numTables.
        int numTables = ReadU16(data, 2);
        if (numTables <= 0 || 4 + numTables * 8 > data.Length) return null;

        for (int i = 0; i < numTables; i++)
        {
            // EncodingRecord: uint16 platformID, uint16 encodingID,
            //                 uint32 subtableOffset (from start of cmap).
            int recOffset = 4 + i * 8;
            uint subtableOffset = ReadU32(data, recOffset + 4);

            // Format-0 subtable size = 6 (header) + 256 (glyphIdArray).
            if (subtableOffset > int.MaxValue ||
                subtableOffset + 6 + 256 > (uint)data.Length) continue;

            int headerOff = (int)subtableOffset;
            int format = ReadU16(data, headerOff);
            if (format != 0) continue;

            // Format 0 layout:
            //   uint16 format (=0), uint16 length, uint16 language,
            //   uint8 glyphIdArray[256].
            int arrayStart = headerOff + 6;
            var map = new ushort[256];
            bool anyGlyph = false;
            for (int b = 0; b < 256; b++)
            {
                ushort gid = data[arrayStart + b];
                map[b] = gid;
                if (gid != 0) anyGlyph = true;
            }
            return anyGlyph ? map : null;
        }
        return null;
    }

    private static int ReadU16(byte[] d, int o) =>
        (d[o] << 8) | d[o + 1];

    private static uint ReadU32(byte[] d, int o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) |
        ((uint)d[o + 2] << 8) | (uint)d[o + 3];
}
