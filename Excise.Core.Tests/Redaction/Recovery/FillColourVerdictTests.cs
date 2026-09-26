using System.Globalization;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1830 — what every colour verdict in the redaction audit decides for one fill,
/// side by side, so a drift between detectors is a visible row rather than a
/// defect found on a real document.
///
/// <para>Columns, each driven through its detector's own entry point:
/// <b>A</b> <see cref="HiddenTextDetector"/> reports text under a later bar of
/// this colour; <b>C</b> it reports readable text drawn ON a text-sized bar of
/// this colour as a visible failed redaction (its darkness gate);
/// <b>D</b> <c>DarkFilledBoxes</c> returns the bar; <b>M</b>
/// <see cref="RedactionMarkDetector"/> counts it as a mark; <b>V</b>
/// <see cref="CoveredContentRecovery"/> counts it as covering the vector path
/// under it.</para>
///
/// <para>The columns differ only by threshold: one <see cref="FillColour"/>
/// supplies the colour, its luminance and its CMYK conversion. CMYK rows follow
/// what mutool paints, not <c>(1-c)(1-k)</c>: 60% K renders at luminance 0.51,
/// so it is not a mark.</para>
/// </summary>
public class FillColourVerdictTests
{
    private static readonly string[] Fills =
    {
        "",                                   // no colour operator: §8.4.1 Table 52 initial black
        "0 g", "0.3 g", "0.4 g", "0.5 g", "0.94 g", "0.96 g", "1 g",
        "1 0 0 rg", "0 1 0 rg", "0 0 1 rg", "1 0 1 rg", "0 1 1 rg", "1 1 0 rg",
        "0 0.5 0 rg", "0 0.7 0 rg",
        "0 0 0 1 k",                          // pure K
        "0.6 0.4 0.4 1 k",                    // rich black
        "0 0 0 0.5 k", "0 0 0 0.6 k", "0 0 0 0.7 k", "0 0 0 0.85 k",
        "1 0 0 0 k", "0 1 0 0 k", "0 0 1 0 k",
        "0 0 0 0 k", "0 0 0 0.03 k",
        "/DeviceCMYK cs 0 0 0 0.6 scn",
    };

    [Fact]
    public void EveryDetectorsVerdict_ForEachFill()
    {
        var expected = string.Join('\n', new[]
        {
            "                             | A C D M V",
            "(none)                       | Y Y Y Y Y",
            "0 g                          | Y Y Y Y Y",
            "0.3 g                        | Y Y - Y Y",
            "0.4 g                        | Y - - Y Y",
            "0.5 g                        | Y - - - -",
            "0.94 g                       | Y - - - -",
            "0.96 g                       | - - - - -",
            "1 g                          | - - - - -",
            "1 0 0 rg                     | Y Y - Y Y",
            "0 1 0 rg                     | Y - - - -",
            "0 0 1 rg                     | Y Y - Y Y",
            "1 0 1 rg                     | Y - - Y Y",
            "0 1 1 rg                     | Y - - - -",
            "1 1 0 rg                     | Y - - - -",
            "0 0.5 0 rg                   | Y Y - Y Y",
            "0 0.7 0 rg                   | Y - - Y Y",
            "0 0 0 1 k                    | Y Y Y Y Y",
            "0.6 0.4 0.4 1 k              | Y Y Y Y Y",
            "0 0 0 0.5 k                  | Y - - - -",
            "0 0 0 0.6 k                  | Y - - - -",
            "0 0 0 0.7 k                  | Y - - Y Y",
            "0 0 0 0.85 k                 | Y Y - Y Y",
            "1 0 0 0 k                    | Y - - - -",
            "0 1 0 0 k                    | Y Y - Y Y",
            "0 0 1 0 k                    | Y - - - -",
            "0 0 0 0 k                    | - - - - -",
            "0 0 0 0.03 k                 | - - - - -",
            "/DeviceCMYK cs 0 0 0 0.6 scn | Y - - - -",
        });

        var actual = new StringBuilder("                             | A C D M V");
        foreach (var fill in Fills)
            actual.Append('\n').Append(CultureInfo.InvariantCulture, $"{(fill.Length == 0 ? "(none)" : fill),-28} | {Verdicts(fill)}");

        actual.ToString().Should().Be(expected);
    }

    // Every "black filled rectangle" pin downstream uses 0 g or 0 0 0 rg; this holds the name for CMYK black.
    [Theory]
    [InlineData(0, 0, 0, 1)]         // pure K previews as (0.14, 0.12, 0.13)
    [InlineData(0.6, 0.4, 0.4, 1)]   // rich black
    public void CmykBlack_IsDescribedAsBlack(double c, double m, double y, double k)
        => FillColour.FromCmyk(c, m, y, k).Describe().Should().Be("black");

    private static string Verdicts(string fill)
    {
        const string Secret = "SECRET";
        // Text, a stroked path for CoveredContentRecovery to find, then the bar over both.
        using var under = PdfDocument.Open(RecoveryFixtureBuilder.Build(
            $"BT /F1 14 Tf 72 700 Td ({Secret}) Tj ET\n" +
            "72 702 m 130 710 l S\n" +
            $"q {fill} 68 696 72 18 re f Q\n"));
        var page = under.GetPage(1);

        var hides = HiddenTextDetector.ScanPage(page).Any(h => h.Text.Contains(Secret));
        var darkBox = HiddenTextDetector.DarkFilledBoxes(page).Count > 0;
        var mark = RedactionMarkDetector.DetectPage(page, 1).Any(m => m.Kind == RedactionMarkKind.FilledBox);
        var covers = CoveredContentRecovery.Scan(under).Any(c => c.Kind == "vector");

        // Pairing C judges darkness only once contrast is high, so draw the text in
        // whichever of white and black reads on this bar.
        var visibleFailed = new[] { "1 g", "0 g" }.Any(text =>
        {
            using var on = PdfDocument.Open(RecoveryFixtureBuilder.Build(
                $"q {fill} 68 696 72 18 re f Q\n" +
                $"BT {text} /F1 14 Tf 72 700 Td ({Secret}) Tj ET\n"));
            return HiddenTextDetector.ScanPage(on.GetPage(1), includeVisibleFailedRedactions: true)
                .Any(h => h.HiddenBy.Contains("redaction-shaped"));
        });

        static char F(bool b) => b ? 'Y' : '-';
        return $"{F(hides)} {F(visibleFailed)} {F(darkBox)} {F(mark)} {F(covers)}";
    }
}
