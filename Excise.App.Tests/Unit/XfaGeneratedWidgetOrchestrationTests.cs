using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Core.Xfa;
using Excise.Ocr;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #2028 (XFA Phase 3 S1) at the orchestration layer. Decision 17: the AcroForm widgets excise
/// generated for a dynamic XFA form are baked into their pages BEFORE a redaction runs. Since #2037
/// the engine does it (<c>PdfXfaLayout.RemoveXfaFormForRedaction</c>), so these check that the
/// orchestrators (<see cref="RedactionService"/> for areas, <see cref="TermRedactionRunner"/> for
/// terms, which the CLI also runs) still get it and still tell the user. And the form overlay leaves
/// the generated fields alone: S1 is display only. Independent-tool checks of the same rules (mutool,
/// qpdf) are in Excise.Rendering.Tests (XfaGeneratedWidgetOracleTests).
/// </summary>
public sealed class XfaGeneratedWidgetOrchestrationTests : IDisposable
{
    private const string Secret = "Quillfeather";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-xfa-s1-app-{Guid.NewGuid():N}");

    public XfaGeneratedWidgetOrchestrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>FullName shows the secret; Shadow, hidden, binds the same data node.</summary>
    private static PdfDocument LaidOutForm()
    {
        var document = PdfDocument.Open(XfaTestForms.BuildPdf(
            XfaTestForms.Template(
                "<field name=\"FullName\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui></field>"
                + "<field name=\"Shadow\" presence=\"hidden\" x=\"1in\" y=\"3in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui>"
                + "<bind match=\"dataRef\" ref=\"$record.FullName\"/></field>",
                layout: "position"),
            XfaTestForms.Data($"<FullName>Jane {Secret}</FullName>")));
        document.ApplyXfaLayout().Status.Should().Be(XfaLayoutStatus.LaidOut);
        document.GetAcroForm()!.FindField("form1[0].Shadow[0]")!.Value.Should().Contain(Secret,
            "fixture sanity: the hidden generated field holds the value");
        return document;
    }

    [Fact]
    public void RedactionService_RedactArea_FlattensTheGeneratedFieldsFirst_AndReportsIt()
    {
        using var document = LaidOutForm();
        var service = new RedactionService(NullLogger<RedactionService>.Instance, new NullLoggerFactory());

        // FullName's box, top-left page points: content area inset 18pt, field at 1in.
        var page = document.GetPage(1);
        service.RedactArea(page, PdfPageRect.VisualPoints(1, 88, 88, 296, 32), RedactionOptions.Default with { DrawBox = false });

        document.RedactionLedger.XfaRemovals.Should().Contain(r => r.StartsWith("generated XFA fields flattened", StringComparison.Ordinal),
            "decision 17's report row reaches the redacted-copy report");
        (document.GetAcroForm()?.Fields ?? Array.Empty<PdfField>()).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Secret).Should().BeEmpty(
            "without the flatten the hidden widget's /V keeps the value (planted runs recorded on #2037)");
    }

    [Fact]
    public void TermRedactionRunner_FlattensTheGeneratedFieldsFirst_AndSaysSo()
    {
        var input = Path.Combine(_dir, "laid-out.pdf");
        var output = Path.Combine(_dir, "redacted.pdf");
        using (var document = LaidOutForm())
            document.Save(input);

        var result = TermRedactionRunner.Execute(new TermRedactionRequest(input, output, Secret, RedactionOptions.Default)
        {
            AllowLowConfidence = true,
        });

        result.CarrierNotes.Should().Contain(n => n.Contains("generated XFA fields flattened", StringComparison.Ordinal));
        var saved = File.ReadAllBytes(output);
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        (reopened.GetAcroForm()?.Fields ?? Array.Empty<PdfField>()).Should().BeEmpty(
            "the generated fields were baked into the page before the redaction");
        reopened.DetectXfaForm().Should().Be(PdfXfaFormKind.None, "decision 5 still removes /XFA");
    }

    [Fact]
    public void FormOverlay_LeavesGeneratedXfaFieldsOut_AndKeepsOtherFields()
    {
        using var document = LaidOutForm();
        var page = document.GetPage(1);
        page.GetFormFields().Should().NotBeEmpty("fixture sanity: the generated fields are on the page");

        MainWindowViewModel.OverlayFields(page).Should().BeEmpty(
            "S1 is display only: the page already shows the generated widgets' appearances");

        document.AddTextField(1, new PdfRectangle(400, 100, 550, 120), "UserNote");
        MainWindowViewModel.OverlayFields(document.GetPage(1)).Select(f => f.FullName).Should().Equal("UserNote");
    }
}
