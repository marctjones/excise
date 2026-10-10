using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Tests.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// RemoveField is the inverse of the AcroForm authoring methods (#1811). The saved bytes are
/// judged by SavedPdfLeakScanner (which decompresses streams), not by excise's own parser.
/// </summary>
public class AcroFormRemoveFieldTests
{
    private static PdfDocument TwoFieldDocument()
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "keepthisfield");
        doc.AddTextField(1, new PdfRectangle(72, 650, 300, 670), "dropthisfield", defaultValue: "DROPPEDVALUE");
        return doc;
    }

    [Fact]
    public void RemoveField_TakesTheFieldOutOfTheFormAndThePage_AndLeavesTheOtherAlone()
    {
        using var doc = TwoFieldDocument();

        doc.RemoveField("dropthisfield").Should().BeTrue();

        doc.GetAcroForm()!.Fields.Select(f => f.FullName).Should().Equal("keepthisfield");
        doc.GetPage(1).GetAnnotations().Should().ContainSingle("only the kept field's widget stays on the page");
    }

    [Fact]
    public void RemoveField_LeavesNoTraceOfTheFieldInTheSavedBytes()
    {
        using var doc = TwoFieldDocument();
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "dropthisfield").Should().NotBeEmpty(
            "control: the name is in the file before the removal");

        doc.RemoveField("dropthisfield");
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, "dropthisfield").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "DROPPEDVALUE").Should().BeEmpty(
            "the removed field's value must not survive as an unreachable object");
        SavedPdfLeakScanner.FindTerm(saved, "keepthisfield").Should().NotBeEmpty();
    }

    [Fact]
    public void RemoveField_ReportsFalseForAFieldThatIsNotThere()
    {
        using var doc = TwoFieldDocument();
        doc.RemoveField("nosuchfield").Should().BeFalse();
        doc.GetAcroForm()!.Fields.Should().HaveCount(2);
    }

    [Fact]
    public void RemoveField_ThenAddAgain_RestoresAWorkingField()
    {
        using var doc = TwoFieldDocument();
        doc.RemoveField("dropthisfield");

        doc.AddTextField(1, new PdfRectangle(72, 650, 300, 670), "dropthisfield");

        doc.GetAcroForm()!.Fields.Select(f => f.FullName).Should().BeEquivalentTo(new[] { "keepthisfield", "dropthisfield" });
    }

    [Fact]
    public void RemoveField_ListedInCalculationOrder_IsNotInTheSavedBytes()
    {
        // #2015: /AcroForm /CO still named the removed field and kept it, and its value, reachable.
        using var doc = TwoFieldDocument();
        var acroForm = (PdfDictionary)doc.Resolve(doc.Catalog["AcroForm"]);
        var dropped = doc.GetAcroForm()!.FindField("dropthisfield")!;
        acroForm["CO"] = new PdfArray { doc.GetReferenceTo(dropped.RawDictionary)! };

        doc.RemoveField("dropthisfield").Should().BeTrue();
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, "DROPPEDVALUE").Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        var reopenedForm = (PdfDictionary)reopened.Resolve(reopened.Catalog["AcroForm"]);
        if (reopenedForm.GetOptional("CO") is { } co)
            ((PdfArray)reopened.Resolve(co)).Should().BeEmpty("no /CO entry names a field that is gone");
    }
}
