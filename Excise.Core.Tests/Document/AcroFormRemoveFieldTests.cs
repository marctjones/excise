using AwesomeAssertions;
using Excise.Core.Document;
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
}
