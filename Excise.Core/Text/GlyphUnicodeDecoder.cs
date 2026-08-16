using System.Text;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Primitives;

namespace Excise.Core.Text;

/// <summary>
/// Resolves a character code (and, under a registered encoding CMap, its CID)
/// to Unicode: the ONE cascade both content state machines decode through.
///
/// <para><b>Why it exists (#981).</b> This cascade lived privately in
/// <see cref="TextExtractor"/> and ran to nine steps.
/// <see cref="Content.ContentStreamParser"/> had its own, three steps long —
/// /ToUnicode stream, registered CID→Unicode, WinAnsi — and a comment claiming
/// it "mirrors TextExtractor.DecodeCharacter" that was the opposite of the
/// truth. Redaction matches page LETTERS (TextExtractor) against operator TEXT
/// (ContentStreamParser) in LetterFinder, so wherever the two decoded the same
/// bytes differently, the match failed and glyph-level removal degraded to
/// whole-operator removal, or missed entirely. Every font shape covered by the
/// six missing steps was affected: MacRoman-encoded Type1, /Differences-remapped
/// simple fonts, non-embedded CIDFontType2, symbolic TrueType, embedded
/// Identity-H, and /ToUnicode /Identity-H as a NAME.</para>
///
/// <para><b>The cascade, in priority order</b> — earlier steps are the
/// producer's own declarations, later ones are recovery heuristics:</para>
/// <list type="number">
/// <item>/ToUnicode CMap STREAM (§9.10.3).</item>
/// <item>/ToUnicode as the predefined CMap NAME /Identity-H|V: the 2-byte code
/// IS the UTF-16BE scalar (#715).</item>
/// <item>Registered CID→Unicode, the Adobe-&lt;Ordering&gt;-UCS2 CMap for the
/// font's /CIDSystemInfo (§9.10.2 method (b), #515). Passed IN rather than
/// built here: both callers already derive it inside their own CID machinery,
/// which also owns the codespace segmentation it comes with.</item>
/// <item>Embedded font program's own cmap read in REVERSE, for an embedded
/// Type0 + Identity-H|V with no /ToUnicode (#515 slice 3).</item>
/// <item>/Encoding &lt;&lt; /Differences &gt;&gt; glyph names, then that
/// dictionary's /BaseEncoding (#662).</item>
/// <item>/Encoding as the NAME /WinAnsiEncoding or /MacRomanEncoding.</item>
/// <item>Standard Macintosh glyph order, for a NON-embedded Identity
/// CIDFontType2 (#532).</item>
/// <item>Simple symbolic TrueType (3,0) cmap (#791).</item>
/// <item>WinAnsi, the historical default.</item>
/// </list>
///
/// <para>Immutable and derived entirely from one font dictionary, so callers
/// cache one instance per /Font resource and share it across every Tf that
/// selects it. The registered CID map is the one input that is NOT font-dict
/// state at this level, which is why it is a Decode parameter — keeping this
/// object cacheable without a second construction phase.</para>
/// </summary>
internal sealed class GlyphUnicodeDecoder
{
    /// <summary>
    /// The decoder for "no font resolved" — WinAnsi only, which is what both
    /// machines did before a /Tf named a resolvable font.
    /// </summary>
    public static readonly GlyphUnicodeDecoder None = new(null, null);

    private readonly PdfDocument? _document;
    private readonly Dictionary<int, string>? _toUnicodeMap;
    private readonly bool _toUnicodeIdentity;
    private readonly Dictionary<int, string>? _embeddedCidToUnicode;
    private readonly Dictionary<int, string>? _differencesGlyphNames;
    private readonly string? _differencesBaseEncoding;
    private readonly string? _fontEncodingName;
    private readonly bool _useStandardMacGlyphOrder;
    private readonly Dictionary<int, string>? _simpleSymbolCodeToUnicode;

    private GlyphUnicodeDecoder(PdfDocument? document, PdfDictionary? font)
    {
        _document = document;
        if (document == null || font == null)
            return;

        // Order matters and is TextExtractor.LoadFontDerivedState's, unchanged:
        // every heuristic below the /ToUnicode stream is gated on that stream
        // being absent.
        _toUnicodeMap = LoadToUnicodeMap(font);
        (_differencesGlyphNames, _differencesBaseEncoding) = LoadDifferencesEncoding(font);
        _toUnicodeIdentity = _toUnicodeMap == null && ToUnicodeIsIdentity(font);
        _useStandardMacGlyphOrder = _toUnicodeMap == null && UsesStandardMacGlyphOrderFallback(font);
        _embeddedCidToUnicode = _toUnicodeMap == null ? LoadEmbeddedCidToUnicodeMap(font) : null;
        _simpleSymbolCodeToUnicode = _toUnicodeMap == null ? LoadSimpleSymbolTrueTypeMap(font) : null;
        _fontEncodingName =
            (document.Resolve(font.GetOptional("Encoding") ?? PdfNull.Instance) as PdfName)?.Value;
    }

