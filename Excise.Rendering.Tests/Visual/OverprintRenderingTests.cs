using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Spec-driven tests for DeviceCMYK overprint (#634, ISO 32000-1 §8.6.7):
/// with /OP (strokes) or /op (fills) set and /OPM 1 ("nonzero overprint
/// mode"), a DeviceCMYK paint whose component is exactly zero must leave
/// that colorant of the backdrop unchanged instead of knocking it out to 0.
///
/// The discriminating fixture paints a yellow square (0 0 1 0 k — C, M and
/// K all zero) over a cyan square (1 0 0 0 k):
///   - overprint ON + OPM 1  → overlap keeps the cyan colorant → green;
///   - overprint OFF, or OPM 0 → overlap knocks cyan out → plain yellow.
/// Every expectation here is RELATIVE (overprint output must equal an
/// explicitly painted merged colour, knockout output must equal a plain
/// paint) so the assertions do not depend on the CMYK→RGB preview formula.
/// The same fixture is corroborated against an independent oracle
/// (Ghostscript -dOverprint=/simulate) in
/// Differential/OverprintDifferentialTests.
/// </summary>
public sealed class OverprintRenderingTests
{
    // Device coordinates at 72 DPI on the 300x300 page (deviceY = 300 - pdfY):
    // overlap of the two squares, a background-only point, and untouched white.
    private static readonly (int X, int Y) Overlap = (100, 200);
    private static readonly (int X, int Y) BackgroundOnly = (40, 260);
    private static readonly (int X, int Y) White = (250, 50);

    // ---------------------------------------------------------------
    // Inside a DeviceCMYK transparency group the renderer keeps a true
    // per-pixel CMYK backdrop, so OPM 1 overprint must be EXACT: painting
    // 0 0 1 0 with overprint over 1 0 0 0 must equal painting 1 0 1 0.
    // ---------------------------------------------------------------

    [Fact]
    public void GroupPage_Opm1Overprint_EqualsExplicitlyPaintedMergedColor()
    {
        var overprint = ProbeOverlap(RenderVariant(OverprintFill, deviceCmykGroup: true));
        var merged = ProbeOverlap(RenderVariant(ExplicitMergedFill, deviceCmykGroup: true));
        var knockout = ProbeOverlap(RenderVariant(PlainFill, deviceCmykGroup: true));

        AssertSameColor(overprint, merged, 2,
            "OPM 1 zero components must take the group backdrop's colorants exactly");
        Math.Abs(overprint.Red - knockout.Red).Should().BeGreaterThan(100,
            "the overprinted overlap must NOT be the knocked-out plain yellow");
    }

    [Fact]
    public void GroupPage_Opm0Overprint_KnocksOutLikePlainPaint()
    {
        var opm0 = ProbeOverlap(RenderVariant(Opm0Fill, deviceCmykGroup: true));
        var plain = ProbeOverlap(RenderVariant(PlainFill, deviceCmykGroup: true));

        // OPM 0 paints every DeviceCMYK colorant, zeros included — identical
        // to no overprint. Over-applying overprint here is a regression
        // (the Ghent GWG011 left patch is exactly this trap).
        AssertSameColor(opm0, plain, 2, "OPM 0 must paint zero components");
    }

    // ---------------------------------------------------------------
    // Outside any transparency group the page only has RGB pixels; the
    // backdrop colorants are estimated by inverting the preview conversion,
    // so the result is approximate — but the defining property (the
    // underlying colorant SURVIVES rather than being knocked out) must hold.
    // ---------------------------------------------------------------

    [Fact]
    public void PlainPage_Opm1Overprint_PreservesUnderlyingColorant()
    {
        using var bitmap = RenderVariant(OverprintFill, deviceCmykGroup: false);
        var overlap = Probe(bitmap, Overlap);
        var background = Probe(bitmap, BackgroundOnly);
        var white = Probe(bitmap, White);

        // Knocked-out yellow would be (255, ~241, 0). The preserved cyan
        // colorant must keep red strongly suppressed and green dominant.
        overlap.Red.Should().BeLessThan(100,
            "the cyan colorant under a zero-C overprint fill must survive (knockout would leave red ≈ 255)");
        overlap.Green.Should().BeGreaterThan(100, "cyan + yellow reads green");
        background.Red.Should().BeLessThan(60, "the cyan-only region is unaffected");
        white.Red.Should().Be(255, "unpainted background stays white");
        white.Green.Should().Be(255);
        white.Blue.Should().Be(255);
    }

    [Fact]
    public void PlainPage_NoOverprintAndOpm0_AreByteIdenticalKnockouts()
    {
        var plain = ProbeOverlap(RenderVariant(PlainFill, deviceCmykGroup: false));
        var opm0 = ProbeOverlap(RenderVariant(Opm0Fill, deviceCmykGroup: false));

        plain.Red.Should().BeGreaterThan(200, "no overprint: yellow knocks the cyan out");
        AssertSameColor(opm0, plain, 1,
            "OPM 0 with overprint on must not change DeviceCMYK painting at all");
    }

    [Fact]
    public void ExtGState_OpWithoutLowercaseOp_SetsFillOverprintToo()
    {
        // ISO 32000-1 Table 58: a gs dict that has /OP but no /op sets op to
        // OP's value — the fill below must overprint even though only /OP
        // appears in the dictionary.
        var opOnly = ProbeOverlap(RenderVariant(OpOnlyFill, deviceCmykGroup: false));
        var overprint = ProbeOverlap(RenderVariant(OverprintFill, deviceCmykGroup: false));

        AssertSameColor(opOnly, overprint, 1, "/OP without /op must set the fill overprint flag");
    }

    [Fact]
    public void ExtGState_OpmPersistsAcrossGsDictionariesThatOmitIt()
    {
        // /OPM is sticky like every ExtGState entry: GWG011 sets /OPM 1 in one
        // gs and toggles /OP in later dicts that omit /OPM. First gs sets only
        // OPM 1 (flags stay false), second sets only the flags.
        var persisted = ProbeOverlap(RenderVariant(PersistedOpmFill, deviceCmykGroup: false));
        var overprint = ProbeOverlap(RenderVariant(OverprintFill, deviceCmykGroup: false));

        AssertSameColor(persisted, overprint, 1, "OPM must persist across gs dictionaries that omit it");
    }

    [Fact]
    public void PlainPage_StrokeOverprint_PreservesUnderlyingColorant()
    {
        using var bitmap = RenderVariant(OverprintStroke, deviceCmykGroup: false);
        var onStroke = Probe(bitmap, Overlap); // the 20pt stroke passes through (100,100) pdf

        onStroke.Red.Should().BeLessThan(100,
            "a zero-C overprint STROKE must keep the cyan colorant underneath");
        onStroke.Green.Should().BeGreaterThan(100);
    }

    // ------------------------------------------------------------------

    private const string Background = "1 0 0 0 k 20 20 160 160 re f\n";
    private const string OverprintFill = Background + "/GSop gs 0 0 1 0 k 60 60 80 80 re f\n";
    private const string Opm0Fill = Background + "/GSop0 gs 0 0 1 0 k 60 60 80 80 re f\n";
    private const string PlainFill = Background + "0 0 1 0 k 60 60 80 80 re f\n";
    private const string ExplicitMergedFill = Background + "1 0 1 0 k 60 60 80 80 re f\n";
    private const string OpOnlyFill = Background + "/GSOPonly gs 0 0 1 0 k 60 60 80 80 re f\n";
    private const string PersistedOpmFill = Background + "/GSopm gs /GSflags gs 0 0 1 0 k 60 60 80 80 re f\n";
    private const string OverprintStroke = Background + "/GSop gs 0 0 1 0 K 20 w 60 100 m 140 100 l S\n";

    private const string Resources =
        "/ExtGState << " +
        "/GSop << /Type /ExtGState /OP true /op true /OPM 1 >> " +
        "/GSop0 << /Type /ExtGState /OP true /op true /OPM 0 >> " +
        "/GSOPonly << /Type /ExtGState /OP true /OPM 1 >> " +
        "/GSopm << /Type /ExtGState /OPM 1 >> " +
        "/GSflags << /Type /ExtGState /OP true /op true >> " +
        ">>";

    private static SKBitmap RenderVariant(string content, bool deviceCmykGroup)
    {
        using var doc = PdfDocument.Open(BuildSinglePagePdf(content, Resources, deviceCmykGroup));
        return new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });
    }

    private static SKColor ProbeOverlap(SKBitmap bitmap)
    {
        using (bitmap)
        {
            return Probe(bitmap, Overlap);
        }
    }

    private static SKColor Probe(SKBitmap bitmap, (int X, int Y) point)
        => bitmap.GetPixel(point.X, point.Y);

    private static void AssertSameColor(SKColor actual, SKColor expected, int tolerance, string because)
    {
        var delta = Math.Max(
            Math.Abs(actual.Red - expected.Red),
            Math.Max(Math.Abs(actual.Green - expected.Green), Math.Abs(actual.Blue - expected.Blue)));
        delta.Should().BeLessThanOrEqualTo(tolerance,
            $"{because} (expected {expected}, was {actual})");
    }

    internal static byte[] BuildSinglePagePdf(string content, string resources, bool deviceCmykGroup)
    {
        var sb = new StringBuilder();
        var offsets = new long[5];
        sb.Append("%PDF-1.7\n");

        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R\n");
        if (deviceCmykGroup)
            sb.Append("   /Group << /S /Transparency /CS /DeviceCMYK >>\n");
        sb.Append($"   /Resources << {resources} >>\n>>\nendobj\n");

        offsets[4] = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        var xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        for (var i = 1; i <= 4; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size 5 >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
