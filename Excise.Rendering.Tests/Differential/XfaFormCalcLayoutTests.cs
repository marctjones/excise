using System.Diagnostics;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1570: what a dynamic form SHOWS after its FormCalc scripts ran, read back by mutool from the saved
/// file (not by excise). The expected text is the arithmetic the scripts state.
/// </summary>
public class XfaFormCalcLayoutTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static string Field(string name, string inner = "") =>
        $"<field name=\"{name}\" w=\"3in\" h=\"0.4in\"><ui><textEdit/></ui><font typeface=\"Arial\" size=\"10pt\"/>{inner}</field>";

    private static string Calc(string script) => $"<calculate><script>{script}</script></calculate>";

    private static string Init(string script, string contentType = "application/x-formcalc") =>
        $"<event activity=\"initialize\"><script contentType=\"{contentType}\">{script}</script></event>";

    private (string Text, XfaLayoutResult Result) LayOut(string body, string? data = null, XfaLayoutOptions? options = null)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        using var document = PdfDocument.Open(XfaTestForms.BuildPdf(XfaTestForms.Template(body), data));
        var result = document.ApplyXfaLayout(options, TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        var path = Path.Combine(Path.GetTempPath(), $"excise-fc-{Guid.NewGuid():N}.pdf");
        document.Save(path);
        _temp.Add(path);
        var pages = MutoolTextExtractor.ExtractAllPages(path, 1);
        pages.Should().NotBeNull("mutool must read the laid-out file");
        return (pages![0], result);
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void Calculate_ShowsTheSumOfTwoFields()
    {
        var (text, result) = LayOut(
            Field("A") + Field("B") + Field("Total", Calc("A + B")),
            XfaTestForms.Data("<A>3</A><B>4</B>"));

        Lines(text).Should().Equal("3", "4", "7");
        result.ScriptsRun.Should().ContainKey("calculate");
        result.FieldsWrittenByScripts.Should().Contain("Total");
        result.ScriptFailures.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_Settles_WhenAFieldDependsOnALaterOne()
    {
        // C is read before B is computed; a second pass gives it the right value (5 * 2 + 1).
        var (text, _) = LayOut(
            Field("A") + Field("C", Calc("B + 1")) + Field("B", Calc("A * 2")),
            XfaTestForms.Data("<A>5</A>"));

        Lines(text).Should().Equal("5", "11", "10");
    }

    [Fact]
    public void Initialize_CanWriteAValue()
    {
        var (text, result) = LayOut(Field("Greeting", Init("$ = Concat(\"Hello\", \" there\")")));

        Lines(text).Should().Equal("Hello there");
        result.ScriptsRun.Should().ContainKey("initialize");
    }

    [Fact]
    public void Presence_HidesAnotherField_AndItsSpaceIsGone()
    {
        var (text, _) = LayOut(
            Field("Keep", Init("Gone.presence = \"hidden\"")) + Field("Gone") + Field("After"),
            XfaTestForms.Data("<Keep>KEEP</Keep><Gone>GONE</Gone><After>AFTER</After>"));

        Lines(text).Should().Equal("KEEP", "AFTER");
    }

    [Fact]
    public void Presence_IsPerInstance_NotWrittenToTheSharedTemplate()
    {
        // One template subform, three data rows: only the middle instance hides itself.
        var template = "<subform name=\"Row\" layout=\"tb\" w=\"3in\"><occur min=\"1\" max=\"-1\"/>"
            + Field("Label", Init("if ($ == \"row-b\") then $.presence = \"hidden\" endif")) + "</subform>";
        var data = XfaTestForms.Data("<Row><Label>row-a</Label></Row><Row><Label>row-b</Label></Row><Row><Label>row-c</Label></Row>");

        var (text, _) = LayOut(template, data);

        Lines(text).Should().Equal("row-a", "row-c");
    }

    [Fact]
    public void AFailingScript_IsUndone_AndTheOthersStillRun()
    {
        var (text, result) = LayOut(
            Field("A") + Field("B") + Field("Broken", Calc("A = 99\n 1 / 0")) + Field("Total", Calc("A + B")),
            XfaTestForms.Data("<A>3</A><B>4</B>"));

        // The failed script's write to A is rolled back; the good script sees the original values.
        Lines(text).Should().Equal("3", "4", "7");
        result.ScriptFailures.Should().ContainSingle().Which.Should().Contain("Division by zero");
    }

    [Fact]
    public void RunFormCalcOff_LeavesTheFormAsTheDataSaysIt()
    {
        var (text, result) = LayOut(
            Field("A") + Field("B") + Field("Total", Calc("A + B")),
            XfaTestForms.Data("<A>3</A><B>4</B>"),
            new XfaLayoutOptions { RunFormCalc = false });

        Lines(text).Should().Equal("3", "4");
        result.ScriptsRun.Should().BeEmpty();
        result.ScriptsNotRun.Should().ContainKey("calculate");
    }

    [Fact]
    public void JavaScript_IsNeverRun()
    {
        var (text, result) = LayOut(Field("X", Init("this.rawValue = \"from JS\";", "application/x-javascript")));

        text.Should().NotContain("from JS");
        result.ScriptsNotRun.Should().ContainKey("initialize");
        result.ScriptsRun.Should().BeEmpty();
    }

    [Fact]
    public void ANonSettlingForm_StopsAtThePassCap_AndSaysSo()
    {
        var (_, result) = LayOut(
            Field("P", Calc("Q + 1")) + Field("Q", Calc("P + 1")));

        result.Omissions.Should().Contain(o => o.Contains("still changing", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostileScript_CostsTheLimit_NotTheForm()
    {
        var clock = Stopwatch.StartNew();
        var (text, result) = LayOut(
            Field("Loop", Init("while (1) do endwhile")) + Field("Fine", Init("$ = \"still here\"")));

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
        Lines(text).Should().Equal("still here");
        result.ScriptFailures.Should().ContainSingle();
    }

    [Fact]
    public void ScriptsThatCallOutside_DoNotReachAnything()
    {
        var (text, result) = LayOut(
            Field("X", Init("Get(\"https://example.invalid/x\")")) + Field("Y", Init("xfa.host.messageBox(\"hi\")")));

        text.Trim().Should().BeEmpty();
        result.ScriptFailures.Should().HaveCount(2);
    }
}

/// <summary>
/// #1570 and redaction. A script can hold a secret in its own text and can produce a value that is
/// not in the data at all, so redacting a laid-out form must take out the pages' text and the whole
/// XFA packet (template scripts included). Checked with tools that are not excise: the saved bytes
/// with streams inflated, and mutool's text and catalog.
/// </summary>
public class XfaFormCalcRedactionTests : IDisposable
{
    private const string Secret = "Quillfeather";
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static string Field(string name, string inner = "") =>
        $"<field name=\"{name}\" w=\"3in\" h=\"0.4in\"><ui><textEdit/></ui><font typeface=\"Arial\" size=\"10pt\"/>{inner}</field>";

    private void RedactAndAssertGone(string body, string? data)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        using var document = PdfDocument.Open(XfaTestForms.BuildPdf(XfaTestForms.Template(body), data));
        var laidOut = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        laidOut.Status.Should().Be(XfaLayoutStatus.LaidOut);
        laidOut.ScriptsRun.Should().NotBeEmpty("the secret must have come from a script for this test to mean anything");

        var path = Path.Combine(Path.GetTempPath(), $"excise-fc-redact-{Guid.NewGuid():N}.pdf");
        MutoolTextExtractor.ExtractPage(SaveCopy(document, path), 1).Should().Contain(Secret, "it is on the page before redaction");

        document.RedactText(Secret);
        var redacted = Path.Combine(Path.GetTempPath(), $"excise-fc-redacted-{Guid.NewGuid():N}.pdf");
        document.Save(redacted);
        _temp.Add(redacted);

        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(redacted), Secret).Should().BeEmpty(
            "neither the page nor the XFA packet, script text included, may keep the term");
        MutoolTextExtractor.ExtractPage(redacted, 1).Should().NotContain(Secret);
    }

    private string SaveCopy(PdfDocument document, string path)
    {
        document.Save(path);
        _temp.Add(path);
        return path;
    }

    [Fact]
    public void ASecretAScriptWrites_LiteralInItsSource_IsRedactedEverywhere() =>
        RedactAndAssertGone(
            Field("Out", $"<event activity=\"initialize\"><script>$ = \"{Secret}\"</script></event>"), data: null);

    [Fact]
    public void ASecretOnlyACalculationProduces_IsRedactedEverywhere() =>
        RedactAndAssertGone(
            Field("A") + Field("B") + Field("Whole", "<calculate><script>Concat(A, B)</script></calculate>"),
            XfaTestForms.Data("<A>Quill</A><B>feather</B>"));
}
