using System.Diagnostics;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1547 phase 2: what a laid-out XFA form SHOWS, read back by mutool (not
/// excise) and compared with positions computed from the template by hand.
/// Every expected coordinate below is XFA arithmetic on the fixture, stated
/// in the comment next to it: the content area starts at (0.25in, 0.25in) =
/// (18pt, 18pt), and mutool reports glyph quads with a top-left origin.
/// </summary>
public class XfaLayoutOracleTests : IDisposable
{
    private const double Tolerance = 1.0;
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private string LayOutAndSave(string template, string? data = null)
    {
        using var document = PdfDocument.Open(XfaTestForms.BuildPdf(template, data));
        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        var path = Path.Combine(Path.GetTempPath(), $"excise-xfa-{Guid.NewGuid():N}.pdf");
        document.Save(path);
        _temp.Add(path);
        return path;
    }

    private static List<MutoolStext.Char> Chars(string path)
    {
        var chars = MutoolStext.ExtractChars(path);
        chars.Should().NotBeNull("mutool must read the laid-out file");
        return chars!;
    }

    /// <summary>The first char of the first occurrence of <paramref name="text"/> on <paramref name="page"/>.</summary>
    private static MutoolStext.Char? Find(List<MutoolStext.Char> chars, string text, int page = 1)
    {
        var onPage = chars.Where(c => c.Page == page).ToList();
        var joined = string.Concat(onPage.Select(c => c.C));
        var index = joined.IndexOf(text, StringComparison.Ordinal);
        if (index < 0)
            return null;
        // Map the string index back to a char (each entry is one code point).
        int offset = 0;
        foreach (var c in onPage)
        {
            if (offset == index)
                return c;
            offset += c.C.Length;
        }
        return null;
    }

    [Fact]
    public void BoundValue_Caption_AndDraw_SitWhereTheTemplatePutsThem()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = LayOutAndSave(
            XfaTestForms.PositionedTemplate(),
            XfaTestForms.Data("<FullName>Jane Q Public</FullName>"));
        var chars = Chars(path);
        var all = string.Concat(chars.Select(c => c.C));

        all.Should().NotContain(XfaTestForms.Placeholder, "the placeholder page is replaced");
        all.Should().NotContain("template default", "bound data replaces the template value");

        // Field FullName: x = 18 + 1in = 90, y = 18 + 1in = 90, 4in x 0.4in.
        // Caption reserve 1in: the value starts at 90 + 72 = 162.
        var value = Find(chars, "Jane Q Public");
        value.Should().NotBeNull("mutool must read the bound value on page 1");
        value!.Value.X0.Should().BeApproximately(162, Tolerance);
        value.Value.Y0.Should().BeGreaterThanOrEqualTo(90 - Tolerance);
        value.Value.Y1.Should().BeLessThanOrEqualTo(90 + 28.8 + Tolerance);

        var caption = Find(chars, "Name");
        caption.Should().NotBeNull();
        caption!.Value.X0.Should().BeApproximately(90, Tolerance);

        // Draw Heading: x = 18 + 1in = 90, y = 18 + 0.5in = 54, h = 0.3in.
        var heading = Find(chars, "Applicant details");
        heading.Should().NotBeNull();
        heading!.Value.X0.Should().BeApproximately(90, Tolerance);
        heading.Value.Y0.Should().BeGreaterThanOrEqualTo(54 - Tolerance);
        heading.Value.Y1.Should().BeLessThanOrEqualTo(54 + 21.6 + Tolerance);
    }

    [Fact]
    public void RepeatingRows_FlowAcrossPages_InDataOrder()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // 60 rows of 0.5in in a 10.5in content area: 21 per page.
        var template = XfaTestForms.Template(
            "<subform name=\"Row\" layout=\"tb\" w=\"8in\"><occur min=\"1\" max=\"-1\"/>"
            + "<field name=\"Label\" w=\"8in\" h=\"0.5in\"><ui><textEdit/></ui>"
            + "<font typeface=\"Arial\" size=\"10pt\"/></field></subform>");
        var data = XfaTestForms.Data(string.Concat(Enumerable.Range(1, 60).Select(i => $"<Row><Label>Item-{i:D2}</Label></Row>")));
        var path = LayOutAndSave(template, data);

        var pages = MutoolTextExtractor.ExtractAllPages(path, 3);
        pages.Should().NotBeNull();
        string[] Lines(int page) => pages![page].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Lines(0).Should().Equal(Enumerable.Range(1, 21).Select(i => $"Item-{i:D2}"));
        Lines(1).Should().Equal(Enumerable.Range(22, 21).Select(i => $"Item-{i:D2}"));
        Lines(2).Should().Equal(Enumerable.Range(43, 18).Select(i => $"Item-{i:D2}"));

        // Row 22 is the first object on page 2: top of the content area, y = 18.
        var chars = Chars(path);
        var first = Find(chars, "Item-22", page: 2);
        first.Should().NotBeNull();
        first!.Value.Y0.Should().BeGreaterThanOrEqualTo(18 - Tolerance);
        first.Value.Y1.Should().BeLessThanOrEqualTo(18 + 36 + Tolerance);
    }

    [Fact]
    public void TableCells_StartAtTheirColumnOffsets()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // columnWidths 2in 3in: column 2 starts at 18 + 144 = 162.
        // Row 2 starts one row (0.4in = 28.8pt) below row 1, at 18 + 28.8.
        string Cell(string text) =>
            $"<draw h=\"0.4in\"><ui><textEdit/></ui><value><text>{text}</text></value><font typeface=\"Arial\"/></draw>";
        var template = XfaTestForms.Template(
            "<subform name=\"T\" layout=\"table\" columnWidths=\"2in 3in\">"
            + $"<subform layout=\"row\">{Cell("CellA1")}{Cell("CellB1")}</subform>"
            + $"<subform layout=\"row\">{Cell("CellA2")}{Cell("CellB2")}</subform>"
            + "</subform>");
        var chars = Chars(LayOutAndSave(template));

        Find(chars, "CellA1")!.Value.X0.Should().BeApproximately(18, Tolerance);
        Find(chars, "CellB1")!.Value.X0.Should().BeApproximately(162, Tolerance);
        Find(chars, "CellB2")!.Value.X0.Should().BeApproximately(162, Tolerance);
        Find(chars, "CellA2")!.Value.Y0.Should().BeGreaterThanOrEqualTo(18 + 28.8 - Tolerance);
    }

    [Fact]
    public void ExclusiveGroup_MarksTheMemberTheDataSelects()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Yes at x = 18 + 1in = 90; No at x = 18 + 3in = 234. The check mark
        // (ZapfDingbats a20) is drawn inside the 10pt box at the
        // field's left edge.
        string Member(string name, string x, string on) =>
            $"<field name=\"{name}\" x=\"{x}\" y=\"0\" w=\"1in\" h=\"0.3in\">"
            + "<ui><checkButton shape=\"square\" size=\"10pt\"/></ui>"
            + $"<items><text>{on}</text><text>0</text></items></field>";
        var template = XfaTestForms.Template(
            "<exclGroup name=\"Answer\" layout=\"position\" w=\"6in\" h=\"0.3in\">"
            + Member("Yes", "1in", "Y") + Member("No", "3in", "N") + "</exclGroup>",
            layout: "position");
        var chars = Chars(LayOutAndSave(template, XfaTestForms.Data("<Answer>N</Answer>")));

        // The fixture draws no text, so every glyph mutool finds is a check
        // mark. (mutool does not map ZapfDingbats a20 to U+2714; it reports
        // the code, so the mark is identified by being the only glyph.)
        var marks = chars.Where(c => c.Page == 1).ToList();
        marks.Should().ContainSingle("exactly one member is on");
        marks[0].X0.Should().BeInRange(234 - Tolerance, 244 + Tolerance);
    }

    [Fact]
    public void PasswordField_ShowsMaskCharacters_NotTheValue()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var template = XfaTestForms.Template(
            "<field name=\"Pin\" w=\"3in\" h=\"0.3in\"><ui><passwordEdit/></ui><font typeface=\"Arial\"/></field>");
        var path = LayOutAndSave(template, XfaTestForms.Data("<Pin>hunter2</Pin>"));

        var text = MutoolTextExtractor.ExtractPage(path, 1);
        text.Should().NotBeNull();
        text.Should().NotContain("hunter2");
        text.Should().Contain("*******");
    }

    [Fact]
    public void RichText_KeepsParagraphs()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var template = XfaTestForms.Template(
            "<draw name=\"Intro\" w=\"6in\"><ui><textEdit/></ui><font typeface=\"Arial\" size=\"10pt\"/>"
            + "<value><exData contentType=\"text/html\"><body xmlns=\"http://www.w3.org/1999/xhtml\">"
            + "<p>First <b>bold</b> paragraph</p>\n  <p>Second paragraph</p></body></exData></value></draw>");
        var text = MutoolTextExtractor.ExtractPage(LayOutAndSave(template), 1);

        text.Should().NotBeNull();
        var lines = text!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().Equal("First bold paragraph", "Second paragraph");
    }

    [Fact]
    public void LaidOutPages_RenderWithInk_InAnIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = LayOutAndSave(
            XfaTestForms.PositionedTemplate(),
            XfaTestForms.Data("<FullName>Jane Q Public</FullName>"));

        var png = Path.Combine(Path.GetTempPath(), $"excise-xfa-{Guid.NewGuid():N}.png");
        _temp.Add(png);
        var psi = new ProcessStartInfo("mutool") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "draw", "-r", "288", "-o", png, path, "1" })
            psi.ArgumentList.Add(a);
        using (var process = Process.Start(psi)!)
        {
            process.WaitForExit(30_000).Should().BeTrue();
            process.ExitCode.Should().Be(0);
        }

        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        // The widget's top border: a 0.5pt line on y = 90 from x = 162 to 378,
        // which at 288 dpi (4 px/pt) is rows 359-360, columns 648-1512.
        int dark = 0;
        for (int x = 648; x < 1512; x++)
        {
            if (Enumerable.Range(358, 4).Any(y => bitmap.GetPixel(x, y).Red < 128))
                dark++;
        }
        dark.Should().BeGreaterThan(800, "mutool draws the widget's top border");

        // ... and the row above the widget (y = 80pt) carries no ink there.
        int stray = 0;
        for (int x = 648; x < 1512; x++)
        {
            if (bitmap.GetPixel(x, 320).Red < 128)
                stray++;
        }
        stray.Should().Be(0);
    }
}
