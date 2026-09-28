using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1825: how a laid-out XFA form's widgets LOOK, judged against pdf.js (5.x
/// pdf.worker.mjs and pdf_viewer.css), rendered by mutool and read back as
/// pixels. The expectations come from pdf.js, not from excise:
/// <list type="bullet">
/// <item>a checkButton inside an exclGroup is an HTML radio button (a circle); excise
/// honours an explicit <c>shape="square"</c>, which pdf.js ignores;</item>
/// <item><c>.xfaTextfield, .xfaSelect</c> carry rgba(0, 54, 255, 0.13), which over
/// white is (222, 229, 255); an input or textarea in a readOnly field does not;</item>
/// <item><c>validate nullTest="error"</c> makes the control <c>:required</c>, outlined
/// 1.5px red outside its box;</item>
/// </list>
/// Page coordinates: the content area starts at (18pt, 18pt); rendering at
/// 144 dpi puts 2 px on every point.
/// </summary>
public class XfaWidgetAppearanceTests : IDisposable
{
    private const int Dpi = 144;
    private const double Px = Dpi / 72.0;
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private SKBitmap LayOutAndRender(string body, string? data = null)
    {
        var template = XfaTestForms.Template(body, layout: "position");
        using var document = PdfDocument.Open(XfaTestForms.BuildPdf(template, data));
        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        var path = Path.Combine(Path.GetTempPath(), $"excise-xfa-{Guid.NewGuid():N}.pdf");
        document.Save(path);
        _temp.Add(path);
        var bitmap = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        bitmap.Should().NotBeNull("mutool must render the laid-out page");
        return bitmap!;
    }

    private static SKColor At(SKBitmap bitmap, double xPt, double yPt)
        => bitmap.GetPixel((int)Math.Round(xPt * Px), (int)Math.Round(yPt * Px));

    /// <summary>Pixels darker than mid-grey in the rectangle, in points.</summary>
    private static int Ink(SKBitmap bitmap, double x, double y, double w, double h)
    {
        int count = 0;
        for (int py = (int)(y * Px); py < (int)((y + h) * Px); py++)
        {
            for (int px = (int)(x * Px); px < (int)((x + w) * Px); px++)
            {
                var c = bitmap.GetPixel(px, py);
                if (c.Red + c.Green + c.Blue < 3 * 128)
                    count++;
            }
        }
        return count;
    }

    private static string Check(string name, string x, string shape = "") =>
        $"<field name=\"{name}\" x=\"{x}\" y=\"0\" w=\"30pt\" h=\"30pt\">"
        + $"<ui><checkButton{shape} size=\"20pt\"><border><fill><color value=\"255,255,255\"/></fill></border></checkButton></ui>"
        + "<items><text>1</text><text>0</text></items></field>";

    [Fact]
    public void ExclGroupMember_IsACircle_AndALoneCheckBoxStaysSquare()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // The shape of IRCC's IMM 5257e radio buttons: no shape attribute, a white-filled
        // border. Each 20pt box is centred in a 30pt-high widget: the radio's box is
        // x 90-110, y 23-43; the lone check box's is x 234-254, y 23-43. The widgets'
        // left edges are clip edges, so the right side is where the outline is looked for.
        using var bitmap = LayOutAndRender(
            "<exclGroup name=\"Answer\" layout=\"position\" x=\"1in\" y=\"0\" w=\"2in\" h=\"30pt\">"
            + Check("Yes", "0") + "</exclGroup>"
            + "<subform name=\"Box\" layout=\"position\" x=\"3in\" y=\"0\" w=\"2in\" h=\"30pt\">"
            + Check("Agree", "0") + "</subform>");

        // Both outlines cross the middle of their right side.
        Ink(bitmap, 109, 31, 2, 4).Should().BeGreaterThan(0, "the radio's outline passes its right midpoint");
        Ink(bitmap, 253, 31, 2, 4).Should().BeGreaterThan(0, "the check box's outline passes its right midpoint");

        // A square's outline runs through its corner; a circle's is 5.9pt away from it.
        Ink(bitmap, 108, 41, 3, 3).Should().Be(0, "pdf.js draws an exclGroup member as a radio: a circle");
        Ink(bitmap, 252, 41, 3, 3).Should().BeGreaterThan(0, "a check box outside an exclGroup stays square");

        // The border outlines the box, not the whole 30pt-wide widget.
        Ink(bitmap, 112, 19, 6, 28).Should().Be(0, "nothing is drawn right of the radio's 20pt box");
    }

    [Fact]
    public void TextField_HasThePdfJsTint_AndARedOutlineWhenRequired()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Name: x 90-234, y 90-120, required. Note: x 306-450, y 90-120, readOnly.
        using var bitmap = LayOutAndRender(
            "<field name=\"Name\" x=\"1in\" y=\"1in\" w=\"2in\" h=\"30pt\"><ui><textEdit/></ui>"
            + "<validate nullTest=\"error\"/></field>"
            + "<field name=\"Note\" x=\"4in\" y=\"1in\" w=\"2in\" h=\"30pt\" access=\"readOnly\"><ui><textEdit/></ui></field>");

        var tint = At(bitmap, 160, 105);
        ((int)tint.Red).Should().BeInRange(219, 225, "rgba(0,54,255,0.13) over white");
        ((int)tint.Green).Should().BeInRange(226, 232);
        ((int)tint.Blue).Should().BeGreaterThan(250);

        var readOnly = At(bitmap, 380, 105);
        ((int)readOnly.Red).Should().BeGreaterThan(250, "pdf.js clears the tint on a readOnly input");

        // The 1.5pt outline straddles nothing inside the box: it lies from 88.5 to 90 on the left.
        var outline = At(bitmap, 89.25, 105);
        ((int)outline.Red).Should().BeGreaterThan(200, "a required field is outlined in red");
        ((int)outline.Green).Should().BeLessThan(60);
        ((int)outline.Blue).Should().BeLessThan(60);
        var notRequired = At(bitmap, 305.25, 105);
        ((int)notRequired.Green).Should().BeGreaterThan(200, "an optional field has no red outline");
    }
}
