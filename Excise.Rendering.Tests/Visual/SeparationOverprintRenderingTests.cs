using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Spec-driven tests for Separation / DeviceN overprint (#634, ISO 32000-1
/// §8.6.7). A Separation or DeviceN colour is tint-transformed to its
/// DeviceCMYK alternate; overprint then leaves the process colorants the
/// transform outputs as zero UNCHANGED in the backdrop. Unlike DeviceCMYK,
/// this rule applies whenever /OP or /op is set REGARDLESS of /OPM — the
/// colorants a Separation/DeviceN space does not name are always left alone.
/// (Empirically corroborated by Ghostscript's overprint simulation in
/// Differential/SeparationOverprintDifferentialTests, which renders /OPM 0 and
/// /OPM 1 identically for a Separation fill.)
///
/// The discriminating fixture paints a spot colour whose tint transform maps
/// to yellow (0 0 1 0 — C, M and K all zero) over a cyan backdrop (1 0 0 0 k):
///   - overprint ON  → overlap keeps the cyan colorant → green;
///   - overprint OFF → overlap knocks cyan out → plain yellow.
/// Every expectation is RELATIVE (a preserved colorant reads green, a knockout
/// reads yellow; the two OPM variants must match each other) so the assertions
/// do not depend on the CMYK→RGB preview formula.
/// </summary>
public sealed class SeparationOverprintRenderingTests
{
    // Device coordinates at 72 DPI on the 300x300 page (deviceY = 300 - pdfY).
    private static readonly (int X, int Y) Overlap = (100, 200);
    private static readonly (int X, int Y) BackgroundOnly = (40, 260);
    private static readonly (int X, int Y) White = (250, 50);

    [Fact]
    public void SeparationOverprint_PreservesUnderlyingColorant()
    {
        using var bitmap = RenderVariant(SepOverprintFill);
        var overlap = Probe(bitmap, Overlap);
        var background = Probe(bitmap, BackgroundOnly);
        var white = Probe(bitmap, White);

        overlap.Red.Should().BeLessThan(100,
            "the cyan colorant under a zero-C Separation overprint fill must survive (knockout would leave red ≈ 255)");
        overlap.Green.Should().BeGreaterThan(100, "cyan + yellow reads green");
        background.Red.Should().BeLessThan(60, "the cyan-only region is unaffected");
        white.Red.Should().Be(255, "unpainted background stays white");
        white.Green.Should().Be(255);
        white.Blue.Should().Be(255);
    }

    [Fact]
    public void SeparationKnockout_WithoutOverprint_KnocksTheColorantOut()
    {
        var knockout = ProbeOverlap(RenderVariant(SepPlainFill));
        var overprint = ProbeOverlap(RenderVariant(SepOverprintFill));

        knockout.Red.Should().BeGreaterThan(200,
            "no overprint: the spot's yellow knocks the cyan out");
        Math.Abs(knockout.Red - overprint.Red).Should().BeGreaterThan(100,
            "overprint must visibly differ from the knockout it replaces");
    }

    [Fact]
    public void SeparationOverprint_AppliesRegardlessOfOverprintMode()
    {
        // The defining Separation/DeviceN property: overprint does NOT depend
        // on /OPM. OPM 0 must overprint exactly like OPM 1 (both keep cyan),
        // whereas a DeviceCMYK fill under OPM 0 would knock out.
        var opm1 = ProbeOverlap(RenderVariant(SepOverprintFill));
        var opm0 = ProbeOverlap(RenderVariant(SepOverprintOpm0Fill));

        AssertSameColor(opm0, opm1, 2,
            "a Separation overprint fill must ignore /OPM (unnamed colorants stay put either way)");
        opm0.Red.Should().BeLessThan(100, "OPM 0 must still preserve the cyan colorant for a Separation");
    }

    [Fact]
    public void DeviceNOverprint_PreservesUnderlyingColorant_LikeSeparation()
    {
        var devn = ProbeOverlap(RenderVariant(DevNOverprintFill));
        var sep = ProbeOverlap(RenderVariant(SepOverprintFill));

        AssertSameColor(devn, sep, 2,
            "a DeviceN colour with the same tint transform must overprint like the Separation");
    }

    [Fact]
    public void SeparationStrokeOverprint_PreservesUnderlyingColorant()
    {
        using var bitmap = RenderVariant(SepOverprintStroke);
        var onStroke = Probe(bitmap, Overlap); // the 20pt stroke passes through pdf (100,100)

        onStroke.Red.Should().BeLessThan(100,
            "a zero-C Separation overprint STROKE must keep the cyan colorant underneath");
        onStroke.Green.Should().BeGreaterThan(100);
    }

    // ------------------------------------------------------------------

    private const string Background = "1 0 0 0 k 20 20 160 160 re f\n";
    private const string SepOverprintFill = Background + "/GSop gs /CSsep cs 1 scn 60 60 80 80 re f\n";
    private const string SepOverprintOpm0Fill = Background + "/GSop0 gs /CSsep cs 1 scn 60 60 80 80 re f\n";
    private const string SepPlainFill = Background + "/CSsep cs 1 scn 60 60 80 80 re f\n";
    private const string DevNOverprintFill = Background + "/GSop gs /CSdevn cs 1 scn 60 60 80 80 re f\n";
    private const string SepOverprintStroke = Background + "/GSop gs /CSsep CS 1 SCN 20 w 60 100 m 140 100 l S\n";

    // A Separation "MyYellow" and a single-colorant DeviceN, both with a
    // FunctionType 2 tint transform mapping tint 1 -> DeviceCMYK (0 0 1 0).
    private const string Resources =
        "/ExtGState << " +
        "/GSop << /Type /ExtGState /OP true /op true /OPM 1 >> " +
        "/GSop0 << /Type /ExtGState /OP true /op true /OPM 0 >> " +
        ">> " +
        "/ColorSpace << " +
        "/CSsep [ /Separation /MyYellow /DeviceCMYK " +
        "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 1 0] /N 1 >> ] " +
        "/CSdevn [ /DeviceN [ /MySpot ] /DeviceCMYK " +
        "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 1 0] /N 1 >> ] " +
        ">>";

    private static SKBitmap RenderVariant(string content)
    {
        using var doc = PdfDocument.Open(
            OverprintRenderingTests.BuildSinglePagePdf(content, Resources, deviceCmykGroup: false));
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
}
