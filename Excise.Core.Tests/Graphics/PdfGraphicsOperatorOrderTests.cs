using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// ISO 32000-2 §8.2 (Figure 9): inside a path object — from the first <c>m</c>/<c>re</c>
/// to the painting operator — only path-construction and clipping operators may appear;
/// colour (§8.6.8) and general graphics-state operators belong at the content-stream
/// level. #1851: the path API emitted <c>RG</c>/<c>w</c> between <c>m</c> and <c>S</c>.
/// </summary>
public class PdfGraphicsOperatorOrderTests
{
    private static readonly HashSet<string> PathConstruction = ["m", "l", "c", "v", "y", "h", "re", "W", "W*"];
    private static readonly HashSet<string> PathPainting = ["S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n"];

    /// <summary>
    /// Fails when anything but a path-construction or clipping operator sits inside a path
    /// object, or a path object is left unpainted. One operator per line, as
    /// <see cref="PdfGraphics"/> emits them.
    /// </summary>
    internal static void AssertPathObjectsWellFormed(string operators)
    {
        bool inPath = false;
        foreach (var line in operators.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var op = line[(line.LastIndexOf(' ') + 1)..];
            if (inPath)
            {
                if (PathPainting.Contains(op)) { inPath = false; continue; }
                PathConstruction.Should().Contain(op,
                    $"only path-construction operators may appear inside a path object (§8.2), in:\n{operators}");
            }
            else if (op is "m" or "re")
            {
                inPath = true;
            }
        }
        inPath.Should().BeFalse($"every path object must end with a painting operator, in:\n{operators}");
    }

    private static (PdfDocument Doc, PdfPage Page) NewPage()
    {
        var doc = PdfDocument.CreateNew();
        return (doc, doc.Pages.AddBlank(200, 200));
    }

    private static string Emit(Action<PdfGraphics> draw)
    {
        var (doc, page) = NewPage();
        using (doc)
        {
            using var g = page.GetGraphics();
            draw(g);
            return g.GetOperators();
        }
    }

    // Default pen/brush bytes for the shape helpers that were already in state-first order.
    // These are pinned from the output before #1851 and must never change.

    [Fact]
    public void DrawLine_DefaultPen_BytesUnchanged() =>
        Emit(g => g.DrawLine(0, 0, 100, 50, PdfPen.Black))
            .Should().Be("0 G\n1 w\n0 0 m\n100 50 l\nS\n");

    [Fact]
    public void DrawRectangle_FillOnly_BytesUnchanged() =>
        Emit(g => g.DrawRectangle(10, 20, 30, 40, PdfBrush.Red))
            .Should().Be("1 0 0 rg\n10 20 30 40 re\nf\n");

    [Fact]
    public void DrawRectangle_StrokeOnly_BytesUnchanged() =>
        Emit(g => g.DrawRectangle(10, 20, 30, 40, null, new PdfPen(PdfColor.Blue, 2)))
            .Should().Be("0 0 1 RG\n2 w\n10 20 30 40 re\nS\n");

    [Fact]
    public void DrawRectangle_FillAndStroke_BytesUnchanged() =>
        Emit(g => g.DrawRectangle(10, 20, 30, 40, PdfBrush.Red, PdfPen.Black))
            .Should().Be("1 0 0 rg\n0 G\n1 w\n10 20 30 40 re\nB\n");

    [Fact]
    public void DrawString_DefaultBrush_BytesUnchanged() =>
        Emit(g => g.DrawString("Hi", PdfFont.Helvetica(12), PdfBrush.Black, 10, 20))
            .Should().Be("0 g\nBT\n/F1 12 Tf\n10 20 Td\n(Hi) Tj\nET\n");

    // The path API: state first, then the path, then the painting operator.

    [Fact]
    public void Stroke_EmitsPenStateBeforeThePath()
    {
        var ops = Emit(g =>
        {
            g.BeginPath();
            g.MoveTo(0, 0);
            g.LineTo(100, 0);
            g.CurveTo(10, 20, 30, 40, 50, 0);
            g.ClosePath();
            g.Stroke(PdfPen.Black);
        });

        ops.Should().Be("0 G\n1 w\n0 0 m\n100 0 l\n10 20 30 40 50 0 c\nh\nS\n");
        AssertPathObjectsWellFormed(ops);
    }

    [Fact]
    public void Fill_EmitsBrushStateBeforeThePath()
    {
        var ops = Emit(g =>
        {
            g.MoveTo(0, 0);
            g.LineTo(5, 5);
            g.Fill(PdfBrush.Blue);
        });

        ops.Should().Be("0 0 1 rg\n0 0 m\n5 5 l\nf\n");
        AssertPathObjectsWellFormed(ops);
    }

    [Fact]
    public void FillAndStroke_EmitsBothStatesBeforeThePath()
    {
        var ops = Emit(g =>
        {
            g.MoveTo(0, 0);
            g.LineTo(5, 5);
            g.FillAndStroke(PdfBrush.Green, new PdfPen(PdfColor.Red, 3));
        });

        ops.Should().Be("0 1 0 rg\n1 0 0 RG\n3 w\n0 0 m\n5 5 l\nB\n");
        AssertPathObjectsWellFormed(ops);
    }

    [Fact]
    public void MixedDrawing_EveryPathObjectIsWellFormed()
    {
        var ops = Emit(g =>
        {
            g.SaveState();
            g.Translate(5, 5);
            g.DrawRectangle(10, 10, 50, 20, PdfBrush.Red, PdfPen.Black);
            g.DrawLine(0, 0, 100, 100, PdfPen.Black);
            g.MoveTo(0, 0);
            g.LineTo(10, 10);
            g.Stroke(PdfPen.Red);
            g.DrawString("ok", PdfFont.Helvetica(12), PdfBrush.Black, 50, 100);
            g.MoveTo(0, 0);
            g.LineTo(5, 5);
            g.FillAndStroke(PdfBrush.Green, PdfPen.Black);
            g.RestoreState();
        });

        AssertPathObjectsWellFormed(ops);
    }

    [Fact]
    public void UnpaintedPath_IsEndedWithNoOp_OnDispose()
    {
        var (doc, page) = NewPage();
        using (doc)
        {
            using (var g = page.GetGraphics())
            {
                g.DrawLine(0, 0, 1, 1, PdfPen.Black);
                g.MoveTo(0, 0);
                g.LineTo(5, 5);
                g.GetOperators().Should().NotContain("5 5 l", "a path is emitted when it is painted");
            }

            var content = System.Text.Encoding.Latin1.GetString(page.GetContentStreamBytes());
            content.Should().EndWith("0 0 m\n5 5 l\nn\n");
            AssertPathObjectsWellFormed(content);
        }
    }
}
