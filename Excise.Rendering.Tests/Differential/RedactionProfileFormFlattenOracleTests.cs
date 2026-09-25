using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1857: Maximum's form flatten, read back by tools that are not excise.
/// <c>RedactionProfileTests</c> holds the same two-sided assertion with
/// <c>SavedPdfLeakScanner</c>; this is where qpdf and mutool are.
/// </summary>
public sealed class RedactionProfileFormFlattenOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"form-flatten-oracle-{Guid.NewGuid():N}");

    public RedactionProfileFormFlattenOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Redact(CarrierTrapFixtures.Trap trap, RedactionOptions options, string name)
    {
        var path = Path.Combine(_dir, name + ".pdf");
        using var doc = PdfDocument.Open(trap.Build(false));
        doc.RedactText("NOMATCHXYZ", options);
        doc.Save(path);
        return path;
    }

    /// <summary>qpdf's own reading of the form: whether it has one, and each field's value.</summary>
    private static (bool HasForm, string[] Values) QpdfAcroForm(string path)
    {
        var json = CarrierTrapIndependentCorroborationTests.RunTool("qpdf", "--json", "--json-key=acroform", path)
                   ?? throw new InvalidOperationException("qpdf --json failed on " + path);
        using var doc = JsonDocument.Parse(json);
        var acroform = doc.RootElement.GetProperty("acroform");
        return (acroform.GetProperty("hasacroform").GetBoolean(),
            acroform.GetProperty("fields").EnumerateArray()
                .Select(f => f.GetProperty("value").GetString() ?? "").ToArray());
    }

    /// <summary>Every dictionary and string in the file, without stream data.</summary>
    private static string QpdfObjectsWithoutStreams(string path) =>
        Encoding.UTF8.GetString(CarrierTrapIndependentCorroborationTests.RunTool(
            "qpdf", "--json", "--json-key=qpdf", "--json-stream-data=none", path)
            ?? throw new InvalidOperationException("qpdf --json failed on " + path));

    /// <summary><c>mutool show</c> of page 1's own dictionary.</summary>
    private static string MutoolPageDictionary(string path)
    {
        var pages = Encoding.Latin1.GetString(
            CarrierTrapIndependentCorroborationTests.RunTool("mutool", "show", path, "pages") ?? Array.Empty<byte>());
        var number = Regex.Match(pages, @"page 1 = (\d+) 0 R").Groups[1].Value;
        number.Should().NotBeEmpty($"mutool must list page 1: {pages}");
        return Encoding.Latin1.GetString(
            CarrierTrapIndependentCorroborationTests.RunTool("mutool", "show", path, number) ?? Array.Empty<byte>());
    }

    [Fact]
    public void Maximum_LeavesNoFormForQpdfOrMutool_StandardKeepsIt()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable && MutoolReferenceRenderer.IsAvailable,
            "qpdf and mutool are the independent readers (brew install qpdf mupdf-tools)");
        var trap = CarrierTrapFixtures.Get("acroform-all-carriers");
        var t = trap.Token;
        var fieldOnly = new[] { t + "DEFAULT", t + "RICH", t + "CAPTION", t + "TOOLTIP" };

        // Standard, the planted failure: qpdf reads the form and its value, and
        // the widget is in the page's /Annots.
        var standard = Redact(trap, RedactionOptions.Default, "standard");
        var (standardHasForm, standardValues) = QpdfAcroForm(standard);
        standardHasForm.Should().BeTrue();
        standardValues.Should().Contain(v => v.Contains(t + "VALUE"));
        var standardDump = CarrierTrapIndependentCorroborationTests.QpdfDump(standard);
        foreach (var token in fieldOnly)
            standardDump.Should().Contain(token, "Standard keeps the form, so the fixture carries every field carrier");
        MutoolPageDictionary(standard).Should().MatchRegex(@"/Annots \[ \d+ 0 R \]");

        var maximum = Redact(trap, RedactionOptions.Maximum, "maximum");
        var (maximumHasForm, maximumValues) = QpdfAcroForm(maximum);
        (!maximumHasForm || maximumValues.Length == 0).Should().BeTrue(
            "#1857: the flatten did nothing and /AcroForm /Fields still reached the field");
        var maximumDump = CarrierTrapIndependentCorroborationTests.QpdfDump(maximum);
        foreach (var token in fieldOnly)
            maximumDump.Should().NotContain(token, $"{token}: no field dictionary survives a Maximum flatten");
        MutoolPageDictionary(maximum).Should().NotMatchRegex(@"/Annots \[ \d+ 0 R",
            "no widget is left in the page's /Annots");

        // The value is the one carrier that stays, and only as painted page
        // text: in the page per mutool, and in no dictionary per qpdf.
        MutoolTextExtractor.ExtractPage(maximum, 1).Should().Contain(t + "VALUE");
        QpdfObjectsWithoutStreams(maximum).Should().NotContain(t + "VALUE");
    }
}
