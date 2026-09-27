using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Xunit;
using static Excise.Core.Tests.Graphics.PdfGraphicsOperatorOrderTests;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// #1851: pen dash/cap/join/miter/opacity and brush opacity. Every parameter is written
/// in the state-first position, only when it differs from what the context last set,
/// and opacity goes through one deduplicated page ExtGState per value.
/// </summary>
public class PdfPenStateTests
{
    private static readonly PdfPen HiddenEdge = new(PdfColor.Black, 0.5) { DashArray = [3, 3], Opacity = 0.35 };

    private static PdfDictionary ExtGStates(PdfDocument doc, PdfPage page) =>
        page.Resources!.ResolveDictionary(doc, "ExtGState")!;

    [Fact]
    public void DashedTranslucentStroke_StateComesBeforeThePath()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();

        g.MoveTo(0, 0);
        g.LineTo(100, 0);
        g.Stroke(HiddenEdge);
        g.DrawLine(0, 10, 100, 10, HiddenEdge);

        var ops = g.GetOperators();
        ops.Should().Be(
            "0 G\n0.5 w\n[3 3] 0 d\n/GS1 gs\n0 0 m\n100 0 l\nS\n" +
            "0 G\n0.5 w\n0 10 m\n100 10 l\nS\n");
        AssertPathObjectsWellFormed(ops);
    }

    [Fact]
    public void Opacity_IsOneExtGStatePerValuePerPage_AndSurvivesSave()
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        for (int i = 0; i < 3; i++)
        {
            // A fresh context each time, and an opaque line between, so every
            // translucent draw has to select the state again.
            using var g = page.GetGraphics();
            g.DrawLine(0, i, 100, i, HiddenEdge);
            g.DrawLine(0, i, 100, i, PdfPen.Black);
            g.DrawRectangle(0, 0, 10, 10, null, HiddenEdge);
        }

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var reopenedPage = reopened.GetPage(1);
        var states = ExtGStates(reopened, reopenedPage);
        states.Count.Should().Be(2, "one state for /CA 0.35 and one for /CA 1 — not one per draw");
        reopenedPage.GetExtGState("GS1")!.GetNumber("CA").Should().Be(0.35);
        reopenedPage.GetExtGState("GS1")!.ContainsKey("ca").Should().BeFalse("a stroke must not change the fill alpha");
        reopenedPage.GetExtGState("GS2")!.GetNumber("CA").Should().Be(1);

        var content = System.Text.Encoding.Latin1.GetString(reopenedPage.GetContentStreamBytes());
        (content.Split("/GS1 gs").Length - 1).Should().Be(6, "all six translucent draws select the one shared state");
        AssertPathObjectsWellFormed(content);
    }

    [Fact]
    public void FillAndStroke_DifferentOpacities_OneStateCarriesBoth()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();

        g.MoveTo(0, 0);
        g.LineTo(50, 50);
        g.ClosePath();
        g.FillAndStroke(new PdfBrush(PdfColor.Blue) { Opacity = 0.5 }, new PdfPen(PdfColor.Red) { Opacity = 0.25 });

        g.GetOperators().Should().Be("0 0 1 rg\n1 0 0 RG\n1 w\n/GS1 gs\n0 0 m\n50 50 l\nh\nB\n");
        var state = page.GetExtGState("GS1")!;
        state.GetNumber("CA").Should().Be(0.25);
        state.GetNumber("ca").Should().Be(0.5);
    }

    [Fact]
    public void BrushOpacity_AppliesToFillsAndText()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();
        var translucent = new PdfBrush(PdfColor.Green) { Opacity = 0.4 };

        g.DrawRectangle(0, 0, 10, 10, translucent);
        g.DrawString("Hi", PdfFont.Helvetica(12), PdfBrush.Black, 10, 20);

        g.GetOperators().Should().Be(
            "0 1 0 rg\n/GS1 gs\n0 0 10 10 re\nf\n" +
            "0 g\n/GS2 gs\nBT\n/F1 12 Tf\n10 20 Td\n(Hi) Tj\nET\n");
        page.GetExtGState("GS1")!.GetNumber("ca").Should().Be(0.4);
        page.GetExtGState("GS2")!.GetNumber("ca").Should().Be(1);
    }

    [Fact]
    public void CapJoinMiter_AreWrittenOnlyWhenTheyChange()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();
        var pen = new PdfPen(PdfColor.Black, 2) { LineCap = PdfLineCap.Round, LineJoin = PdfLineJoin.Bevel, MiterLimit = 4 };

        g.DrawRectangle(0, 0, 10, 10, null, pen);
        g.DrawRectangle(0, 0, 10, 10, null, pen);
        g.DrawLine(0, 0, 5, 5, PdfPen.Black);

        g.GetOperators().Should().Be(
            "0 G\n2 w\n1 J\n2 j\n4 M\n0 0 10 10 re\nS\n" +
            "0 G\n2 w\n0 0 10 10 re\nS\n" +
            "0 G\n1 w\n0 J\n0 j\n10 M\n0 0 m\n5 5 l\nS\n");
    }

    [Fact]
    public void DefaultPenAfterDashedPen_ResetsTheDash()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();

        g.DrawLine(0, 0, 100, 0, new PdfPen(PdfColor.Black) { DashArray = [4, 2], DashPhase = 1 });
        g.DrawLine(0, 10, 100, 10, PdfPen.Black);

        g.GetOperators().Should().Be(
            "0 G\n1 w\n[4 2] 1 d\n0 0 m\n100 0 l\nS\n" +
            "0 G\n1 w\n[] 0 d\n0 10 m\n100 10 l\nS\n");
    }

    [Fact]
    public void RestoreState_RestoresTheTrackedParameters()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();

        g.SaveState();
        g.DrawLine(0, 0, 100, 0, HiddenEdge);
        g.RestoreState();
        g.DrawLine(0, 10, 100, 10, PdfPen.Black);

        g.GetOperators().Should().EndWith("Q\n0 G\n1 w\n0 10 m\n100 10 l\nS\n",
            "Q already put the dash and alpha back, so the default pen writes nothing for them");
    }

    [Fact]
    public void Dispose_PutsBackInitialState_ForTheNextContextOnThePage()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawLine(0, 0, 100, 0, new PdfPen(PdfColor.Black) { DashArray = [2], LineCap = PdfLineCap.Square, Opacity = 0.5 });

        var content = System.Text.Encoding.Latin1.GetString(page.GetContentStreamBytes());
        content.Should().EndWith("S\n[] 0 d\n0 J\n/GS2 gs\n");
        page.GetExtGState("GS2")!.GetNumber("CA").Should().Be(1);

        using var next = page.GetGraphics();
        next.DrawLine(0, 0, 1, 1, PdfPen.Black);
        next.GetOperators().Should().Be("0 G\n1 w\n0 0 m\n1 1 l\nS\n");
    }

    [Fact]
    public void DefaultPenAndBrush_AddNoExtGState()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
        {
            g.DrawRectangle(0, 0, 10, 10, PdfBrush.Red, PdfPen.Black);
            g.DrawString("Hi", PdfFont.Helvetica(12), PdfBrush.Black, 10, 20);
        }

        page.Resources!.ContainsKey("ExtGState").Should().BeFalse();
    }

    [Fact]
    public void PenDefaults_AreTheSpecInitialState()
    {
        var pen = new PdfPen(PdfColor.Black);
        pen.DashArray.Should().BeNull();
        pen.DashPhase.Should().Be(0);
        pen.LineCap.Should().Be(PdfLineCap.Butt);
        pen.LineJoin.Should().Be(PdfLineJoin.Miter);
        pen.MiterLimit.Should().Be(10);
        pen.Opacity.Should().Be(1);
        new PdfBrush(PdfColor.Black).Opacity.Should().Be(1);
    }

    [Fact]
    public void PenValidation_FollowsTheSpec()
    {
        new PdfPen(PdfColor.Black) { DashArray = [] }.DashArray.Should().BeNull("an empty dash array is a solid line");
        new PdfPen(PdfColor.Black) { DashArray = [3, 0] }.DashArray.Should().Equal(3, 0);
        new PdfPen(PdfColor.Black) { DashPhase = -2 }.DashPhase.Should().Be(-2, "§8.4.3.6 normalises a negative phase");
        new PdfPen(PdfColor.Black) { Opacity = 1.5 }.Opacity.Should().Be(1);
        new PdfBrush(PdfColor.Black) { Opacity = -1 }.Opacity.Should().Be(0);

        FluentActions.Invoking(() => new PdfPen(PdfColor.Black) { DashArray = [3, -1] }).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new PdfPen(PdfColor.Black) { DashArray = [0, 0] }).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new PdfPen(PdfColor.Black) { MiterLimit = 0.5 }).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new PdfPen(PdfColor.Black) { LineCap = (PdfLineCap)3 }).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DashArray_IsCopied_SoThePenStaysImmutable()
    {
        var lengths = new double[] { 3, 3 };
        var pen = new PdfPen(PdfColor.Black) { DashArray = lengths };
        lengths[0] = 9;
        pen.DashArray.Should().Equal(3, 3);
    }
}
