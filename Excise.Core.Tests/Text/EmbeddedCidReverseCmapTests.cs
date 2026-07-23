using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Text;
using Excise.Core.Tests.Fixtures;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #515 slice 3 — GID→Unicode via the EMBEDDED font program's own character
/// map, read in reverse, for Type0 + Identity-H/V fonts with NO /ToUnicode.
///
/// This class of font previously extracted as garbage: #532's
/// standard-Mac-glyph-order fallback is deliberately scoped to NON-embedded
/// fonts (embedded subset glyph orders are arbitrary, so a Mac-order guess
/// would corrupt them), so embedded Identity-H no-ToUnicode text fell through
/// to DecodeWinAnsi(rawGID). Garbled extraction is a silent redaction failure
/// (CLAUDE.md limitation #1: excise cannot redact what it cannot read).
///
/// Every fixture here is GENERATED at test time around a real, checked-in
/// font program (DejaVu Sans TTF / Inconsolata CFF / Libertinus OTTO), with
/// the content-stream codes computed FROM the font's own tables — no
/// hard-coded GIDs, and a genuine red→green: pre-fix, each positive test's
/// extraction is raw-GID garbage.
/// </summary>
public class EmbeddedCidReverseCmapTests
{
    private const string Word = "Redact";

    // ---------- /FontFile2 (CIDFontType2, TrueType) ----------

    [Fact] // pre-fix: extracts raw GIDs as Latin-1 garbage
    public void EmbeddedTrueType_IdentityH_NoToUnicode_ExtractsViaReverseCmap()
    {
        var font = TestFontFixtures.LoadDejaVuSansBytes();
        var gids = GidsFor(font, Word);

        var pdf = BuildEmbeddedType0Pdf(font, fontFileKey: "FontFile2",
            codes: gids, toUnicode: null, cidToGidMap: null);

        Extract(pdf).Should().Contain(Word,
            "an embedded Identity-H CIDFontType2 with no /ToUnicode must decode via the " +
            "embedded TrueType cmap read in reverse (GID→Unicode), not raw-GID WinAnsi");
    }

    [Fact] // /CIDToGIDMap stream: code→CID→GID must be applied before the reverse cmap
    public void EmbeddedTrueType_CidToGidMapStream_IsAppliedBeforeReverseCmap()
    {
        var font = TestFontFixtures.LoadDejaVuSansBytes();
        var gids = GidsFor(font, Word);

        // CIDs 1..N remap to the word's GIDs via a CIDToGIDMap stream. The
        // content stream uses the low CIDs — decoding them correctly requires
        // honoring the stream (identity CID==GID would hit unrelated glyphs).
        var cidToGid = new byte[(gids.Length + 1) * 2];
        for (int i = 0; i < gids.Length; i++)
        {
            cidToGid[(i + 1) * 2] = (byte)(gids[i] >> 8);
            cidToGid[(i + 1) * 2 + 1] = (byte)(gids[i] & 0xFF);
        }
        var codes = Enumerable.Range(1, gids.Length).ToArray();

        var pdf = BuildEmbeddedType0Pdf(font, fontFileKey: "FontFile2",
            codes: codes, toUnicode: null, cidToGidMap: cidToGid);

        Extract(pdf).Should().Contain(Word,
            "a /CIDToGIDMap stream maps code→CID→GID; the reverse-cmap lookup must key on the GID");
    }

    [Fact] // guard: a /ToUnicode stream always outranks the embedded reverse cmap
    public void ToUnicodeStream_IsNotOverriddenByEmbeddedReverseCmap()
    {
        var font = TestFontFixtures.LoadDejaVuSansBytes();
        var gids = GidsFor(font, "R");

        // ToUnicode says this code is "Z" — the producer's declared map wins
        // even though the embedded cmap says the GID is "R".
        var toUnicode = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            $"1 beginbfchar\n<{gids[0]:X4}> <005A>\nendbfchar\nendcmap\nend end";

        var pdf = BuildEmbeddedType0Pdf(font, fontFileKey: "FontFile2",
            codes: gids, toUnicode: toUnicode, cidToGidMap: null);

        var text = Extract(pdf);
        text.Should().Contain("Z", "/ToUnicode is the producer's declared map and has priority");
        text.Should().NotContain("R", "the embedded reverse cmap must not override /ToUnicode");
    }

    [Fact] // codes the embedded cmap can't resolve keep the pre-existing fallback (don't invent)
    public void UnmappedGid_FallsThroughToExistingBehavior()
    {
        var font = TestFontFixtures.LoadDejaVuSansBytes();
        var known = GidsFor(font, "A");
        var unmapped = 0xFFFE; // far beyond any real DejaVu GID

        var pdf = BuildEmbeddedType0Pdf(font, fontFileKey: "FontFile2",
            codes: new[] { known[0], unmapped }, toUnicode: null, cidToGidMap: null);

        // Must not throw, and the mapped code must still decode.
        Extract(pdf).Should().Contain("A");
    }

    // ---------- /FontFile3 (CIDFontType0, CFF) ----------

