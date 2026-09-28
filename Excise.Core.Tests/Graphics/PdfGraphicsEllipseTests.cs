using System.Globalization;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// #1910: DrawEllipse / DrawCircle / DrawArc compose MoveTo/CurveTo/ClosePath with the
/// existing paint methods. Geometry is checked against the analytic ellipse by sampling
/// the emitted Bezier segments, not by comparing to another approximation.
/// </summary>
public class PdfGraphicsEllipseTests
{
    private static string Emit(Action<PdfGraphics> draw)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 400);
        using var g = page.GetGraphics();
        draw(g);
        return g.GetOperators();
    }

    private static double[] Operands(string line) =>
        line.Split(' ')[..^1].Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    private static (double X, double Y) StartPoint(string ops) =>
        ops.Split('\n').Where(l => l.EndsWith(" m")).Select(Operands).Select(o => (o[0], o[1])).Single();

    private static List<double[]> Curves(string ops) =>
        ops.Split('\n').Where(l => l.EndsWith(" c")).Select(Operands).ToList();

    /// <summary>Largest |distance from centre − r| / r over finely sampled points of every segment.</summary>
    private static double MaxRelativeRadialError(string ops, double cx, double cy, double r)
    {
        var (px, py) = StartPoint(ops);
        double worst = 0;
        foreach (var c in Curves(ops))
        {
            for (int i = 0; i <= 200; i++)
            {
                double t = i / 200.0, u = 1 - t;
                double x = u * u * u * px + 3 * u * u * t * c[0] + 3 * u * t * t * c[2] + t * t * t * c[4];
                double y = u * u * u * py + 3 * u * u * t * c[1] + 3 * u * t * t * c[3] + t * t * t * c[5];
                worst = Math.Max(worst, Math.Abs(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r) / r);
            }
            (px, py) = (c[4], c[5]);
        }
        return worst;
    }

    [Fact]
    public void DrawCircle_StaysWithinAFractionOfAPercentOfTheTrueCircle()
    {
        var ops = Emit(g => g.DrawCircle(200, 200, 100, null, PdfPen.Black));

        Curves(ops).Should().HaveCount(4);
        // The quarter-arc cubic's own error is 2.7e-4 r; a wrong kappa (0.5, or 0.55) exceeds 5e-4.
        MaxRelativeRadialError(ops, 200, 200, 100).Should().BeLessThan(5e-4);
    }

    [Fact]
    public void DrawArc_UsesTheControlPointsOfItsOwnPieceAngle()
    {
        // 100 degrees splits into two 50-degree pieces; the quarter-arc 0.5523 would overshoot them.
        var ops = Emit(g => g.DrawArc(100, 100, 200, 200, 10, 100, PdfPen.Black));

        Curves(ops).Should().HaveCount(2);
        MaxRelativeRadialError(ops, 200, 200, 100).Should().BeLessThan(5e-4);
    }

    [Fact]
    public void DrawArc_PositiveSweepRunsCounterClockwiseFromPlusX()
    {
        var ops = Emit(g => g.DrawArc(100, 100, 200, 200, 0, 90, PdfPen.Black));

        StartPoint(ops).Should().Be((300, 200));
        var end = Curves(ops).Single();
        (end[4], end[5]).Should().Be((200, 300), "0 to 90 degrees ends straight above the centre in y-up space");
    }

    [Fact]
    public void DrawArc_NegativeSweepRunsClockwise()
    {
        var ops = Emit(g => g.DrawArc(100, 100, 200, 200, 0, -90, PdfPen.Black));

        var end = Curves(ops).Single();
        (end[4], end[5]).Should().Be((200, 100));
    }

    [Theory]
    [InlineData(90, 1)]
    [InlineData(91, 2)]
    [InlineData(270, 3)]
    [InlineData(720, 4)]
    public void DrawArc_SplitsTheSweepIntoPiecesOfAtMost90Degrees(double sweep, int pieces) =>
        Curves(Emit(g => g.DrawArc(0, 0, 100, 100, 0, sweep, PdfPen.Black))).Should().HaveCount(pieces);

    [Fact]
    public void DrawEllipse_ScalesEachAxisByItsOwnRadius()
    {
        var ops = Emit(g => g.DrawEllipse(0, 0, 200, 100, PdfBrush.Red, null));

        StartPoint(ops).Should().Be((200, 50));
        Curves(ops).Select(c => (c[4], c[5])).Should().Equal((100, 100), (0, 50), (100, 0), (200, 50));
    }

    [Fact]
    public void DrawEllipse_WritesStateFirstThenAClosedWellFormedPath()
    {
        var ops = Emit(g => g.DrawEllipse(0, 0, 200, 100, PdfBrush.Red, PdfPen.Black));

        ops.Should().StartWith("1 0 0 rg\n0 G\n1 w\n200 50 m\n");
        ops.Should().EndWith("h\nB\n");
        PdfGraphicsOperatorOrderTests.AssertPathObjectsWellFormed(ops);
    }

    [Fact]
    public void DrawEllipse_FillOnlyFillsAndStrokeOnlyStrokes()
    {
        Emit(g => g.DrawCircle(50, 50, 10, PdfBrush.Red, null)).Should().EndWith("h\nf\n");
        Emit(g => g.DrawCircle(50, 50, 10, null, PdfPen.Black)).Should().EndWith("h\nS\n");
    }

    [Fact]
    public void DrawEllipse_WithNeitherFillNorStroke_WritesNothing() =>
        Emit(g => g.DrawEllipse(0, 0, 10, 10, null, null)).Should().BeEmpty();

    [Fact]
    public void MixedDrawingWithShapes_EveryPathObjectIsWellFormed()
    {
        var ops = Emit(g =>
        {
            g.SaveState();
            g.DrawRectangle(10, 10, 50, 20, PdfBrush.Red, PdfPen.Black);
            g.DrawCircle(100, 100, 30, PdfBrush.Blue, new PdfPen(PdfColor.Red, 2) { Opacity = 0.5 });
            g.DrawArc(0, 0, 80, 40, 45, 200, new PdfPen(PdfColor.Black, 1) { DashArray = [3, 2] });
            g.DrawEllipse(10, 10, 30, 60, null, PdfPen.Black);
            g.RestoreState();
        });

        PdfGraphicsOperatorOrderTests.AssertPathObjectsWellFormed(ops);
    }
}
