using System;
using System.Collections.Generic;

namespace Excise.Core.Fonts;

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

        // #1148 — per-glyph advance width in font units (glyph index → advance).
        // Interpreted from the Type2 charstrings (nominalWidthX + charstring
        // width operand, else defaultWidthX); CFF carries no hmtx table, so the
        // charstring is the ONLY source of advance for a /FontFile3 program with
        // no PDF /Widths array. Empty for CID-keyed fonts and non-Type2
        // charstrings — see the AdvanceWidths comment in Parse for why.
        public int[] AdvanceWidths = Array.Empty<int>();

        // Design grid the advance widths are expressed in (the 1/FontMatrix[0]
        // em, 1000 for the near-universal [0.001 0 0 0.001 0 0]). Mirrors
        // TrueTypeFontFile.UnitsPerEm so a caller converts to the 1000ths-of-em
        // width-cascade convention identically: AdvanceWidth(g) * 1000 / UnitsPerEm.
        public int UnitsPerEm = 1000;

        // #1148 — advance width (font units) for <paramref name="glyphIndex"/>,
        // or 0 when the index is out of range or no widths were interpreted.
        // Deliberately mirrors <see cref="TrueTypeFontFile.AdvanceWidth"/> so the
        // embedded-CFF width rung is the same three lines as the TrueType one.
        public int AdvanceWidth(int glyphIndex) =>
            (uint)glyphIndex < (uint)AdvanceWidths.Length ? AdvanceWidths[glyphIndex] : 0;
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

            // Global Subr INDEX — captured (not skipped) so #1148's Type2 width
            // interpreter can follow a callgsubr; a glyph's advance can sit on
            // the stack when the width-deciding operator lives inside a subr.
            var globalSubrs = ReadIndex(ref reader);

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
            if (isCidKeyed && charsetOffset is 0 or 1 or 2)
            {
                // CID-keyed fonts cannot use the predefined SID charsets; a
                // predefined/absent charset offset means the Identity mapping
                // (glyph index N → CID N), which is also how FreeType resolves
                // it. Before #515 this fell into the IsoAdobe branch below —
                // accidentally identity for glyphs ≤ 228 (IsoAdobe SIDs are
                // sequential) but unmapped for every glyph above, so large
                // identity-charset CID fonts lost all high glyphs.
                for (int g = 1; g < numGlyphs; g++)
                    charsetByGlyph[g] = g;
            }
            else if (charsetOffset == 0)
            {
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

            // #1148 — interpret per-glyph advance widths from the Type2
            // charstrings. CFF has no hmtx, so this is the only advance source
            // for a /FontFile3 program with no PDF /Widths array.
            //
            // CID-keyed is deliberately skipped: its per-glyph Private DICTs live
            // behind FDArray/FDSelect, AND the width cascade never reaches an
            // embedded-program rung for a 2-byte font — it returns from the
            // /W + /DW (_cidMetrics) branch first — so a CID-CFF advance here
            // would be unreachable. Non-Type2 charstrings (CharstringType != 2,
            // Top DICT op 12 6) are skipped for lack of a Type1 interpreter.
            // Either way AdvanceWidths stays empty and AdvanceWidth returns 0,
            // the "unknown" the caller already handles.
            int[] advanceWidths = Array.Empty<int>();
            int charstringType = topDict.TryGetValue(1206, out var ctOp) && ctOp.Count > 0
                ? (int)ctOp[0] : 2;
            if (!isCidKeyed && charstringType == 2)
            {
                // Isolated so a malformed charstring/Private DICT costs only the
                // widths (accessor returns 0, the handled "unknown"), never the
                // whole parse — rendering and subsetting callers must still get
                // their glyph-name map from a font whose metrics happen to be bad.
                try
                {
                    advanceWidths = InterpretAdvanceWidths(cff, topDict, globalSubrs, charStringsOffset, numGlyphs);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    advanceWidths = Array.Empty<int>();
                }
            }

            return new CffFontInfo
            {
                NumGlyphs = numGlyphs,
                XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax,
                GlyphNameToIndex = nameToIndex,
                GlyphNames = glyphNames,
                IsCidKeyed = isCidKeyed,
                CidToGlyph = cidToGlyph,
                // CFF FontMatrix is stored as reals, which ParseDict flattens to
                // 0; rather than mis-read it, assume the standard 1000-unit em
                // ([0.001 0 0 0.001 0 0]), true for effectively every embedded
                // /FontFile3. A non-standard FontMatrix would scale the advance —
                // an accepted limitation, mirrored by the FontBBox 1000 default.
                UnitsPerEm = 1000,
                AdvanceWidths = advanceWidths,
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

    // --- #1148: Type2 charstring advance-width interpretation ---

    /// <summary>
    /// Advance width (font units) per glyph index, read from the Type2
    /// charstrings. Reads the non-CID Private DICT (defaultWidthX op 20,
    /// nominalWidthX op 21, local Subrs op 19) then interprets each charstring
    /// far enough to recover its optional leading width operand.
    /// </summary>
    private static int[] InterpretAdvanceWidths(
        byte[] cff, Dictionary<int, List<double>> topDict,
        List<byte[]> globalSubrs, int charStringsOffset, int numGlyphs)
    {
        int defaultWidthX = 0, nominalWidthX = 0;
        var localSubrs = new List<byte[]>();

        // Private DICT: Top DICT op 18 = [size, offset], offset from the CFF start.
        if (topDict.TryGetValue(18, out var pv) && pv.Count >= 2)
        {
            int privSize = (int)pv[0];
            int privOffset = (int)pv[1];
            if (privOffset >= 0 && privSize > 0 && (long)privOffset + privSize <= cff.Length)
            {
                var privBytes = new byte[privSize];
                Array.Copy(cff, privOffset, privBytes, 0, privSize);
                var privDict = ParseDict(privBytes);
                if (privDict.TryGetValue(20, out var dw) && dw.Count > 0) defaultWidthX = (int)dw[0];
                if (privDict.TryGetValue(21, out var nw) && nw.Count > 0) nominalWidthX = (int)nw[0];
                // Local Subrs op 19: offset is relative to the Private DICT start.
                if (privDict.TryGetValue(19, out var ls) && ls.Count > 0)
                {
                    int lsOffset = privOffset + (int)ls[0];
                    if (lsOffset >= 0 && lsOffset < cff.Length)
                    {
                        var r = new CffReader(cff);
                        r.Seek(lsOffset);
                        localSubrs = ReadIndex(ref r);
                    }
                }
            }
        }

        var csReader = new CffReader(cff);
        csReader.Seek(charStringsOffset);
        var charStrings = ReadIndex(ref csReader);

        var widths = new int[numGlyphs];
        int lbias = SubrBias(localSubrs.Count);
        int gbias = SubrBias(globalSubrs.Count);
        for (int g = 0; g < numGlyphs; g++)
        {
            widths[g] = g < charStrings.Count
                ? Type2CharstringWidth(charStrings[g], localSubrs, globalSubrs, lbias, gbias, nominalWidthX, defaultWidthX)
                : defaultWidthX;
        }
        return widths;
    }

    // Type2 local/global subr number bias (Adobe TN#5177 §4.7).
    private static int SubrBias(int count) =>
        count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    /// <summary>
    /// The advance width encoded in a single Type2 charstring: nominalWidthX
    /// plus the optional leading width operand, or defaultWidthX when absent.
    /// The width, when present, is the extra first operand before the FIRST of
    /// {hstem/vstem/hstemhm/vstemhm, hintmask/cntrmask, [hv]moveto/rmoveto,
    /// endchar}, so interpretation stops at that operator — no stem counting or
    /// mask-byte skipping needed. Follows callsubr/callgsubr on a shared operand
    /// stack because the deciding operator may live inside a subr while the
    /// width sits below the subr index. Any unexpected operator, or a malformed
    /// program, falls back to defaultWidthX; a recursion cap and step budget
    /// keep a hostile font from hanging (Pitfall 3).
    /// </summary>
    private static int Type2CharstringWidth(
        byte[] charString, List<byte[]> local, List<byte[]> global,
        int lbias, int gbias, int nominalWidthX, int defaultWidthX)
    {
        var stack = new List<double>(48);
        int width = defaultWidthX;
        bool decided = false;
        int steps = 0;

        void Run(byte[] code, int depth)
        {
            if (decided || depth > 10) return;
            int i = 0;
            while (i < code.Length && !decided)
            {
                if (++steps > 200_000) { decided = true; return; }
                int b = code[i++];

                if (b == 28)
                {
                    if (i + 1 >= code.Length) { decided = true; return; }
                    stack.Add((short)((code[i] << 8) | code[i + 1]));
                    i += 2;
                    continue;
                }
                if (b >= 32)
                {
                    double val;
                    if (b <= 246) val = b - 139;
                    else if (b <= 250) { if (i >= code.Length) { decided = true; return; } val = (b - 247) * 256 + code[i++] + 108; }
                    else if (b <= 254) { if (i >= code.Length) { decided = true; return; } val = -(b - 251) * 256 - code[i++] - 108; }
                    else
                    {
                        if (i + 3 >= code.Length) { decided = true; return; }
                        int fixed1616 = (code[i] << 24) | (code[i + 1] << 16) | (code[i + 2] << 8) | code[i + 3];
                        i += 4;
                        val = fixed1616 / 65536.0;
                    }
                    stack.Add(val);
                    continue;
                }

                // Operator.
                switch (b)
                {
                    case 1: case 3: case 18: case 23: // hstem vstem hstemhm vstemhm
                    case 19: case 20:                 // hintmask cntrmask (implicit vstem)
                        if ((stack.Count & 1) == 1) width = nominalWidthX + (int)stack[0];
                        decided = true;
                        return;
                    case 21: // rmoveto (2 args)
                        if (stack.Count > 2) width = nominalWidthX + (int)stack[0];
                        decided = true;
                        return;
                    case 22: case 4: // hmoveto vmoveto (1 arg)
                        if (stack.Count > 1) width = nominalWidthX + (int)stack[0];
                        decided = true;
                        return;
                    case 14: // endchar (0 args, or 4 for seac)
                        if (stack.Count == 1 || stack.Count == 5) width = nominalWidthX + (int)stack[0];
                        decided = true;
                        return;
                    case 10: // callsubr
                        if (stack.Count > 0)
                        {
                            int idx = (int)stack[^1] + lbias;
                            stack.RemoveAt(stack.Count - 1);
                            if ((uint)idx < (uint)local.Count) Run(local[idx], depth + 1);
                        }
                        else { decided = true; return; }
                        break;
                    case 29: // callgsubr
                        if (stack.Count > 0)
                        {
                            int idx = (int)stack[^1] + gbias;
                            stack.RemoveAt(stack.Count - 1);
                            if ((uint)idx < (uint)global.Count) Run(global[idx], depth + 1);
                        }
                        else { decided = true; return; }
                        break;
                    case 11: // return
                        return;
                    default:
                        // Anything else before the first width-deciding operator
                        // (including a 12-escape arithmetic/flex op) cannot carry
                        // a width — bail to defaultWidthX rather than guess.
                        decided = true;
                        return;
                }
            }
        }

        Run(charString, 0);
        return width;
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

    /// <summary>
    /// The 391 CFF standard strings (Adobe CFF spec, Appendix A). SID 0-390
    /// resolve here; SIDs above 390 index the font's own String INDEX.
    /// </summary>
    /// <remarks>
    /// This table held <b>244</b> entries and was misaligned from <b>SID 151</b>
    /// onward: "onesuperior", "twosuperior", "threesuperior", "minus" and
    /// "multiply" each appeared THREE times, and the whole Latin-1 accented
    /// block (SID 171-228, Aacute..zcaron) plus every small-cap/oldstyle name
    /// (SID 229-390) was absent. SIDs 0-150 were correct, which is why it
    /// looked fine on plain ASCII and failed only on accented text.
    ///
    /// Consequences, via CffParser.ResolveSid: SIDs 151-243 returned the WRONG
    /// glyph name and 244-390 returned null.
    ///
    /// Measured effect: pdf.js issue4573.pdf goes MISSING_CONTENT -> PASS.
    ///
    /// TextExtractor also reaches ResolveSid (via CffParser.Parse), so a wrong
    /// SID is structurally capable of producing wrong EXTRACTED text — which
    /// under CLAUDE.md's "redaction completeness is bounded by extraction
    /// coverage" would be a redaction-security defect, not display polish.
    /// Stated carefully because it is NOT demonstrated: issue4573 extracts "ü"
    /// correctly even with the broken table (its Unicode arrives by another
    /// route), so on every fixture available the observed damage is rendering
    /// only. No corpus page is known to lose text this way.
    ///
    /// Regenerated from fontTools' cffStandardStrings rather than typed from
    /// recall — the failure mode being fixed is precisely a hand-maintained
    /// list with duplicated and dropped runs.
    /// </remarks>
    private static string[] BuildStandardStrings() => new[]
    {
        ".notdef","space","exclam","quotedbl","numbersign","dollar","percent","ampersand","quoteright",
        "parenleft","parenright","asterisk","plus","comma","hyphen","period","slash","zero","one",
        "two","three","four","five","six","seven","eight","nine","colon","semicolon","less","equal",
        "greater","question","at","A","B","C","D","E","F","G","H","I","J","K","L","M","N","O",
        "P","Q","R","S","T","U","V","W","X","Y","Z","bracketleft","backslash","bracketright",
        "asciicircum","underscore","quoteleft","a","b","c","d","e","f","g","h","i","j","k","l",
        "m","n","o","p","q","r","s","t","u","v","w","x","y","z","braceleft","bar","braceright",
        "asciitilde","exclamdown","cent","sterling","fraction","yen","florin","section","currency",
        "quotesingle","quotedblleft","guillemotleft","guilsinglleft","guilsinglright","fi","fl",
        "endash","dagger","daggerdbl","periodcentered","paragraph","bullet","quotesinglbase",
        "quotedblbase","quotedblright","guillemotright","ellipsis","perthousand","questiondown",
        "grave","acute","circumflex","tilde","macron","breve","dotaccent","dieresis","ring","cedilla",
        "hungarumlaut","ogonek","caron","emdash","AE","ordfeminine","Lslash","Oslash","OE","ordmasculine",
        "ae","dotlessi","lslash","oslash","oe","germandbls","onesuperior","logicalnot","mu","trademark",
        "Eth","onehalf","plusminus","Thorn","onequarter","divide","brokenbar","degree","thorn",
        "threequarters","twosuperior","registered","minus","eth","multiply","threesuperior","copyright",
        "Aacute","Acircumflex","Adieresis","Agrave","Aring","Atilde","Ccedilla","Eacute","Ecircumflex",
        "Edieresis","Egrave","Iacute","Icircumflex","Idieresis","Igrave","Ntilde","Oacute","Ocircumflex",
        "Odieresis","Ograve","Otilde","Scaron","Uacute","Ucircumflex","Udieresis","Ugrave","Yacute",
        "Ydieresis","Zcaron","aacute","acircumflex","adieresis","agrave","aring","atilde","ccedilla",
        "eacute","ecircumflex","edieresis","egrave","iacute","icircumflex","idieresis","igrave",
        "ntilde","oacute","ocircumflex","odieresis","ograve","otilde","scaron","uacute","ucircumflex",
        "udieresis","ugrave","yacute","ydieresis","zcaron","exclamsmall","Hungarumlautsmall",
        "dollaroldstyle","dollarsuperior","ampersandsmall","Acutesmall","parenleftsuperior","parenrightsuperior",
        "twodotenleader","onedotenleader","zerooldstyle","oneoldstyle","twooldstyle","threeoldstyle",
        "fouroldstyle","fiveoldstyle","sixoldstyle","sevenoldstyle","eightoldstyle","nineoldstyle",
        "commasuperior","threequartersemdash","periodsuperior","questionsmall","asuperior","bsuperior",
        "centsuperior","dsuperior","esuperior","isuperior","lsuperior","msuperior","nsuperior",
        "osuperior","rsuperior","ssuperior","tsuperior","ff","ffi","ffl","parenleftinferior",
        "parenrightinferior","Circumflexsmall","hyphensuperior","Gravesmall","Asmall","Bsmall",
        "Csmall","Dsmall","Esmall","Fsmall","Gsmall","Hsmall","Ismall","Jsmall","Ksmall","Lsmall",
        "Msmall","Nsmall","Osmall","Psmall","Qsmall","Rsmall","Ssmall","Tsmall","Usmall","Vsmall",
        "Wsmall","Xsmall","Ysmall","Zsmall","colonmonetary","onefitted","rupiah","Tildesmall",
        "exclamdownsmall","centoldstyle","Lslashsmall","Scaronsmall","Zcaronsmall","Dieresissmall",
        "Brevesmall","Caronsmall","Dotaccentsmall","Macronsmall","figuredash","hypheninferior",
        "Ogoneksmall","Ringsmall","Cedillasmall","questiondownsmall","oneeighth","threeeighths",
        "fiveeighths","seveneighths","onethird","twothirds","zerosuperior","foursuperior","fivesuperior",
        "sixsuperior","sevensuperior","eightsuperior","ninesuperior","zeroinferior","oneinferior",
        "twoinferior","threeinferior","fourinferior","fiveinferior","sixinferior","seveninferior",
        "eightinferior","nineinferior","centinferior","dollarinferior","periodinferior","commainferior",
        "Agravesmall","Aacutesmall","Acircumflexsmall","Atildesmall","Adieresissmall","Aringsmall",
        "AEsmall","Ccedillasmall","Egravesmall","Eacutesmall","Ecircumflexsmall","Edieresissmall",
        "Igravesmall","Iacutesmall","Icircumflexsmall","Idieresissmall","Ethsmall","Ntildesmall",
        "Ogravesmall","Oacutesmall","Ocircumflexsmall","Otildesmall","Odieresissmall","OEsmall",
        "Oslashsmall","Ugravesmall","Uacutesmall","Ucircumflexsmall","Udieresissmall","Yacutesmall",
        "Thornsmall","Ydieresissmall","001.000","001.001","001.002","001.003","Black","Bold",
        "Book","Light","Medium","Regular","Roman","Semibold"
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
