using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Fonts;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.Core.Xfa;
using Excise.TestSupport;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #2037: decision 17's flatten of the AcroForm widgets excise generated for a dynamic XFA form runs
/// inside the redaction engine (<c>PdfXfaLayout.RemoveXfaFormForRedaction</c>, the first step of every
/// <c>RedactArea</c>/<c>RedactAreas</c>/<c>RedactText</c>), so a library caller cannot skip it. The leak
/// checks here use the inflating byte scanner; mutool, qpdf and mutool pixels read the same cases in
/// Excise.Rendering.Tests (XfaGeneratedWidgetOracleTests), with the planted runs that prove the order.
/// </summary>
public class XfaRedactionEngineFlattenTests
{
    private const string Secret = "Quillfeather";
    private const string FlattenRow = PdfXfaLayout.GeneratedFieldsFlattenedRow;

    // FullName's box: x 1in..5in, y 1in..1.4in from the top of the content area (inset 0.25in).
    private static readonly PdfRectangle FullNameBox = new(90, 792 - 120, 380, 792 - 88);

    /// <summary>FullName shows the secret; Shadow, hidden, binds the same data node (a hidden generated copy).</summary>
    private static PdfDocument LaidOutHiddenCopyForm(bool emitWidgets = true)
    {
        var document = PdfDocument.Open(XfaTestForms.BuildPdf(
            XfaTestForms.Template(
                "<field name=\"FullName\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui></field>"
                + "<field name=\"Shadow\" presence=\"hidden\" x=\"1in\" y=\"3in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui>"
                + "<bind match=\"dataRef\" ref=\"$record.FullName\"/></field>"
                + "<field name=\"City\" x=\"1in\" y=\"5in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui></field>",
                layout: "position"),
            XfaTestForms.Data($"<FullName>Jane {Secret}</FullName><City>Springfield</City>")));
        var result = document.ApplyXfaLayout(new XfaLayoutOptions { EmitWidgets = emitWidgets }, TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        if (emitWidgets)
            document.GetAcroForm()!.FindField("form1[0].Shadow[0]")!.Value.Should().Be($"Jane {Secret}",
                "fixture sanity: the hidden generated field holds the value (K.2)");
        return document;
    }

    [Fact]
    public void PositiveControl_WithoutRedaction_TheScannerSeesTheHiddenCopy()
    {
        using var document = LaidOutHiddenCopyForm();
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Secret).Should().NotBeEmpty(
            "the check below must be able to see the generated widgets' copies");
    }

