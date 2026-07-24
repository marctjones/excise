using System.Globalization;
using System.Text;
using Excise.Core.Content;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Rendering.Fonts;
using SkiaSharp;
using CoreCffParser = Excise.Core.Fonts.CffParser;

namespace Excise.Rendering;

internal partial class RenderContext
{
    #region Text Rendering

    private void BeginText()
    {
        ClearPendingTextClipPath();
        _inTextBlock = true;
        _textState.Reset();
    }

    private void EndText()
    {
        ApplyPendingTextClipPath();
        _inTextBlock = false;
    }

    private void SetFont(string fontName, double fontSize)
    {
        // Remove leading / if present
        if (fontName.StartsWith("/"))
            fontName = fontName.Substring(1);

        _textState.FontName = fontName;
        _textState.FontSize = (float)fontSize;

        // Try to get the font from the active resources (innermost Form
        // XObject's /Resources, falling back to the page) to determine the
        // base font and encoding.
        var fontDict = ResolveFontFromActiveResources(fontName);
        _currentFont = ResolveRenderFont(fontName, fontDict);
    }

    /// <summary>
    /// Builds the complete resolved font state for one <c>Tf</c> operator
    /// (#513). This is the old <c>SetFont</c> body, unchanged branch for
    /// branch — same order, same fallback behavior — just packed into one
    /// immutable <see cref="Fonts.ResolvedRenderFont"/> instead of assigned
    /// to ~18 scattered <c>_current*</c> fields one at a time.
    ///
    /// The ORDER below is load-bearing, not incidental: encoding maps
    /// (<paramref name="fontDict"/>'s /Encoding) must be built before the
    /// embedded typeface is loaded, because Type1 byte-to-glyph mapping and
    /// CFF width/cmap embedding read the encoding/width data while resolving
    /// the typeface — see the <c>codeToGlyphName</c>/<c>codeToUnicode</c>/
    /// <c>fontWidths</c> locals threaded into <see cref="TryLoadEmbeddedTypeface"/>
    /// below. Reordering this silently breaks glyph mapping for those font
    /// classes.
    /// </summary>
    private Fonts.ResolvedRenderFont ResolveRenderFont(string fontName, Excise.Core.Primitives.PdfDictionary? fontDict)
    {
        var resolvedFont = PdfFontResolver.Resolve(fontName, fontDict, _page.Document);
        var diagnostics = new List<string>();

        // /Encoding can be either a Name (e.g. /WinAnsiEncoding) or a Dictionary
        // with /BaseEncoding and /Differences. The dictionary form is how embedded
        // subset fonts remap small character codes to specific glyphs — without
        // handling it, text decodes as control characters and renders invisibly.
        // Must resolve the indirect reference; most real PDFs use `/Encoding N 0 R`.
        var encodingDict = resolvedFont.EncodingDictionary;
        var encodingName = resolvedFont.EncodingName;

        char[]? codeToUnicode = null;
        Dictionary<char, byte>? unicodeToCode = null;
        string?[]? codeToGlyphName = null;
        if (encodingDict != null)
        {
            (codeToUnicode, codeToGlyphName, unicodeToCode) = BuildEncodingMaps(encodingDict, encodingName);
        }
        else if (resolvedFont.IsType3)
        {
            var map = BuildBaseEncodingTable(encodingName);
            codeToUnicode = map;
            codeToGlyphName = BuildBaseEncodingGlyphNameTable(map);
            unicodeToCode = BuildUnicodeToCodeMap(map);
        }

        // Parse the font's glyph width table FIRST. The CFF→OpenType wrapper
        // (called inside TryLoadEmbeddedTypeface below) reads these to build
        // hmtx — without populating them first, every embedded font would be
        // wrapped with stale widths from the previously-active font, producing
        // visibly wrong layout (mid-word gaps and overlaps).
        var fontWidths = resolvedFont.Widths;
        var firstChar = resolvedFont.FirstChar;

        // Prefer a typeface loaded from the PDF's own embedded font stream
        // (/FontFile = Type 1, /FontFile2 = TrueType, /FontFile3 = OpenType/CFF).
        // When no embedded data is present, fall through to the system-font mapping.
        var toUnicodeMap = resolvedFont.ToUnicodeMap;
        var embedded = TryLoadEmbeddedTypeface(
            fontDict, toUnicodeMap, codeToGlyphName, codeToUnicode, encodingName,
            fontWidths, firstChar, diagnostics);
        var hasEmbeddedProgram = embedded != null;
        var hasRawType1Program = embedded != null && fontDict != null
            && _embeddedRawType1FontDicts.Contains(fontDict);
        var typeface = embedded ?? GetTypeface(
            resolvedFont.BaseFont,
            suppressSyntheticStyleForMissingType0: resolvedFont.IsType0);
        var byteToGlyph = embedded != null && fontDict != null
            && _embeddedTypefaceByteToGlyph.TryGetValue(fontDict, out var btg)
            ? btg : null;
        var cffCidToGlyph = embedded != null && fontDict != null
            && _embeddedCffCidToGlyph.TryGetValue(fontDict, out var cffMap)
            ? cffMap : null;

        // Type0 (composite CID) fonts need a completely different content-stream
        // parse (2 bytes per character, widths indexed via /W not /Widths).
        Dictionary<int, float>? cidWidths = null;
        var cidDefaultWidth = 1000f;
        ushort[]? cidToGidMap = null;
        var cidUseUnicodeCmap = false;
        CidCMap? cidEncodingCMap = null;
        if (resolvedFont.IsType0 && fontDict != null)
        {
            cidEncodingCMap = TryGetType0EncodingCMap(fontDict);

            var cidFont = PdfFontResolver.ResolveDescendantFont(resolvedFont, _page.Document);
            if (cidFont != null)
            {
                cidDefaultWidth = (float)cidFont.GetNumber("DW", 1000);
                var w = ResolveArray(cidFont, "W");
                if (w != null)
                    cidWidths = ParseWArray(w);

                // /CIDToGIDMap is /Identity (or absent) for most modern Type0
                // fonts and CID == GID. Subset CIDFontType2 fonts produced by
                // veraPDF Test Builder, Word, etc. ship a remapping stream
                // (2-byte big-endian uint16 per CID); without applying it,
                // glyph IDs miss every glyph in the subset and pages render
                // .notdef-only blanks.
                var cidToGidObj = cidFont.GetOptional("CIDToGIDMap");
                if (cidToGidObj != null)
                {
                    var resolved = _page.Document.Resolve(cidToGidObj);
                    if (resolved is Excise.Core.Primitives.PdfStream cidStream)
                    {
                        try
                        {
                            var data = cidStream.DecodedData;
                            int count = data.Length / 2;
                            var map = new ushort[count];
                            for (int i = 0; i < count; i++)
                                map[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
                            cidToGidMap = map;
                        }
                        catch
                        {
                            cidToGidMap = null;
                            diagnostics.Add("CIDToGIDMap stream unreadable; falling back to identity CID=GID.");
                        }
                    }
                }

                var toUnicodeName = fontDict.GetNameOrNull("ToUnicode");
                cidUseUnicodeCmap =
                    hasEmbeddedProgram &&
                    cidToGidMap == null &&
                    cidEncodingCMap == null &&
                    (toUnicodeName == "Identity-H" || toUnicodeName == "Identity-V");
            }
        }

        return new Fonts.ResolvedRenderFont(
            resolvedFont,
            codeToUnicode,
            unicodeToCode,
            codeToGlyphName,
            typeface,
            byteToGlyph,
            hasEmbeddedProgram,
            hasRawType1Program,
            cidWidths,
            cidDefaultWidth,
            cidUseUnicodeCmap,
            cidEncodingCMap,
            cidToGidMap,
            cffCidToGlyph,
            diagnostics);
    }

    private CidCMap? TryGetType0EncodingCMap(Excise.Core.Primitives.PdfDictionary fontDict)
    {
        if (_type0EncodingCMaps.TryGetValue(fontDict, out var cached))
            return cached;

        CidCMap? cmap = null;
        try
        {
            var encodingObj = fontDict.GetOptional("Encoding");
            if (encodingObj != null)
            {
                var resolved = _page.Document.Resolve(encodingObj);
                if (resolved is Excise.Core.Primitives.PdfStream stream)
                {
                    cmap = CidCMap.Parse(stream.DecodedData);
                }
                else if (resolved is Excise.Core.Primitives.PdfName name
                         && name.Value is not ("Identity-H" or "Identity-V"))
                {
                    // Registered (predefined) CMap NAME as /Encoding (#515), e.g.
                    // /UniGB-UCS2-H or /90ms-RKSJ-H: load the same embedded Adobe
                    // CMap the extractor uses so glyph selection goes through
                    // code → CID (honoring the CMap's mixed 1/2-byte codespaces)
                    // instead of misreading the bytes as 2-byte identity CIDs.
                    // Unknown names return null and keep the identity fallback.
                    cmap = PredefinedCMapProvider.TryGetEncodingCMap(name.Value);
                }
            }
        }
        catch
        {
            cmap = null;
        }

        _type0EncodingCMaps[fontDict] = cmap;
        return cmap;
    }

    // Resolve `dict[key]` as a dictionary, following indirect references.
    // PdfDictionary.GetDictionaryOrNull does a direct type-check and misses the
    // common case where the value is a `N 0 R` reference — most FontDescriptor,
    // /Widths, and /Encoding entries in real PDFs are stored that way.
    private Excise.Core.Primitives.PdfDictionary? ResolveDict(
        Excise.Core.Primitives.PdfDictionary dict, string key)
    {
        var obj = dict.GetOptional(key);
        if (obj == null) return null;
        var resolved = _page.Document.Resolve(obj);
        return resolved as Excise.Core.Primitives.PdfDictionary;
    }

    private Excise.Core.Primitives.PdfArray? ResolveArray(
        Excise.Core.Primitives.PdfDictionary dict, string key)
    {
        var obj = dict.GetOptional(key);
        if (obj == null) return null;
        var resolved = _page.Document.Resolve(obj);
        return resolved as Excise.Core.Primitives.PdfArray;
    }

    private bool TryGetResolvedNumber(PdfObject? obj, out double value)
    {
        value = 0;
        if (obj == null)
            return false;

        try
        {
            var resolved = _page.Document.Resolve(obj);
            return resolved.TryGetNumber(out value);
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private bool TryGetArrayNumber(PdfArray? array, int index, out double value)
    {
        value = 0;
        return array != null &&
               index >= 0 &&
               index < array.Count &&
               TryGetResolvedNumber(array[index], out value);
    }

    private double ArrayNumberOrDefault(PdfArray? array, int index, double defaultValue = 0)
        => TryGetArrayNumber(array, index, out var value) ? value : defaultValue;

    private SKMatrix GetMatrix(PdfArray? array)
    {
        if (array == null || array.Count < 6)
            return new SKMatrix(1, 0, 0, 0, 1, 0, 0, 0, 1);

        return new SKMatrix(
            (float)ArrayNumberOrDefault(array, 0, 1),
            (float)ArrayNumberOrDefault(array, 2),
            (float)ArrayNumberOrDefault(array, 4),
            (float)ArrayNumberOrDefault(array, 1),
            (float)ArrayNumberOrDefault(array, 3, 1),
            (float)ArrayNumberOrDefault(array, 5),
            0,
            0,
            1);
    }

    // Parse the /W array of a CIDFont (PDF spec 9.7.4.3). Two forms are
    // interleaved in a single array:
    //   cid [w1 w2 w3 ...]     → assigns w1..wN to cid, cid+1, cid+2, ...
    //   cid_start cid_end w    → assigns w to every CID in [cid_start, cid_end]
    // Widths are in glyph units (1/1000 of the designed em).
    private static Dictionary<int, float> ParseWArray(Excise.Core.Primitives.PdfArray w)
    {
        var map = new Dictionary<int, float>();
        int i = 0;
        while (i < w.Count)
        {
            if (!IsNumber(w[i])) { i++; continue; }
            int cid = (int)w.GetNumber(i);
            i++;
            if (i >= w.Count) break;

            if (w[i] is Excise.Core.Primitives.PdfArray inner)
            {
                for (int j = 0; j < inner.Count; j++)
                    map[cid + j] = (float)inner.GetNumber(j);
                i++;
            }
            else if (IsNumber(w[i]) && i + 1 < w.Count && IsNumber(w[i + 1]))
            {
                int endCid = (int)w.GetNumber(i);
                float width = (float)w.GetNumber(i + 1);
                for (int c = cid; c <= endCid; c++)
                    map[c] = width;
                i += 2;
            }
            else
            {
                i++; // Malformed — skip and recover.
            }
        }
        return map;
    }

    private static bool IsNumber(Excise.Core.Primitives.PdfObject o) =>
        o is Excise.Core.Primitives.PdfInteger || o is Excise.Core.Primitives.PdfReal;

    // Load the font's embedded file (TrueType or OpenType/CFF) as an SKTypeface
    // so glyphs render in the face the PDF actually specifies, with the widths
    // and kerning the PDF's /Widths table was authored against. Cached per-
    // dict for the life of this RenderContext; disposed at end of Render().
    private SKTypeface? TryLoadEmbeddedTypeface(
        Excise.Core.Primitives.PdfDictionary? fontDict,
        IReadOnlyDictionary<int, string>? toUnicodeMap,
        string?[]? codeToGlyphName,
        char[]? codeToUnicode,
        string encodingName,
        float[]? fontWidths,
        int firstChar,
        List<string> diagnostics)
    {
        if (fontDict == null) return null;
        if (_embeddedTypefaces.TryGetValue(fontDict, out var cached))
            return cached;

        // Handle both simple and Type0 (CID) fonts: Type0 carries the embedded
        // file inside its /DescendantFonts[0]/FontDescriptor, not on itself.
        var descriptor = ResolveDict(fontDict, "FontDescriptor");
        if (descriptor == null)
        {
            var descendants = ResolveArray(fontDict, "DescendantFonts");
            if (descendants != null && descendants.Count > 0)
            {
                var descendantObj = _page.Document.Resolve(descendants[0]);
                if (descendantObj is Excise.Core.Primitives.PdfDictionary cidFontDict)
                    descriptor = ResolveDict(cidFontDict, "FontDescriptor");
            }
        }
        if (descriptor == null) return null;

        // /FontFile  (Type 1 PostScript) → SkiaSharp/FreeType loads PFA/PFB directly.
        // /FontFile2 (TrueType) → SkiaSharp loads directly.
        // /FontFile3 (OpenType/CFF) → if already SFNT-wrapped, Skia loads it;
        //   if it's raw Type1C/CIDFontType0C (more common in modern PDFs),
        //   we wrap it in a minimal OpenType container first.
        var ff1 = descriptor.GetOptional("FontFile");
        var ff2 = descriptor.GetOptional("FontFile2");
        var ff3 = descriptor.GetOptional("FontFile3");

        byte[]? fontBytes = null;
        bool isType1 = false;
        bool isCff = false;
        if (ff1 != null && _page.Document.Resolve(ff1) is Excise.Core.Primitives.PdfStream s1)
        {
            try { fontBytes = s1.DecodedData; } catch { }
            isType1 = fontBytes != null;
        }
        else if (ff2 != null && _page.Document.Resolve(ff2) is Excise.Core.Primitives.PdfStream s2)
        {
            try { fontBytes = s2.DecodedData; } catch { }
        }
        else if (ff3 != null && _page.Document.Resolve(ff3) is Excise.Core.Primitives.PdfStream s3)
        {
            try { fontBytes = s3.DecodedData; } catch { }
            var subtype = s3.GetNameOrNull("Subtype");
            // Type1C and CIDFontType0C are raw CFF without SFNT wrapper; OpenType
            // is already SFNT-wrapped and passes through.
            isCff = subtype == "Type1C" || subtype == "CIDFontType0C";
        }
        if (fontBytes == null || fontBytes.Length == 0) return null;

        // For raw CFF (Type1C / CIDFontType0C), synthesize an OpenType container
        // so Skia can load it. The wrapper's cmap has been independently verified
        // (CffWrapperTests.Wrapped_CffSkiaCanResolveKnownGlyphs) — Skia resolves
        // every Unicode char to the correct CFF glyph index.
        // For CID-keyed CFF (Adobe-Japan1 etc.) the wrapper produces a minimal
        // cmap and returns the CID → glyph index map via cffCidToGlyph; the
        // renderer threads that through SetFont so RenderCidBytes can dispatch
        // glyph IDs directly.
        byte[] loadableBytes = fontBytes;
        Dictionary<int, int>? cffCidToGlyph = null;
        ushort[]? cffByteToGlyph = null;
        ushort[]? type1ByteToGlyph = null;
        if (isCff)
        {
            var wrapped = TryWrapCffAsOpenType(
                fontBytes, fontDict, descriptor, codeToGlyphName, codeToUnicode, encodingName,
                fontWidths, firstChar, out cffCidToGlyph, out cffByteToGlyph);
            if (wrapped != null) loadableBytes = wrapped;
        }
        else if (isType1)
        {
            type1ByteToGlyph = ShouldBuildType1ByteToGlyphMap(fontDict, codeToGlyphName)
                ? TryBuildType1ByteToGlyph(fontBytes, codeToGlyphName)
                : null;
        }

        SKTypeface? typeface;
        lock (_typefaceLoadLock)
        {
            try
            {
                using var data = SKData.CreateCopy(loadableBytes);
                typeface = SKTypeface.FromData(data);
            }
            catch { typeface = null; }

            if (typeface == null) return null;

            // Sanity-probe the wrapped font — for some CFF subsets (most commonly
            // dingbat fonts produced by the XEP toolchain) Skia loads our wrapper
            // and resolves the cmap, but its CFF interpreter finds no charstring
            // outlines and silently draws nothing. Detect that and fall back to
            // the system-font path so the user at least sees *some* glyph for the
            // codepoint instead of empty whitespace.
            if (isCff && !ProducesGlyphOutlines(typeface))
            {
                diagnostics.Add("Embedded CFF program produced no glyph outlines; falling back to a system typeface.");
                typeface.Dispose();
                return null;
            }
        }

        _embeddedTypefaces[fontDict] = typeface;
        if (isType1)
            _embeddedRawType1FontDicts.Add(fontDict);
        _embeddedTypefaceByteToGlyph[fontDict] = cffByteToGlyph
            ?? type1ByteToGlyph
            ?? ResolveByteCodeCmap(typeface, fontDict, toUnicodeMap);
        _embeddedCffCidToGlyph[fontDict] = cffCidToGlyph;
        return typeface;
    }

    private static ushort[]? TryBuildType1ByteToGlyph(byte[] fontBytes, string?[]? pdfCodeToGlyphName)
    {
        try
        {
            var fontEncoding = ParseType1Encoding(fontBytes);
            var charStringNames = ParseType1CharStringNames(fontBytes);
            if (charStringNames.Count == 0)
                return null;

            var glyphNameToId = new Dictionary<string, ushort>(StringComparer.Ordinal);
            for (int i = 0; i < charStringNames.Count && i <= ushort.MaxValue; i++)
            {
                if (!glyphNameToId.ContainsKey(charStringNames[i]))
                    glyphNameToId[charStringNames[i]] = (ushort)i;
            }

            var sourceNames = HasAnyGlyphNames(pdfCodeToGlyphName)
                ? pdfCodeToGlyphName
                : fontEncoding;
            if (!HasAnyGlyphNames(sourceNames))
                return null;

            var map = new ushort[256];
            var mapped = 0;
            for (int code = 0; code < map.Length; code++)
            {
                var glyphName = sourceNames?[code];
                if (string.IsNullOrEmpty(glyphName))
                    continue;

                if (glyphNameToId.TryGetValue(glyphName, out var glyphId))
                {
                    map[code] = glyphId;
                    if (glyphId != 0)
                        mapped++;
                }
            }

            return mapped > 0 ? map : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasAnyGlyphNames(string?[]? names)
        => names != null && names.Any(static name => !string.IsNullOrEmpty(name));

    private static bool ShouldBuildType1ByteToGlyphMap(
        Excise.Core.Primitives.PdfDictionary fontDict,
        string?[]? pdfCodeToGlyphName)
    {
        if (HasAnyGlyphNames(pdfCodeToGlyphName))
            return false;

        var encodingName = fontDict.GetNameOrNull("Encoding");
        return encodingName != null &&
               encodingName is not "WinAnsiEncoding" and not "MacRomanEncoding" and not "StandardEncoding";
    }

    private static string?[]? ParseType1Encoding(byte[] fontBytes)
    {
        var eexec = IndexOfAscii(fontBytes, "eexec");
        var clearLength = eexec >= 0 ? eexec : fontBytes.Length;
        var clear = Encoding.Latin1.GetString(fontBytes, 0, clearLength);
        var names = new string?[256];
        var mapped = 0;
        var index = 0;
        while ((index = clear.IndexOf("dup", index, StringComparison.Ordinal)) >= 0)
        {
            var p = index + 3;
            SkipAsciiWhite(clear, ref p);
            if (!TryReadInt(clear, ref p, out var code) || code < 0 || code >= 256)
            {
                index += 3;
                continue;
            }

            SkipAsciiWhite(clear, ref p);
            if (p >= clear.Length || clear[p] != '/')
            {
                index += 3;
                continue;
            }

            p++;
            var start = p;
            while (p < clear.Length && IsPdfNameChar(clear[p]))
                p++;
            if (p == start)
            {
                index += 3;
                continue;
            }

            var glyphName = clear[start..p];
            SkipAsciiWhite(clear, ref p);
            if (p + 3 <= clear.Length && string.Equals(clear.AsSpan(p, Math.Min(3, clear.Length - p)).ToString(), "put", StringComparison.Ordinal))
            {
                names[code] = glyphName;
                mapped++;
            }

            index = p;
        }

        return mapped > 0 ? names : null;
    }

    private static List<string> ParseType1CharStringNames(byte[] fontBytes)
    {
        var decrypted = DecryptType1Eexec(fontBytes);
        if (decrypted == null || decrypted.Length == 0)
            return new List<string>();

        var text = Encoding.Latin1.GetString(decrypted);
        var charStrings = text.IndexOf("/CharStrings", StringComparison.Ordinal);
        if (charStrings < 0)
            return new List<string>();

        var names = new List<string>();
        var index = charStrings + "/CharStrings".Length;
        while ((index = text.IndexOf('/', index)) >= 0)
        {
            index++;
            if (index >= text.Length)
                break;

            var start = index;
            while (index < text.Length && IsPdfNameChar(text[index]))
                index++;
            if (index == start)
                continue;

            var glyphName = text[start..index];
            var p = index;
            SkipAsciiWhite(text, ref p);
            if (!TryReadInt(text, ref p, out _))
                continue;

            SkipAsciiWhite(text, ref p);
            if (!StartsType1CharStringOperator(text, p))
                continue;

            names.Add(glyphName);
        }

        return names;
    }

    private static byte[]? DecryptType1Eexec(byte[] fontBytes)
    {
        var eexec = IndexOfAscii(fontBytes, "eexec");
        if (eexec < 0)
            return null;

        var start = eexec + "eexec".Length;
        while (start < fontBytes.Length && IsAsciiWhite(fontBytes[start]))
            start++;
        if (start >= fontBytes.Length)
            return null;

        var encrypted = LooksLikeAsciiHex(fontBytes, start)
            ? ReadAsciiHexBytes(fontBytes, start)
            : fontBytes[start..];
        if (encrypted.Length <= 4)
            return null;

        var plain = DecryptType1Bytes(encrypted, 55665);
        return plain.Length > 4 ? plain[4..] : Array.Empty<byte>();
    }

    private static byte[] DecryptType1Bytes(byte[] encrypted, int seed)
    {
        const int c1 = 52845;
        const int c2 = 22719;
        var r = seed;
        var plain = new byte[encrypted.Length];
        for (int i = 0; i < encrypted.Length; i++)
        {
            var cipher = encrypted[i];
            plain[i] = (byte)(cipher ^ (r >> 8));
            r = ((cipher + r) * c1 + c2) & 0xffff;
        }

        return plain;
    }

    private static bool LooksLikeAsciiHex(byte[] data, int start)
    {
        var significant = 0;
        var hex = 0;
        for (int i = start; i < data.Length && significant < 16; i++)
        {
            var b = data[i];
            if (IsAsciiWhite(b))
                continue;

            significant++;
            if (IsAsciiHex(b))
                hex++;
        }

        return significant >= 8 && hex == significant;
    }

    private static byte[] ReadAsciiHexBytes(byte[] data, int start)
    {
        var nibbles = new List<int>();
        for (int i = start; i < data.Length; i++)
        {
            var b = data[i];
            if (IsAsciiWhite(b))
                continue;
            if (!TryHexValue(b, out var value))
                break;
            nibbles.Add(value);
        }

        if ((nibbles.Count & 1) == 1)
            nibbles.Add(0);

        var bytes = new byte[nibbles.Count / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((nibbles[i * 2] << 4) | nibbles[i * 2 + 1]);
        return bytes;
    }

    private static int IndexOfAscii(byte[] data, string needle)
    {
        var bytes = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i <= data.Length - bytes.Length; i++)
        {
            var matched = true;
            for (int j = 0; j < bytes.Length; j++)
            {
                if (data[i + j] != bytes[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return i;
        }

        return -1;
    }

    private static bool StartsType1CharStringOperator(string text, int index)
    {
        if (index >= text.Length)
            return false;
        if (text[index] == '-')
            return true;
        if (index + 2 <= text.Length && text.AsSpan(index, 2).SequenceEqual("RD"))
            return true;
        return false;
    }

    private static void SkipAsciiWhite(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool TryReadInt(string text, ref int index, out int value)
    {
        value = 0;
        var start = index;
        var sign = 1;
        if (index < text.Length && text[index] == '-')
        {
            sign = -1;
            index++;
        }

        var parsed = false;
        while (index < text.Length && char.IsDigit(text[index]))
        {
            parsed = true;
            value = checked(value * 10 + (text[index] - '0'));
            index++;
        }

        if (!parsed)
        {
            index = start;
            return false;
        }

        value *= sign;
        return true;
    }

    private static bool IsPdfNameChar(char c)
        => !char.IsWhiteSpace(c) && c is not '/' and not '[' and not ']' and not '<' and not '>' and not '(' and not ')';

    private static bool IsAsciiWhite(byte b)
        => b is 0x00 or 0x09 or 0x0a or 0x0c or 0x0d or 0x20;

    private static bool IsAsciiHex(byte b)
        => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    private static bool TryHexValue(byte b, out int value)
    {
        if (b >= '0' && b <= '9')
        {
            value = b - '0';
            return true;
        }
        if (b >= 'a' && b <= 'f')
        {
            value = b - 'a' + 10;
            return true;
        }
        if (b >= 'A' && b <= 'F')
        {
            value = b - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Detect typefaces that need the byte-coded glyph-ID draw path because
    /// Skia's shaper can't read their cmap, and pre-compute the
    /// byte→glyphId lookup once.
    ///
    /// We probe Skia first: if <c>SKFont.GetGlyph((int)c)</c> resolves
    /// common Unicode codepoints to real glyphs, the font has Unicode
    /// coverage Skia can shape and we don't need the workaround. Otherwise
    /// we read any format-0 subtable from the cmap. Returns null when no
    /// override is needed (the common case for modern Type 0 / CID fonts
    /// with Identity-H or Unicode-mapped cmaps).
    /// </summary>
    private static ushort[]? ResolveByteCodeCmap(
        SKTypeface typeface,
        Excise.Core.Primitives.PdfDictionary? fontDict,
        IReadOnlyDictionary<int, string>? toUnicodeMap)
    {
        // Type0 (CID) fonts go through a separate draw path that already
        // walks bytes 2 at a time and resolves through the descendant font;
        // the format-0 workaround would double-encode.
        if (fontDict?.GetNameOrNull("Subtype") == "Type0") return null;

        var byteMap = CmapFormat0Table.TryRead(typeface);
        if (byteMap == null)
            return null;

        if (ToUnicodeMapsToMissingEmbeddedGlyphs(typeface, toUnicodeMap))
            return byteMap;

        using var probe = new SKFont(typeface, 12f);
        int[] unicodeProbe = { 'A', 'a', 'M', 'e', '0', ' ', 'i' };
        foreach (var cp in unicodeProbe)
        {
            if (probe.GetGlyph(cp) != 0)
            {
                // Skia can shape this font directly via its cmap.
                return null;
            }
        }

        // No Unicode coverage Skia can see — fall back to the format-0
        // subtable if present.
        return byteMap;
    }

    private static bool ToUnicodeMapsToMissingEmbeddedGlyphs(
        SKTypeface typeface,
        IReadOnlyDictionary<int, string>? toUnicodeMap)
    {
        if (toUnicodeMap == null || toUnicodeMap.Count == 0)
            return false;

        using var probe = new SKFont(typeface, 12f);
        foreach (var text in toUnicodeMap.Values)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune))
                    continue;

                if (probe.GetGlyph(rune.Value) == 0)
                    return true;
            }
        }

        return false;
    }

    private static bool ProducesGlyphOutlines(SKTypeface typeface)
    {
        // Sample up to 16 evenly-distributed glyph indices; if none have an
        // outline, the CFF program is unreadable for our purposes.
        int n = typeface.GlyphCount;
        if (n <= 1) return false;
        int probes = Math.Min(16, n - 1);
        int step = Math.Max(1, (n - 1) / probes);
        using var font = new SKFont(typeface, 100f);
        for (int i = 1; i <= probes; i++)
        {
            ushort gid = (ushort)Math.Min(n - 1, i * step);
            using var p = font.GetGlyphPath(gid);
            if (p != null && p.PointCount > 0) return true;
        }
        return false;
    }

    private byte[]? TryWrapCffAsOpenType(
        byte[] cff,
        Excise.Core.Primitives.PdfDictionary fontDict,
        Excise.Core.Primitives.PdfDictionary descriptor,
        string?[]? codeToGlyphName,
        char[]? codeToUnicode,
        string encodingName,
        float[]? fontWidths,
        int firstChar,
        out Dictionary<int, int>? cffCidToGlyph,
        out ushort[]? cffByteToGlyph)
    {
        cffCidToGlyph = null;
        cffByteToGlyph = null;
        var cffInfo = CoreCffParser.Parse(cff);
        if (cffInfo == null) return null;

        var unicodeToGlyph = new Dictionary<char, int>(256);
        var glyphWidths = new Dictionary<int, ushort>(256);

        if (cffInfo.IsCidKeyed)
        {
            // CID-keyed CFF (Adobe-Japan1 / Adobe-CNS1 / Adobe-Korea1). The
            // CFF charset stores CIDs, not glyph names, so the AdobeGlyphList
            // path doesn't apply — there's no Unicode → name → glyph chain
            // to walk. Skip cmap construction and rely on the renderer
            // dispatching glyphs via SKTextEncoding.GlyphId; CFF glyph
            // ordering is preserved by the wrapper, so the OpenType glyph
            // index Skia ultimately uses == the CFF glyph index ==
            // CidToGlyph[cid] from the descendant font's CFF.
            cffCidToGlyph = cffInfo.CidToGlyph;
        }
        else
        {
            cffByteToGlyph = BuildCffSimpleByteToGlyph(cffInfo, codeToGlyphName);

            // Build Unicode → glyph-index map and glyph-index → PDF-width map.
            // Both derive from walking the PDF's character codes 0..255, resolving
            // each to (Unicode, glyph name) and then looking up the glyph index in
            // the CFF charset.
            for (int code = 0; code < 256; code++)
            {
                char unicode = GetUnicodeForCode((byte)code, codeToUnicode, encodingName);
                if (unicode == '\0') continue;
                if (!AdobeGlyphList.TryGetName(unicode, out var glyphName)) continue;
                if (!cffInfo.GlyphNameToIndex.TryGetValue(glyphName, out var glyphIndex)) continue;

                if (!unicodeToGlyph.ContainsKey(unicode))
                    unicodeToGlyph[unicode] = glyphIndex;

                // If /Widths covers this code, use it as the per-glyph hmtx width.
                if (fontWidths != null)
                {
                    int idx = code - firstChar;
                    if (idx >= 0 && idx < fontWidths.Length)
                        glyphWidths[glyphIndex] = (ushort)Math.Clamp(fontWidths[idx], 0, 65535);
                }
            }

            if (cffByteToGlyph != null && fontWidths != null)
            {
                for (int code = 0; code < cffByteToGlyph.Length; code++)
                {
                    int glyphIndex = cffByteToGlyph[code];
                    if (glyphIndex == 0) continue;

                    int idx = code - firstChar;
                    if (idx >= 0 && idx < fontWidths.Length)
                        glyphWidths[glyphIndex] = (ushort)Math.Clamp(fontWidths[idx], 0, 65535);
                }
            }
        }

        short xMin = cffInfo.XMin, yMin = cffInfo.YMin, xMax = cffInfo.XMax, yMax = cffInfo.YMax;
        var bbox = ResolveArray(descriptor, "FontBBox");
        if (bbox != null && bbox.Count >= 4)
        {
            xMin = (short)bbox.GetNumber(0);
            yMin = (short)bbox.GetNumber(1);
            xMax = (short)bbox.GetNumber(2);
            yMax = (short)bbox.GetNumber(3);
        }

        var info = new Fonts.CffToOpenType.PdfFontInfo
        {
            PsName = descriptor.GetNameOrNull("FontName")
                     ?? fontDict.GetNameOrNull("BaseFont")
                     ?? "Unknown",
            XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax,
            Ascent = (short)descriptor.GetNumber("Ascent", 800),
            Descent = (short)descriptor.GetNumber("Descent", -200),
            WeightClass = (ushort)Math.Clamp((int)descriptor.GetNumber("FontWeight", 400), 1, 1000),
            UnicodeToGlyph = unicodeToGlyph,
            GlyphWidths = glyphWidths.Count > 0 ? glyphWidths : null,
        };

        return Fonts.CffToOpenType.Wrap(cff, cffInfo.NumGlyphs, info);
    }

    private static ushort[]? BuildCffSimpleByteToGlyph(CoreCffParser.CffFontInfo cffInfo, string?[]? codeToGlyphName)
    {
        if (codeToGlyphName == null) return null;

        var map = new ushort[256];
        var mapped = 0;
        for (int code = 0; code < map.Length; code++)
        {
            var glyphName = codeToGlyphName[code];
            if (string.IsNullOrEmpty(glyphName)) continue;

            if (cffInfo.GlyphNameToIndex.TryGetValue(glyphName, out var glyphIndex))
            {
                map[code] = (ushort)Math.Clamp(glyphIndex, 0, ushort.MaxValue);
                if (glyphIndex != 0) mapped++;
            }
        }

        return mapped > 0 ? map : null;
    }

    // Decode a raw PDF character code to its Unicode char under a font's
    // encoding. Prefers the /Differences-derived map when present, otherwise
    // falls back to the named base encoding (WinAnsi/MacRoman). Takes the
    // encoding as parameters (rather than reading _currentFont) so it can be
    // called both mid-resolution — while codeToUnicode/encodingName are still
    // local variables in ResolveRenderFont, not yet packed into a
    // ResolvedRenderFont — and post-resolution from render-time call sites,
    // which pass _currentFont's values explicitly.
    private static char GetUnicodeForCode(byte code, char[]? codeToUnicode, string encodingName)
    {
        if (codeToUnicode != null)
            return codeToUnicode[code];
        if (encodingName == "ZapfDingbatsEncoding")
            return ZapfDingbatsEncodingTable[code];

        var encoding = encodingName == "MacRomanEncoding"
            ? Encoding.GetEncoding(10000)
            : Encoding.GetEncoding(1252);
        var s = encoding.GetString(new[] { code });
        return s.Length > 0 ? s[0] : '\0';
    }

    // Build code→Unicode (and inverse) tables for a font whose /Encoding is a
    // dictionary. Seeds from the named base encoding (WinAnsi/MacRoman), then
    // overlays entries from the /Differences array. Per PDF spec 9.6.5:
    // Differences is a sequence of numbers (starting code) and names (glyph
    // names), e.g. [32 /space /exclam /quotedbl 39 /quoteright].
    private (char[] CodeToUnicode, string?[] CodeToGlyphName, Dictionary<char, byte> UnicodeToCode) BuildEncodingMaps(
        Excise.Core.Primitives.PdfDictionary encodingDict, string baseEncoding)
    {
        var map = BuildBaseEncodingTable(baseEncoding);
        var glyphNames = BuildBaseEncodingGlyphNameTable(map);

        var differences = ResolveArray(encodingDict, "Differences");
        if (differences != null)
        {
            int currentCode = 0;
            for (int i = 0; i < differences.Count; i++)
            {
                var item = differences[i];
                if (item is Excise.Core.Primitives.PdfName name)
                {
                    if (currentCode >= 0 && currentCode < 256)
                    {
                        glyphNames[currentCode] = name.Value;
                        map[currentCode] = AdobeGlyphList.TryGet(name.Value, out var ch)
                            ? ch
                            : '\0';
                    }
                    currentCode++;
                }
                else if (item is Excise.Core.Primitives.PdfInteger intNum)
                {
                    currentCode = (int)intNum.Value;
                }
                else if (item is Excise.Core.Primitives.PdfReal realNum)
                {
                    currentCode = (int)realNum.Value;
                }
            }
        }

        return (map, glyphNames, BuildUnicodeToCodeMap(map));
    }

    private static Dictionary<char, byte> BuildUnicodeToCodeMap(char[] map)
    {
        var unicodeToCode = new Dictionary<char, byte>(256);
        for (int b = 0; b < 256; b++)
        {
            var c = map[b];
            if (c != '\0' && !unicodeToCode.ContainsKey(c))
                unicodeToCode[c] = (byte)b;
        }

        return unicodeToCode;
    }

    private static char[] BuildBaseEncodingTable(string encodingName)
    {
        if (encodingName == "ZapfDingbatsEncoding")
            return ZapfDingbatsEncodingTable.ToArray();

        var encoding = encodingName == "MacRomanEncoding"
            ? Encoding.GetEncoding(10000)
            : Encoding.GetEncoding(1252);

        var map = new char[256];
        var buffer = new byte[1];
        for (int b = 0; b < 256; b++)
        {
            buffer[0] = (byte)b;
            var decoded = encoding.GetString(buffer);
            map[b] = decoded.Length > 0 ? decoded[0] : '\0';
        }
        return map;
    }

    private static readonly char[] ZapfDingbatsEncodingTable = BuildZapfDingbatsEncodingTable();

    private static char[] BuildZapfDingbatsEncodingTable()
    {
        var map = new char[256];
        ushort[] values =
        [
            0x0020, 0x2701, 0x2702, 0x2703, 0x2704, 0x260E, 0x2706, 0x2707,
            0x2708, 0x2709, 0x261B, 0x261E, 0x270C, 0x270D, 0x270E, 0x270F,
            0x2710, 0x2711, 0x2712, 0x2713, 0x2714, 0x2715, 0x2716, 0x2717,
            0x2718, 0x2719, 0x271A, 0x271B, 0x271C, 0x271D, 0x271E, 0x271F,
            0x2720, 0x2721, 0x2722, 0x2723, 0x2724, 0x2725, 0x2726, 0x2727,
            0x2728, 0x2605, 0x2729, 0x272A, 0x272B, 0x272C, 0x272D, 0x272E,
            0x272F, 0x2730, 0x2731, 0x2732, 0x2733, 0x2734, 0x2735, 0x2736,
            0x2737, 0x2738, 0x2739, 0x273A, 0x273B, 0x273C, 0x273D, 0x273E,
            0x273F, 0x2740, 0x2741, 0x2742, 0x2743, 0x2744, 0x2745, 0x2746,
            0x2747, 0x2748, 0x2749, 0x274A, 0x274B, 0x25CF, 0x274D, 0x25A0,
            0x274F, 0x2750, 0x2751, 0x2752, 0x25B2, 0x25BC, 0x25C6, 0x2756,
            0x25D7, 0x2758, 0x2759, 0x275A, 0x275B, 0x275C, 0x275D, 0x275E,
        ];

        for (int i = 0; i < values.Length; i++)
            map[0x20 + i] = (char)values[i];

        ushort[] upperValues =
        [
            0x2761, 0x2762, 0x2763, 0x2764, 0x2765, 0x2766, 0x2767, 0x2663,
            0x2666, 0x2665, 0x2660, 0x2460, 0x2461, 0x2462, 0x2463, 0x2464,
            0x2465, 0x2466, 0x2467, 0x2468, 0x2469, 0x2776, 0x2777, 0x2778,
            0x2779, 0x277A, 0x277B, 0x277C, 0x277D, 0x277E, 0x277F, 0x2780,
            0x2781, 0x2782, 0x2783, 0x2784, 0x2785, 0x2786, 0x2787, 0x2788,
            0x2789, 0x278A, 0x278B, 0x278C, 0x278D, 0x278E, 0x278F, 0x2790,
            0x2791, 0x2792, 0x2793, 0x2794, 0x2192, 0x2194, 0x2195, 0x2798,
            0x2799, 0x279A, 0x279B, 0x279C, 0x279D, 0x279E, 0x279F, 0x27A0,
            0x27A1, 0x27A2, 0x27A3, 0x27A4, 0x27A5, 0x27A6, 0x27A7, 0x27A8,
            0x27A9, 0x27AA, 0x27AB, 0x27AC, 0x27AD, 0x27AE, 0x27AF, 0x0000,
            0x27B1, 0x27B2, 0x27B3, 0x27B4, 0x27B5, 0x27B6, 0x27B7, 0x27B8,
            0x27B9, 0x27BA, 0x27BB, 0x27BC, 0x27BD, 0x27BE,
        ];

        for (int i = 0; i < upperValues.Length; i++)
            map[0xA1 + i] = upperValues[i] == 0 ? '\0' : (char)upperValues[i];

        return map;
    }

    private static string?[] BuildBaseEncodingGlyphNameTable(char[] unicodeMap)
    {
        var glyphNames = new string?[256];
        for (int b = 0; b < glyphNames.Length; b++)
        {
            var c = unicodeMap[b];
            if (c != '\0' && AdobeGlyphList.TryGetName(c, out var glyphName))
                glyphNames[b] = glyphName;
        }

        return glyphNames;
    }

    // Effective font size applied to glyph drawing: raw Tf size scaled by the
    // text matrix's Y-scale (handles the common `1 Tf` + `s 0 0 s ... Tm` idiom).
    private float GetEffectiveFontSize()
    {
        var c = _textState.TextMatrixC;
        var d = _textState.TextMatrixD;
        var yScale = (float)Math.Sqrt(c * c + d * d);
        if (yScale < 1e-6f) yScale = 1f;
        return _textState.FontSize * yScale;
    }

    // Horizontal-to-vertical aspect ratio of the text matrix. Most PDFs use a
    // uniform Tm (X-scale == Y-scale) so this is 1. When they don't — e.g. a
    // condensed heading like SCOTUS's `14.2001 0 0 15 ... Tm` for SUPREME COURT
    // — glyphs must render horizontally squeezed by this ratio and advance
    // must scale by this ratio too, otherwise accumulated per-glyph error
    // shows up as mid-word gaps.
    private float GetTextMatrixXYRatio()
    {
        var a = _textState.TextMatrixA;
        var b = _textState.TextMatrixB;
        var c = _textState.TextMatrixC;
        var d = _textState.TextMatrixD;
        var xScale = (float)Math.Sqrt(a * a + b * b);
        var yScale = (float)Math.Sqrt(c * c + d * d);
        if (xScale < 1e-6f || yScale < 1e-6f) return 1f;
        return xScale / yScale;
    }

    private SKMatrix CreateTextRenderingMatrix(float x, float y, float horizontalScale, float ySign)
    {
        var a = _textState.TextMatrixA;
        var b = _textState.TextMatrixB;
        var c = _textState.TextMatrixC;
        var d = _textState.TextMatrixD;
        var yScale = (float)Math.Sqrt(c * c + d * d);
        if (yScale < 1e-6f)
            yScale = 1f;

        // The existing draw path handles the PDF-vs-Skia vertical glyph
        // direction through ySign. Preserve that behavior by removing only
        // the Tm.d sign from the normalized text-matrix Y basis here.
        var verticalSign = d >= 0 ? 1f : -1f;
        var basisA = a / yScale;
        var basisB = b / yScale;
        var basisC = c / (yScale * verticalSign);
        var basisD = d / (yScale * verticalSign);

        return new SKMatrix(
            basisA * horizontalScale,
            basisC * ySign,
            x,
            basisB * horizontalScale,
            basisD * ySign,
            y,
            0,
            0,
            1);
    }

    private void AdvanceTextMatrixX(float distance)
    {
        var a = _textState.TextMatrixA;
        var b = _textState.TextMatrixB;
        var xScale = (float)Math.Sqrt(a * a + b * b);
        if (xScale < 1e-6f)
        {
            _textState.TextMatrixE += distance;
            return;
        }

        _textState.TextMatrixE += distance * (a / xScale);
        _textState.TextMatrixF += distance * (b / xScale);
    }

    private SKTypeface GetTypeface(string baseFont, bool suppressSyntheticStyleForMissingType0 = false)
    {
        // PDF subset fonts wear a 6-letter+'+' prefix (e.g. GFEDCB+MyriadPro-Semibold).
        // Strip it before matching — otherwise even "ZapfDingbats" subsets fall
        // through to Sans-Serif and the glyphs come out as missing-glyph boxes.
        var bareName = baseFont;
        if (bareName.Length >= 8 && bareName[6] == '+')
            bareName = bareName.Substring(7);

        var style = SKFontStyle.Normal;
        if (!suppressSyntheticStyleForMissingType0)
        {
            if ((bareName.Contains("Bold") || bareName.Contains("Medi"))
                && (bareName.Contains("Italic") || bareName.Contains("Oblique") || bareName.Contains("Ital")))
                style = SKFontStyle.BoldItalic;
            else if (bareName.Contains("Bold") || bareName.Contains("Semibold") || bareName.Contains("Medium") || bareName.Contains("Medi"))
                style = SKFontStyle.Bold;
            else if (bareName.Contains("Italic") || bareName.Contains("Oblique") || bareName.Contains("Ital"))
                style = SKFontStyle.Italic;
        }

        // Match standard PDF base fonts. Allow both exact and prefix matches so
        // family-named subsets ("ZapfDingbatsStd", "MyriadPro-Semibold", etc.)
        // route to the right system substitute.
        string family;
        if (Starts(bareName, "Helvetica")
            || Starts(bareName, "Arial")
            || Starts(bareName, "NimbusSanL"))
            family = "Helvetica";
        else if (Starts(bareName, "Times")
                 || Starts(bareName, "NimbusRomNo9L")
                 || Starts(bareName, "Bookman"))
            family = "Times New Roman";
        else if (Starts(bareName, "Courier")
                 || Starts(bareName, "NimbusMonL")
                 || Starts(bareName, "CMTT"))
            family = "Courier New";
        else if (Starts(bareName, "Symbol"))
            family = "Symbol";
        else if (bareName.Contains("Dingbat") || bareName.Contains("Wingding"))
            return GetTypefaceWithGlyphCoverage(
                style,
                ['✔', '✘', '♠', '♥', '♦', '♣'],
                "Zapf Dingbats",
                "ZapfDingbats",
                "Apple Symbols",
                "Noto Sans Symbols2",
                "Noto Sans Symbols",
                "Segoe UI Symbol",
                "OpenSymbol",
                "Symbola",
                "DejaVu Sans");
        else if (IsCondensedFontName(bareName))
            return GetTypefaceWithGlyphCoverage(
                style,
                ['A', 'a', 'e', 'i', 'n', 't'],
                "Avenir Next Condensed",
                "Arial Narrow",
                "Helvetica Condensed",
                "Helvetica Neue Condensed",
                "Liberation Sans Narrow",
                "Nimbus Sans Narrow",
                "Noto Sans Condensed",
                "DejaVu Sans Condensed",
                "Arial");
        else
            family = "Sans-Serif";

        return GetTypefaceFromFamily(family, style);
    }

    private static bool IsCondensedFontName(string fontName)
        => fontName.Contains("Condensed", StringComparison.OrdinalIgnoreCase)
           || fontName.Contains("Compressed", StringComparison.OrdinalIgnoreCase)
           || fontName.Contains("Narrow", StringComparison.OrdinalIgnoreCase);

    private static SKTypeface GetTypefaceFromFamily(string family, SKFontStyle style)
    {
        lock (_typefaceLoadLock)
        {
            return SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
        }
    }

    private static SKTypeface GetTypefaceWithGlyphCoverage(
        SKFontStyle style,
        char[] requiredGlyphs,
        params string[] familyNames)
    {
        lock (_typefaceLoadLock)
        {
            foreach (var familyName in familyNames)
            {
                var typeface = SKTypeface.FromFamilyName(familyName, style);
                if (typeface == null)
                    continue;

                if (HasGlyphCoverage(typeface, requiredGlyphs))
                    return typeface;

                typeface.Dispose();
            }

            return SKTypeface.FromFamilyName("Sans-Serif", style) ?? SKTypeface.Default;
        }
    }

    private static bool HasGlyphCoverage(SKTypeface typeface, IReadOnlyList<char> chars)
    {
        using var font = new SKFont(typeface, 12f);
        foreach (var c in chars)
        {
            if (font.GetGlyph(c) == 0)
                return false;
        }

        return true;
    }

    private static bool Starts(string s, string prefix) =>
        s.StartsWith(prefix, StringComparison.Ordinal);

    private void TextMove(double tx, double ty)
    {
        // PDF spec 9.4.2: Td's (tx, ty) are in UNSCALED text space units; the
        // new text matrix is [1 0 0 1 tx ty] × TextLineMatrix. The translation
        // lives in the right-hand side, so after composition:
        //   new_e = a*tx + c*ty + e
        //   new_f = b*tx + d*ty + f
        // Previously we added tx/ty directly to device-space e/f, which under
        // any Tm scale (e.g. `1 Tf` + `10.02 0 0 10.02 Tm`) produced line
        // breaks ~10x too small and pulled subsequent text up under the
        // previous line.
        var a = _textState.TextMatrixA;
        var b = _textState.TextMatrixB;
        var c = _textState.TextMatrixC;
        var d = _textState.TextMatrixD;
        var dx = a * tx + c * ty;
        var dy = b * tx + d * ty;
        _textState.TextMatrixE = _textState.LineMatrixE + (float)dx;
        _textState.TextMatrixF = _textState.LineMatrixF + (float)dy;
        _textState.LineMatrixE = _textState.TextMatrixE;
        _textState.LineMatrixF = _textState.TextMatrixF;
    }

    private void SetTextMatrix(double a, double b, double c, double d, double e, double f)
    {
        _textState.TextMatrixA = (float)a;
        _textState.TextMatrixB = (float)b;
        _textState.TextMatrixC = (float)c;
        _textState.TextMatrixD = (float)d;
        _textState.TextMatrixE = (float)e;
        _textState.TextMatrixF = (float)f;
        _textState.LineMatrixE = (float)e;
        _textState.LineMatrixF = (float)f;
    }

    private void TextNewLine()
    {
        // T* operator: Move to start of next line using leading
        TextMove(0, -_textState.TextLeading);
    }

    private void ShowText(PdfString? text)
    {
        if (text == null || text.Bytes.Length == 0) return;
        ShowTextBytes(text.Bytes);
    }

    private void ShowTextBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        if (_currentFont?.IsType3 == true)
            RenderType3Bytes(bytes);
        else if (_currentFont?.IsType0 == true)
            RenderCidBytes(bytes);
        else
            RenderText(DecodeTextBytes(bytes), bytes);
    }

    private void ShowTextArray(PdfArray? array)
    {
        // TJ operator: array of strings and position adjustments.
        if (array == null)
            return;

        foreach (var operand in array)
        {
            if (operand is PdfString text)
            {
                ShowText(text);
            }
            else if (operand.TryGetNumber(out var adjustment))
            {
                // TJ position adjustment is in thousandths of text-space units,
                // which map to device-space X via the text matrix's X-scale
                // (not Y-scale). For non-uniform Tm (e.g. SCOTUS "SUPREME COURT"
                // with 14.2001/15 ratio), using yScale instead of xScale
                // compounds a ~6% per-glyph error into visible mid-word gaps.
                var effectiveSize = GetEffectiveFontSize();
                var xyRatio = GetTextMatrixXYRatio();
                var xOffset = (float)(-adjustment * effectiveSize / 1000.0) * xyRatio;
                AdvanceTextMatrixX(xOffset * _textState.HorizontalScale / 100.0f);
            }
        }
    }

    private void RenderText(string text, byte[]? sourceBytes = null)
    {
        if (!_inTextBlock || _currentFont?.Typeface == null)
            return;
        var currentFont = _currentFont!;

        var effectiveSize = GetEffectiveFontSize();
        var xyRatio = GetTextMatrixXYRatio();
        var mode = _textState.RenderMode;
        var fillText = TextRenderModeFills(mode);
        var strokeText = TextRenderModeStrokes(mode);
        var clipText = TextRenderModeAddsClip(mode);
        var fillWithPattern = fillText && _state.FillPatternName != null;
        SKPath? localFillPatternPath = fillWithPattern ? new SKPath() : null;

        // SkiaSharp 3 separated SKPaint and SKFont — draw calls now take
        // both arguments rather than a paint that wraps a font.
        using var font = CreateTextFont(currentFont.Typeface!, effectiveSize);
        using var fillPaint = CreateTextPaint(SKPaintStyle.Fill, _state.FillColor, _state.FillAlpha);
        using var strokePaint = CreateTextPaint(SKPaintStyle.Stroke, _state.StrokeColor, _state.StrokeAlpha);
        using var measurePaint = new SKPaint { IsAntialias = _options.AntiAlias };
        using var strokeDash = CreateDashEffect();
        if (strokeDash != null)
            strokePaint.PathEffect = strokeDash;

        // Calculate position in PDF coordinates
        var x = _textState.TextMatrixE;
        var y = _textState.TextMatrixF + _textState.TextRise;

        // The canvas has been transformed with Scale(scale, -scale) to flip
        // Y for paths. Un-flip for text. When the text matrix has non-uniform
        // X/Y scaling, squeeze glyphs horizontally to match the X-scale.
        //
        // For Y direction, follow the *sign* of Tm.d:
        //   d > 0 (typical PDF, Y-up text-space) → -1 cancels outer Y-flip
        //   d < 0 (browser-style Tm `1 0 0 -1`, e.g. WeasyPrint, Word, Word-derived
        //         and most CJK-producing toolchains) → +1 keeps Skia's natural
        //         Y-down glyph drawing, which the outer flip turns into Y-up
        //         on screen exactly as the PDF intends.
        // Without this, browser-flipped text (and most CJK) renders upside-down.
        //
        // Per PDF 32000-2 §9.4.4 the text rendering matrix multiplies the
        // X axis by Th (= Tz / 100). Tz=100 is the default and the no-op,
        // but conformance fixtures use Tz=300, Tz=30000 etc. to test wide
        // / condensed text. Without folding Th into this Scale the glyph
        // itself is drawn at native width while the cursor advances by
        // Th×width, leaving microscopic letters with huge gaps between
        // them (visible as excise rendering only ~5% of the expected pixel
        // count on TWG A001 / 6-1-12-t02 fixtures).
        float ySign = _textState.TextMatrixD >= 0 ? -1f : 1f;
        float th = _textState.HorizontalScale / 100.0f;
        if (!IsOptionalContentSuppressed)
        {
            _canvas.Save();
            var textMatrix = CreateTextRenderingMatrix(x, y, th, ySign);
            _canvas.Concat(in textMatrix);

            bool drawWithPdfWidths =
                !currentFont.HasEmbeddedProgram &&
                currentFont.Widths != null &&
                sourceBytes != null &&
                text.Length == sourceBytes.Length;

            if (drawWithPdfWidths)
            {
                // Walk the bytes in lock-step with the decoded characters,
                // drawing each glyph at the cumulative PDF-/Widths position
                // *plus* the character/word spacing the PDF asked for.
                // Visible layout matches what the PDF author authored
                // against Times/Helvetica, regardless of the system font we
                // substituted for the actual glyphs.
                //
                // Per-glyph cursor advance after drawing byte b:
                //     /Widths[b]/1000 * fontSize    (intended glyph width)
                //   + Tc                             (character spacing)
                //   + (b == 0x20 ? Tw : 0)           (word spacing on space)
                //
                // Multiplied by the horizontal-scaling factor Tz (Th) per
                // PDF spec 9.4.4.
                //
                // We're inside a canvas that's already been scaled by xyRatio
                // for the X axis, so cursor is in the pre-xyRatio frame.
                // Tc / Tw are unscaled; we don't apply Tm's xScale here
                // because the canvas transform handles it.
                // Per-glyph advance per PDF spec 9.4.4:
                //   tx = (w0/1000 + Tc + (b == 0x20 ? Tw : 0)) * Tm_scale * Th
                // With Tf=1 and Tm scale = effectiveSize, Tm_scale = effectiveSize.
                // Multiplying everything together puts cursor in the canvas frame
                // we just set up with Scale(xyRatio, -1).
                // The outer Scale already folded Th into the canvas X axis, so
                // cursor advances in the *pre-Th* frame: (w/1000 + spacing) * Tfs.
                // Multiplying by Th again here would double-apply the horizontal
                // scale and over-shoot per-glyph spacing under any non-default Tz.
                float cursor = 0f;
                float tc = _textState.CharSpacing;
                float tw = _textState.WordSpacing;
                SKPath? localClipPath = clipText ? new SKPath() : null;
                for (int i = 0; i < sourceBytes!.Length; i++)
                {
                    var glyphText = text[i].ToString();
                    int idx = sourceBytes[i] - currentFont.FirstChar;
                    float w = idx >= 0 && idx < currentFont.Widths!.Length
                        ? currentFont.Widths[idx]
                        : currentFont.MissingWidth;
                    var pdfGlyphWidth = Math.Max(0f, (w / 1000f) * effectiveSize);
                    var naturalGlyphWidth = font.MeasureText(glyphText, measurePaint);
                    var fallbackGlyphScale = pdfGlyphWidth > 0f && naturalGlyphWidth > 0f
                        ? Math.Min(1f, pdfGlyphWidth / naturalGlyphWidth)
                        : 1f;

                    if (fillText)
                    {
                        if (fillWithPattern)
                        {
                            using var glyphPath = font.GetTextPath(glyphText, SKPoint.Empty);
                            AddScaledGlyphPath(localFillPatternPath, glyphPath, cursor, fallbackGlyphScale);
                        }
                        else
                        {
                            // #710: fill from the outline path, not the
                            // platform glyph mask (see FillTextUsingGlyphPath).
                            using var fillGlyphPath = BuildScaledGlyphPath(font, glyphText, cursor, fallbackGlyphScale);
                            FillTextUsingGlyphPath(
                                fillGlyphPath, fillPaint,
                                () => DrawFallbackGlyph(glyphText, cursor, fallbackGlyphScale, font, fillPaint));
                        }
                    }
                    if (strokeText)
                        RenderWithCurrentSoftMask(
                            () => DrawFallbackGlyph(glyphText, cursor, fallbackGlyphScale, font, strokePaint),
                            strokePaint);
                    if (localClipPath != null)
                    {
                        using var glyphPath = font.GetTextPath(glyphText, SKPoint.Empty);
                        if (glyphPath != null && !glyphPath.IsEmpty)
                        {
                            using var transformedGlyphPath = new SKPath();
                            var glyphMatrix = new SKMatrix(
                                fallbackGlyphScale, 0, cursor,
                                0, 1, 0,
                                0, 0, 1);
                            glyphPath.Transform(glyphMatrix, transformedGlyphPath);
                            localClipPath.AddPath(transformedGlyphPath, SKPathAddMode.Append);
                        }
                    }

                    float spacing = tc + (sourceBytes[i] == 0x20 ? tw : 0f);
                    cursor += (w / 1000f + spacing) * effectiveSize;
                }
                AddPendingTextClipPath(localClipPath, x, y, th, ySign);
                localClipPath?.Dispose();
            }
            else if (currentFont.ByteToGlyph != null && sourceBytes != null)
            {
                // The active typeface's cmap is byte-coded (Mac Roman / format-0)
                // and Skia's shaper can't read it. Look each PDF byte code up in
                // the parsed cmap and dispatch via SKTextBlob with explicit
                // glyph IDs (SkiaSharp 3 dropped the DrawText(byte[], …)
                // overload — SKTextBlob is the supported entry point).
                // Without this branch every glyph would render as .notdef and
                // the page would be blank.
                var gids = BuildGlyphIds(sourceBytes, currentFont.ByteToGlyph);

                // Character/word spacing (Tc/Tw) is applied when *advancing
                // the cursor* between Tj calls (see SumPdfWidths below), but
                // a naive default-positioned SKTextBlob run lays out glyphs
                // using only the wrapped font's own hmtx — it has no idea Tc
                // or Tw exist. When either is non-zero the glyph run drawn
                // here silently drifts from the PDF-intended (and
                // cursor-tracked) positions: each glyph after a space ends up
                // further right than the PDF asked for, and by the end of a
                // long/justified line the drift is large enough for the
                // final glyph to visually collide with whatever is drawn
                // next (e.g. issue #652 — Tw=-0.588 over 10 spaces shifted
                // "movement"'s trailing "t" ~6pt right of where the
                // following em-dash correctly starts, visually merging the
                // two). Fix: when Tc/Tw actually apply, position each glyph
                // explicitly using cumulative /Widths advances plus Tc/Tw,
                // matching the PDF-spec formula used to track the cursor
                // (SumPdfWidths below): tx = (w0/1000 * Tfs) + Tc + Tw — Tc
                // and Tw are ALREADY in unscaled text-space units and must
                // NOT be multiplied by font size again (unlike w0, which is
                // in thousandths of an em and does need the Tfs scale).
                // "cursor" here lives in the pre-xyRatio canvas frame (the
                // canvas's own Scale(xyRatio, …) converts it to device
                // space), so Tc/Tw are scaled by the text matrix's own
                // Y-axis scale (yScale) rather than by effectiveSize, to
                // land in that same frame — consistent with how effectiveSize
                // itself is fontSize*yScale.
                float tcSpacing = _textState.CharSpacing;
                float twSpacing = _textState.WordSpacing;
                bool needsExplicitSpacing =
                    (tcSpacing != 0f || twSpacing != 0f) &&
                    currentFont.Widths != null;

                SKPoint[]? positions = null;
                if (needsExplicitSpacing)
                {
                    var tmC = _textState.TextMatrixC;
                    var tmD = _textState.TextMatrixD;
                    var yScale = (float)Math.Sqrt(tmC * tmC + tmD * tmD);
                    if (yScale < 1e-6f) yScale = 1f;

                    positions = new SKPoint[gids.Length];
                    float cursor = 0f;
                    for (int i = 0; i < sourceBytes.Length; i++)
                    {
                        positions[i] = new SKPoint(cursor, 0);
                        int idx = sourceBytes[i] - currentFont.FirstChar;
                        float w = idx >= 0 && idx < currentFont.Widths!.Length
                            ? currentFont.Widths[idx]
                            : currentFont.MissingWidth;
                        float spacing = (tcSpacing + (sourceBytes[i] == 0x20 ? twSpacing : 0f)) * yScale;
                        cursor += (w / 1000f) * effectiveSize + spacing;
                    }
                }

                using var blob = positions != null
                    ? BuildPositionedGlyphBlob(gids, positions, font)
                    : BuildGlyphBlob(gids, font);
                if (blob != null)
                {
                    if (fillText && fillWithPattern)
                    {
                        using var localPath = positions != null
                            ? BuildGlyphIdTextPath(gids, positions, font)
                            : BuildGlyphIdTextPath(gids, font, measurePaint);
                        if (localPath != null && !localPath.IsEmpty)
                            localFillPatternPath!.AddPath(localPath, SKPathAddMode.Append);
                    }
                    else if (fillText)
                    {
                        // #710: fill from the outline path, not the platform
                        // glyph mask (see FillTextUsingGlyphPath).
                        using var fillGlyphPath = positions != null
                            ? BuildGlyphIdTextPath(gids, positions, font)
                            : BuildGlyphIdTextPath(gids, font, measurePaint);
                        var blobToFill = blob;
                        FillTextUsingGlyphPath(
                            fillGlyphPath, fillPaint,
                            () => _canvas.DrawText(blobToFill, 0, 0, fillPaint));
                    }
                    if (strokeText)
                        RenderWithCurrentSoftMask(
                            () => _canvas.DrawText(blob, 0, 0, strokePaint),
                            strokePaint);
                }

                if (clipText)
                {
                    using var localClipPath = positions != null
                        ? BuildGlyphIdTextPath(gids, positions, font)
                        : BuildGlyphIdTextPath(gids, font, measurePaint);
                    AddPendingTextClipPath(localClipPath, x, y, th, ySign);
                }
            }
            else
            {
                if (fillText && fillWithPattern)
                {
                    using var localPath = font.GetTextPath(text, SKPoint.Empty);
                    if (localPath != null && !localPath.IsEmpty)
                        localFillPatternPath!.AddPath(localPath, SKPathAddMode.Append);
                }
                else if (fillText)
                {
                    // #710: fill from the outline path, not the platform
                    // glyph mask (see FillTextUsingGlyphPath).
                    using var fillGlyphPath = font.GetTextPath(text, SKPoint.Empty);
                    FillTextUsingGlyphPath(
                        fillGlyphPath, fillPaint,
                        () => _canvas.DrawText(text, 0, 0, font, fillPaint));
                }
                if (strokeText)
                    RenderWithCurrentSoftMask(
                        () => _canvas.DrawText(text, 0, 0, font, strokePaint),
                        strokePaint);
                if (clipText)
                {
                    using var localClipPath = font.GetTextPath(text, SKPoint.Empty);
                    AddPendingTextClipPath(localClipPath, x, y, th, ySign);
                }
            }

            _canvas.Restore();
            RenderTextPatternFill(localFillPatternPath, x, y, th, ySign);
        }

        localFillPatternPath?.Dispose();

        // Advance the cursor by what the PDF *intended*, which is not
        // always what Skia just drew.
        //   - PDF supplies /Widths → trust the PDF's explicit widths,
        //     embedded or not (#584). PDF /Widths is authoritative per ISO
        //     32000 9.2.4 regardless of what's baked into the font program;
        //     for a substituted system typeface this also avoids per-glyph
        //     drift into visible mid-word gaps (the birth-cert form is the
        //     canary — that fixture has no embedded program, so this branch
        //     already covered it before #584 and still does).
        //     For an *embedded* CFF program specifically, this branch used
        //     to be skipped entirely (only the substituted-typeface case
        //     trusted /Widths) on the assumption that Skia's own MeasureText
        //     against the real embedded outlines is always right. It isn't:
        //     the CFF→OpenType wrapper's hmtx is built from /Widths keyed by
        //     CFF glyph INDEX (CffToOpenType.BuildHmtx), but a subsetted font
        //     can have the same glyph index reachable from more than one PDF
        //     character code with different declared widths, and a glyph
        //     whose code falls outside every alias's /Widths coverage gets a
        //     hardcoded stub (500) instead of its real width — hmtx and the
        //     PDF's own /Widths can disagree. Going straight to /Widths
        //     (SumPdfWidths, indexed by PDF code, not glyph index) removes
        //     that disagreement instead of trusting whichever one hmtx
        //     happened to end up with. Confirmed via #584: this makes the
        //     computed advance for a specific real-world em-dash glyph
        //     exactly the intended full em (was previously reachable only
        //     through the buggy hmtx path) — though the glyph OUTLINE for
        //     that same font still renders at the wrong scale/baseline, a
        //     separate, unresolved defect in the wrapper's synthesized
        //     metrics tables (see #584's follow-up notes).
        //   - Otherwise (no /Widths at all) fall back to Skia's MeasureText.
        float widthInFontUnits;
        bool advanceFromPdfWidths =
            currentFont.Widths != null &&
            sourceBytes != null;

        if (advanceFromPdfWidths)
        {
            widthInFontUnits = SumPdfWidths(sourceBytes!) * effectiveSize;
        }
        else if (currentFont.ByteToGlyph != null && sourceBytes != null)
        {
            // Same byte-coded glyph-ID path as the draw branch above —
            // SkiaSharp 3 moved MeasureText off SKPaint, the glyph-id
            // overload now lives on SKFont.
            var gids = BuildGlyphIds(sourceBytes, currentFont.ByteToGlyph);
            widthInFontUnits = font.MeasureText(new ReadOnlySpan<ushort>(gids), measurePaint);
        }
        else
        {
            widthInFontUnits = font.MeasureText(text, measurePaint);
        }

        var width = widthInFontUnits * xyRatio;
        var charCount = sourceBytes?.Length ?? text.Length;
        var spaceCount = sourceBytes != null
            ? sourceBytes.Count(b => b == 0x20)
            : text.Count(c => c == ' ');

        // PDF spec 9.4.4: Tc and Tw are in UNSCALED text space units. Scale by
        // the text matrix's X-scale before adding to device-space advance,
        // otherwise Tw-heavy layouts overlap themselves (birth-cert form).
        var tmA = _textState.TextMatrixA;
        var tmB = _textState.TextMatrixB;
        var xScale = (float)Math.Sqrt(tmA * tmA + tmB * tmB);
        if (xScale < 1e-6f) xScale = 1f;
        width += charCount * _textState.CharSpacing * xScale;
        width += spaceCount * _textState.WordSpacing * xScale;
        width *= _textState.HorizontalScale / 100.0f;

        AdvanceTextMatrixX(width);
    }

    private static bool TextRenderModeFills(int mode) => mode is 0 or 2 or 4 or 6;

    private static bool TextRenderModeStrokes(int mode) => mode is 1 or 2 or 5 or 6;

    private static bool TextRenderModeAddsClip(int mode) => mode is 4 or 5 or 6 or 7;

    private static SKFont CreateTextFont(SKTypeface typeface, float size)
    {
        return new SKFont(typeface, size)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Normal,
            LinearMetrics = true,
            Subpixel = true
        };
    }

    private void DrawFallbackGlyph(string glyphText, float cursor, float horizontalScale, SKFont font, SKPaint paint)
    {
        if (Math.Abs(horizontalScale - 1f) < 0.001f)
        {
            _canvas.DrawText(glyphText, cursor, 0, font, paint);
            return;
        }

        _canvas.Save();
        try
        {
            _canvas.Translate(cursor, 0);
            _canvas.Scale(horizontalScale, 1);
            _canvas.DrawText(glyphText, 0, 0, font, paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>
    /// Fill a text run from its glyph <b>outline path</b> instead of
    /// <c>SKCanvas.DrawText</c>. DrawText rasterizes glyph masks through
    /// the platform scaler behind Skia's glyph cache (CoreText/CoreGraphics
    /// on macOS, hinted FreeType on Linux, DirectWrite on Windows); those
    /// coverage curves are platform-dependent and measurably different
    /// from the unhinted-FreeType coverage every PDF reference renderer
    /// (mutool / pdftocairo / Ghostscript) produces. Measured on an
    /// identical embedded Type 1C outline at 10–20pt/150dpi on macOS,
    /// CoreText masks carried ~17% more ink (~+0.45px of stem width, the
    /// #710/#584 "excise text renders heavier" root cause) with
    /// Hinting=Normal, and ~11% less with Hinting=None — neither matches.
    /// Filling the outline path uses Skia's own analytic scan converter:
    /// exact area coverage of the true outline, identical on every
    /// platform, and within ~0.1% ink of mutool on the same outline.
    ///
    /// <paramref name="fallbackDraw"/> runs when the run yields no outline
    /// geometry: bitmap-only faces (e.g. color emoji) have no outlines but
    /// do render via DrawText, and an all-whitespace run is a harmless
    /// no-op either way. See issue #710.
    ///
    /// <b>Scope (#710 regression fix):</b> the outline-path fill applies to
    /// the font programs where it measurably closes the gap to the
    /// references — embedded CFF/Type1C, TrueType, OpenType, and
    /// system-substituted faces. Embedded <b>raw Type 1 (/FontFile)</b>
    /// faces keep the DrawText glyph-mask path: for those faces the
    /// platform masks already match mutool almost exactly
    /// (highlights.pdf p5 @72dpi: DrawText diffFraction 0.00067 vs
    /// DrawPath 0.0152 against mutool, with pdftocairo agreeing), while
    /// the analytic outline fill of the very same outlines lands
    /// symmetrically darker AND lighter on nearly every small-size glyph
    /// edge — a per-edge coverage disagreement, not a weight or geometry
    /// error (glyph paths are scale-invariant and identical at 9.5pt and
    /// 100pt). The #710 CFF stem-darkening mismatch that motivated the
    /// outline fill does not occur on the platform's raw-Type1 raster
    /// path, so switching those faces to DrawPath only ADDED error.
    /// Verified both ways: PdfJsFontFallbackDifferentialTests (raw Type 1
    /// vs mutool) and TextRasterInkParityTests (embedded CFF ink parity).
    /// </summary>
    private void FillTextUsingGlyphPath(SKPath? glyphRunPath, SKPaint fillPaint, Action fallbackDraw)
    {
        if (_currentFont?.HasRawType1Program == true)
        {
            RenderWithCurrentSoftMask(fallbackDraw, fillPaint);
            return;
        }

        if (glyphRunPath != null && !glyphRunPath.IsEmpty)
        {
            RenderWithCurrentSoftMask(() => _canvas.DrawPath(glyphRunPath, fillPaint), fillPaint);
            return;
        }

        RenderWithCurrentSoftMask(fallbackDraw, fillPaint);
    }

    /// <summary>
    /// Outline-path equivalent of <see cref="DrawFallbackGlyph"/> for fill
    /// mode (see <see cref="FillTextUsingGlyphPath"/> for why fills avoid
    /// DrawText). Returns the glyph positioned at <paramref name="cursor"/>
    /// with the fallback horizontal squeeze applied, or null when the glyph
    /// has no outline (bitmap-only face) and the caller must fall back to
    /// <see cref="DrawFallbackGlyph"/>.
    /// </summary>
    private static SKPath? BuildScaledGlyphPath(SKFont font, string glyphText, float cursor, float horizontalScale)
    {
        using var glyphPath = font.GetTextPath(glyphText, SKPoint.Empty);
        if (glyphPath == null || glyphPath.IsEmpty)
            return null;

        var positioned = new SKPath();
        var glyphMatrix = new SKMatrix(
            horizontalScale, 0, cursor,
            0, 1, 0,
            0, 0, 1);
        glyphPath.Transform(glyphMatrix, positioned);
        return positioned;
    }

    private SKPaint CreateTextPaint(SKPaintStyle style, SKColor color, float alpha)
    {
        var paint = new SKPaint
        {
            Style = style,
            Color = color.WithAlpha((byte)Math.Clamp(alpha * 255, 0, 255)),
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };

        if (style == SKPaintStyle.Stroke)
        {
            paint.StrokeWidth = (float)_state.LineWidth;
            paint.StrokeCap = _state.LineCap switch
            {
                1 => SKStrokeCap.Round,
                2 => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt
            };
            paint.StrokeJoin = _state.LineJoin switch
            {
                1 => SKStrokeJoin.Round,
                2 => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Miter
            };
            paint.StrokeMiter = _state.MiterLimit;
        }

        return paint;
    }

    private void RenderTextPatternFill(SKPath? localTextPath, float x, float y, float horizontalScale, float ySign)
    {
        if (localTextPath == null || localTextPath.IsEmpty || _state.FillPatternName == null)
            return;

        using var textPath = new SKPath();
        var textMatrix = CreateTextRenderingMatrix(x, y, horizontalScale, ySign);
        localTextPath.Transform(textMatrix, textPath);
        RenderFillPattern(textPath);
    }

    private static void AddScaledGlyphPath(SKPath? destination, SKPath? glyphPath, float cursor, float horizontalScale)
    {
        if (destination == null || glyphPath == null || glyphPath.IsEmpty)
            return;

        using var transformedGlyphPath = new SKPath();
        var glyphMatrix = new SKMatrix(
            horizontalScale, 0, cursor,
            0, 1, 0,
            0, 0, 1);
        glyphPath.Transform(glyphMatrix, transformedGlyphPath);
        destination.AddPath(transformedGlyphPath, SKPathAddMode.Append);
    }

    private void AddPendingTextClipPath(SKPath? localPath, float x, float y, float horizontalScale, float scaleY)
    {
        if (localPath == null || localPath.IsEmpty)
            return;

        var matrix = CreateTextRenderingMatrix(x, y, horizontalScale, scaleY);
        using var transformed = new SKPath();
        localPath.Transform(matrix, transformed);
        if (transformed.IsEmpty)
            return;

        _pendingTextClipPath ??= new SKPath();
        _pendingTextClipPath.AddPath(transformed, SKPathAddMode.Append);
    }

    private void ApplyPendingTextClipPath()
    {
        if (_pendingTextClipPath == null)
            return;

        using var clipPath = _pendingTextClipPath;
        _pendingTextClipPath = null;
        if (clipPath.IsEmpty)
            return;

        clipPath.FillType = SKPathFillType.Winding;
        _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);
    }

    private void ClearPendingTextClipPath()
    {
        _pendingTextClipPath?.Dispose();
        _pendingTextClipPath = null;
    }

    private static SKPath BuildGlyphIdTextPath(ushort[] gids, SKFont font, SKPaint measurePaint)
    {
        var path = new SKPath();
        if (gids.Length == 0)
            return path;

        var widths = font.GetGlyphWidths(new ReadOnlySpan<ushort>(gids), measurePaint);
        float cursor = 0f;
        for (int i = 0; i < gids.Length; i++)
        {
            using var glyphPath = font.GetGlyphPath(gids[i]);
            if (glyphPath != null && !glyphPath.IsEmpty)
                path.AddPath(glyphPath, cursor, 0, SKPathAddMode.Append);
            if (i < widths.Length)
                cursor += widths[i];
        }

        return path;
    }

    private static SKPath BuildGlyphIdTextPath(ushort[] gids, SKPoint[] positions, SKFont font)
    {
        var path = new SKPath();
        var count = Math.Min(gids.Length, positions.Length);
        for (int i = 0; i < count; i++)
        {
            using var glyphPath = font.GetGlyphPath(gids[i]);
            if (glyphPath != null && !glyphPath.IsEmpty)
                path.AddPath(glyphPath, positions[i].X, positions[i].Y, SKPathAddMode.Append);
        }

        return path;
    }

    private static SKPath BuildGlyphIdTextPath(ushort[] gids, SKPoint[] positions, float[] horizontalScales, SKFont font)
    {
        var path = new SKPath();
        var count = Math.Min(Math.Min(gids.Length, positions.Length), horizontalScales.Length);
        for (int i = 0; i < count; i++)
        {
            using var glyphPath = font.GetGlyphPath(gids[i]);
            if (glyphPath == null || glyphPath.IsEmpty)
                continue;

            var scale = horizontalScales[i];
            if (Math.Abs(scale - 1f) < 0.001f)
            {
                path.AddPath(glyphPath, positions[i].X, positions[i].Y, SKPathAddMode.Append);
                continue;
            }

            using var transformedGlyphPath = new SKPath();
            var glyphMatrix = new SKMatrix(
                scale, 0, positions[i].X,
                0, 1, positions[i].Y,
                0, 0, 1);
            glyphPath.Transform(glyphMatrix, transformedGlyphPath);
            path.AddPath(transformedGlyphPath, SKPathAddMode.Append);
        }

        return path;
    }

    private void DrawPositionedGlyphIds(ushort[] gids, SKPoint[] positions, float[] horizontalScales, SKFont font, SKPaint paint)
    {
        var count = Math.Min(Math.Min(gids.Length, positions.Length), horizontalScales.Length);
        for (int i = 0; i < count; i++)
        {
            using var blob = BuildGlyphBlob(new[] { gids[i] }, font);
            if (blob == null)
                continue;

            var scale = horizontalScales[i];
            if (Math.Abs(scale - 1f) < 0.001f)
            {
                _canvas.DrawText(blob, positions[i].X, positions[i].Y, paint);
                continue;
            }

            _canvas.Save();
            try
            {
                _canvas.Translate(positions[i].X, positions[i].Y);
                _canvas.Scale(scale, 1);
                _canvas.DrawText(blob, 0, 0, paint);
            }
            finally
            {
                _canvas.Restore();
            }
        }
    }

    /// <summary>
    /// Total advance for <paramref name="bytes"/> in the current simple
    /// font, expressed as a fraction of the font's em (multiply by font
    /// size to get points). Indexes <c>_currentFont.Widths</c> by
    /// (byte − FirstChar); falls back to /MissingWidth or 0 for codes
    /// outside the table.
    /// </summary>
    private float SumPdfWidths(byte[] bytes)
    {
        var widths = _currentFont?.Widths;
        if (widths == null || widths.Length == 0) return 0f;
        float total = 0f;
        int firstChar = _currentFont!.FirstChar;
        float missingWidth = _currentFont.MissingWidth;
        for (int i = 0; i < bytes.Length; i++)
        {
            int idx = bytes[i] - firstChar;
            float w = idx >= 0 && idx < widths.Length
                ? widths[idx]
                : missingWidth;
            // PDF /Widths are in 1/1000 of em.
            total += w / 1000f;
        }
        return total;
    }

    // Map PDF byte codes through the format-0 cmap into glyph IDs.
    // Used for simple fonts whose embedded typeface has only a
    // format-0 cmap; the byte→glyph map was parsed once at
    // typeface-load time. SkiaSharp 3 routes glyph IDs through
    // SKTextBlob (BuildGlyphBlob below) — the v2 byte-array
    // SKTextEncoding.GlyphId path was removed.
    private static ushort[] BuildGlyphIds(byte[] sourceBytes, ushort[] byteToGlyph)
    {
        var gids = new ushort[sourceBytes.Length];
        for (int i = 0; i < sourceBytes.Length; i++)
            gids[i] = byteToGlyph[sourceBytes[i]];
        return gids;
    }

    /// <summary>
    /// Wrap a glyph-id array in an <see cref="SKTextBlob"/> for
    /// dispatch through <see cref="SKCanvas.DrawText(SKTextBlob,float,float,SKPaint)"/>.
    /// SkiaSharp 3 dropped <c>SKCanvas.DrawText(byte[], …)</c>, and
    /// <see cref="SKTextBlob.Create"/> has no <c>ReadOnlySpan&lt;ushort&gt;</c>
    /// overload — only <c>SKTextBlobBuilder.AddRun</c> takes glyph IDs.
    /// Origin (0, 0) since the caller has already concatenated the
    /// right Translate/Scale onto the canvas; per-glyph advance comes
    /// from the font's hmtx via the run's default positioning.
    /// </summary>
    private static SKTextBlob? BuildGlyphBlob(ushort[] gids, SKFont font)
    {
        if (gids.Length == 0) return null;
        using var builder = new SKTextBlobBuilder();
        builder.AddRun(new ReadOnlySpan<ushort>(gids), font, SKPoint.Empty);
        return builder.Build();
    }

    private static SKTextBlob? BuildPositionedGlyphBlob(ushort[] gids, SKPoint[] positions, SKFont font)
    {
        if (gids.Length == 0) return null;
        var count = Math.Min(gids.Length, positions.Length);
        if (count == 0) return null;
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(
            new ReadOnlySpan<ushort>(gids, 0, count),
            font,
            new ReadOnlySpan<SKPoint>(positions, 0, count));
        return builder.Build();
    }

    private void RenderType3Bytes(byte[] bytes)
    {
        if (!_inTextBlock || _currentFont?.Dictionary == null || bytes.Length == 0)
            return;
        var fontDict = _currentFont.Dictionary;

        var charProcs = ResolveDict(fontDict, "CharProcs");
        var fontResources = ResolveDict(fontDict, "Resources");
        var fontMatrix = GetType3FontMatrix(fontDict);
        var canPaint = !IsOptionalContentSuppressed && _textState.RenderMode is not (3 or 7);
        var cursorTextUnits = 0f;
        var th = _textState.HorizontalScale / 100.0f;

        foreach (var code in bytes)
        {
            if (canPaint &&
                charProcs != null &&
                TryResolveType3CharProc(charProcs, code, out var charProc))
            {
                RenderType3Glyph(charProc, fontResources, fontMatrix, cursorTextUnits, th);
            }

            cursorTextUnits += GetType3TextSpaceAdvance(code, fontMatrix);
            cursorTextUnits += _textState.CharSpacing;
            if (code == 0x20)
                cursorTextUnits += _textState.WordSpacing;
        }

        var effectiveSize = GetEffectiveFontSize();
        var width = cursorTextUnits * effectiveSize * GetTextMatrixXYRatio() * th;
        AdvanceTextMatrixX(width);
    }

    private bool TryResolveType3CharProc(
        Excise.Core.Primitives.PdfDictionary charProcs,
        byte code,
        out Excise.Core.Primitives.PdfStream charProc)
    {
        charProc = null!;
        var glyphName = GetGlyphNameForCode(code);
        if (string.IsNullOrEmpty(glyphName))
            return false;

        var charProcObj = charProcs.GetOptional(glyphName);
        if (charProcObj == null)
            return false;

        if (_page.Document.Resolve(charProcObj) is not Excise.Core.Primitives.PdfStream stream)
            return false;

        charProc = stream;
        return true;
    }

    private string? GetGlyphNameForCode(byte code)
    {
        var glyphName = _currentFont?.CodeToGlyphName?[code];
        if (!string.IsNullOrEmpty(glyphName))
            return glyphName;

        var unicode = GetUnicodeForCode(code, _currentFont?.CodeToUnicode, _currentFont?.EncodingName ?? "WinAnsiEncoding");
        return unicode != '\0' && AdobeGlyphList.TryGetName(unicode, out var name)
            ? name
            : null;
    }

    private SKMatrix GetType3FontMatrix(Excise.Core.Primitives.PdfDictionary fontDict)
    {
        var matrixArray = ResolveArray(fontDict, "FontMatrix");
        return matrixArray != null && matrixArray.Count >= 6
            ? GetMatrix(matrixArray)
            : new SKMatrix(0.001f, 0, 0, 0, 0.001f, 0, 0, 0, 1);
    }

    private float GetType3TextSpaceAdvance(byte code, SKMatrix fontMatrix)
    {
        var rawWidth = GetSimpleFontWidth(code);
        var fontMatrixX = Math.Abs(fontMatrix.ScaleX) > 1e-9f ? fontMatrix.ScaleX : 0.001f;
        return rawWidth * fontMatrixX;
    }

    private float GetSimpleFontWidth(byte code)
    {
        var widths = _currentFont?.Widths;
        var missingWidth = _currentFont?.MissingWidth ?? 0f;
        if (widths == null || widths.Length == 0)
            return missingWidth;

        var index = code - _currentFont!.FirstChar;
        return index >= 0 && index < widths.Length
            ? widths[index]
            : missingWidth;
    }

    private void RenderType3Glyph(
        Excise.Core.Primitives.PdfStream charProc,
        Excise.Core.Primitives.PdfDictionary? fontResources,
        SKMatrix fontMatrix,
        float cursorTextUnits,
        float horizontalScale)
    {
        if (!_type3GlyphStack.Add(charProc))
            return;

        var savedCanvasCount = _canvas.SaveCount;
        var savedStateStack = SnapshotGraphicsStateStack();
        var savedState = _state.Clone();
        var savedTextState = _textState.Clone();
        var savedFont = _currentFont;
        var savedInTextBlock = _inTextBlock;
        var savedCurrentPath = _currentPath;
        var savedPendingClipEvenOdd = _pendingClipEvenOdd;
        var savedPendingTextClipPath = _pendingTextClipPath;
        var savedColorLocked = _type3GlyphColorLocked;

        _currentPath = null;
        _pendingClipEvenOdd = null;
        _pendingTextClipPath = null;
        _inTextBlock = false;
        // Each glyph starts colored; its own d1 operator (if present) re-locks.
        _type3GlyphColorLocked = false;
        _canvas.Save();
        _resourcesStack.Push(fontResources);

        try
        {
            var textMatrix = new SKMatrix(
                _textState.TextMatrixA,
                _textState.TextMatrixC,
                _textState.TextMatrixE,
                _textState.TextMatrixB,
                _textState.TextMatrixD,
                _textState.TextMatrixF + _textState.TextRise,
                0,
                0,
                1);
            _canvas.Concat(in textMatrix);
            _canvas.Scale(_textState.FontSize * horizontalScale, _textState.FontSize);
            _canvas.Translate(cursorTextUnits, 0);
            _canvas.Concat(in fontMatrix);

            ExecuteContentBytes(charProc.DecodedData);
        }
        finally
        {
            _currentPath?.Dispose();
            _pendingTextClipPath?.Dispose();
            RestoreGraphicsStateStack(savedStateStack);
            _state = savedState;
            _textState = savedTextState;
            _currentFont = savedFont;
            _inTextBlock = savedInTextBlock;
            _currentPath = savedCurrentPath;
            _pendingClipEvenOdd = savedPendingClipEvenOdd;
            _pendingTextClipPath = savedPendingTextClipPath;
            _type3GlyphColorLocked = savedColorLocked;
            _resourcesStack.Pop();
            _canvas.RestoreToCount(savedCanvasCount);
            _type3GlyphStack.Remove(charProc);
        }
    }

    // Type0 rendering path. Content-stream bytes are character codes; the
    // active Encoding CMap maps them to CIDs. Identity-H/Identity-V are the
    // common 2-byte no-op maps, while embedded CMap streams can remap retained
    // Unicode-ish codes onto the descendant font's CID/glyph space.
    private void RenderCidBytes(byte[] bytes)
    {
        if (!_inTextBlock || _currentFont?.Typeface == null || bytes.Length == 0)
            return;
        var currentFont = _currentFont!;

        var cids = currentFont.CidEncodingCMap?.Decode(bytes) ?? DecodeIdentityCidBytes(bytes);
        if (cids.Length == 0)
            return;

        var count = cids.Length;
        var effectiveSize = GetEffectiveFontSize();
        using var font = CreateTextFont(currentFont.Typeface!, effectiveSize);
        // Two parallel arrays: CIDs (used for /W width lookup, which is
        // keyed by CID per spec) and GIDs (what Skia actually draws). The
        // CID → GID resolution depends on the descendant font subtype:
        //   - CIDFontType2 with /CIDToGIDMap stream → CidToGidMap
        //     is the array indexed by CID (handles Word / NotoSans / Office
        //     subsets).
        //   - CIDFontType0 (CFF-keyed, Adobe-Japan1 etc.) → the mapping is
        //     inside the embedded CFF charset; CffCidToGlyph holds it.
        //   - CIDFontType2 with /CIDToGIDMap = /Identity (or absent) → CID
        //     equals GID and we draw straight through.
        var gids = new ushort[count];
        var positions = new SKPoint[count];
        float[]? fallbackGlyphScales = !currentFont.HasEmbeddedProgram
            ? new float[count]
            : null;
        var cursor = 0f;
        for (int i = 0; i < count; i++)
        {
            var cid = cids[i];
            ushort gid;
            if (currentFont.CidToGidMap != null && cid >= 0 && cid < currentFont.CidToGidMap.Length)
                gid = currentFont.CidToGidMap[cid];
            else if (currentFont.CffCidToGlyph != null
                     && currentFont.CffCidToGlyph.TryGetValue(cid, out var cffGid))
                gid = (ushort)cffGid;
            else if (currentFont.CidUseUnicodeCmap)
                gid = (ushort)(font.GetGlyph(cid) is var unicodeGid && unicodeGid != 0 ? unicodeGid : cid);
            else
                gid = ToGlyphId(cid);
            gids[i] = gid;

            positions[i] = new SKPoint(cursor, 0);
            var pdfGlyphWidth = GetCidWidthThousandths(cid) * effectiveSize / 1000f;
            if (fallbackGlyphScales != null)
            {
                using var glyphPath = font.GetGlyphPath(gid);
                var boundsWidth = glyphPath != null && !glyphPath.IsEmpty
                    ? glyphPath.Bounds.Width
                    : 0f;
                fallbackGlyphScales[i] = pdfGlyphWidth > 0f && boundsWidth > 0f
                    ? Math.Min(1f, pdfGlyphWidth / boundsWidth)
                    : 1f;
            }

            cursor += pdfGlyphWidth;
        }

        var xyRatio = GetTextMatrixXYRatio();
        var mode = _textState.RenderMode;
        var fillText = TextRenderModeFills(mode);
        var strokeText = TextRenderModeStrokes(mode);
        var clipText = TextRenderModeAddsClip(mode);
        var fillWithPattern = fillText && _state.FillPatternName != null;
        SKPath? localFillPatternPath = fillWithPattern ? new SKPath() : null;

        // SkiaSharp 3: SKPaint no longer carries the font or text encoding;
        // SKTextBlob (built below) embeds glyph IDs natively, so DrawText
        // doesn't need a paint-side encoding hint anymore.
        using var fillPaint = CreateTextPaint(SKPaintStyle.Fill, _state.FillColor, _state.FillAlpha);
        using var strokePaint = CreateTextPaint(SKPaintStyle.Stroke, _state.StrokeColor, _state.StrokeAlpha);
        using var measurePaint = new SKPaint { IsAntialias = _options.AntiAlias };
        using var strokeDash = CreateDashEffect();
        if (strokeDash != null)
            strokePaint.PathEffect = strokeDash;

        // Match RenderText's Tm.d-aware Y-flip — without this, all CJK text
        // and any other content authored with a browser-style flipped Tm
        // (`1 0 0 -1`) renders upside-down.
        float ySign = _textState.TextMatrixD >= 0 ? -1f : 1f;
        float x = _textState.TextMatrixE;
        float y = _textState.TextMatrixF + _textState.TextRise;
        if (!IsOptionalContentSuppressed)
        {
            _canvas.Save();
            var textMatrix = CreateTextRenderingMatrix(x, y, _textState.HorizontalScale / 100.0f, ySign);
            _canvas.Concat(in textMatrix);

            // Build a glyph-id text blob — SkiaSharp 3 routes glyph IDs
            // through SKTextBlob (the v2 byte[] overload was removed). The
            // GID array was already remapped through /CIDToGIDMap (or
            // CFF charset) above, so we feed it straight in.
            if (fallbackGlyphScales != null)
            {
                if (fillText && fillWithPattern)
                {
                    using var localPath = BuildGlyphIdTextPath(gids, positions, fallbackGlyphScales, font);
                    if (localPath != null && !localPath.IsEmpty)
                        localFillPatternPath!.AddPath(localPath, SKPathAddMode.Append);
                }
                else if (fillText)
                {
                    // #710: fill from the outline path, not the platform
                    // glyph mask (see FillTextUsingGlyphPath).
                    using var fillGlyphPath = BuildGlyphIdTextPath(gids, positions, fallbackGlyphScales, font);
                    FillTextUsingGlyphPath(
                        fillGlyphPath, fillPaint,
                        () => DrawPositionedGlyphIds(gids, positions, fallbackGlyphScales, font, fillPaint));
                }
                if (strokeText)
                    RenderWithCurrentSoftMask(
                        () => DrawPositionedGlyphIds(gids, positions, fallbackGlyphScales, font, strokePaint),
                        strokePaint);
            }
            else
            {
                using var blob = BuildPositionedGlyphBlob(gids, positions, font);
                if (blob != null)
                {
                    if (fillText && fillWithPattern)
                    {
                        using var localPath = BuildGlyphIdTextPath(gids, positions, font);
                        if (localPath != null && !localPath.IsEmpty)
                            localFillPatternPath!.AddPath(localPath, SKPathAddMode.Append);
                    }
                    else if (fillText)
                    {
                        // #710: fill from the outline path, not the platform
                        // glyph mask (see FillTextUsingGlyphPath).
                        using var fillGlyphPath = BuildGlyphIdTextPath(gids, positions, font);
                        var blobToFill = blob;
                        FillTextUsingGlyphPath(
                            fillGlyphPath, fillPaint,
                            () => _canvas.DrawText(blobToFill, 0, 0, fillPaint));
                    }
                    if (strokeText)
                        RenderWithCurrentSoftMask(
                            () => _canvas.DrawText(blob, 0, 0, strokePaint),
                            strokePaint);
                }
            }

            if (clipText)
            {
                using var localClipPath = fallbackGlyphScales != null
                    ? BuildGlyphIdTextPath(gids, positions, fallbackGlyphScales, font)
                    : BuildGlyphIdTextPath(gids, positions, font);
                AddPendingTextClipPath(localClipPath, x, y, _textState.HorizontalScale / 100.0f, ySign);
            }

            _canvas.Restore();
            RenderTextPatternFill(
                localFillPatternPath,
                x,
                y,
                _textState.HorizontalScale / 100.0f,
                ySign);
        }

        localFillPatternPath?.Dispose();

        // Advance by summed widths from /W (with /DW as fallback per CID).
        float sumThousandthsOfEm = 0f;
        foreach (var cid in cids)
            sumThousandthsOfEm += GetCidWidthThousandths(cid);
        var width = sumThousandthsOfEm * effectiveSize / 1000f * xyRatio;
        width *= _textState.HorizontalScale / 100.0f;
        AdvanceTextMatrixX(width);
    }

    private static int[] DecodeIdentityCidBytes(byte[] bytes)
    {
        var count = bytes.Length / 2;
        if (count == 0)
            return Array.Empty<int>();

        var cids = new int[count];
        for (var i = 0; i < count; i++)
            cids[i] = (bytes[i * 2] << 8) | bytes[i * 2 + 1];
        return cids;
    }

    private static ushort ToGlyphId(int cid)
        => cid is >= 0 and <= ushort.MaxValue ? (ushort)cid : (ushort)0;

    private float GetCidWidthThousandths(int cid)
        => (_currentFont?.CidWidths != null && _currentFont.CidWidths.TryGetValue(cid, out var width))
            ? width
            : _currentFont?.CidDefaultWidth ?? 1000f;

    // Returns the raw PDF string bytes WITHOUT decoding via encoding. Simple
    // fonts route these through DecodeTextBytes → Unicode → RenderText; Type0
    // fonts interpret the bytes directly as 2-byte CIDs via RenderCidBytes.
    private byte[] ParsePdfStringBytes(string operand)
    {
        if (string.IsNullOrEmpty(operand))
            return Array.Empty<byte>();

        // Literal string: (text)
        if (operand.StartsWith("(") && operand.EndsWith(")"))
            return UnescapePdfStringBytes(operand.Substring(1, operand.Length - 2));

        // Hex string: <hexdata>
        if (operand.StartsWith("<") && operand.EndsWith(">"))
            return DecodeHexStringBytes(operand.Substring(1, operand.Length - 2));

        return Encoding.Latin1.GetBytes(operand);
    }

    internal static byte[] UnescapePdfStringBytes(string s)
    {
        var unescaped = new List<byte>(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var next = s[i + 1];
                switch (next)
                {
                    case 'n': unescaped.Add((byte)'\n'); i += 2; break;
                    case 'r': unescaped.Add((byte)'\r'); i += 2; break;
                    case 't': unescaped.Add((byte)'\t'); i += 2; break;
                    case 'b': unescaped.Add((byte)'\b'); i += 2; break;
                    case 'f': unescaped.Add((byte)'\f'); i += 2; break;
                    case '(': unescaped.Add((byte)'('); i += 2; break;
                    case ')': unescaped.Add((byte)')'); i += 2; break;
                    case '\\': unescaped.Add((byte)'\\'); i += 2; break;
                    case '\r':
                    case '\n':
                        // PDF spec 7.3.4.2: backslash followed by an EOL
                        // marker — both shall be ignored. Treat CRLF as a
                        // single EOL.
                        i += 2;
                        if (next == '\r' && i < s.Length && s[i] == '\n')
                            i++;
                        break;
                    default:
                        if (char.IsDigit(next))
                        {
                            var octal = "";
                            i++;
                            while (i < s.Length && octal.Length < 3 && char.IsDigit(s[i]) && s[i] < '8')
                                octal += s[i++];
                            unescaped.Add((byte)Convert.ToInt32(octal, 8));
                        }
                        else
                        {
                            // Backslash before unknown char — spec 7.3.4.2:
                            // backslash is ignored; emit just `next`.
                            unescaped.Add((byte)next);
                            i += 2;
                        }
                        break;
                }
            }
            else
            {
                // The content stream was decoded as Latin1, so char = byte.
                unescaped.Add((byte)s[i++]);
            }
        }
        return unescaped.ToArray();
    }

    private static byte[] DecodeHexStringBytes(string hex)
    {
        hex = new string(hex.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (hex.Length % 2 != 0) hex += "0";

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, null, out var b))
                bytes[i] = b;
        }
        return bytes;
    }

    private string DecodeTextBytes(byte[] bytes)
    {
        // If the current font has an /Encoding dictionary, use the
        // /BaseEncoding + /Differences-derived map. Without this, embedded
        // subset fonts (which remap codes like 3 → "N", 4 → "A" via
        // /Differences) decode as control characters and render invisibly.
        var codeToUnicode = _currentFont?.CodeToUnicode;
        if (codeToUnicode != null)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                var c = codeToUnicode[b];
                if (c != '\0') sb.Append(c);
            }
            return sb.ToString();
        }

        // Named-encoding fast path. WinAnsiEncoding = cp1252 is the default
        // for most modern PDFs. No font set yet (no Tf seen) falls back to
        // the same WinAnsiEncoding default the old _currentFontEncoding
        // field's constructor init used.
        var encodingName = _currentFont?.EncodingName ?? "WinAnsiEncoding";
        if (encodingName == "ZapfDingbatsEncoding")
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                var c = ZapfDingbatsEncodingTable[b];
                if (c != '\0') sb.Append(c);
            }

            return sb.ToString();
        }

        if (encodingName == "MacRomanEncoding")
            return Encoding.GetEncoding(10000).GetString(bytes);
        return Encoding.GetEncoding(1252).GetString(bytes);
    }

    #endregion
}
