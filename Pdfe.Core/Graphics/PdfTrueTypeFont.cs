using System.Globalization;
using System.IO.Compression;
using System.Text;
using Pdfe.Core.Document;
using Pdfe.Core.Fonts;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Graphics;

/// <summary>
/// An embedded TrueType font drawn as a PDF <b>Type0 / Identity-H</b> composite
/// font. Unlike the base-14 <see cref="PdfFont"/>, this embeds the actual font
/// program so arbitrary Unicode (CJK, Arabic, accented Latin, …) renders, and
/// attaches a ToUnicode CMap so the text stays selectable/extractable.
///
/// <para><see cref="EncodeString"/> emits 2-byte glyph ids (Identity-H), and
/// <see cref="BuildFontDictionary"/> installs the FontFile2 / descendant font /
/// descriptor / ToUnicode objects into the document. The full font is embedded
/// (subsetting is a future optimization — see #378).</para>
/// </summary>
internal sealed class PdfTrueTypeFont : PdfFont
{
    private readonly TrueTypeFontFile _ttf;
    private readonly double _scale;   // font units -> text space at 1 pt
    private readonly HashSet<int> _usedGids = new() { 0 };   // accumulated as text is drawn (#393)

    internal override bool PreferIndirectFontDictionary => true;

    public PdfTrueTypeFont(byte[] fontData, double size)
        : base("F1", SafeBaseName(TrueTypeFontFile.Parse(fontData).PostScriptName), size)
    {
        _ttf = TrueTypeFontFile.Parse(fontData);
        _scale = 1.0 / _ttf.UnitsPerEm;
    }