    [Fact]
    public void DirectRedactArea_FlattensFirst_AndNoGeneratedFieldOrHiddenCopyIsLeft()
    {
        using var document = LaidOutHiddenCopyForm();
        var page = document.GetPage(1);
        _ = page.Letters;   // a page read before the redaction, as the GUI does

        page.RedactArea(FullNameBox, RedactionOptions.Default with { DrawBox = false });

        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Secret).Should().BeEmpty(
            "the engine flattened the generated fields before removing the box's glyphs; the hidden widget's /V went with them");
        (document.GetAcroForm()?.Fields ?? Array.Empty<PdfField>()).Should().BeEmpty();
        document.RedactionLedger.XfaRemovals.Should().HaveCount(2).And.Satisfy(
            r => r.StartsWith(FlattenRow, StringComparison.Ordinal),
            r => r.StartsWith("/XFA", StringComparison.Ordinal));
        document.RedactionLedger.XfaRemovals[0].Should().StartWith(FlattenRow, "the flatten runs before decision 5");
    }

    [Fact]
    public void DirectRedactAreasWithReport_NamesTheFlatten_OnlyOnTheCallThatDidIt()
    {
        using var document = LaidOutHiddenCopyForm();

        var first = document.GetPage(1).RedactAreasWithReport(new[] { FullNameBox }, RedactionOptions.Default with { DrawBox = false });
        var second = document.GetPage(1).RedactAreasWithReport(new[] { new PdfRectangle(500, 50, 560, 80) }, RedactionOptions.Default with { DrawBox = false });

        first.Carriers.Should().Contain(c => c.Carrier.StartsWith(FlattenRow, StringComparison.Ordinal) && c.Scrubbed);
        first.Carriers.Should().Contain(c => c.Carrier.StartsWith("/XFA", StringComparison.Ordinal) && c.Scrubbed);
        second.Carriers.Should().NotContain(c => c.Carrier.StartsWith(FlattenRow, StringComparison.Ordinal) || c.Carrier.StartsWith("/XFA", StringComparison.Ordinal),
            "an already flattened document has nothing left to flatten or remove");
        document.RedactionLedger.XfaRemovals.Count(r => r.StartsWith(FlattenRow, StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void DirectRedactText_FlattensFirst_AndReportsTheFlattenBeforeTheXfaRemoval()
    {
        using var document = LaidOutHiddenCopyForm();

        var report = document.RedactText(Secret, RedactionOptions.Default);

        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Secret).Should().BeEmpty();
        report.Survived.Should().Be(0);
        var rows = report.Carriers.Select(c => c.Carrier).ToList();
        var flattenAt = rows.FindIndex(r => r.StartsWith(FlattenRow, StringComparison.Ordinal));
        flattenAt.Should().BeGreaterThanOrEqualTo(0);
        rows.FindIndex(r => r.StartsWith("/XFA", StringComparison.Ordinal)).Should().BeGreaterThan(flattenAt);
        (document.GetAcroForm()?.Fields ?? Array.Empty<PdfField>()).Should().BeEmpty();
    }

    [Fact]
    public void DirectRedaction_AfterTheXfaFormWasRemoved_StillFlattensTheWidgets()
    {
        using var laidOut = LaidOutHiddenCopyForm();
        laidOut.RemoveXfaForm().Should().BeTrue();
        using var document = PdfDocument.Open(laidOut.SaveToBytes());

        var report = document.GetPage(1).RedactAreaWithReport(FullNameBox, RedactionOptions.Default with { DrawBox = false });

        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Secret).Should().BeEmpty();
        report.Carriers.Should().ContainSingle(c => c.Carrier.StartsWith(FlattenRow, StringComparison.Ordinal),
            "a laid-out copy whose XFA was removed still carries the generated widgets");
        report.Carriers.Should().NotContain(c => c.Carrier.StartsWith("/XFA", StringComparison.Ordinal));
    }

    /// <summary>Documents with no generated widget: plain, AcroForm only, static XFA, and a Phase 2 layout (marker, no widgets).</summary>
    public static TheoryData<string> NoGeneratedWidgetShapes => new() { "plain", "acroform", "static-xfa", "phase2-layout" };

    private static PdfDocument Shape(string shape)
    {
        switch (shape)
        {
            case "plain":
            {
                var document = PdfDocument.CreateNew();
                var page = document.Pages.AddBlank(612, 792);
                using (var graphics = page.GetGraphics())
                    graphics.DrawString("Plain page", PdfFont.Helvetica(12), PdfBrush.Black, 72, 700);
                return PdfDocument.Open(document.SaveToBytes());
            }
            case "acroform":
            {
                var document = PdfDocument.CreateNew();
                document.Pages.AddBlank(612, 792);
                document.AddTextField(1, new PdfRectangle(100, 600, 300, 620), "Name");
                return PdfDocument.Open(document.SaveToBytes());
            }
            case "static-xfa":
            {
                // Round-tripped once, as the other shapes are, so the comparison saves start from excise's own output.
                using var built = PdfDocument.Open(XfaTestForms.BuildStaticPdf("Jane Static", "Springfield", "only in datasets"));
                return PdfDocument.Open(built.SaveToBytes());
            }
            default:
            {
                using var laidOut = LaidOutHiddenCopyForm(emitWidgets: false);
                return PdfDocument.Open(laidOut.SaveToBytes());
            }
        }
    }

    /// <summary>
    /// Zero behaviour change where there is nothing to flatten: the engine's flatten step returns
    /// nothing, records nothing and writes nothing (the saved bytes are identical), so every redaction
    /// of such a document is exactly what it was before #2037. Its cost is one <c>/PieceInfo</c> lookup
    /// per page, beside the per-page <c>/Annots</c> walk the attachment pass already makes.
    /// </summary>
    [Theory]
    [MemberData(nameof(NoGeneratedWidgetShapes))]
    public void NoGeneratedWidgets_TheEngineFlattenStep_ChangesNothing(string shape)
    {
        using var document = Shape(shape);
        var before = document.SaveToBytes();
        document.SaveToBytes().Should().Equal(before, "fixture sanity: saving is deterministic");

        PdfXfaLayout.FlattenGeneratedXfaFields(document).Should().BeNull();

        document.SaveToBytes().Should().Equal(before, "the flatten step wrote nothing");
        document.RedactionLedger.XfaRemovals.Should().BeEmpty();
        PdfXfaLayout.RemoveXfaFormForRedaction(document).Should().NotContain(r => r.StartsWith(FlattenRow, StringComparison.Ordinal));
        document.RedactionLedger.XfaRemovals.Should().NotContain(r => r.StartsWith(FlattenRow, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(NoGeneratedWidgetShapes))]
    public void NoGeneratedWidgets_ARedactionReportsNoFlatten(string shape)
    {
        using var document = Shape(shape);
        var report = document.GetPage(1).RedactAreaWithReport(new PdfRectangle(60, 680, 200, 720), RedactionOptions.Default with { DrawBox = false });
        report.Carriers.Should().NotContain(c => c.Carrier.StartsWith(FlattenRow, StringComparison.Ordinal));
        report.Carriers.Where(c => c.Carrier.StartsWith("/XFA", StringComparison.Ordinal)).Should().HaveCount(shape is "static-xfa" or "phase2-layout" ? 1 : 0,
            "decision 5 is unchanged");
    }
}
