using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Xfa;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #1547 phase 2: structural and safety contracts of <see cref="PdfXfaLayout"/>.
/// What the laid-out pages SHOW is checked with independent tools in
/// Excise.Rendering.Tests (XfaLayoutOracleTests); these tests only check what
/// excise did to the document, and that hostile input cannot hurt it.
/// </summary>
public class XfaLayoutTests
{
    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(pdf);

    [Fact]
    public void PlainDocument_IsNotTouched()
    {
        using var document = Open(TestPdfWithoutXfa());
        var before = document.Pages[0].GetContentStreamBytes();

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(XfaLayoutStatus.NotDynamicXfa);
        document.PageCount.Should().Be(1);
        document.Pages[0].GetContentStreamBytes().Should().Equal(before);
    }

    [Fact]
    public void StaticXfa_IsNotLaidOut()
    {
        // A static form (AcroForm widgets present) is shown through its AcroForm.
        using var document = Open(XfaTestForms.BuildPdf(XfaTestForms.PositionedTemplate(), needsRendering: false));
        document.AddTextField(1, new PdfRectangle(72, 600, 300, 620), "name");

        document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.NotDynamicXfa);
    }

    [Fact]
    public void XfaWithoutNeedsRendering_KeepsItsOwnPages()
    {
        // No widgets and no /NeedsRendering: classified dynamic, but the pages
        // are the document's real content.
        using var document = Open(XfaTestForms.BuildPdf(XfaTestForms.PositionedTemplate(), needsRendering: false));
        document.DetectXfaForm().Should().Be(PdfXfaFormKind.Dynamic, "precondition");
        var before = document.Pages[0].GetContentStreamBytes();

        document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.NotDynamicXfa);
        document.Pages[0].GetContentStreamBytes().Should().Equal(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DynamicForm_ReplacesThePlaceholderPage_AndKeepsTheXfaForm(bool singleStream)
    {
        using var document = Open(XfaTestForms.BuildPdf(XfaTestForms.PositionedTemplate(), singleStream: singleStream));
        var placeholder = document.Pages[0].Dictionary;

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        result.PageCount.Should().Be(1);
        document.PageCount.Should().Be(1);
        document.Pages[0].Dictionary.Should().NotBeSameAs(placeholder);
        document.Pages[0].Width.Should().Be(612);
        document.Pages[0].Height.Should().Be(792);
        document.HasXfaLayoutPages().Should().BeTrue();
        document.DetectXfaForm().Should().Be(PdfXfaFormKind.Dynamic, "the XFA form is kept for XFA viewers");
    }

    [Fact]
    public void SavedLayout_IsNotLaidOutAgainOnReopen()
    {
        byte[] saved;
        using (var document = Open(XfaTestForms.BuildPdf(XfaTestForms.PositionedTemplate())))
        {
            document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
                .Status.Should().Be(XfaLayoutStatus.LaidOut);
            saved = document.SaveToBytes();
        }

        using var reopened = Open(saved);
        reopened.HasXfaLayoutPages().Should().BeTrue();
        var content = reopened.Pages[0].GetContentStreamBytes();

        reopened.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.AlreadyLaidOut);
        reopened.PageCount.Should().Be(1);
        reopened.Pages[0].GetContentStreamBytes().Should().Equal(content);
    }

    [Fact]
    public void AddingAPage_ClearsTheLaidOutMark()
    {
        using var document = Open(XfaTestForms.BuildPdf(XfaTestForms.PositionedTemplate()));
        document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        document.Pages.AddBlank();

        document.HasXfaLayoutPages().Should().BeFalse("only pages excise generated are a rendition of the form");
    }

    [Fact]
    public void RepeatingSubform_FollowsTheData_AndPaginates()
    {
        // 60 rows of 0.5in in a 10.5in content area: 21 per page, 3 pages.
        var template = XfaTestForms.Template(
            "<subform name=\"Row\" layout=\"tb\" w=\"8in\"><occur min=\"1\" max=\"-1\"/>"
            + "<field name=\"Label\" w=\"8in\" h=\"0.5in\"><ui><textEdit/></ui></field></subform>");
        var data = XfaTestForms.Data(string.Concat(Enumerable.Range(1, 60).Select(i => $"<Row><Label>Row {i}</Label></Row>")));
        using var document = Open(XfaTestForms.BuildPdf(template, data));

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        document.PageCount.Should().Be(3);
    }

    [Fact]
    public void BreakBefore_StartsANewPage_OnTheNamedPageArea()
    {
        var pageSet =
            "<pageSet><pageArea name=\"Portrait\" id=\"Portrait\"><contentArea x=\"0.5in\" y=\"0.5in\" w=\"7.5in\" h=\"10in\"/>"
            + "<medium stock=\"letter\" short=\"8.5in\" long=\"11in\"/></pageArea>"
            + "<pageArea name=\"Wide\" id=\"Wide\"><contentArea x=\"0.5in\" y=\"0.5in\" w=\"10in\" h=\"7.5in\"/>"
            + "<medium stock=\"letter\" short=\"8.5in\" long=\"11in\" orientation=\"landscape\"/></pageArea></pageSet>";
        var template = XfaTestForms.Template(
            "<subform name=\"First\" w=\"7in\" h=\"1in\"/>"
            + "<subform name=\"Second\" w=\"7in\" h=\"1in\"><breakBefore targetType=\"pageArea\" target=\"#Wide\"/></subform>",
            pageSet: pageSet);
        using var document = Open(XfaTestForms.BuildPdf(template));

        document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.LaidOut);

        document.PageCount.Should().Be(2);
        document.Pages[0].Width.Should().Be(612);
        document.Pages[1].Width.Should().Be(792);
        document.Pages[1].Height.Should().Be(612);
    }

    [Fact]
    public void EvenAndOddBreaks_InsertBlankPagesForParity()
    {
        // PDFium's xfa_break_before_after.pdf, reduced: full-page subforms with
        // every break kind. Worked by hand: p1 A; p2 B (contentArea break);
        // p3 C, pending contentArea break; p4 D; p5 E, pending page break;
        // p6 F (already even); p7 G, pending even break -> p8 blank;
        // H wants odd -> p9 H; p10 I. The trailing break adds nothing.
        string Page(string name, string brk) =>
            $"<subform name=\"{name}\" w=\"7in\" h=\"10in\">{brk}</subform>";
        var pageSet = "<pageSet><pageArea name=\"P\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"8in\" h=\"10.5in\"/>"
            + "<medium stock=\"letter\"/></pageArea></pageSet>";
        var template = XfaTestForms.Template(
            Page("A", "")
            + Page("B", "<breakBefore targetType=\"contentArea\"/>")
            + Page("C", "<breakAfter targetType=\"contentArea\"/>")
            + Page("D", "<breakBefore targetType=\"pageArea\"/>")
            + Page("E", "<breakAfter targetType=\"pageArea\"/>")
            + Page("F", "<breakBefore targetType=\"pageEven\"/>")
            + Page("G", "<breakAfter targetType=\"pageEven\"/>")
            + Page("H", "<breakBefore targetType=\"pageOdd\"/>")
            + Page("I", "<breakAfter targetType=\"pageOdd\"/>"),
            pageSet: pageSet);
        using var document = Open(XfaTestForms.BuildPdf(template));

        document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.LaidOut);
        document.PageCount.Should().Be(10);
    }

    [Fact]
    public void Scripts_AreReportedByEvent_NotRun()
    {
        var template = XfaTestForms.Template(
            "<field name=\"A\" w=\"2in\" h=\"0.3in\"><ui><textEdit/></ui>"
            + "<event activity=\"initialize\"><script contentType=\"application/x-javascript\">this.rawValue = 'x';</script></event>"
            + "<calculate><script>1 + 1</script></calculate></field>");
        using var document = Open(XfaTestForms.BuildPdf(template));

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.ScriptsNotRun.Should().Contain(new KeyValuePair<string, int>("initialize", 1));
        result.ScriptsNotRun.Should().Contain(new KeyValuePair<string, int>("calculate", 1));
    }

    [Fact]
    public void NoTemplatePacket_Fails_AndLeavesTheDocumentUntouched()
    {
        // The phase-1 fixture shape: a <template/> outside the XFA namespace.
        using var document = Open(XfaTestForms.BuildPdf("<template/>"));
        var before = document.Pages[0].GetContentStreamBytes();

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(XfaLayoutStatus.Failed);
        result.FailureReason.Should().NotBeNullOrEmpty();
        document.PageCount.Should().Be(1);
        document.Pages[0].GetContentStreamBytes().Should().Equal(before);
        document.HasXfaLayoutPages().Should().BeFalse();
    }

    [Fact]
    public void TruncatedXml_Fails()
    {
        using var document = Open(XfaTestForms.BuildPdf($"<template xmlns=\"{XfaTestForms.TemplateNamespace}\"><subform>"));

        document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.Failed);
        document.PageCount.Should().Be(1);
    }

    [Fact]
    public void Dtd_IsRefused_SoNoEntityIsExpanded()
    {
        var xdp = "<!DOCTYPE xdp:xdp [<!ENTITY x SYSTEM \"file:///etc/hosts\">]>"
            + "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">"
            + XfaTestForms.Template("<draw name=\"D\" w=\"2in\" h=\"1in\"><value><text>&x;</text></value></draw>")
            + "</xdp:xdp>";
        using var document = Open(XfaTestForms.BuildPdfFromXdp(xdp));

        document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.Failed, "a DTD is prohibited, not resolved");
    }

    [Fact]
    public void DeepNesting_Fails_WithinTheBound()
    {
        var depth = XfaBudget.MaxDepth + 10;
        var body = string.Concat(Enumerable.Repeat("<subform layout=\"tb\">", depth))
            + string.Concat(Enumerable.Repeat("</subform>", depth));
        using var document = Open(XfaTestForms.BuildPdf(XfaTestForms.Template(body)));

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(XfaLayoutStatus.Failed);
        result.FailureReason.Should().Contain("deeper");
        document.PageCount.Should().Be(1);
    }

    [Fact]
    public void UnboundedRepeats_AreCapped()
    {
        // initial="1000000" would be a million instances; the per-subform cap holds.
        var template = XfaTestForms.Template(
            "<subform name=\"R\" w=\"1in\" h=\"0.01in\"><occur min=\"0\" max=\"-1\" initial=\"1000000\"/></subform>");
        using var document = Open(XfaTestForms.BuildPdf(template));

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        // 2000 × 0.72pt = 1440pt: two 756pt content areas.
        document.PageCount.Should().Be(2);
    }

    [Fact]
    public void PageBomb_Fails_AtThePageCap()
    {
        var template = XfaTestForms.Template(
            "<subform name=\"R\" w=\"1in\" h=\"11in\"><occur min=\"0\" max=\"-1\" initial=\"1500\"/></subform>");
        using var document = Open(XfaTestForms.BuildPdf(template));

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(XfaLayoutStatus.Failed);
        result.FailureReason.Should().Contain("pages");
        document.PageCount.Should().Be(1, "a failed layout leaves the placeholder");
    }

    [Fact]
    public void CircularPrototype_DoesNotLoop()
    {
        var template = XfaTestForms.Template(
            "<proto><font id=\"a\" use=\"#b\" size=\"12pt\"/><font id=\"b\" use=\"#a\" weight=\"bold\"/></proto>"
            + "<draw name=\"D\" w=\"2in\" h=\"1in\"><font use=\"#a\"/><value><text>Hello</text></value></draw>");
        using var document = Open(XfaTestForms.BuildPdf(template));

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        result.Omissions.Should().Contain(o => o.Contains("circular prototype"));
    }

    [Fact]
    public void OtherFilePrototype_IsNeverLoaded()
    {
        var template = XfaTestForms.Template(
            "<draw name=\"D\" w=\"2in\" h=\"1in\" usehref=\"https://example.invalid/fragment.xdp#som($template.#subform)\">"
            + "<value><text>Hello</text></value></draw>");
        using var document = Open(XfaTestForms.BuildPdf(template));

        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);

        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        result.Omissions.Should().Contain(o => o.Contains("another file"));
    }

    [Fact]
    public void Cancellation_Throws_AndLeavesTheDocumentUntouched()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The budget checks the token every few ticks, so a large enough form
        // is guaranteed to observe it.
        var big = XfaTestForms.Template(
            "<subform name=\"R\" w=\"1in\" h=\"0.01in\"><occur min=\"0\" max=\"-1\" initial=\"1000\"/></subform>");
        using var bigDocument = Open(XfaTestForms.BuildPdf(big));

        var act = () => bigDocument.ApplyXfaLayout(cancellationToken: cts.Token);

        act.Should().Throw<OperationCanceledException>();
        bigDocument.PageCount.Should().Be(1);
        bigDocument.HasXfaLayoutPages().Should().BeFalse();
    }

    [Fact]
    public void RemoveXfaForm_DropsTheEntries_AndThePacketStreams()
    {
        const string secret = "Zanzibar Quillfeather";
        var data = XfaTestForms.Data($"<FullName>{secret}</FullName>");
        using var document = Open(XfaTestForms.BuildPdf(XfaTestForms.PositionedTemplate(), data));

        document.RemoveXfaForm().Should().BeTrue();

        document.DetectXfaForm().Should().Be(PdfXfaFormKind.None);
        document.Catalog.ContainsKey("NeedsRendering").Should().BeFalse();
        // Carrier-agnostic: the saved bytes, streams inflated, by a scanner
        // that is not excise's parser.
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), secret).Should().BeEmpty();
    }

    [Fact]
    public void UnicodeParagraphSeparator_BreaksTheLine()
    {
        var paragraphs = XfaText.PlainParagraphs("Class: node \u2029Object: caption", XfaFontSpec.Default);

        paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text)))
            .Should().Equal("Class: node ", "Object: caption");
    }

    [Fact]
    public void Measurements_UseXfaUnits()
    {
        XfaMeasure.Parse("0.25in").Should().BeApproximately(18, 1e-9);
        XfaMeasure.Parse("25.4mm").Should().BeApproximately(72, 1e-9);
        XfaMeasure.Parse("2.54cm").Should().BeApproximately(72, 1e-9);
        XfaMeasure.Parse("10pt").Should().Be(10);
        XfaMeasure.Parse("1000mp").Should().Be(1);
        XfaMeasure.Parse("2").Should().Be(144, "a bare length is in inches");
        XfaMeasure.Parse("12", "pt").Should().Be(12);
        XfaMeasure.Parse("wide").Should().BeNull();
        XfaMeasure.Parse(null).Should().BeNull();
    }

    private static byte[] TestPdfWithoutXfa()
    {
        var document = PdfDocument.CreateNew();
        var page = document.Pages.AddBlank();
        using (var graphics = page.GetGraphics())
            graphics.DrawString("Plain", Excise.Core.Graphics.PdfFont.Helvetica(12), Excise.Core.Graphics.PdfBrush.Black, 72, 700);
        return document.SaveToBytes();
    }
}
