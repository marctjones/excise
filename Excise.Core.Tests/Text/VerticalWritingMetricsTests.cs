using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #515 slice 5 — vertical writing metrics. Before this slice, vertical
/// (WMode 1) Type0 text advanced by the glyph's HORIZONTAL width in the +y
/// direction — i.e. UP the page and by the wrong amount — and the /W2 and
/// /DW2 vertical metric tables (PDF 32000-1:2008 §9.7.4.3) were not read at
/// all. Every expected coordinate below is computed by hand from the spec
/// formulas (§9.4.4: ty = w1y·Tfs + Tc + Tw; §9.4.3: TJ subtracts from the
/// vertical coordinate; §9.7.4.3: default /DW2 = [880 −1000], default
/// position vector v = (w0∕2, DW2[0])) — the oracle is the spec, not excise.
///
/// Wrong vertical geometry is a redaction problem, not cosmetics: letter
/// bounding boxes drive area redaction and the black-box overlay, so a
/// vertical line whose letters claim to run UP the page redacts the wrong
/// region (CLAUDE.md limitation #1). The round-trip test at the bottom pins
/// the full path: RedactText on vertical Identity-V text removes the target
/// glyphs from the saved bytes and keeps the rest.
/// </summary>
public class VerticalWritingMetricsTests
{
    // All fixtures: /F0 at 24pt, pen starts at (72, 700). One text-space unit
    // of glyph metric (1/1000 em) is 0.024pt here.
    private const double Size = 24;
    private const double PenX = 72;
    private const double PenY = 700;

    // ---------- advance: /DW2 default, /DW2 explicit, /W2 per-CID ----------

    [Fact] // spec default DW2 [880 -1000]: each glyph advances one em DOWN
    public void IdentityV_DefaultMetrics_AdvanceOneEmDown()
    {
        // /ToUnicode /Identity-H pins decoding to UTF-16BE (#716) so the
        // values below don't depend on the #532 non-embedded-font heuristic.
        var pdf = BuildType0Pdf("/Identity-V", "<00410042> Tj",
            fontExtras: "/ToUnicode/Identity-H");
        var letters = Extract(pdf);

        letters.Select(l => l.Value).Should().Equal("A", "B");
        letters[0].StartX.Should().BeApproximately(PenX, 0.01);
        letters[1].StartX.Should().BeApproximately(PenX, 0.01,
            "vertical writing must not advance along X");
        letters[0].StartY.Should().BeApproximately(PenY, 0.01);
        letters[1].StartY.Should().BeApproximately(PenY - Size, 0.01,
            "w1y defaults to −1000/1000 em (§9.7.4.3): 24pt DOWN, not up");
    }

    [Fact] // explicit /DW2 replaces the default vertical displacement
    public void IdentityV_Dw2_ControlsDefaultAdvance()
    {
        var pdf = BuildType0Pdf("/Identity-V", "<00410042> Tj", cidExtras: "/DW2[880 -2000]");
        var letters = Extract(pdf);

        letters[1].StartY.Should().BeApproximately(PenY - 2 * Size, 0.01,
            "/DW2 [880 -2000] means a 2-em downward advance per glyph");
    }

    [Fact] // /W2 overrides /DW2 for its CIDs only
    public void IdentityV_W2PerCid_OverridesDw2()
    {
        // CID 0x41 ('A' under Identity-V): w1y = -500 → half-em advance.
        // CID 0x42 keeps the -1000 default.
        var pdf = BuildType0Pdf("/Identity-V", "<004100420043> Tj",
            cidExtras: "/W2[65[-500 342 880]]");
        var letters = Extract(pdf);

        letters[1].StartY.Should().BeApproximately(PenY - 0.5 * Size, 0.01,
            "/W2 w1y=-500 for CID 65 is a half-em advance");
        letters[2].StartY.Should().BeApproximately(PenY - 1.5 * Size, 0.01,
            "CID 66 has no /W2 entry and falls back to /DW2's default full em");
    }

    // ---------- TJ, Th, Tc/Tw interactions ----------

    [Fact] // §9.4.3: the TJ number is subtracted from the VERTICAL coordinate
    public void IdentityV_TjAdjustment_MovesAlongY()
    {
        var pdf = BuildType0Pdf("/Identity-V", "[<0041> 500 <0041>] TJ");
        var letters = Extract(pdf);

        letters[1].StartX.Should().BeApproximately(PenX, 0.01,
            "a TJ adjustment in vertical mode must not move X");
        letters[1].StartY.Should().BeApproximately(PenY - Size - 0.5 * Size, 0.01,
            "500/1000 · 24pt subtracted from the vertical coordinate after the 1-em advance");
    }

