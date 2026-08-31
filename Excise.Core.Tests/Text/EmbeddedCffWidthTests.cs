using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Tests.Fixtures;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #1148 — a simple font with an embedded CFF program (/FontFile3) but NO
/// /Widths array. The width cascade used to fall through to the standard-14
/// guess (the /FontFile2 half was wired by #1102); now it reads the real
/// advance from the CFF Type2 charstring. This is the other half of #1102 and
/// the wiring the #1104 advance-parity ruler judges.
///
/// <para><b>Ground truth is a tool that is not the width rung.</b> The expected
/// advance is <see cref="CffParser.CffFontInfo.AdvanceWidth"/> read straight off
/// the parsed program — the accessor #1148 landed first, itself pinned against
/// fontTools' T2WidthExtractor in <c>CffAdvanceWidthTests</c>. So this test
/// proves the WIRING (code→gid→advance through <c>GetCharWidth</c>), not the
/// charstring interpreter, which is verified elsewhere.</para>
/// </summary>
public class EmbeddedCffWidthTests
{
    /// <summary>
    /// One page, one line of text in a /Type1 font whose /FontDescriptor embeds
    /// a raw CFF via /FontFile3, with NO /Widths on the font dict. /BaseFont is a
    /// real standard-14 name (/Helvetica) so that, with the CFF rung disabled,
    /// the cascade falls to Helvetica's AFM metric — a DIFFERENT number from the
    /// monospaced CFF advance, which is what makes the rung load-bearing.
    /// </summary>
    private static byte[] BuildPdf(byte[] cff)
    {
        var content = "BT /F1 100 Tf 50 700 Td (A) Tj ET\n";
        var sb = new StringBuilder();
        var offs = new System.Collections.Generic.List<int>();
        void Obj(string b) { offs.Add(Encoding.Latin1.GetByteCount(sb.ToString())); sb.Append(b); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream\nendobj\n");
        // Font: NO /Widths. WinAnsi so 'A' maps by ASCII. /BaseFont is a real
        // standard-14 name so the disable-the-rung path has a distinct metric.
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
            "/FirstChar 32 /LastChar 255 /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /FontDescriptor /FontName /Helvetica /Flags 32 " +
            "/FontBBox [-200 -300 1000 900] /ItalicAngle 0 /Ascent 900 /Descent -300 " +
            "/CapHeight 700 /StemV 80 /FontFile3 7 0 R >>\nendobj\n");
        // The embedded program (raw CFF). /Subtype /Type1C per PDF §9.9.
        var head = $"7 0 obj\n<< /Length {cff.Length} /Subtype /Type1C >>\nstream\n";
        var pre = Encoding.Latin1.GetByteCount(sb.ToString());
        offs.Add(pre);
        var bytes = new System.Collections.Generic.List<byte>();
        bytes.AddRange(Encoding.Latin1.GetBytes(head));
        bytes.AddRange(cff);
        bytes.AddRange(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));
        sb.Append(Encoding.Latin1.GetString(bytes.ToArray()));

        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void SimpleCffWithoutWidths_UsesTheEmbeddedCharstringAdvance_NotStandard14()
    {
        var cff = TestFontFixtures.LoadInconsolataCffBytes();
        var info = CffParser.Parse(cff);
        info.Should().NotBeNull("Inconsolata.cff is a valid raw CFF");

        // Ground truth from the font itself: 'A' advance in text-space points at
        // 100pt. Inconsolata is monospaced at 500 font units / 1000 em.
        info!.GlyphNameToIndex.TryGetValue("A", out var gid).Should().BeTrue("Inconsolata contains 'A'");
        var expectedA = info.AdvanceWidth(gid) * 100.0 / info.UnitsPerEm;
        expectedA.Should().BeApproximately(50.0, 0.001, "Inconsolata 'A' is 500/1000 em == 50pt at 100pt");

        // Load-bearing discriminator: Helvetica 'A' (the standard-14 fall-through
        // when the rung is disabled) is 667/1000 em == 66.7pt at 100pt. The two
        // are far apart, so a test that lands on expectedA can only have gone
        // through the CFF rung.
        var standard14A = StandardFontMetrics.GetWidthOrFallback("Helvetica", 'A') * 100.0 / 1000.0;
        standard14A.Should().BeApproximately(66.7, 0.1, "Helvetica AFM 'A' is 667");
        expectedA.Should().NotBeApproximately(standard14A, 5.0,
            "the fixture only proves something if the CFF advance differs from the standard-14 guess");

        using var doc = PdfDocument.Open(BuildPdf(cff));
        var letters = doc.GetPage(1).Letters.Where(l => l.Value == "A").ToList();
        letters.Should().NotBeEmpty("the 'A' must extract");

        letters[0].Width.Should().BeApproximately(expectedA, 1.0,
            "the advance must come from the embedded /FontFile3 charstring (#1148), not the " +
            "standard-14 Helvetica metric the cascade would use with the rung removed");
    }

    /// <summary>
    /// Mutation guard, kept as a live assertion: the width the rung produces
    /// (50pt) is NOT the width the disabled cascade would produce (Helvetica's
    /// 66.7pt). If a future edit removed the CFF rung, the extracted 'A' would
    /// snap to the standard-14 value and this — the assertion above — would fail.
    /// Pinned separately so the discriminating numbers are documented on their
    /// own.
    /// </summary>
    [Fact]
    public void CffAdvance_And_Standard14Guess_AreDistinguishable()
    {
        var info = CffParser.Parse(TestFontFixtures.LoadInconsolataCffBytes())!;
        info.GlyphNameToIndex.TryGetValue("A", out var gid);
        var cffWidth = info.AdvanceWidth(gid) * 100.0 / info.UnitsPerEm;               // 50.0
        var std14 = StandardFontMetrics.GetWidthOrFallback("Helvetica", 'A') * 100.0 / 1000.0; // 66.7

        System.Math.Abs(cffWidth - std14).Should().BeGreaterThan(10.0,
            "the mutation-proof depends on these two widths being far apart");
    }
}
