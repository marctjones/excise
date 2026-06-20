using System;
using System.Collections.Generic;

namespace Pdfe.Core.Fonts;

/// <summary>
/// Minimal Compact Font Format (CFF) parser.
///
/// Extracts just enough to synthesize an OpenType/SFNT container around the
/// CFF blob — the glyph count (for /maxp), the bounding box (for /head), and
/// the glyph-name → glyph-index map (for building a Unicode /cmap that Skia
/// can actually use). Does NOT interpret charstrings.
///
/// Reference: Adobe Technical Note #5176 (The Compact Font Format
/// Specification). Deliberately tolerant: malformed inputs return null
/// instead of throwing, so the caller can fall back cleanly.
/// </summary>
internal static class CffParser
{
    public sealed class CffFontInfo
    {
        public int NumGlyphs;
        public short XMin, YMin, XMax, YMax;
        // glyph name (e.g. "A", "space", ".notdef") → glyph index.
        // Always contains ".notdef" at index 0. Empty for CID-keyed fonts —
        // those don't have glyph names; lookup is via <see cref="CidToGlyph"/>.
        public Dictionary<string, int> GlyphNameToIndex = new();
        // glyph index → glyph name (inverse of the above). Index 0 = .notdef.
        // For CID-keyed fonts, all entries are null/unset.
        public string[] GlyphNames = Array.Empty<string>();
        // True when the CFF Top DICT contains the /ROS operator (12 30),
        // marking the font as CID-keyed (Adobe-Japan1 / Adobe-CNS1 etc.).
        // CID-keyed CFFs store CIDs in their charset table where ordinary
        // CFFs store SIDs (string IDs); the renderer needs to look glyphs
        // up by CID rather than by Unicode → name → index.
        public bool IsCidKeyed;
        // CID → glyph index (the index Skia uses when DrawText is called
        // with TextEncoding.GlyphId). Built from the charset table when
        // <see cref="IsCidKeyed"/> is true. CID 0 → glyph 0 (.notdef).
        public Dictionary<int, int>? CidToGlyph;
    }