    [Fact] // raw name-keyed CFF: GID→glyph-name→Unicode via the AGL
    public void EmbeddedRawCff_IdentityH_NoToUnicode_ExtractsViaGlyphNames()
    {
        var cff = TestFontFixtures.LoadInconsolataCffBytes();
        var info = CffParser.Parse(cff);
        Assert.NotNull(info);
        info!.IsCidKeyed.Should().BeFalse("Inconsolata.cff is a name-keyed CFF");

        // Per §9.7.4.2, a non-CID-keyed CFF descendant uses the CID directly
        // as glyph index — compute the glyph indices from the charset.
        //
        // Deliberately NOT plain ASCII letters: this fixture's glyph order
        // mirrors Latin-1 for those (GID('R') == 0x52), so the raw-GID WinAnsi
        // fallback would pass by coincidence and the test would prove nothing.
        // These glyphs sit at indices with no Latin-1 relationship at all.
        var glyphs = new[] { "OE", "ellipsis", "bullet", "dagger" };
        var codes = glyphs.Select(n => info.GlyphNameToIndex[n]).ToArray();

        var pdf = BuildEmbeddedType0Pdf(cff, fontFileKey: "FontFile3",
            codes: codes, toUnicode: null, cidToGidMap: null,
            descendantSubtype: "CIDFontType0", fontFileSubtype: "Type1C");

        Extract(pdf).Should().Contain("Œ…•†",
            "an embedded name-keyed CFF resolves GID→glyph-name→Unicode via the Adobe Glyph List");
    }

    [Fact] // OpenType-wrapped CFF ('OTTO'): the sfnt cmap is the Unicode source
    public void EmbeddedOpenTypeCff_IdentityH_NoToUnicode_ExtractsViaSfntCmap()
    {
        var otf = TestFontFixtures.LoadLibertinusSerifCffBytes();
        var gids = GidsFor(otf, Word);

        var pdf = BuildEmbeddedType0Pdf(otf, fontFileKey: "FontFile3",
            codes: gids, toUnicode: null, cidToGidMap: null,
            descendantSubtype: "CIDFontType0", fontFileSubtype: "OpenType");

        Extract(pdf).Should().Contain(Word,
            "an OpenType-wrapped CFF exposes its sfnt cmap; reversed, it is the GID→Unicode source");
    }

    // ---------- helpers ----------

    private static int[] GidsFor(byte[] sfntFont, string word)
    {
        var ttf = TrueTypeFontFile.Parse(sfntFont);
        return word.Select(c =>
        {
            var gid = ttf.GidForCodepoint(c);
            gid.Should().BeGreaterThan(0, $"fixture font must map '{c}'");
            return gid;
        }).ToArray();
    }

    private static string Extract(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return new TextExtractor(doc.GetPage(1)).ExtractText();
    }

    /// <summary>
    /// Builds a single-page PDF with a Type0 / Identity-H font whose descendant
    /// embeds <paramref name="fontFile"/> and (deliberately) has NO /ToUnicode
    /// unless one is passed. The content stream shows the 2-byte
    /// <paramref name="codes"/> as one hex string. Binary-safe (the font bytes
    /// are written raw), unlike the ASCII-only builders in
    /// <see cref="TextExtractorType0Tests"/>.
    /// </summary>
    private static byte[] BuildEmbeddedType0Pdf(
        byte[] fontFile, string fontFileKey, int[] codes, string? toUnicode,
        byte[]? cidToGidMap, string descendantSubtype = "CIDFontType2",
        string? fontFileSubtype = null)
    {
        var ms = new MemoryStream();
        var offsets = new long[11];
        void Ascii(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        void Obj(int n) => offsets[n] = ms.Length;

        var hex = string.Concat(codes.Select(c => c.ToString("X4")));
        var content = $"BT /F0 24 Tf 72 700 Td <{hex}> Tj ET";

        Ascii("%PDF-1.7\n");
        Obj(1); Ascii("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Obj(2); Ascii("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Obj(3); Ascii("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                      "/Resources<</Font<</F0 4 0 R>>>>/Contents 9 0 R>> endobj\n");

        Obj(4);
        Ascii("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
              "/Encoding/Identity-H/DescendantFonts[5 0 R]" +
              (toUnicode != null ? "/ToUnicode 10 0 R" : "") + ">> endobj\n");

        Obj(5);
        Ascii($"5 0 obj <</Type/Font/Subtype/{descendantSubtype}/BaseFont/Test" +
              "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>" +
              "/FontDescriptor 6 0 R/DW 1000" +
              (cidToGidMap != null ? "/CIDToGIDMap 8 0 R" : "") + ">> endobj\n");

        Obj(6);
        Ascii("6 0 obj <</Type/FontDescriptor/FontName/Test/Flags 4" +
              "/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800/Descent -200" +
              $"/CapHeight 700/StemV 80/{fontFileKey} 7 0 R>> endobj\n");

        Obj(7);
        Ascii($"7 0 obj <</Length {fontFile.Length}" +
              (fontFileKey == "FontFile2" ? $"/Length1 {fontFile.Length}" : "") +
              (fontFileSubtype != null ? $"/Subtype/{fontFileSubtype}" : "") +
              ">>\nstream\n");
        ms.Write(fontFile, 0, fontFile.Length);
        Ascii("\nendstream endobj\n");

        if (cidToGidMap != null)
        {
            Obj(8);
            Ascii($"8 0 obj <</Length {cidToGidMap.Length}>>\nstream\n");
            ms.Write(cidToGidMap, 0, cidToGidMap.Length);
            Ascii("\nendstream endobj\n");
        }

        Obj(9);
        Ascii($"9 0 obj <</Length {content.Length}>>\nstream\n{content}\nendstream endobj\n");

        if (toUnicode != null)
        {
            Obj(10);
            Ascii($"10 0 obj <</Length {toUnicode.Length}>>\nstream\n{toUnicode}\nendstream endobj\n");
        }

        var xref = ms.Length;
        Ascii("xref\n0 11\n0000000000 65535 f \n");
        for (int i = 1; i <= 10; i++)
        {
            if (offsets[i] == 0)
                Ascii("0000000000 65535 f \n");
            else
                Ascii(offsets[i].ToString("D10") + " 00000 n \n");
        }
        Ascii($"trailer <</Size 11/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
