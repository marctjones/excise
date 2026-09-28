using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1824: a container's <c>minH</c> under a flowing parent does not reserve space, as in pdf.js.
/// Real forms carry design-time <c>minH</c> taller than their page; honouring it pushed IMM 5257e's
/// section 10 onto an extra page and clipped Ohio's section B2 away. Pages are read back by mutool
/// and compared with pdf.js 6.3.289 (<c>enableXfa</c>, headless Chrome), never with excise.
/// </summary>
public class XfaFlowedMinHeightOracleTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private string LayOutAndSave(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        var path = Path.Combine(Path.GetTempPath(), $"excise-xfa-{Guid.NewGuid():N}.pdf");
        document.Save(path);
        _temp.Add(path);
        return path;
    }

    private static int PageCount(string path)
    {
        using var document = PdfDocument.Open(File.ReadAllBytes(path));
        return document.PageCount;
    }

    private static int Words(string? text)
        => text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length ?? 0;

    /// <summary>
    /// The two shapes the real forms use: a positioned subform (IMM MaritalStatus) and a
    /// <c>keep intact="contentArea"</c> flowed subform (Ohio Ballot_Printing_General), each with a
    /// <c>minH</c> taller than the 10.5in content area and 0.5in of content.
    /// </summary>
    [Theory]
    [InlineData("<subform name=\"Middle\" w=\"8in\" minH=\"11in\">", "positioned")]
    [InlineData("<subform name=\"Middle\" layout=\"tb\" w=\"8in\" minH=\"11in\"><keep intact=\"contentArea\"/>", "keep intact")]
    public void ContainerMinH_UnderAFlowingParent_DoesNotPushLaterContentOffThePage(string open, string shape)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        string Draw(string name, string text, string h) =>
            $"<draw name=\"{name}\" w=\"8in\" h=\"{h}\"><ui><textEdit/></ui>"
            + $"<value><text>{text}</text></value><font typeface=\"Arial\" size=\"10pt\"/></draw>";
        var template = XfaTestForms.Template(
            Draw("A", "AlphaTop", "1in")
            + open + Draw("B", "BravoMiddle", "0.5in") + "</subform>"
            + Draw("C", "CharlieEnd", "1in"));

        // 1in + 0.5in + 1in of content fit one 10.5in content area, as pdf.js lays it out.
        var path = LayOutAndSave(XfaTestForms.BuildPdf(template));

        PageCount(path).Should().Be(1, $"the {shape} subform holds 0.5in of content");
        var text = MutoolTextExtractor.ExtractPage(path, 1);
        text.Should().NotBeNull("mutool must read the laid-out page");
        text.Should().Contain("AlphaTop").And.Contain("BravoMiddle").And.Contain("CharlieEnd");
    }

    // pdf.js 6.3.289 visible words per page (dropdown option lists excluded), measured on the
    // SHA-pinned files from scripts/download-xfa-real-corpus.sh. mutool reads the same pages to
    // within a few words: it also counts the static page footer and field values pdf.js leaves
    // blank, so the per-page tolerance is 5%.
    [Theory]
    [InlineData("imm5257e.pdf", new[] { 346, 405, 260, 395, 675 })]
    [InlineData("imm1295e.pdf", new[] { 333, 380, 283, 396, 697 })]
    public void ImmForms_FirstPageHoldsSection10_AndEveryPageMatchesPdfJs(string file, int[] pdfJsWords)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var source = TestRepoLayout.FindFile("test-pdfs", "xfa-real", file);
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason(
            "xfa-real corpus (scripts/download-xfa-real-corpus.sh)", $"test-pdfs/xfa-real/{file}"));

        var path = LayOutAndSave(File.ReadAllBytes(source!));

        PageCount(path).Should().Be(pdfJsWords.Length, "pdf.js lays the form out on 5 pages");
        var pages = MutoolTextExtractor.ExtractAllPages(path, pdfJsWords.Length);
        pages.Should().NotBeNull();
        pages![0].Should().Contain("FOR OFFICE USE ONLY", "pdf.js fits section 10 and the office box on page 1");
        for (int i = 0; i < pdfJsWords.Length; i++)
            Words(pages[i]).Should().BeCloseTo(pdfJsWords[i], (uint)(pdfJsWords[i] / 20), $"page {i + 1} against pdf.js");
    }

    [Fact]
    public void OhioExpenseReport_Page3_HoldsTheWholeBallotPrintingSection()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        const string file = "ohio-expense-report.pdf";
        var source = TestRepoLayout.FindFile("test-pdfs", "xfa-real", file);
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason(
            "xfa-real corpus (scripts/download-xfa-real-corpus.sh)", $"test-pdfs/xfa-real/{file}"));

        var path = LayOutAndSave(File.ReadAllBytes(source!));

        PageCount(path).Should().Be(9, "pdf.js lays the form out on 9 pages");
        var page3 = MutoolTextExtractor.ExtractPage(path, 3);
        page3.Should().NotBeNull();
        // pdf.js page 3: B2a to B2c, the B2 grand total, then the direct-mail and postage sections.
        page3.Should().Contain("B2c: Ballot Printing - Special Election(s) - SUBTOTAL")
            .And.Contain("(total of B2a-B2c)")
            .And.Contain("Absentee Ballots - Direct-Mail Expense")
            .And.Contain("B4: Postage - Absentee Ballots -GRAND TOTAL");
        // pdf.js draws 315 words on page 3 (298 in the #1824 table, which filtered differently);
        // excise drew 127 before the fix. Every pdf.js word is present; mutool also counts the
        // footer, the computed totals and the first line of the next section, hence 10%.
        Words(page3).Should().BeCloseTo(315, 32);
        // Page breaks after page 3 still differ from pdf.js (#1824 gap 3): not pinned here.
    }
}
