using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.TestSupport;
using Back = Excise.TestSupport.RemovedPageBackReference;
using F = Excise.TestSupport.RemovedPageFixtures;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #2015: the save after a widget delete cuts the widget, its emptied parent field and the parent's
/// /CO entry; restoring the snapshot taken before the delete brings all three back, in order.
/// Judged on the saved bytes (leak scanner) and a reopened document, not on the live objects.
/// </summary>
public sealed class FieldTreeLinkSnapshotTests
{
    [Fact]
    public void DeleteWidget_Save_Restore_PutsTheParentFieldBackInFieldsAndCalculationOrder()
    {
        using var doc = PdfDocument.Open(F.Build(Back.AcroFormFieldKids, "1.7"));
        var widget = doc.GetPage(2).GetAnnotations().Single(a => a.RawDictionary.GetOptional("Subtype") is PdfName { Value: "Widget" });
        var snapshot = FieldTreeLinkSnapshot.Capture(doc, widget.RawDictionary);
        snapshot.ListCount.Should().Be(3, "the /Fields, /CO and parent /Kids lists name the widget or its field");

        doc.RemoveAnnotation(2, widget).Should().BeTrue();
        var cut = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(cut, F.FieldValue).Should().BeEmpty("control: the save cut the field");
        doc.GetAcroForm()!.Fields.Should().BeEmpty("control: the save cut the emptied field from /Fields");

        snapshot.Restore();
        ((PdfArray)doc.Resolve(doc.GetPage(2).Dictionary["Annots"])).Add(doc.GetReferenceTo(widget.RawDictionary)!);
        var restored = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(restored, F.FieldValue).Should().NotBeEmpty();
        using var reopened = PdfDocument.Open(restored);
        reopened.GetAcroForm()!.Fields.Select(f => f.FullName).Should().Equal("parent");
        var acroForm = (PdfDictionary)reopened.Resolve(reopened.Catalog["AcroForm"]);
        ((PdfArray)reopened.Resolve(acroForm["CO"])).Should().HaveCount(1);
        reopened.GetPage(2).GetAnnotations().Should().HaveCount(2, "the note and the restored widget");
    }

    [Fact]
    public void Capture_OfAnAnnotationThatIsNotAFormWidget_HoldsNothing()
    {
        using var doc = PdfDocument.Open(F.Build(Back.AcroFormFieldKids, "1.7"));
        var other = doc.GetPage(2).GetAnnotations().First(a => a.RawDictionary.GetOptional("Subtype") is not PdfName { Value: "Widget" });
        FieldTreeLinkSnapshot.Capture(doc, other.RawDictionary).ListCount.Should().Be(0);
    }
}
