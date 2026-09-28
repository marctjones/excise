using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1824: where XFA content breaks across pages. Pages are read back by mutool and compared with
/// pdf.js 6.3.289 (<c>enableXfa</c>, headless Chrome), never with excise. The synthetic forms were
/// laid out by pdf.js too; each comment gives what pdf.js drew.
/// </summary>
public class XfaPaginationOracleTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private (string Path, XfaLayoutResult Result) LayOutAndSave(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        var path = Path.Combine(Path.GetTempPath(), $"excise-xfa-{Guid.NewGuid():N}.pdf");
        document.Save(path);
        _temp.Add(path);
        return (path, result);
    }

    private static int PageCount(string path)
    {
        using var document = PdfDocument.Open(File.ReadAllBytes(path));
        return document.PageCount;
    }

    private static string Draw(string name, string text, string h) =>
        $"<draw name=\"{name}\" w=\"8in\" h=\"{h}\"><ui><textEdit/></ui>"
        + $"<value><text>{text}</text></value><font typeface=\"Arial\" size=\"10pt\"/></draw>";

    private static List<string> Pages(string path, int count)
    {
        var pages = MutoolTextExtractor.ExtractAllPages(path, count);
        pages.Should().NotBeNull("mutool must read the laid-out pages");
        return pages!.ToList();
    }

    /// <summary>
    /// Designer writes its style sheets as prototypes under <c>&lt;proto&gt;</c> and refers to them by
    /// SOM: <c>usehref=".#som($template.#subform.designer__stylesheet.X)"</c> (Ohio: 45 references, each
    /// a 2.54mm or 6.35mm margin). pdf.js applies the 1in margin here and draws AlphaFirst on page 1
    /// and BravoStyled on page 2 (5in + 1in + 5in exceeds the 10.5in content area).
    /// </summary>
    [Fact]
    public void StyleSheetPrototype_ReferencedBySom_AppliesItsMargin()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var template = XfaTestForms.Template(
            "<proto><subform name=\"designer__stylesheet\"><subform name=\"Spaced\" w=\"8in\">"
            + "<margin topInset=\"1in\" bottomInset=\"0in\" leftInset=\"0in\" rightInset=\"0in\"/>"
            + "</subform></subform></proto>"
            + Draw("A", "AlphaFirst", "5in")
            + "<subform name=\"Styled\" layout=\"tb\" usehref=\".#som($template.#subform.designer__stylesheet.Spaced)\">"
            + Draw("B", "BravoStyled", "5in") + "</subform>");

        var (path, result) = LayOutAndSave(XfaTestForms.BuildPdf(template));

        result.Omissions.Should().NotContain(o => o.Contains("SOM", StringComparison.Ordinal));
        PageCount(path).Should().Be(2, "pdf.js lays the form out on 2 pages");
        var pages = Pages(path, 2);
        pages[0].Should().Contain("AlphaFirst").And.NotContain("BravoStyled");
        pages[1].Should().Contain("BravoStyled");
    }

    /// <summary>
    /// A <c>simplexPaginated</c> page set picks the page area by <c>pagePosition</c>: after the first
    /// page, a <c>rest</c> page. pdf.js draws DeltaOne on the 5in first page and DeltaTwo and
    /// DeltaThree together on the 10in rest page (2 pages; staying on the first page area takes 3).
    /// </summary>
    [Fact]
    public void PaginatedPageSet_LaterPagesUseTheRestPageArea()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        const string medium = "<medium stock=\"letter\" short=\"8.5in\" long=\"11in\"/>";
        var pageSet = "<pageSet relation=\"simplexPaginated\">"
            + $"<pageArea name=\"First\" pagePosition=\"first\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"8in\" h=\"5in\"/>{medium}</pageArea>"
            + $"<pageArea name=\"Rest\" pagePosition=\"rest\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"8in\" h=\"10in\"/>{medium}</pageArea>"
            + "</pageSet>";
        var template = XfaTestForms.Template(
            Draw("D1", "DeltaOne", "4in") + Draw("D2", "DeltaTwo", "4in") + Draw("D3", "DeltaThree", "4in"),
            pageSet: pageSet);

        var (path, _) = LayOutAndSave(XfaTestForms.BuildPdf(template));

        PageCount(path).Should().Be(2, "pdf.js lays the form out on 2 pages");
        var pages = Pages(path, 2);
        pages[0].Should().Contain("DeltaOne");
        pages[1].Should().Contain("DeltaTwo").And.Contain("DeltaThree");
    }

    /// <summary>
    /// pdf.js fits a fixed-height object up to 2pt taller than its space (layout.js
    /// <c>checkDimensions</c>, <c>ERROR = 2</c>): it draws this 0.5pt-too-tall subform whole on page 1
    /// and EchoAfter on page 2, so the layout report must not call it clipped. HSBC's <c>page3</c> is
    /// 0.55pt taller than its content area.
    /// </summary>
    [Theory]
    [InlineData("756.5pt", false)]
    [InlineData("760pt", true)]
    public void FixedHeightSubform_TallerThanTheContentArea_IsReportedClippedOnlyBeyondPdfJsTolerance(string h, bool clipped)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var template = XfaTestForms.Template(
            $"<subform name=\"Tall\" w=\"8in\" h=\"{h}\">"
            + "<draw name=\"T\" x=\"0in\" y=\"0in\" w=\"8in\" h=\"1in\"><ui><textEdit/></ui>"
            + "<value><text>TangoTall</text></value><font typeface=\"Arial\" size=\"10pt\"/></draw></subform>"
            + Draw("E", "EchoAfter", "1in"));

        var (path, result) = LayOutAndSave(XfaTestForms.BuildPdf(template));

        // The content area is 10.5in = 756pt. 760pt is past pdf.js's tolerance: the note must still
        // fire. pdf.js draws both rows on the same 2 pages.
        result.Omissions.Any(o => o.Contains("clipped", StringComparison.Ordinal)).Should().Be(clipped);
        PageCount(path).Should().Be(2);
        var pages = Pages(path, 2);
        pages[0].Should().Contain("TangoTall");
        pages[1].Should().Contain("EchoAfter");
    }

    // pdf.js 6.3.289 visible words per page (dropdown option lists excluded), measured on the
    // SHA-pinned file from scripts/download-xfa-real-corpus.sh. Before #1824's second fix excise drew
    // 462/263/333/223/203/259/247/247/52 (no style-sheet margins, first-page area on every page).
    private static readonly int[] OhioPdfJsWords = { 439, 278, 315, 226, 199, 248, 228, 217, 86 };

    [Fact]
    public void OhioExpenseReport_EveryPageBreaksWherePdfJsBreaks()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        const string file = "ohio-expense-report.pdf";
        var source = TestRepoLayout.FindFile("test-pdfs", "xfa-real", file);
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason(
            "xfa-real corpus (scripts/download-xfa-real-corpus.sh)", $"test-pdfs/xfa-real/{file}"));

        var (path, _) = LayOutAndSave(File.ReadAllBytes(source!));

        PageCount(path).Should().Be(OhioPdfJsWords.Length, "pdf.js lays the form out on 9 pages");
        var pages = Pages(path, OhioPdfJsWords.Length);
        for (int i = 0; i < OhioPdfJsWords.Length; i++)
        {
            // pdf.js's text excludes field values; excise draws FormCalc's computed totals
            // ("0.00000000"), which mutool reads. Without them the counts match exactly on
            // macOS mutool; 2% absorbs extraction differences between mutool builds.
            var words = pages[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Count(w => w != "0.00000000");
            words.Should().BeCloseTo(OhioPdfJsWords[i], (uint)Math.Max(2, OhioPdfJsWords[i] / 50), $"page {i + 1} against pdf.js");
        }
        // pdf.js page 9 opens with the last two rows of section I1.
        pages[7].Should().NotContain("Board office telephone");
        pages[8].Should().Contain("Board office telephone").And.Contain("Almost Done!");
    }

    [Fact]
    public void HsbcClosureForm_Page3FitsItsPage_AsInPdfJs()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        const string file = "hsbc-cloture-compte.pdf";
        var source = TestRepoLayout.FindFile("test-pdfs", "xfa-real", file);
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason(
            "xfa-real corpus (scripts/download-xfa-real-corpus.sh)", $"test-pdfs/xfa-real/{file}"));

        var (path, result) = LayOutAndSave(File.ReadAllBytes(source!));

        result.Omissions.Should().NotContain(o => o.Contains("clipped", StringComparison.Ordinal),
            "pdf.js fits page3 (0.55pt taller than its content area) on one page");
        int[] pdfJsWords = { 214, 293, 290, 248 };
        PageCount(path).Should().Be(pdfJsWords.Length);
        var pages = Pages(path, pdfJsWords.Length);
        for (int i = 0; i < pdfJsWords.Length; i++)
        {
            pages[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length
                .Should().BeCloseTo(pdfJsWords[i], (uint)(pdfJsWords[i] / 50), $"page {i + 1} against pdf.js");
        }
    }
}
