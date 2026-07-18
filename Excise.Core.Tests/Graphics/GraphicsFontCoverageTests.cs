using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// Coverage-focused tests for the base-14 <see cref="PdfFont"/> width tables and
/// <see cref="PdfGraphics"/> primitives (#351).
/// </summary>
public class GraphicsFontCoverageTests
{
    [Fact]
    public void StandardFonts_MeasureWidth_PerFamily()
    {
        // Exercises the Courier (monospace) and Helvetica/Times width tables.
        PdfFont.Courier(12).MeasureWidth("iiiii").Should()
            .BeApproximately(PdfFont.Courier(12).MeasureWidth("WWWWW"), 0.001,
                "Courier is monospace");
        PdfFont.Helvetica(12).MeasureWidth("W").Should()
            .BeGreaterThan(PdfFont.Helvetica(12).MeasureWidth("i"), "Helvetica is proportional");
        PdfFont.TimesRoman(12).MeasureWidth("Hello").Should().BeGreaterThan(0);
        PdfFont.TimesBold(12).Should().NotBeNull();
        PdfFont.TimesItalic(12).Should().NotBeNull();
        PdfFont.CourierBold(12).Should().NotBeNull();
        PdfFont.CourierOblique(12).Should().NotBeNull();
        PdfFont.HelveticaOblique(12).Should().NotBeNull();
        PdfFont.Helvetica(12).WithSize(18).Size.Should().Be(18);
        PdfFont.Helvetica(12).WithName("X").Name.Should().Be("X");
        PdfGraphics.MeasureString("", PdfFont.Helvetica(12)).Width.Should().Be(0);
        PdfGraphics.MeasureString("abc", PdfFont.Helvetica(12)).Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Graphics_EmptyText_IsNoOp_AndPrimitivesEmit()
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
        {
            g.DrawString("", PdfFont.Helvetica(12), PdfBrush.Black, 10, 10);              // early-return path
            g.DrawString("", PdfFont.Helvetica(12), PdfBrush.Black, 10, 10, TextAlignment.Center);
            g.DrawString("ok", PdfFont.Helvetica(12), PdfBrush.Black, 50, 100, TextAlignment.Right);

            g.SaveState();
            g.Translate(5, 5);
            g.Scale(1.1, 1.1);
            g.Rotate(15);
            g.Transform(1, 0, 0, 1, 2, 2);
            g.DrawRectangle(10, 10, 50, 20, PdfBrush.Red);
            g.DrawRectangle(10, 40, 50, 20, PdfBrush.White, new PdfPen(PdfColor.Black, 1));
            g.DrawLine(0, 0, 100, 100, PdfPen.Black);
            g.BeginPath();
            g.MoveTo(0, 0);
            g.LineTo(10, 10);
            g.CurveTo(20, 20, 30, 0, 40, 10);
            g.ClosePath();
            g.Stroke(PdfPen.Black);
            g.MoveTo(0, 0);
            g.LineTo(5, 5);
            g.Fill(PdfBrush.Blue);
            g.MoveTo(0, 0);
            g.LineTo(5, 5);
            g.FillAndStroke(PdfBrush.Green, PdfPen.Black);
            g.RestoreState();
            g.GetOperators().Should().NotBeNullOrEmpty();
        }

        var act = () => PdfDocument.Open(doc.SaveToBytes()).Dispose();
        act.Should().NotThrow();
    }
}
