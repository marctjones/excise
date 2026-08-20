using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Text.Segmentation;
using Excise.Core.Tests.Fixtures;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// RTL (Arabic/Hebrew) extraction and redaction in <b>Type0 / Identity-H</b>
/// fonts, where the content-stream operands are 2-byte CIDs (glyph indices of
/// an embedded font), NOT character codes (#632).
///
/// This is the redaction-critical CID case CLAUDE.md calls out: a saved-bytes
/// search — even UTF-16BE — is BLIND to the Arabic string, because the string
/// never appears in the file as Unicode; only CIDs do. Correctness here can
/// therefore only be asserted by an extractor (excise's, cross-checked against
/// mutool in <c>Excise.Rendering.Tests.Differential.RtlType0RedactionDifferentialTests</c>)
/// and by the glyph-removal render — never by grepping the bytes.
///
/// The fixtures embed the real, checked-in DejaVu Sans TTF (which covers both
/// Hebrew and Arabic base letters) and paint the word's GIDs in VISUAL order
/// (reversed — the common producer encoding). Expected logical strings are
/// spec-derived (UAX #9) and corroborated by the python-bidi reference and by
/// mutool in the differential suite.
/// </summary>
public class RtlType0ExtractionTests
{
    private const string ArabicWord = "سلام"; // logical
    private const string HebrewWord = "שלום"; // logical

    private static readonly int[] ArabicScalars = { 0x0633, 0x0644, 0x0627, 0x0645 };
    private static readonly int[] HebrewScalars = { 0x05E9, 0x05DC, 0x05D5, 0x05DD };

    [Fact]
    public void Type0IdentityH_VisualOrderArabic_ExtractsLogicalOrder()
    {
        var pdf = Type0RtlFixtures.SingleLineVisualOrder(ArabicScalars);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().Be(ArabicWord,
            "a Type0/Identity-H visual-order run must reorder to logical order, exactly as the " +
            "single-byte path does — the CID encoding must not defeat the bidi reorder");
    }

    [Fact]
    public void Type0IdentityH_VisualOrderHebrew_ExtractsLogicalOrder()
    {
        var pdf = Type0RtlFixtures.SingleLineVisualOrder(HebrewScalars);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().Be(HebrewWord);
    }

    [Fact]
    public void Type0IdentityH_DigitIslandLine_ExtractsLogicalOrder()
    {
        // Logical "عمر 30 سنة" carried in visual order in a CID font. The
        // digit-island rule (UAX #9 W4) must survive the CID encoding.
        int[] visual = { 0x0629, 0x0646, 0x0633, 0x0020, '3', '0', 0x0020, 0x0631, 0x0645, 0x0639 };
        int[] logical = { 0x0639, 0x0645, 0x0631, 0x0020, '3', '0', 0x0020, 0x0633, 0x0646, 0x0629 };
        var pdf = Type0RtlFixtures.SingleLine(visual);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().Be(Str(logical));
    }

    [Fact]
    public void Type0IdentityH_RedactLogicalNeedle_RemovesVisualOrderWord()
    {
        var pdf = Type0RtlFixtures.SingleLineVisualOrder(ArabicScalars);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(ArabicWord).VerifiedRemovals;

        removed.Should().BeGreaterThan(0,
            "a logical-order needle must match the visual-order CID run; 0 matches is the " +
            "silent-failure mode (excise cannot redact what excise cannot read)");
        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        reopened.GetPage(1).Text.Should().NotContain(ArabicWord);
    }

    /// <summary>
    /// A line whose paragraph direction is LTR (a Latin label followed by an
    /// Arabic name) is handled correctly TODAY: the Latin prefix is logically
    /// and visually first, so within-run RTL reversal alone yields full logical
    /// order, and a phrase needle spanning the direction change matches.
    /// Regression guard for that working case.
    /// </summary>
    [Fact]
    public void LtrLabelThenRtlName_ExtractsLogicalOrder_AndSpanningPhraseRedacts()
    {
        int[] khalid = { 0x062E, 0x0627, 0x0644, 0x062F }; // خالد
        var visual = "Name ".Select(c => (int)c).Concat(khalid.Reverse()).ToArray();

        using (var doc = PdfDocument.Open(Type0RtlFixtures.SingleLine(visual)))
            doc.GetPage(1).Text.Should().Be("Name " + Str(khalid));

        using (var doc = PdfDocument.Open(Type0RtlFixtures.SingleLine(visual)))
            doc.RedactText(Str(khalid)).VerifiedRemovals.Should().BeGreaterThan(0, "per-word redaction");

        using (var doc = PdfDocument.Open(Type0RtlFixtures.SingleLine(visual)))
            doc.RedactText("Name " + Str(khalid)).VerifiedRemovals.Should().BeGreaterThan(0,
                "a phrase spanning the LTR->RTL boundary matches on an LTR-base line");
    }