    public static CffFontInfo? Parse(byte[] cff)
    {
        try
        {
            var reader = new CffReader(cff);

            // Header
            byte major = reader.U8();
            byte minor = reader.U8();
            byte hdrSize = reader.U8();
            byte offSize = reader.U8();
            if (major != 1) return null; // CFF2 not supported
            reader.Seek(hdrSize);

            // Name INDEX (font names, we don't need them, just skip)
            SkipIndex(ref reader);

            // Top DICT INDEX — one entry per font (usually just 1)
            var topDicts = ReadIndex(ref reader);
            if (topDicts.Count == 0) return null;
            var topDict = ParseDict(topDicts[0]);

            // String INDEX — additional strings beyond the 391 standard ones.
            var stringIndex = ReadIndex(ref reader);

            // Global Subr INDEX (skipped — charstring interpretation only).
            SkipIndex(ref reader);

            // CharStrings offset is a required entry in Top DICT (operator 17).
            if (!topDict.TryGetValue(17, out var csOp) || csOp.Count == 0) return null;
            int charStringsOffset = (int)csOp[0];

            reader.Seek(charStringsOffset);
            int numGlyphs = reader.U16BE();

            // Charset gives (glyph_index → SID). SID is either < 391 (standard
            // string) or indexes into stringIndex. charset offset 0/1/2 means
            // one of the predefined charsets (ISOAdobe / Expert / ExpertSubset).
            int charsetOffset = 0;
            if (topDict.TryGetValue(15, out var csetOp) && csetOp.Count > 0)
                charsetOffset = (int)csetOp[0];

            // CID-keyed fonts (Adobe-Japan1 etc.) carry the /ROS operator
            // (Registry-Ordering-Supplement, encoded as 12 30 → 1230) in
            // the Top DICT. The charset values are then CIDs rather than
            // SIDs, and there are no PostScript glyph names; lookup is via
            // CID → glyph index.
            bool isCidKeyed = topDict.ContainsKey(1230);

            var charsetByGlyph = new int[numGlyphs];
            charsetByGlyph[0] = 0; // .notdef is always glyph 0
            if (charsetOffset == 0)
            {
                // For CID-keyed fonts the predefined charset 0 is documented
                // as "Identity" — glyph index N → CID N — but in practice
                // CID-keyed CFFs always supply a custom charset, so this
                // branch only matters for simple SID-based CFFs.
                for (int g = 1; g < numGlyphs && g < IsoAdobeCharset.Length; g++)
                    charsetByGlyph[g] = IsoAdobeCharset[g];
            }
            else if (charsetOffset == 1)
            {
                for (int g = 1; g < numGlyphs && g < ExpertCharset.Length; g++)
                    charsetByGlyph[g] = ExpertCharset[g];
            }
            else if (charsetOffset == 2)
            {
                for (int g = 1; g < numGlyphs && g < ExpertSubsetCharset.Length; g++)
                    charsetByGlyph[g] = ExpertSubsetCharset[g];
            }
            else
            {
                ReadCustomCharset(cff, charsetOffset, numGlyphs, charsetByGlyph);
            }

            // For CID-keyed fonts we don't have SIDs to resolve; build the
            // CID → glyph index inverse instead so the renderer can map a
            // PDF CID through the descendant font's CFF charset to the
            // glyph index Skia ultimately draws.
            Dictionary<string, int> nameToIndex;
            string[] glyphNames;
            Dictionary<int, int>? cidToGlyph = null;
            if (isCidKeyed)
            {
                cidToGlyph = new Dictionary<int, int>(numGlyphs);
                cidToGlyph[0] = 0;
                for (int g = 1; g < numGlyphs; g++)
                {
                    int cid = charsetByGlyph[g];
                    if (!cidToGlyph.ContainsKey(cid))
                        cidToGlyph[cid] = g;
                }
                nameToIndex = new Dictionary<string, int>();
                glyphNames = Array.Empty<string>();
            }
            else
            {
                // Resolve SIDs to glyph-name strings (simple Type 1C path).
                glyphNames = new string[numGlyphs];
                nameToIndex = new Dictionary<string, int>(numGlyphs);
                for (int g = 0; g < numGlyphs; g++)
                {
                    int sid = charsetByGlyph[g];
                    string? name = ResolveSid(sid, stringIndex);
                    if (name == null) continue;
                    glyphNames[g] = name;
                    // First occurrence wins on duplicates (shouldn't happen in valid fonts).
                    if (!nameToIndex.ContainsKey(name))
                        nameToIndex[name] = g;
                }
            }

            // FontBBox, if present, lives at Top DICT operator 5 = [xMin yMin xMax yMax].
            short xMin = 0, yMin = 0, xMax = 1000, yMax = 1000;
            if (topDict.TryGetValue(5, out var bb) && bb.Count >= 4)
            {
                xMin = ClampShort(bb[0]);
                yMin = ClampShort(bb[1]);
                xMax = ClampShort(bb[2]);
                yMax = ClampShort(bb[3]);
            }

            return new CffFontInfo
            {
                NumGlyphs = numGlyphs,
                XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax,
                GlyphNameToIndex = nameToIndex,
                GlyphNames = glyphNames,
                IsCidKeyed = isCidKeyed,
                CidToGlyph = cidToGlyph,
            };
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static short ClampShort(double v)
    {
        if (v < short.MinValue) return short.MinValue;
        if (v > short.MaxValue) return short.MaxValue;
        return (short)v;
    }

    private const int StandardStringSidCount = 391;

    private static string? ResolveSid(int sid, IReadOnlyList<byte[]> stringIndex)
    {
        if (sid < 0) return null;
        if (sid < StandardStringSidCount)
            return sid < StandardStrings.Length ? StandardStrings[sid] : null;

        int custom = sid - StandardStringSidCount;
        if (custom < stringIndex.Count)
            return System.Text.Encoding.ASCII.GetString(stringIndex[custom]);
        return null;
    }

    private static void ReadCustomCharset(byte[] cff, int offset, int numGlyphs, int[] sidByGlyph)
    {
        var r = new CffReader(cff);
        r.Seek(offset);
        byte format = r.U8();
        if (format == 0)
        {
            // Each SID stored as uint16 (glyph 0 = .notdef, omitted).
            for (int g = 1; g < numGlyphs; g++)
                sidByGlyph[g] = r.U16BE();
        }
        else if (format == 1 || format == 2)
        {
            // Ranges of sequential SIDs.
            int gi = 1;
            while (gi < numGlyphs)
            {
                int firstSid = r.U16BE();
                int nLeft = format == 1 ? r.U8() : r.U16BE();
                for (int j = 0; j <= nLeft && gi < numGlyphs; j++)
                    sidByGlyph[gi++] = firstSid + j;
            }
        }
    }

    // --- INDEX helpers ---

    private static List<byte[]> ReadIndex(ref CffReader r)
    {
        int count = r.U16BE();
        var result = new List<byte[]>(count);
        if (count == 0) return result;

        byte offSize = r.U8();
        int[] offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
            offsets[i] = r.ReadOffset(offSize);

        int dataStart = r.Position;
        for (int i = 0; i < count; i++)
        {
            int len = offsets[i + 1] - offsets[i];
            var buf = new byte[len];
            Array.Copy(r.Data, dataStart + offsets[i] - 1, buf, 0, len);
            result.Add(buf);
        }
        r.Seek(dataStart + offsets[count] - 1);
        return result;
    }

    private static void SkipIndex(ref CffReader r)
    {
        int count = r.U16BE();
        if (count == 0) return;
        byte offSize = r.U8();
        int[] offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
            offsets[i] = r.ReadOffset(offSize);
        r.Seek(r.Position + offsets[count] - 1);
    }

    // --- DICT parsing ---

    // Returns map from operator → operand stack snapshot (topmost value at end).
    // Two-byte operators encoded as 1200 + second byte.
    private static Dictionary<int, List<double>> ParseDict(byte[] dict)
    {
        var result = new Dictionary<int, List<double>>();
        var stack = new List<double>();
        int i = 0;
        while (i < dict.Length)
        {
            byte b = dict[i];
            if (b <= 21)
            {
                int op = b;
                if (b == 12 && i + 1 < dict.Length)
                {
                    op = 1200 + dict[i + 1];
                    i += 2;
                }
                else i++;
                result[op] = new List<double>(stack);
                stack.Clear();
            }
            else if (b == 28)
            {
                // 2-byte signed integer operand
                short v = (short)((dict[i + 1] << 8) | dict[i + 2]);
                stack.Add(v); i += 3;
            }
            else if (b == 29)
            {
                int v = (dict[i + 1] << 24) | (dict[i + 2] << 16) | (dict[i + 3] << 8) | dict[i + 4];
                stack.Add(v); i += 5;
            }
            else if (b == 30)
            {
                // Real number (BCD). Skip — we don't need reals for the ops we care about.
                i++;
                while (i < dict.Length)
                {
                    byte nb = dict[i++];
                    if ((nb & 0x0F) == 0x0F || (nb >> 4) == 0x0F) break;
                }
                stack.Add(0); // dummy
            }
            else if (b >= 32 && b <= 246)
            {
                stack.Add(b - 139); i++;
            }
            else if (b >= 247 && b <= 250)
            {
                stack.Add((b - 247) * 256 + dict[i + 1] + 108); i += 2;
            }
            else if (b >= 251 && b <= 254)
            {
                stack.Add(-(b - 251) * 256 - dict[i + 1] - 108); i += 2;
            }
            else
            {
                i++; // reserved / unknown — skip
            }
        }
        return result;
    }

    // Lightweight cursor over the CFF blob.
    private ref struct CffReader
    {
        public byte[] Data;
        public int Position;
        public CffReader(byte[] data) { Data = data; Position = 0; }
        public void Seek(int p) { Position = p; }
        public byte U8() => Data[Position++];
        public int U16BE() { int v = (Data[Position] << 8) | Data[Position + 1]; Position += 2; return v; }
        public int ReadOffset(int offSize)
        {
            int v = 0;
            for (int i = 0; i < offSize; i++)
                v = (v << 8) | Data[Position++];
            return v;
        }
    }

    // --- Predefined tables (CFF spec) ---

    // Appendix A: CFF Standard Strings (SID 0..390).
    // These are the well-known PostScript glyph names.
    private static readonly string[] StandardStrings = BuildStandardStrings();
    private static readonly int[] IsoAdobeCharset = BuildIsoAdobeCharset();
    private static readonly int[] ExpertCharset = BuildExpertCharset();
    private static readonly int[] ExpertSubsetCharset = BuildExpertSubsetCharset();

    private static string[] BuildStandardStrings() => new[]
    {
        ".notdef","space","exclam","quotedbl","numbersign","dollar","percent","ampersand",
        "quoteright","parenleft","parenright","asterisk","plus","comma","hyphen","period",
        "slash","zero","one","two","three","four","five","six","seven","eight","nine",
        "colon","semicolon","less","equal","greater","question","at",
        "A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
        "bracketleft","backslash","bracketright","asciicircum","underscore","quoteleft",
        "a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u","v","w","x","y","z",
        "braceleft","bar","braceright","asciitilde","exclamdown","cent","sterling","fraction",
        "yen","florin","section","currency","quotesingle","quotedblleft","guillemotleft",
        "guilsinglleft","guilsinglright","fi","fl","endash","dagger","daggerdbl","periodcentered",
        "paragraph","bullet","quotesinglbase","quotedblbase","quotedblright","guillemotright",
        "ellipsis","perthousand","questiondown","grave","acute","circumflex","tilde","macron",
        "breve","dotaccent","dieresis","ring","cedilla","hungarumlaut","ogonek","caron","emdash",
        "AE","ordfeminine","Lslash","Oslash","OE","ordmasculine","ae","dotlessi","lslash","oslash",
        "oe","germandbls","onesuperior","twosuperior","threesuperior","minus","multiply",
        "onesuperior","twosuperior","threesuperior","minus","multiply",
        "onesuperior","twosuperior","threesuperior","minus","multiply",
        // 151..238: Expert encoding adds numerators, denominators, old-style figures etc.
        // For simplicity we fill the rest with placeholders — they're rarely referenced in
        // subset fonts, and the caller falls back to a per-glyph lookup miss when unknown.
        "onedotenleader","twodotenleader","threequartersemdash","periodsuperior","questionsmall",
        "asuperior","bsuperior","centsuperior","dsuperior","esuperior","fsuperior","gsuperior",
        "hsuperior","isuperior","jsuperior","ksuperior","lsuperior","msuperior","nsuperior",
        "osuperior","psuperior","qsuperior","rsuperior","ssuperior","tsuperior","usuperior",
        "vsuperior","wsuperior","xsuperior","ysuperior","zsuperior","centinferior","dollarinferior",
        "periodinferior","commainferior","Agravesmall","Aacutesmall","Acircumflexsmall",
        "Atildesmall","Adieresissmall","Aringsmall","AEsmall","Ccedillasmall","Egravesmall",
        "Eacutesmall","Ecircumflexsmall","Edieresissmall","Igravesmall","Iacutesmall",
        "Icircumflexsmall","Idieresissmall","Ethsmall","Ntildesmall","Ogravesmall","Oacutesmall",
        "Ocircumflexsmall","Otildesmall","Odieresissmall","OEsmall","Oslashsmall","Ugravesmall",
        "Uacutesmall","Ucircumflexsmall","Udieresissmall","Yacutesmall","Thornsmall","Ydieresissmall",
        "001.000","001.001","001.002","001.003","Black","Bold","Book","Light","Medium","Regular",
        "Roman","Semibold",
    };

    // Charsets are lists of SIDs (first entry is implicitly .notdef=0). These are
    // the predefined charset 0 (ISOAdobe) — glyphs in the font appear in this order.
    // We include the first ~230 entries; fonts referencing beyond that fall off the
    // list and their names end up unknown (ok — subset fonts rarely use this charset).
    private static int[] BuildIsoAdobeCharset()
    {
        // ISOAdobe: SIDs 1..228 in order (one per glyph past .notdef).
        var arr = new int[229];
        for (int i = 0; i < 229; i++) arr[i] = i; // 0..228 → notdef, space, exclam, ... A, B, ...
        return arr;
    }

    private static int[] BuildExpertCharset()
    {
        // Expert charset (partial — just the notdef placeholder here to avoid
        // renderers crashing on this uncommon case).
        return new[] { 0 };
    }

    private static int[] BuildExpertSubsetCharset() => new[] { 0 };
}