    [Fact] // §9.2.4/§9.4.4: Tz horizontal scaling applies ONLY to horizontal writing
    public void IdentityV_HorizontalScaling_DoesNotScaleVerticalAdvance()
    {
        var pdf = BuildType0Pdf("/Identity-V", "200 Tz <00410042> Tj");
        var letters = Extract(pdf);

        letters[1].StartY.Should().BeApproximately(PenY - Size, 0.01,
            "200% Tz must not double the vertical advance — Th applies only to tx");
    }

    [Fact] // §9.3.3: word spacing applies ONLY to the single-byte code 32 —
           // the 2-byte Identity code <0020> must not fire it
    public void IdentityH_TwoByteSpaceCode_DoesNotFireWordSpacing()
    {
        var pdf = BuildType0Pdf("/Identity-H", "10 Tw <00200041> Tj");
        var letters = Extract(pdf);

        letters[1].StartX.Should().BeApproximately(PenX + Size, 0.01,
            "the 2-byte code <0020> is not the single-byte code 32; Tw (10pt) must not apply");
    }

    [Fact] // …while a REGISTERED CMap's genuinely single-byte code 32 does fire it
    public void RegisteredCMap_OneByteSpaceCode_FiresWordSpacing()
    {
        // 90ms-RKSJ-H: 0x20 and 0x41 are 1-byte codes in its Shift-JIS
        // codespace. Both CIDs are unlisted in /W → /DW 1000 → 24pt advance.
        var pdf = BuildType0Pdf("/90ms-RKSJ-H", "10 Tw <2041> Tj", ordering: "Japan1");
        var letters = Extract(pdf);

        letters.Should().HaveCount(2, "bytes 20 41 are two single-byte Shift-JIS codes");
        letters[1].StartX.Should().BeApproximately(PenX + Size + 10, 0.01,
            "single-byte code 32 fires word spacing per §9.3.3: 24pt width + 10pt Tw");
    }

    // ---------- position vector / glyph bounding box ----------

    [Fact] // §9.7.4.3: with no /W2, v = (w0/2, 880) — the glyph cell is
           // horizontally CENTERED on the vertical origin and hangs below it
    public void IdentityV_GlyphRectangle_CenteredOnVerticalOrigin()
    {
        var pdf = BuildType0Pdf("/Identity-V", "<0041> Tj",
            cidExtras: "/W[65[684]]");
        var letters = Extract(pdf);

        var box = letters[0].GlyphRectangle;
        var w0 = 684.0 / 1000 * Size;                    // 16.416pt
        box.Left.Should().BeApproximately(PenX - w0 / 2, 0.01,
            "default vx = w0/2 centers the glyph on the pen X");
        box.Right.Should().BeApproximately(PenX + w0 / 2, 0.01);
        box.Top.Should().BeApproximately(PenY, 0.01, "the vertical origin is the cell top");
        box.Bottom.Should().BeApproximately(PenY - Size, 0.01, "the cell spans w1y downward");
    }

    // ---------- embedded CMap stream /WMode ----------

    [Fact] // an embedded CMap stream's own /WMode 1 must trigger vertical
           // writing — Identity-V is not the only vertical signal (§9.7.5.2)
    public void EmbeddedCMapStream_WMode1_TriggersVerticalWriting()
    {
        var cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\nbegincmap\n" +
            "/CMapName /Test-V def\n/CMapType 1 def\n/WMode 1 def\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";
        var pdf = BuildType0Pdf("6 0 R", "<00410042> Tj", encodingStream: cmap);
        var letters = Extract(pdf);

        letters.Select(l => l.Value).Should().Equal("A", "B");
        letters[1].StartX.Should().BeApproximately(PenX, 0.01);
        letters[1].StartY.Should().BeApproximately(PenY - Size, 0.01,
            "/WMode 1 in the embedded CMap stream means vertical writing");
    }

    // ---------- redaction round-trip (the security point of the slice) ----------