    /// <summary>
    /// MEASURED LIMITATION (not a silent gap) — whole-line UAX #9 reordering,
    /// split to #785. On an RTL-BASE line whose logical order ends with an LTR
    /// word ("عربى hello"), the within-run reorder produces "hello عربى": each
    /// word is individually logical, but the run order across the line is not
    /// re-derived (the paragraph-direction resolution UAX #9 P2/P3 + L2 would
    /// require, and which is genuinely ambiguous from the stream — the two
    /// logical orders share one visual stream).
    ///
    /// The redaction consequence is BOUNDED and pinned here: the individual
    /// RTL word STILL redacts; only a phrase needle spanning the direction
    /// change does not match. This test documents the exact boundary so the
    /// limitation is measured, not discovered later as a surprise.
    /// </summary>
    [Fact]
    public void Limitation_WholeLineUba_RtlBaseTrailingLtr_PerWordRedactsButSpanningPhraseDoesNot()
    {
        int[] arabic = { 0x0639, 0x0631, 0x0628, 0x0649 }; // logical first word
        var visual = "hello ".Select(c => (int)c).Concat(arabic.Reverse()).ToArray();
        var word = Str(arabic);

        // Extraction is per-run logical, not whole-line logical (see #785).
        using (var doc = PdfDocument.Open(Type0RtlFixtures.SingleLine(visual)))
            doc.GetPage(1).Text.Should().Be("hello " + word,
                "documents the current within-run behaviour; full logical is '" + word + " hello' (#785)");

        // The redaction-critical unit — the individual RTL word — still works.
        using (var doc = PdfDocument.Open(Type0RtlFixtures.SingleLine(visual)))
            doc.RedactText(word).VerifiedRemovals.Should().BeGreaterThan(0,
                "per-word RTL redaction is unaffected by the whole-line gap");

        // Only the direction-spanning phrase is missed (bounded, tracked #785).
        using (var doc = PdfDocument.Open(Type0RtlFixtures.SingleLine(visual)))
            doc.RedactText(word + " hello").VerifiedRemovals.Should().Be(0,
                "a phrase spanning the RTL->LTR direction change is not matched on an RTL-base " +
                "line — the whole-line UAX #9 gap tracked as #785; per-word redaction above still works");
    }

    private static string Str(int[] scalars) =>
        string.Concat(scalars.Select(c => char.ConvertFromUtf32(c)));
}

/// <summary>
/// Builds single-page PDFs with a Type0 / Identity-H CIDFontType2 font that
/// embeds the checked-in DejaVu Sans TTF. The content stream shows 2-byte GIDs
/// (looked up from the font's own cmap), and a /ToUnicode CMap maps each GID
/// back to its scalar — so extraction order is driven purely by the stream's
/// glyph order and the expected text is known exactly. Binary-safe.
/// </summary>
internal static class Type0RtlFixtures
{
    private static readonly byte[] Font = TestFontFixtures.LoadDejaVuSansBytes();
    private static readonly TrueTypeFontFile Ttf = TrueTypeFontFile.Parse(Font);

    /// <summary>The word's scalars are reversed (visual order) then embedded.</summary>
    public static byte[] SingleLineVisualOrder(int[] logicalScalars) =>
        SingleLine(logicalScalars.Reverse().ToArray());

    /// <summary>Embed <paramref name="scalarsInStreamOrder"/> as GIDs in exactly that order.</summary>
    public static byte[] SingleLine(int[] scalarsInStreamOrder)
    {
        var gids = scalarsInStreamOrder.Select(GidFor).ToArray();
        return Build(gids, scalarsInStreamOrder);
    }

    private static int GidFor(int scalar)
    {
        var gid = Ttf.GidForCodepoint(scalar);
        gid.Should().BeGreaterThan(0, $"DejaVu Sans must map U+{scalar:X4}");
        return gid;
    }

    private static byte[] Build(int[] gids, int[] scalars)
    {
        var ms = new MemoryStream();
        var offsets = new long[11];
        void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int n) => offsets[n] = ms.Length;

        var hex = string.Concat(gids.Select(c => c.ToString("X4")));
        var content = $"BT /F0 24 Tf 72 700 Td <{hex}> Tj ET";

        var seen = new HashSet<int>();
        var bf = new StringBuilder();
        int count = 0;
        for (int i = 0; i < gids.Length; i++)
        {
            if (!seen.Add(gids[i])) continue;
            bf.Append($"<{gids[i]:X4}> <{scalars[i]:X4}>\n");
            count++;
        }
        var toUni = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            $"{count} beginbfchar\n{bf}endbfchar\nendcmap\nend end";

        Ascii("%PDF-1.7\n");
        Obj(1); Ascii("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Obj(2); Ascii("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Obj(3); Ascii("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                      "/Resources<</Font<</F0 4 0 R>>>>/Contents 9 0 R>> endobj\n");
        Obj(4); Ascii("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
                      "/Encoding/Identity-H/DescendantFonts[5 0 R]/ToUnicode 10 0 R>> endobj\n");
        Obj(5); Ascii("5 0 obj <</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
                      "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>" +
                      "/FontDescriptor 6 0 R/DW 1000>> endobj\n");
        Obj(6); Ascii("6 0 obj <</Type/FontDescriptor/FontName/Test/Flags 4" +
                      "/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800/Descent -200" +
                      "/CapHeight 700/StemV 80/FontFile2 7 0 R>> endobj\n");
        Obj(7); Ascii($"7 0 obj <</Length {Font.Length}/Length1 {Font.Length}>>\nstream\n");
        ms.Write(Font, 0, Font.Length); Ascii("\nendstream endobj\n");
        Obj(9); Ascii($"9 0 obj <</Length {content.Length}>>\nstream\n{content}\nendstream endobj\n");
        Obj(10); Ascii($"10 0 obj <</Length {toUni.Length}>>\nstream\n{toUni}\nendstream endobj\n");

        var xref = ms.Length;
        Ascii("xref\n0 11\n0000000000 65535 f \n");
        for (int i = 1; i <= 10; i++)
            Ascii(offsets[i] == 0 ? "0000000000 65535 f \n" : offsets[i].ToString("D10") + " 00000 n \n");
        Ascii($"trailer <</Size 11/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
