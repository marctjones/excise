using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.TestSupport;
using Back = Excise.TestSupport.RemovedPageBackReference;
using F = Excise.TestSupport.RemovedPageFixtures;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #2012, same mechanism for a deleted annotation: anything else that still
/// points at it (a reply's /IRT, a structure element's /OBJR, the form's field
/// tree) would keep it, and its text, in the saved file. Oracle: the saved
/// bytes through <see cref="SavedPdfLeakScanner"/>.
/// </summary>
public sealed class RemovedAnnotationLeakTests
{
    [Fact]
    public void DeleteAnnotation_ThatAReplyPointsAt_IsNotInTheFile()
    {
        using var doc = PdfDocument.Open(F.Build(Back.None, "1.7"));
        var note = doc.GetPage(2).GetAnnotations().Single(a => a.RawDictionary.GetOptional("Subtype") is PdfName { Value: "Text" });
        var noteRef = doc.GetReferenceTo(note.RawDictionary)!;
        var reply = new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName("Text"),
            ["Rect"] = new PdfArray { 72, 600, 92, 620 },
            ["Contents"] = new PdfString("KEPTREPLYTEXT"),
            ["IRT"] = noteRef,
        };
        ((PdfArray)doc.Resolve(doc.GetPage(2).Dictionary["Annots"])).Add(doc.AddIndirectObject(reply));

        doc.RemoveAnnotation(2, note).Should().BeTrue();
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, F.AnnotText).Should().BeEmpty("the deleted note must not ship");
        SavedPdfLeakScanner.FindTerm(saved, "KEPTREPLYTEXT").Should().NotBeEmpty("the reply stays");
        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(2).GetAnnotations().Should().ContainSingle();
    }

    [Fact]
    public void DeleteAnnotation_ThatAStructureElementPointsAt_IsNotInTheFile()
    {
        using var doc = PdfDocument.Open(F.Build(Back.None, "1.7"));
        var note = doc.GetPage(2).GetAnnotations().Single(a => a.RawDictionary.GetOptional("Subtype") is PdfName { Value: "Text" });
        var objr = new PdfDictionary
        {
            ["Type"] = new PdfName("OBJR"),
            ["Obj"] = doc.GetReferenceTo(note.RawDictionary)!,
        };
        var element = new PdfDictionary { ["Type"] = new PdfName("StructElem"), ["S"] = new PdfName("Annot"), ["K"] = objr };
        var root = new PdfDictionary { ["Type"] = new PdfName("StructTreeRoot"), ["K"] = doc.AddIndirectObject(element) };
        doc.Catalog["StructTreeRoot"] = doc.AddIndirectObject(root);

        doc.RemoveAnnotation(2, note).Should().BeTrue();
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, F.AnnotText).Should().BeEmpty("the deleted note must not ship");
        using var reopened = PdfDocument.Open(saved);
        reopened.GetStructureTree();
    }

    [Fact]
    public void DeleteWidget_OfAFieldWithNoOtherWidget_DropsTheFieldValue()
    {
        using var doc = PdfDocument.Open(F.Build(Back.AcroFormWidget, "1.7"));
        var widget = doc.GetPage(2).GetAnnotations().Single(a => a.RawDictionary.GetOptional("Subtype") is PdfName { Value: "Widget" });

        doc.RemoveAnnotation(2, widget).Should().BeTrue();
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, F.FieldValue).Should().BeEmpty("a field with no widget left must not ship its value");
        SavedPdfLeakScanner.FindTerm(saved, F.PageText).Should().NotBeEmpty("the page itself stays");
    }

    [Fact]
    public void DeleteAnnotation_ThenUndoByReattaching_ComesBack()
    {
        // The App's undo of a delete puts the same dictionary back in /Annots.
        using var doc = PdfDocument.Open(F.Build(Back.None, "1.7"));
        var note = doc.GetPage(2).GetAnnotations().Single(a => a.RawDictionary.GetOptional("Subtype") is PdfName { Value: "Text" });
        var noteRef = doc.GetReferenceTo(note.RawDictionary)!;

        doc.RemoveAnnotation(2, note).Should().BeTrue();
        ((PdfArray)doc.Resolve(doc.GetPage(2).Dictionary["Annots"])).Add(noteRef);
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, F.AnnotText).Should().NotBeEmpty("the re-attached note is in the document");
    }
}