    /// <summary>Build the decoder for one font dictionary.</summary>
    public static GlyphUnicodeDecoder Build(PdfDocument? document, PdfDictionary? font) =>
        document == null || font == null ? None : new GlyphUnicodeDecoder(document, font);

    /// <summary>
    /// True when the font declared a /ToUnicode CMap STREAM. Callers gate their
    /// registered-CID-map lookup on this: a producer-declared stream wins
    /// outright over the ordering map (§9.10.2).
    /// </summary>
    public bool HasToUnicodeStreamMap => _toUnicodeMap != null;

    /// <summary>
    /// True when /ToUnicode is the predefined CMap NAME /Identity-H or
    /// /Identity-V rather than a stream (#715).
    /// </summary>
    public bool ToUnicodeIsIdentityName => _toUnicodeIdentity;


    /// <param name="charCode">The source character code from the content stream.</param>
    /// <param name="cid">The CID <paramref name="charCode"/> maps to — differs
    /// from the code only under a registered encoding CMap (#515); everywhere
    /// else callers pass the code itself.</param>
    public string Decode(int charCode, int cid, IReadOnlyDictionary<int, string>? registeredCidToUnicode)
    {
        // First, check ToUnicode map (highest priority)
        if (_toUnicodeMap != null && _toUnicodeMap.TryGetValue(charCode, out var unicode))
        {
            return unicode;
        }

        // Predefined /ToUnicode /Identity-H|/Identity-V (a CMap NAME, so no map
        // was built above): the 2-byte code IS the UTF-16BE Unicode scalar.
        // Decode it directly — the WinAnsi default below is only identity by
        // coincidence and mis-maps codes 128–159 (#715 / #515).
        if (_toUnicodeIdentity && charCode is >= 0 and <= 0xFFFF && !char.IsSurrogate((char)charCode))
        {
            return CharToString((char)charCode);
        }

        // Registered CID→Unicode (#515 slice 2): the Adobe-<Ordering>-UCS2 CMap
        // for the font's /CIDSystemInfo ordering, keyed by CID (== code for
        // Identity-H/V; mapped through the registered encoding CMap otherwise).
        // Spec-defined (§9.10.2 method (b)), so it outranks the embedded
        // reverse-cmap and Mac-glyph-order heuristics below; CIDs the ordering
        // map doesn't cover fall through rather than inventing a mapping.
        if (registeredCidToUnicode != null && registeredCidToUnicode.TryGetValue(cid, out var orderingUnicode))
        {
            return orderingUnicode;
        }

        // Embedded Type0 + Identity-H/V + no /ToUnicode (#515 slice 3): the
        // 2-byte code is a CID whose GID lives in the EMBEDDED font program,
        // so the program's own cmap/charset — read in reverse — is the
        // authoritative GID→Unicode source (higher priority than any
        // glyph-order guess; #532's Mac-order fallback stays scoped to
        // non-embedded fonts). Codes the embedded map doesn't cover fall
        // through to the existing behavior rather than inventing a mapping.
        if (_embeddedCidToUnicode != null && _embeddedCidToUnicode.TryGetValue(charCode, out var embeddedUnicode))
        {
            return embeddedUnicode;
        }

        // /Encoding << /Differences [...] >> (#662): a Differences entry for
        // this exact code overrides everything below it — the glyph name it
        // assigns takes priority over both /BaseEncoding and the bare-name
        // fallback, because it's the font's own explicit remapping.
        if (_differencesGlyphNames != null && _differencesGlyphNames.TryGetValue(charCode, out var glyphName))
        {
            var mapped = GlyphNameToUnicode(glyphName);
            if (mapped != null)
                return mapped;
            // Unrecognized glyph name for this one code — fall through to the
            // same default decode used below rather than inventing new
            // guessing logic (deliberately not "return" here).
        }
        else if (_differencesGlyphNames != null)
        {
            // Differences dictionary present, but this code has no override —
            // fall back to its /BaseEncoding (if any) before the bare default.
            if (_differencesBaseEncoding == "WinAnsiEncoding")
                return DecodeWinAnsi(charCode);
            if (_differencesBaseEncoding == "MacRomanEncoding")
                return DecodeMacRoman(charCode);
        }

        // /Encoding as a base-encoding NAME on the font dictionary, resolved
        // once at build time rather than per glyph.
        if (_fontEncodingName == "WinAnsiEncoding")
            return DecodeWinAnsi(charCode);
        if (_fontEncodingName == "MacRomanEncoding")
            return DecodeMacRoman(charCode);

        // Non-embedded Type0/CIDFontType2, Identity-H/V, no /ToUnicode (#532):
        // the 2-byte charCode is the CID = GID (Identity), and with no embedded
        // font program there is no cmap to give GID→Unicode. Reading the GID as
        // a Latin-1 code point (the default below) garbles the text — e.g.
        // issue4722.pdf's "DESCRIPTION" came out "'(6&5,37,21" (a fixed −29
        // shift). The producing app laid the text out against a TrueType font in
        // the standard Macintosh order, so GID→name→Unicode recovers it, matching
        // mutool/pdf.js. Scoped to non-embedded so it never overrides an embedded
        // font's real (possibly subset-reordered) glyph mapping.
        if (_useStandardMacGlyphOrder && StandardMacGlyphOrder.TryGetName(charCode, out var macName))
        {
            var macUnicode = GlyphNameToUnicode(macName);
            if (macUnicode != null)
                return macUnicode;
        }

        // Simple symbolic TrueType with a (3,0) symbol cmap and no ToUnicode/no
        // Encoding (#791): the content byte selects a glyph through the embedded
        // font's (3,0)/F000 cmap, and that glyph's Unicode is recovered from its
        // post name / Unicode cmap. Consulted last so any explicit encoding above
        // still wins; only fires when the embedded program actually resolves this
        // code to a real (non-PUA) Unicode. Without it the byte is echoed through
        // WinAnsi below — the silent mis-decode that bounds redaction.
        if (_simpleSymbolCodeToUnicode != null
            && _simpleSymbolCodeToUnicode.TryGetValue(charCode, out var symbolUnicode))
        {
            return symbolUnicode;
        }

        // Default: assume WinAnsiEncoding for Type1 fonts with standard base fonts
        return DecodeWinAnsi(charCode);
    }

