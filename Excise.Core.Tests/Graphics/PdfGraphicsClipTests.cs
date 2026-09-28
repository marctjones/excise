using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// #1909: Clip / ClipEvenOdd / ClipRectangle end the buffered path with <c>W n</c> or
/// <c>W* n</c> (§8.5.4): the clipping operator after the last construction operator,
/// then the no-op painting operator, with nothing state-changing inside the path object.
/// </summary>
public class PdfGraphicsClipTests
{
    private static string Emit(Action<PdfGraphics> draw)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();
        draw(g);
        return g.GetOperators();
    }

    [Fact]
    public void Clip_EndsTheBuiltPathWithWThenN() =>
        Emit(g =>
        {
            g.SaveState();
            g.MoveTo(0, 0);
            g.LineTo(50, 0);
            g.LineTo(0, 50);
            g.ClosePath();
            g.Clip();
            g.DrawRectangle(0, 0, 100, 100, PdfBrush.Red);
            g.RestoreState();
        }).Should().Be("q\n0 0 m\n50 0 l\n0 50 l\nh\nW\nn\n1 0 0 rg\n0 0 100 100 re\nf\nQ\n");

    [Fact]
    public void ClipEvenOdd_UsesWStar() =>
        Emit(g =>
        {
            g.MoveTo(0, 0);
            g.LineTo(5, 5);
            g.ClipEvenOdd();
        }).Should().Be("0 0 m\n5 5 l\nW*\nn\n");

    [Fact]
    public void ClipRectangle_ClipsToARePath() =>
        Emit(g => g.ClipRectangle(10, 20, 30, 40)).Should().Be("10 20 30 40 re\nW\nn\n");

    [Fact]
    public void Clip_WithNoPath_Throws() =>
        FluentActions.Invoking(() => Emit(g => g.Clip())).Should().Throw<InvalidOperationException>();

    [Fact]
    public void Clip_AfterAPaintedPath_ClipsOnlyTheNewPath()
    {
        var ops = Emit(g =>
        {
            g.MoveTo(0, 0);
            g.LineTo(5, 5);
            g.Stroke(PdfPen.Black);
            g.ClipRectangle(1, 2, 3, 4);
        });

        ops.Should().EndWith("S\n1 2 3 4 re\nW\nn\n");
    }

    [Fact]
    public void MixedDrawingWithClips_EveryPathObjectIsWellFormed()
    {
        var ops = Emit(g =>
        {
            g.SaveState();
            g.DrawCircle(50, 50, 40, null, null);
            g.MoveTo(0, 0);
            g.LineTo(100, 0);
            g.LineTo(50, 100);
            g.ClosePath();
            g.ClipEvenOdd();
            g.DrawCircle(50, 50, 40, PdfBrush.Blue, new PdfPen(PdfColor.Red, 2) { Opacity = 0.5 });
            g.SaveState();
            g.ClipRectangle(10, 10, 30, 30);
            g.DrawRectangle(0, 0, 200, 200, PdfBrush.Red, PdfPen.Black);
            g.RestoreState();
            g.RestoreState();
        });

        PdfGraphicsOperatorOrderTests.AssertPathObjectsWellFormed(ops);
    }
}