    [Fact]
    public void IdentityV_RedactText_RemovesTargetAndKeepsRest()
    {
        // "SECRETKEEP" as 2-byte Identity-V codes, one glyph per em, running
        // down the page. /ToUnicode /Identity-H (#716) pins the decode to
        // UTF-16BE for BOTH the page letters and the content-stream parser —
        // if the two disagreed, glyph-level matching would degrade to the
        // whole-operator fail-safe and wipe the kept text too.
        var codes = string.Concat("SECRETKEEP".Select(c => ((int)c).ToString("X4")));
        var pdf = BuildType0Pdf("/Identity-V", $"<{codes}> Tj",
            fontExtras: "/ToUnicode/Identity-H");
        var input = Path.Combine(Path.GetTempPath(), $"vert-red-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"vert-red-out-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, pdf);
            using (var doc = PdfDocument.Open(input))
            {
                doc.RedactText("SECRET").Should().Be(1,
                    "glyph-level removal must match exactly one occurrence in the vertical run");
                doc.Save(output);
            }

            using var redacted = PdfDocument.Open(output);
            var text = new TextExtractor(redacted.GetPage(1)).ExtractText();
            text.Should().NotContain("SECRET", "the redacted glyphs must be gone");
            text.Should().Contain("KEEP", "adjacent kept glyphs must survive");

            var kept = new TextExtractor(redacted.GetPage(1)).ExtractLetters()
                .Where(l => "KEEP".Contains(l.Value, StringComparison.Ordinal))
                .ToList();
            kept.Should().HaveCount(4);
            kept.Select(l => l.StartY).Should().BeInDescendingOrder(
                "kept glyphs must still lay out as a downward vertical run");

            // Carrier-agnostic saved-bytes check (CLAUDE.md): the UTF-16BE
            // haystack in particular is exactly the Identity-H/V byte
            // encoding of the codes, so surviving glyph codes cannot hide.
            var saved = File.ReadAllBytes(output);
            var haystack = Encoding.ASCII.GetString(saved)
                + Encoding.BigEndianUnicode.GetString(saved)
                + Encoding.UTF8.GetString(saved);
            haystack.Should().NotContain("SECRET");
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    // ---------- helpers ----------

    private static System.Collections.Generic.IReadOnlyList<Letter> Extract(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return new TextExtractor(doc.GetPage(1)).ExtractLetters();
    }

    /// <summary>
    /// Single-page PDF with a non-embedded Type0 font. <paramref name="encoding"/>
    /// is written verbatim after <c>/Encoding</c> (a name like
    /// <c>/Identity-V</c> or the reference <c>6 0 R</c> when
    /// <paramref name="encodingStream"/> supplies an embedded CMap).
    /// <paramref name="ops"/> is emitted inside BT/ET after
    /// <c>/F0 24 Tf 72 700 Td</c>. <paramref name="cidExtras"/> lands in the
    /// descendant CIDFont dictionary (e.g. <c>/W2[…]</c>, <c>/DW2[…]</c>).
    /// </summary>
    private static byte[] BuildType0Pdf(
        string encoding, string ops,
        string cidExtras = "", string ordering = "Identity",
        string? encodingStream = null, string fontExtras = "")
    {
        var sb = new StringBuilder();
        var offsets = new long[7];
        void Obj(int n) => offsets[n] = sb.Length;

        var content = $"BT /F0 24 Tf 72 700 Td {ops} ET";

        sb.Append("%PDF-1.7\n");
        Obj(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Obj(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Obj(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                          "/Resources<</Font<</F0 4 0 R>>>>/Contents 5 0 R>> endobj\n");
        Obj(4); sb.Append("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
                          $"/Encoding {encoding}{fontExtras}" +
                          "/DescendantFonts[<</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
                          $"/CIDSystemInfo<</Registry(Adobe)/Ordering({ordering})/Supplement 0>>" +
                          $"/DW 1000 {cidExtras}>>]>> endobj\n");
        Obj(5); sb.Append($"5 0 obj <</Length {content.Length}>>\nstream\n{content}\nendstream endobj\n");
        if (encodingStream != null)
        {
            Obj(6);
            sb.Append($"6 0 obj <</Type/CMap/CMapName/Test-V/Length {encodingStream.Length}>>" +
                      $"\nstream\n{encodingStream}\nendstream endobj\n");
        }

        var xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++)
        {
            sb.Append(offsets[i] == 0
                ? "0000000000 65535 f \n"
                : offsets[i].ToString("D10") + " 00000 n \n");
        }
        sb.Append($"trailer <</Size 7/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