    private Dictionary<int, string>? LoadToUnicodeMap(PdfDictionary? font)
    {
        if (font == null)
            return null;

        var toUnicodeObj = font.GetOptional("ToUnicode");
        if (toUnicodeObj == null)
            return null;

        // Resolve the reference
        var resolved = _document!.Resolve(toUnicodeObj);
        if (resolved is not PdfStream stream)
            return null;

        try
        {
            return ToUnicodeCMapParser.Parse(stream.DecodedData);
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            // If CMap parsing fails, fall back to encoding
            return null;
        }
    }


    /// <summary>
    /// True when <paramref name="font"/> is a non-embedded Type0 font with
    /// Identity-H/V encoding — the class for which the standard Macintosh glyph
    /// order is the correct GID→Unicode fallback when there is no <c>/ToUnicode</c>
    /// CMap (see <see cref="DecodeCharacter"/>). Embedded descendants are excluded
    /// because their (often subset-reordered) glyph order is authoritative and a
    /// standard-order guess would corrupt correct output. #532.
    /// </summary>
    /// <summary>
    /// True when the font's <c>/ToUnicode</c> is the predefined-CMap name
    /// <c>/Identity-H</c> or <c>/Identity-V</c> (as opposed to a stream). Such a
    /// ToUnicode declares code == Unicode (UTF-16BE); see <see cref="DecodeCharacter"/>.
    /// #715 / #515.
    /// </summary>
    private bool ToUnicodeIsIdentity(PdfDictionary? font)
    {
        if (font == null)
            return false;
        var toUnicode = _document!.Resolve(font.GetOptional("ToUnicode") ?? PdfNull.Instance);
        return toUnicode is PdfName name && (name.Value == "Identity-H" || name.Value == "Identity-V");
    }