    public override double MeasureWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        long units = 0;
        foreach (var cp in Codepoints(text))
            units += _ttf.AdvanceWidth(_ttf.GidForCodepoint(cp));
        return units * _scale * Size;
    }

    public override double Ascender => _ttf.Ascent * _scale * Size;
    public override double Descender => -_ttf.Descent * _scale * Size;
    public override double LineHeight => (_ttf.Ascent - _ttf.Descent) * _scale * Size;

    /// <summary>Encode as a hex string of 2-byte glyph ids: <c>&lt;00410042&gt;</c>.</summary>
    public override string EncodeString(string text)
    {
        var sb = new StringBuilder(text.Length * 4 + 2);
        sb.Append('<');
        foreach (var cp in Codepoints(text))
        {
            int gid = _ttf.GidForCodepoint(cp) & 0xFFFF;
            _usedGids.Add(gid);
            sb.Append(gid.ToString("X4", CultureInfo.InvariantCulture));
        }
        sb.Append('>');
        return sb.ToString();
    }

    internal override PdfDictionary BuildFontDictionary(PdfDocument document)
    {
        double toGlyphSpace = 1000.0 / _ttf.UnitsPerEm;   // PDF glyph space = 1000/em
        int Scale(int fontUnits) => (int)Math.Round(fontUnits * toGlyphSpace);

        // 1. FontFile2 — the embedded TTF, FlateDecode-compressed. Built with the
        // full font now; a pre-save action (registered below) replaces it with a
        // subset once every drawn glyph is known.
        byte[] compressed = Deflate(_ttf.Data);
        var ff2Dict = new PdfDictionary();
        ff2Dict.SetInt("Length1", _ttf.Data.Length);
        ff2Dict.SetName("Filter", "FlateDecode");
        ff2Dict.SetInt("Length", compressed.Length);
        var ff2 = new PdfStream(ff2Dict, compressed);
        var ff2Ref = document.AddIndirectObject(ff2);

        // 2. FontDescriptor.
        var fd = new PdfDictionary();
        fd.SetName("Type", "FontDescriptor");
        fd.SetName("FontName", BaseFont);
        fd.SetInt("Flags", 4); // Symbolic — safe for an embedded CID font
        var bbox = new PdfArray();
        bbox.Add(Scale(_ttf.XMin)); bbox.Add(Scale(_ttf.YMin));
        bbox.Add(Scale(_ttf.XMax)); bbox.Add(Scale(_ttf.YMax));
        fd["FontBBox"] = bbox;
        fd.SetInt("ItalicAngle", 0);
        fd.SetInt("Ascent", Scale(_ttf.Ascent));
        fd.SetInt("Descent", Scale(_ttf.Descent));
        fd.SetInt("CapHeight", Scale(_ttf.Ascent));
        fd.SetInt("StemV", _ttf.IsBold ? 140 : 80);
        fd["FontFile2"] = ff2Ref;
        var fdRef = document.AddIndirectObject(fd);

        // 3. /W advance widths for every glyph: [0 [w0 w1 w2 …]].
        var widths = new PdfArray();
        for (int g = 0; g < _ttf.GlyphCount; g++)
            widths.Add(Scale(_ttf.AdvanceWidth(g)));
        var wArr = new PdfArray();
        wArr.Add(0);
        wArr.Add((PdfObject)widths);

        // 4. CIDFontType2 descendant font.
        var cid = new PdfDictionary();
        cid.SetName("Type", "Font");
        cid.SetName("Subtype", "CIDFontType2");
        cid.SetName("BaseFont", BaseFont);
        var csi = new PdfDictionary();
        csi.SetString("Registry", "Adobe");
        csi.SetString("Ordering", "Identity");
        csi.SetInt("Supplement", 0);
        cid["CIDSystemInfo"] = csi;
        cid["FontDescriptor"] = fdRef;
        cid.SetName("CIDToGIDMap", "Identity");
        cid["W"] = wArr;
        var cidRef = document.AddIndirectObject(cid);

        // 5. ToUnicode CMap (placeholder; filled with only the used glyphs,
        //    compressed, by the pre-save action).
        var tu = new PdfStream(new PdfDictionary(), Array.Empty<byte>());
        var tuRef = document.AddIndirectObject(tu);

        // 6. Type0 root (returned; AddFont stores it inline in /Font).
        var type0 = new PdfDictionary();
        type0.SetName("Type", "Font");
        type0.SetName("Subtype", "Type0");
        type0.SetName("BaseFont", BaseFont);
        type0.SetName("Encoding", "Identity-H");
        var descendants = new PdfArray();
        descendants.Add((PdfObject)cidRef);
        type0["DescendantFonts"] = descendants;
        type0["ToUnicode"] = tuRef;

        // Defer subsetting to save time (when _usedGids is complete).
        document.RegisterPreSaveAction(() => FinalizeSubset(ff2, fd, cid, type0, tu, toGlyphSpace));
        return type0;
    }

    /// <summary>
    /// At save time: replace FontFile2 with a glyph subset, fill the ToUnicode
    /// CMap for the used glyphs (compressed), trim /W to used glyphs, and apply a
    /// 6-letter subset tag to the font names. Idempotent.
    /// </summary>
    private void FinalizeSubset(PdfStream fontFile2, PdfDictionary fd, PdfDictionary cid,
        PdfDictionary type0, PdfStream toUnicode, double toGlyphSpace)
    {
        byte[] subset = TrueTypeSubsetter.Subset(_ttf.Data, _usedGids);
        byte[] comp = Deflate(subset);
        fontFile2.SetEncodedData(comp);
        fontFile2.SetInt("Length1", subset.Length);
        fontFile2.SetInt("Length", comp.Length);

        // ToUnicode for just the used glyphs, FlateDecode-compressed.
        byte[] cmap = Deflate(Encoding.ASCII.GetBytes(BuildToUnicodeCMap()));
        toUnicode.SetEncodedData(cmap);
        toUnicode.SetName("Filter", "FlateDecode");
        toUnicode.SetInt("Length", cmap.Length);

        // Subset tag: 6 uppercase letters derived from the used-gid set.
        string tagged = SubsetTag() + "+" + BaseFont;
        fd.SetName("FontName", tagged);
        cid.SetName("BaseFont", tagged);
        type0.SetName("BaseFont", tagged);

        // Trim /W to only used glyphs: [ gid [w] gid2 [w2] … ].
        int Scale(int fu) => (int)Math.Round(fu * toGlyphSpace);
        var w = new PdfArray();
        foreach (int g in _usedGids.Where(g => g > 0).OrderBy(g => g))
        {
            w.Add(g);
            var one = new PdfArray();
            one.Add(Scale(_ttf.AdvanceWidth(g)));
            w.Add((PdfObject)one);
        }
        cid["W"] = w;
    }

    /// <summary>Deterministic 6-uppercase-letter subset tag from the used glyphs.</summary>
    private string SubsetTag()
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (int g in _usedGids.OrderBy(x => x)) { h = (h ^ (uint)g) * 16777619; }
            var c = new char[6];
            for (int i = 0; i < 6; i++) { c[i] = (char)('A' + (int)(h % 26)); h /= 26; }
            return new string(c);
        }
    }

    /// <summary>Build a ToUnicode CMap mapping each <em>used</em> glyph id to a codepoint.</summary>
    private string BuildToUnicodeCMap()
    {
        // Reverse the cmap (cp -> gid) once, then map only the glyphs we drew.
        var reverse = new Dictionary<int, int>();
        foreach (var (cp, gid) in _ttf.Cmap)
            if (gid <= 0xFFFF && !reverse.ContainsKey(gid))
                reverse[gid] = cp;
        var gidToCp = new SortedDictionary<int, int>();
        foreach (int g in _usedGids)
            if (g > 0 && g <= 0xFFFF && reverse.TryGetValue(g, out var cp))
                gidToCp[g] = cp;

        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        var entries = gidToCp.ToList();
        for (int i = 0; i < entries.Count; i += 100)
        {
            int n = Math.Min(100, entries.Count - i);
            sb.Append(n).Append(" beginbfchar\n");
            for (int j = i; j < i + n; j++)
            {
                sb.Append('<').Append(entries[j].Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                  .Append(Utf16BeHex(entries[j].Value)).Append(">\n");
            }
            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    private static string Utf16BeHex(int codepoint)
    {
        if (codepoint <= 0xFFFF)
            return codepoint.ToString("X4", CultureInfo.InvariantCulture);
        // Surrogate pair for supplementary planes.
        int v = codepoint - 0x10000;
        int hi = 0xD800 + (v >> 10), lo = 0xDC00 + (v & 0x3FF);
        return hi.ToString("X4", CultureInfo.InvariantCulture) + lo.ToString("X4", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<int> Codepoints(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                yield return text[i];
            }
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var outMs = new MemoryStream();
        using (var z = new ZLibStream(outMs, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return outMs.ToArray();
    }

    // PDF names can't contain spaces; PostScript names normally don't, but be safe.
    private static string SafeBaseName(string psName) =>
        string.IsNullOrWhiteSpace(psName) ? "EmbeddedFont" : psName.Replace(' ', '-');
}