    private bool UsesStandardMacGlyphOrderFallback(PdfDictionary? font)
    {
        if (font == null || font.GetNameOrNull("Subtype") != "Type0")
            return false;

        // A present /ToUnicode is the producer's own declared code→Unicode map —
        // even a predefined-CMap NAME like /Identity-H (which we don't build a
        // lookup table from) means "code == Unicode" and must not be overridden
        // by a standard-glyph-order guess. issue12418_reduced.pdf uses
        // /ToUnicode /Identity-H and extracts correctly without this fallback;
        // only fonts with no /ToUnicode at all are in scope. #532.
        if (font.GetOptional("ToUnicode") != null)
            return false;

        var enc = _document!.Resolve(font.GetOptional("Encoding") ?? PdfNull.Instance);
        if (enc is not PdfName encName || (encName.Value != "Identity-H" && encName.Value != "Identity-V"))
            return false;

        var descObj = _document!.Resolve(font.GetOptional("DescendantFonts") ?? PdfNull.Instance);
        var descendant = descObj switch
        {
            PdfArray arr when arr.Count > 0 => _document!.Resolve(arr[0]) as PdfDictionary,
            PdfDictionary d => d,
            _ => null,
        };
        if (descendant == null)
            return false;

        if (_document!.Resolve(descendant.GetOptional("FontDescriptor") ?? PdfNull.Instance)
            is not PdfDictionary fd)
            return true; // no descriptor → treat as non-embedded

        var embedded = fd.GetOptional("FontFile2") != null
            || fd.GetOptional("FontFile3") != null
            || fd.GetOptional("FontFile") != null;
        return !embedded;
    }

    /// <summary>
    /// For an EMBEDDED Type0 font with Identity-H/V encoding and no
    /// <c>/ToUnicode</c> (#515 slice 3), builds a code (CID) → Unicode map by
    /// reading the embedded font program's own character map in REVERSE:
    /// <list type="bullet">
    /// <item><c>/FontFile2</c> (CIDFontType2 TrueType): the font's cmap table is
    /// Unicode→GID; reversed it is GID→Unicode. <c>/CIDToGIDMap</c> is honored —
    /// for a stream, code→CID→GID first; for <c>/Identity</c> (or absent),
    /// CID == GID.</item>
    /// <item><c>/FontFile3</c> (CIDFontType0 CFF): a non-CID-keyed CFF names its
    /// glyphs, so GID→name→Unicode via the Adobe Glyph List (per §9.7.4.2 the CID
    /// is the glyph index directly); a CID-keyed CFF has no glyph names, so
    /// Unicode is only recoverable from an OpenType wrapper's sfnt cmap
    /// (charset gives CID→GID).</item>
    /// </list>
    /// Returns null when the font is out of scope (non-Type0, has /ToUnicode,
    /// non-Identity encoding, not embedded) or the embedded program yields no
    /// mappings — callers then fall through to the pre-existing behavior.
    /// Scope mirrors #532's guards exactly, on the opposite (embedded) side.
    /// </summary>
    private Dictionary<int, string>? LoadEmbeddedCidToUnicodeMap(PdfDictionary? font)
    {
        if (font == null || font.GetNameOrNull("Subtype") != "Type0")
            return null;

        // A present /ToUnicode (stream or predefined name) is the producer's
        // declared map and wins; this path is only for fonts with none at all.
        if (font.GetOptional("ToUnicode") != null)
            return null;

        var enc = _document!.Resolve(font.GetOptional("Encoding") ?? PdfNull.Instance);
        if (enc is not PdfName encName || (encName.Value != "Identity-H" && encName.Value != "Identity-V"))
            return null;

        Dictionary<int, string>? map = null;
        try
        {
            var descObj = _document!.Resolve(font.GetOptional("DescendantFonts") ?? PdfNull.Instance);
            var descendant = descObj switch
            {
                PdfArray arr when arr.Count > 0 => _document!.Resolve(arr[0]) as PdfDictionary,
                PdfDictionary d => d,
                _ => null,
            };
            if (descendant != null
                && _document!.Resolve(descendant.GetOptional("FontDescriptor") ?? PdfNull.Instance)
                    is PdfDictionary fd)
            {
                if (_document!.Resolve(fd.GetOptional("FontFile2") ?? PdfNull.Instance) is PdfStream ff2)
                    map = BuildCidToUnicodeFromTrueType(ff2.DecodedData, descendant);
                else if (_document!.Resolve(fd.GetOptional("FontFile3") ?? PdfNull.Instance) is PdfStream ff3)
                    map = BuildCidToUnicodeFromCff(ff3.DecodedData);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Malformed/truncated embedded program — fall back to existing
            // behavior rather than failing extraction.
            map = null;
        }

        if (map is { Count: 0 })
            map = null;
        return map;
    }

    /// <summary>
    /// Builds code→Unicode for a SIMPLE (non-Type0) SYMBOLIC TrueType font that
    /// carries a Microsoft-Symbol <c>(3,0)</c> cmap subtable and no
    /// <c>/ToUnicode</c> / no <c>/Encoding</c> (#791). The content byte selects a
    /// glyph through the embedded program's (3,0) cmap (F000-offset per ISO
    /// 32000-2 §9.6.6.4); that glyph's Unicode is recovered from the program's
    /// <c>post</c> glyph names (or a Unicode cmap subtable, if present). Returns
    /// null when out of scope, so the existing WinAnsi fallback is untouched for
    /// every other simple font — keeping the change off the extraction-parity
    /// corpus except where a real (3,0) symbol font would otherwise mis-decode.
    /// </summary>
    private Dictionary<int, string>? LoadSimpleSymbolTrueTypeMap(PdfDictionary? font)
    {
        if (font == null || font.GetNameOrNull("Subtype") != "TrueType")
            return null;
        // Scope to symbolic fonts with no producer-declared encoding — those are
        // the fonts the WinAnsi fallback silently mis-decodes. A present
        // /ToUnicode or /Encoding is the producer's own map and is handled above.
        if (font.GetOptional("ToUnicode") != null || font.GetOptional("Encoding") != null)
            return null;

        Dictionary<int, string>? map = null;
        try
        {
            if (_document!.Resolve(font.GetOptional("FontDescriptor") ?? PdfNull.Instance)
                    is PdfDictionary fd
                && (fd.GetInt("Flags", 0) & 0x4) != 0 // bit 3: Symbolic
                && _document!.Resolve(fd.GetOptional("FontFile2") ?? PdfNull.Instance) is PdfStream ff2)
            {
                var ttf = TrueTypeFontFile.Parse(ff2.DecodedData);
                if (ttf.HasSymbolCmap)
                    map = BuildSymbolCodeToUnicode(ttf);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            map = null; // malformed embedded program — keep existing behavior
        }

        if (map is { Count: 0 })
            map = null;
        return map;
    }

    /// <summary>
    /// code (0..255) → Unicode for a symbolic TrueType: symbol-cmap code→gid, then
    /// gid→Unicode from the program's own Unicode cmap (non-PUA) or post glyph
    /// names. Skips codes whose only recovery is a Private-Use scalar — those are
    /// genuinely unrecoverable and must fall through rather than emit PUA garbage.
    /// </summary>
    private static Dictionary<int, string> BuildSymbolCodeToUnicode(TrueTypeFontFile ttf)
    {
        // gid → non-PUA Unicode from a Unicode cmap subtable, if the font has one.
        var gidToCp = ReverseCmap(ttf.Cmap);
        var result = new Dictionary<int, string>();
        for (int code = 0; code <= 0xFF; code++)
        {
            int gid = ttf.GidForSymbolByte(code);
            if (gid == 0) continue;

            string? unicode = null;
            if (gidToCp.TryGetValue(gid, out var cp) && !IsPrivateUse(cp))
                unicode = char.ConvertFromUtf32(cp);
            if (unicode == null)
            {
                var name = ttf.GlyphName(gid);
                if (name != null) unicode = GlyphNameToUnicode(name);
            }
            if (unicode is { Length: > 0 } && !IsPrivateUse(char.ConvertToUtf32(unicode, 0)))
                result[code] = unicode;
        }
        return result;
    }

    /// <summary>
    /// GID→Unicode for an embedded TrueType program (reverse of its cmap),
    /// re-keyed by CID through <c>/CIDToGIDMap</c> when that is a stream.
    /// Returns null when the program has no usable cmap (common for subsets
    /// that drop it — exactly the case where no mapping should be invented).
    /// </summary>
    private Dictionary<int, string>? BuildCidToUnicodeFromTrueType(byte[] fontData, PdfDictionary descendant)
    {
        TrueTypeFontFile ttf;
        try
        {
            ttf = TrueTypeFontFile.Parse(fontData);
        }
        catch (NotSupportedException)
        {
            return null; // no cmap table / unsupported flavor
        }

        var gidToCodepoint = ReverseCmap(ttf.Cmap);
        if (gidToCodepoint.Count == 0)
            return null;

        // /CIDToGIDMap stream: 2 bytes per CID, big-endian GID. Map
        // code→CID→GID before the reverse-cmap lookup. /Identity or absent
        // means CID == GID.
        var cidToGidObj = descendant.GetOptional("CIDToGIDMap");
        if (cidToGidObj != null && _document!.Resolve(cidToGidObj) is PdfStream cidToGidStream)
        {
            var data = cidToGidStream.DecodedData;
            var byCid = new Dictionary<int, string>();
            int count = data.Length / 2;
            for (int cid = 0; cid < count; cid++)
            {
                int gid = (data[cid * 2] << 8) | data[cid * 2 + 1];
                if (gid != 0 && gidToCodepoint.TryGetValue(gid, out var cp))
                    byCid[cid] = char.ConvertFromUtf32(cp);
            }
            return byCid;
        }

        var byGid = new Dictionary<int, string>(gidToCodepoint.Count);
        foreach (var (gid, cp) in gidToCodepoint)
            byGid[gid] = char.ConvertFromUtf32(cp);
        return byGid;
    }

    /// <summary>
    /// CID→Unicode for an embedded /FontFile3 program: raw CFF (Subtype
    /// /Type1C or /CIDFontType0C) or OpenType-wrapped CFF (Subtype /OpenType).
    /// </summary>
    private static Dictionary<int, string>? BuildCidToUnicodeFromCff(byte[] fontData)
    {
        // OpenType wrapper: its sfnt cmap is a direct Unicode→GID map (reverse
        // it), and the raw CFF table inside carries the charset/CID data.
        Dictionary<int, int>? gidToCodepoint = null;
        byte[]? cff = fontData;
        if (fontData.Length >= 4 && IsSfnt(ReadU32(fontData, 0)))
        {
            try
            {
                gidToCodepoint = ReverseCmap(TrueTypeFontFile.Parse(fontData).Cmap);
            }
            catch (NotSupportedException)
            {
                gidToCodepoint = null;
            }
            cff = ExtractSfntTable(fontData, "CFF ");
        }

        var info = cff != null ? CffParser.Parse(cff) : null;
        var result = new Dictionary<int, string>();

        if (info is { IsCidKeyed: true })
        {
            // CID-keyed CFF: charset gives CID→GID, but glyphs have no names —
            // Unicode is only recoverable via an sfnt cmap (OpenType wrapper).
            if (gidToCodepoint == null || info.CidToGlyph == null)
                return null;
            foreach (var (cid, gid) in info.CidToGlyph)
            {
                if (gid != 0 && gidToCodepoint.TryGetValue(gid, out var cp))
                    result[cid] = char.ConvertFromUtf32(cp);
            }
            return result;
        }

        if (info != null && info.GlyphNames.Length > 0)
        {
            // Non-CID-keyed CFF as a CIDFontType0 descendant: per §9.7.4.2 the
            // CID is used directly as the glyph index. GID→name→Unicode via
            // the AGL / uniXXXX convention; sfnt cmap as a secondary source.
            for (int gid = 1; gid < info.GlyphNames.Length; gid++)
            {
                var name = info.GlyphNames[gid];
                if (!string.IsNullOrEmpty(name))
                {
                    var unicode = GlyphNameToUnicode(name);
                    if (unicode != null)
                    {
                        result[gid] = unicode;
                        continue;
                    }
                }
                if (gidToCodepoint != null && gidToCodepoint.TryGetValue(gid, out var cp))
                    result[gid] = char.ConvertFromUtf32(cp);
            }
            return result;
        }

        // No usable CFF info; fall back to the sfnt cmap alone (CID == GID).
        if (gidToCodepoint != null)
        {
            foreach (var (gid, cp) in gidToCodepoint)
                result[gid] = char.ConvertFromUtf32(cp);
            return result;
        }
        return null;
    }

    /// <summary>
    /// Reverses a font cmap (Unicode codepoint → GID) into GID → codepoint.
    /// When several codepoints share a GID, a non-Private-Use codepoint beats a
    /// Private-Use one, then the smaller codepoint wins — deterministic, and
    /// avoids emitting PUA garbage when a subset also maps a real character.
    /// </summary>
    private static Dictionary<int, int> ReverseCmap(IReadOnlyDictionary<int, int> unicodeToGid)
    {
        var gidToCodepoint = new Dictionary<int, int>();
        foreach (var (cp, gid) in unicodeToGid)
        {
            if (gid == 0 || cp <= 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                continue;
            if (!gidToCodepoint.TryGetValue(gid, out var existing) || PreferCodepoint(cp, existing))
                gidToCodepoint[gid] = cp;
        }
        return gidToCodepoint;
    }

    private static bool PreferCodepoint(int candidate, int existing)
    {
        bool candidatePua = IsPrivateUse(candidate);
        bool existingPua = IsPrivateUse(existing);
        if (candidatePua != existingPua)
            return existingPua; // prefer the non-PUA codepoint
        return candidate < existing;
    }

    private static bool IsPrivateUse(int cp) =>
        (cp >= 0xE000 && cp <= 0xF8FF) || cp >= 0xF0000;

    private static bool IsSfnt(int sfntVersion) =>
        sfntVersion == 0x4F54544F   // 'OTTO' (CFF-based OpenType)
        || sfntVersion == 0x00010000
        || sfntVersion == 0x74727565; // 'true'

    /// <summary>Extracts a table's bytes from an sfnt (OpenType) container, or null.</summary>
    private static byte[]? ExtractSfntTable(byte[] data, string tag)
    {
        if (data.Length < 12 || tag.Length != 4)
            return null;
        int numTables = (data[4] << 8) | data[5];
        for (int i = 0, p = 12; i < numTables && p + 16 <= data.Length; i++, p += 16)
        {
            if (data[p] == tag[0] && data[p + 1] == tag[1] && data[p + 2] == tag[2] && data[p + 3] == tag[3])
            {
                int offset = ReadU32(data, p + 8);
                int length = ReadU32(data, p + 12);
                if (offset < 0 || length <= 0 || (long)offset + length > data.Length)
                    return null;
                var table = new byte[length];
                Array.Copy(data, offset, table, 0, length);
                return table;
            }
        }
        return null;
    }

    private static int ReadU32(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    /// <summary>
    /// Loads a simple font's <c>/Encoding &lt;&lt; /BaseEncoding ... /Differences [...] &gt;&gt;</c>
    /// dictionary (ISO 32000-2 §9.6.6.2) if present. Returns (null, null) when
    /// <c>/Encoding</c> is absent, a bare name, or an indirect object that
    /// doesn't resolve to a dictionary. The Differences array alternates
    /// [startCode name name ... startCode2 name ...] — an integer resets the
    /// running code counter, and each following name is assigned to that
    /// code, incrementing.
    /// </summary>
    private (Dictionary<int, string>? names, string? baseEncoding) LoadDifferencesEncoding(PdfDictionary? font)
    {
        if (font == null)
            return (null, null);

        var encObj = _document!.Resolve(font.GetOptional("Encoding") ?? PdfNull.Instance);
        if (encObj is not PdfDictionary encDict)
            return (null, null);

        var baseEncoding = encDict.GetNameOrNull("BaseEncoding");

        var diffsObj = _document!.Resolve(encDict.GetOptional("Differences") ?? PdfNull.Instance);
        if (diffsObj is not PdfArray diffs || diffs.Count == 0)
            return (null, baseEncoding);

        var map = new Dictionary<int, string>();
        int code = 0;
        foreach (var item in diffs)
        {
            var resolved = _document!.Resolve(item);
            if (TryNumber(resolved, out var codeNum))
            {
                code = (int)codeNum;
            }
            else if (resolved is PdfName glyphName)
            {
                map[code] = glyphName.Value;
                code++;
            }
        }

        return (map, baseEncoding);
    }

    /// <summary>
    /// Converts a PostScript glyph name to Unicode, trying the algorithmic
    /// <c>uniXXXX</c> / <c>uXXXX[XX[XX]]</c> convention first (covers most
    /// modern subset fonts without needing any table), then the Adobe Glyph
    /// List subset in <see cref="AdobeGlyphList"/>. Returns null if neither
    /// recognizes the name.
    /// </summary>
    private static string? GlyphNameToUnicode(string glyphName)
    {
        var fromUniConvention = TryDecodeUniName(glyphName);
        if (fromUniConvention != null)
            return fromUniConvention;

        return AdobeGlyphList.ToUnicode(glyphName);
    }

    /// <summary>
    /// "uniXXXX" (one or more 4-hex-digit groups, e.g. "uniFB01" or a
    /// multi-character "uni00410042") or "uXXXX"/"uXXXXX"/"uXXXXXX" (a single
    /// 4-6 hex digit codepoint) per the Adobe Glyph List naming convention.
    /// </summary>
    private static string? TryDecodeUniName(string glyphName)
    {
        if (glyphName.StartsWith("uni", StringComparison.Ordinal))
        {
            var hex = glyphName.Substring(3);
            if (hex.Length == 0 || hex.Length % 4 != 0 || !IsAllHex(hex))
                return null;

            var sb = new StringBuilder();
            for (int i = 0; i < hex.Length; i += 4)
            {
                if (!int.TryParse(hex.AsSpan(i, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var codePoint))
                    return null;
                sb.Append((char)codePoint);
            }
            return sb.ToString();
        }

        if (glyphName.Length >= 1 && glyphName[0] == 'u' && glyphName.Length != 1)
        {
            var hex = glyphName.Substring(1);
            if (hex.Length is < 4 or > 6 || !IsAllHex(hex))
                return null;

            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var codePoint))
                return null;
            if (codePoint is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                return null;

            return char.ConvertFromUtf32(codePoint);
        }

        return null;
    }

    private static bool IsAllHex(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }

    private static string DecodeWinAnsi(int charCode)
    {
        // WinAnsiEncoding (Windows Code Page 1252)
        // Most chars map directly, special handling for 128-159
        if (charCode < 128 || charCode >= 160)
            return CharToString((char)charCode);

        // Special mappings for 128-159
        return charCode switch
        {
            128 => "\u20AC", // Euro sign
            130 => "\u201A", // Single low-9 quotation mark
            131 => "\u0192", // Latin small letter f with hook
            132 => "\u201E", // Double low-9 quotation mark
            133 => "\u2026", // Horizontal ellipsis
            134 => "\u2020", // Dagger
            135 => "\u2021", // Double dagger
            136 => "\u02C6", // Modifier letter circumflex accent
            137 => "\u2030", // Per mille sign
            138 => "\u0160", // Latin capital letter S with caron
            139 => "\u2039", // Single left-pointing angle quotation mark
            140 => "\u0152", // Latin capital ligature OE
            142 => "\u017D", // Latin capital letter Z with caron
            145 => "\u2018", // Left single quotation mark
            146 => "\u2019", // Right single quotation mark
            147 => "\u201C", // Left double quotation mark
            148 => "\u201D", // Right double quotation mark
            149 => "\u2022", // Bullet
            150 => "\u2013", // En dash
            151 => "\u2014", // Em dash
            152 => "\u02DC", // Small tilde
            153 => "\u2122", // Trade mark sign
            154 => "\u0161", // Latin small letter s with caron
            155 => "\u203A", // Single right-pointing angle quotation mark
            156 => "\u0153", // Latin small ligature oe
            158 => "\u017E", // Latin small letter z with caron
            159 => "\u0178", // Latin capital letter Y with diaeresis
            _ => CharToString((char)charCode)
        };
    }

    private static string DecodeMacRoman(int charCode)
    {
        // MacRomanEncoding - simplified, handle special chars 128-255
        if (charCode < 128)
            return CharToString((char)charCode);

        // Mac Roman special characters (subset)
        return charCode switch
        {
            128 => "\u00C4", // Ä
            129 => "\u00C5", // Å
            130 => "\u00C7", // Ç
            131 => "\u00C9", // É
            132 => "\u00D1", // Ñ
            133 => "\u00D6", // Ö
            134 => "\u00DC", // Ü
            135 => "\u00E1", // á
            136 => "\u00E0", // à
            137 => "\u00E2", // â
            138 => "\u00E4", // ä
            139 => "\u00E3", // ã
            140 => "\u00E5", // å
            141 => "\u00E7", // ç
            142 => "\u00E9", // é
            143 => "\u00E8", // è
            144 => "\u00EA", // ê
            145 => "\u00EB", // ë
            146 => "\u00ED", // í
            147 => "\u00EC", // ì
            148 => "\u00EE", // î
            149 => "\u00EF", // ï
            150 => "\u00F1", // ñ
            151 => "\u00F3", // ó
            152 => "\u00F2", // ò
            153 => "\u00F4", // ô
            154 => "\u00F6", // ö
            155 => "\u00F5", // õ
            156 => "\u00FA", // ú
            157 => "\u00F9", // ù
            158 => "\u00FB", // û
            159 => "\u00FC", // ü
            _ => CharToString((char)charCode)
        };
    }


    // Single-character string cache for the Latin-1 range (#600): the decode
    // fallbacks (DecodeWinAnsi/DecodeMacRoman/identity) allocated a fresh
    // one-char string per glyph. Cached instances are value-equal to what
    // ToString() produced; Letter.Value is only ever compared by value.
    private static readonly string[] Latin1CharStrings = CreateLatin1CharStrings();

    private static string[] CreateLatin1CharStrings()
    {
        var strings = new string[256];
        for (int i = 0; i < strings.Length; i++)
            strings[i] = ((char)i).ToString();
        return strings;
    }

    private static string CharToString(char c) =>
        c <= '\u00FF' ? Latin1CharStrings[c] : c.ToString();

    /// <summary>
    /// A numeric operand as a double. A private copy of TextExtractor's helper
    /// of the same name — three lines, and importing it would mean widening a
    /// private member of the class this one was extracted FROM.
    /// </summary>
    private static bool TryNumber(PdfObject? obj, out double v)
    {
        switch (obj)
        {
            case PdfInteger i: v = i.Value; return true;
            case PdfReal r:    v = r.Value; return true;
            default:           v = 0; return false;
        }
    }
}
