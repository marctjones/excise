using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.TestSupport;
using Back = Excise.TestSupport.RemovedPageBackReference;
using F = Excise.TestSupport.RemovedPageFixtures;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #2014: a removed page's structure elements, and the text they carry
/// themselves (<c>/ActualText</c>, <c>/Alt</c>, <c>/E</c>, <c>/T</c>), must not
/// survive in the saved file; elements with content on a kept page must.
/// Oracle: the saved bytes through <see cref="SavedPdfLeakScanner"/> (every
/// stream inflated, not excise's reader). The structural assertions walk the
/// saved file's objects. The qpdf-backed twin of the base case (and its
/// encrypted save) is <c>Excise.Rendering.Tests.Differential.RemovedPageLeakOracleTests</c>,
/// which builds the same <see cref="Back.StructureElement"/> fixture.
/// </summary>
public sealed class RemovedPageStructureLeakTests
{
    private const string P1 = "3 0 R";
    private const string P2 = "4 0 R";
    private const string P3 = "5 0 R";

    [Theory]
    [InlineData("1.4")]
    [InlineData("1.7")]
    public void RemovePage_ItsElementGoes_KeptPagesElementsStay(string version)
    {
        var saved = RemoveAndSave(F.Build(Back.StructureElement, version), 1);

        AssertGone(saved, F.StructActualText, F.StructAlt, F.StructExpansion, F.StructTitle);
        SavedPdfLeakScanner.FindTerm(saved, F.KeptStructText).Should().NotBeEmpty("page 1's element stays");

        using var doc = PdfDocument.Open(saved);
        var root = Root(doc);
        var kids = Kids(doc, root);
        kids.Should().HaveCount(2, "the kept pages' elements stay, the removed page's goes");
        kids.Select(k => (Resolve(doc, k) as PdfDictionary)?.GetOptional("Pg")).Should().AllBeOfType<PdfReference>();
        ((PdfReference)((PdfDictionary)Resolve(doc, kids[0])).GetOptional("Pg")!).ObjectNum
            .Should().Be(doc.GetPageReference(1)!.ObjectNum);

        // Removed page's /ParentTree entry is gone; the kept ones keep their keys.
        NumsKeys(doc, root).Should().Equal(0, 2);
        var idNames = (PdfArray)Resolve(doc, ((PdfDictionary)Resolve(doc, root.GetOptional("IDTree")!)).GetOptional("Names")!);
        idNames.Should().HaveCount(2, "only the kept element's /ID entry stays");
        doc.GetStructureTree().Should().NotBeNull();
    }

    [Fact]
    public void ElementSpanningRemovedAndKeptPage_StaysWithOnlyItsKeptContent_AndLosesItsTextFields()
    {
        // §14.9.4: /ActualText is an exact replacement for the element's WHOLE
        // content; once part of that content is gone it is neither accurate
        // nor free of the removed page's text, so it goes, with /Alt, /E, /T.
        var saved = RemoveAndSave(Tagged(
            "<< /Type /StructTreeRoot /K [11 0 R] /ParentTree 12 0 R >>",
            $"<< /Type /StructElem /S /P /P 10 0 R /Pg {P2} /K [0 << /Type /MCR /Pg {P3} /MCID 0 >>] "
                + "/ActualText (SPANACTUALSECRET) /Alt (SPANALTSECRET) /E (SPANESECRET) /T (SPANTITLESECRET) /Lang (en) >>",
            "<< /Nums [1 [11 0 R] 2 [11 0 R]] >>"), 1);

        AssertGone(saved, "SPANACTUALSECRET", "SPANALTSECRET", "SPANESECRET", "SPANTITLESECRET");
        using var doc = PdfDocument.Open(saved);
        var root = Root(doc);
        var element = (PdfDictionary)Resolve(doc, Kids(doc, root).Single());
        element.ContainsKey("Pg").Should().BeFalse("its page was removed");
        element.ContainsKey("Lang").Should().BeTrue("only the four text fields go");
        var k = Kids(doc, element);
        k.Should().ContainSingle("the removed page's MCID 0 goes, else it would bind to another page's MCID 0");
        var mcr = (PdfDictionary)Resolve(doc, k[0]);
        ((PdfReference)mcr.GetOptional("Pg")!).ObjectNum.Should().Be(doc.GetPageReference(2)!.ObjectNum);
        NumsKeys(doc, root).Should().Equal(2);
    }

    [Fact]
    public void ChildInheritingTheRemovedPage_GoesWithItsParent()
    {
        // The child has no /Pg: its MCID is on its parent's page (§14.7.2 Table
        // 355 requires /Pg for an integer /K; readers take the nearest
        // ancestor's, and so does the cut, which keeps nothing it cannot place).
        var saved = RemoveAndSave(Tagged(
            "<< /Type /StructTreeRoot /K [11 0 R 13 0 R] /ParentTree 14 0 R >>",
            $"<< /Type /StructElem /S /Sect /P 10 0 R /Pg {P2} /K [12 0 R] /T (SECTTITLESECRET) >>",
            "<< /Type /StructElem /S /P /P 11 0 R /K 0 /ActualText (NESTEDSECRET) >>",
            $"<< /Type /StructElem /S /P /P 10 0 R /Pg {P1} /K 0 /ActualText (KEPTONETEXT) >>",
            "<< /Nums [0 [13 0 R] 1 [12 0 R]] >>"), 1);

        AssertGone(saved, "SECTTITLESECRET", "NESTEDSECRET");
        SavedPdfLeakScanner.FindTerm(saved, "KEPTONETEXT").Should().NotBeEmpty();
        using var doc = PdfDocument.Open(saved);
        Kids(doc, Root(doc)).Should().ContainSingle();
        NumsKeys(doc, Root(doc)).Should().Equal(0);
    }

    [Fact]
    public void ParentWithChildrenOnRemovedAndKeptPages_KeepsTheKeptChild()
    {
        var saved = RemoveAndSave(Tagged(
            "<< /Type /StructTreeRoot /K [11 0 R] /ParentTree 14 0 R >>",
            "<< /Type /StructElem /S /Div /P 10 0 R /K [12 0 R 13 0 R] /Alt (DIVALTSECRET) >>",
            $"<< /Type /StructElem /S /P /P 11 0 R /Pg {P2} /K 0 /ActualText (CHILDSECRET) >>",
            $"<< /Type /StructElem /S /P /P 11 0 R /Pg {P3} /K 0 /ActualText (KEPTCHILDTEXT) >>",
            "<< /Nums [1 [12 0 R] 2 [13 0 R]] >>"), 1);

        AssertGone(saved, "DIVALTSECRET", "CHILDSECRET");
        SavedPdfLeakScanner.FindTerm(saved, "KEPTCHILDTEXT").Should().NotBeEmpty("the kept page's child stays");
        using var doc = PdfDocument.Open(saved);
        var div = (PdfDictionary)Resolve(doc, Kids(doc, Root(doc)).Single());
        var child = (PdfDictionary)Resolve(doc, Kids(doc, div).Single());
        ((PdfString)Resolve(doc, child.GetOptional("ActualText")!)).Value.Should().Be("KEPTCHILDTEXT");
    }

    [Fact]
    public void ParentOnRemovedPageWithAChildOnAKeptPage_KeepsTheChild()
    {
        var saved = RemoveAndSave(Tagged(
            "<< /Type /StructTreeRoot /K [11 0 R] /ParentTree 14 0 R >>",
            $"<< /Type /StructElem /S /Sect /P 10 0 R /Pg {P2} /K [0 12 0 R] /ActualText (SECTSECRET) >>",
            $"<< /Type /StructElem /S /P /P 11 0 R /Pg {P3} /K 0 /ActualText (KEPTPTHREE) >>",
            "<< /Nums [1 [11 0 R] 2 [12 0 R]] >>"), 1);

        AssertGone(saved, "SECTSECRET");
        SavedPdfLeakScanner.FindTerm(saved, "KEPTPTHREE").Should().NotBeEmpty();
        using var doc = PdfDocument.Open(saved);
        var sect = (PdfDictionary)Resolve(doc, Kids(doc, Root(doc)).Single());
        sect.ContainsKey("Pg").Should().BeFalse();
        Kids(doc, sect).Should().ContainSingle().Which.Should().BeOfType<PdfReference>("the MCID 0 of the removed page goes");
    }

    [Fact]
    public void DroppedElementNamedInAKeptPagesParentTreeArray_LeavesANullSlot()
    {
        // Malformed: page 3's MCID array also names page 2's element. Its slot
        // becomes null; removing it would shift MCID 1 onto element 11's slot.
        var saved = RemoveAndSave(Tagged(
            "<< /Type /StructTreeRoot /K [11 0 R 12 0 R] /ParentTree 13 0 R >>",
            $"<< /Type /StructElem /S /P /P 10 0 R /Pg {P2} /K 0 /ActualText (SLOTSECRET) >>",
            $"<< /Type /StructElem /S /P /P 10 0 R /Pg {P3} /K [0 1] >>",
            "<< /Nums [1 [11 0 R] 2 [11 0 R 12 0 R]] >>"), 1);

        AssertGone(saved, "SLOTSECRET");
        using var doc = PdfDocument.Open(saved);
        var root = Root(doc);
        NumsKeys(doc, root).Should().Equal(2);
        var nums = (PdfArray)doc.Resolve(((PdfDictionary)doc.Resolve(root.GetOptional("ParentTree")!)).GetOptional("Nums")!);
        var slots = (PdfArray)doc.Resolve(nums[1]);
        slots.Should().HaveCount(2);
        slots[0].Should().BeOfType<PdfNull>();
        doc.Resolve(slots[1]).Should().BeSameAs(doc.Resolve(Kids(doc, root).Single()));
    }

    [Fact]
    public void ElementWithNoPageAnywhere_IsPlacedThroughTheParentTree()
    {
        // Malformed (an integer /K with no /Pg in reach), but the removed page's
        // /ParentTree entry names it: that is where its content is.
        var saved = RemoveAndSave(Tagged(
            "<< /Type /StructTreeRoot /K [11 0 R 12 0 R] /ParentTree 13 0 R >>",
            "<< /Type /StructElem /S /P /P 10 0 R /K 0 /ActualText (UNPLACEDSECRET) >>",
            "<< /Type /StructElem /S /P /P 10 0 R /K 0 /ActualText (UNPLACEDKEPT) >>",
            "<< /Nums [0 [12 0 R] 1 [11 0 R]] >>"), 1);

        AssertGone(saved, "UNPLACEDSECRET");
        SavedPdfLeakScanner.FindTerm(saved, "UNPLACEDKEPT").Should().NotBeEmpty("page 1's entry names it");
    }

    [Fact]
    public void AnnotationElementOnTheRemovedPage_Goes()
    {
        var saved = RemoveAndSave(Tagged(
            [
                "<< /Type /StructTreeRoot /K [11 0 R] /ParentTree 12 0 R /ParentTreeNextKey 4 >>",
                $"<< /Type /StructElem /S /Link /P 10 0 R /Pg {P2} /K << /Type /OBJR /Obj 13 0 R >> /Alt (LINKALTSECRET) >>",
                "<< /Nums [3 11 0 R] >>",
                $"<< /Type /Annot /Subtype /Link /Rect [72 600 200 620] /StructParent 3 /Contents (LINKCONTENTSSECRET) /P {P2} >>",
            ],
            page2Annots: "/Annots [13 0 R]"), 1);

        AssertGone(saved, "LINKALTSECRET", "LINKCONTENTSSECRET");
        using var doc = PdfDocument.Open(saved);
        Kids(doc, Root(doc)).Should().BeEmpty();
        NumsKeys(doc, Root(doc)).Should().BeEmpty();
    }

    [Fact]
    public void RemovedPageWithNoStructure_LeavesTheTreeAlone()
    {
        var source = Tagged(
            "<< /Type /StructTreeRoot /K [11 0 R 12 0 R] /ParentTree 13 0 R >>",
            $"<< /Type /StructElem /S /P /P 10 0 R /Pg {P1} /K 0 /ActualText (KEPTONE) /T (KEPTONETITLE) >>",
            $"<< /Type /StructElem /S /P /P 10 0 R /Pg {P3} /K 0 /Alt (KEPTTHREEALT) >>",
            "<< /Nums [0 [11 0 R] 2 [12 0 R]] >>");
        var saved = RemoveAndSave(source, 1);

        foreach (var kept in new[] { "KEPTONE", "KEPTONETITLE", "KEPTTHREEALT" })
            SavedPdfLeakScanner.FindTerm(saved, kept).Should().NotBeEmpty(kept);
        using var doc = PdfDocument.Open(saved);
        Kids(doc, Root(doc)).Should().HaveCount(2);
        NumsKeys(doc, Root(doc)).Should().Equal(0, 2);
    }

    [Fact]
    public void RemoveTwoPagesAcrossTwoSaves_BothElementsGo_SecondSaveIsClean()
    {
        using var doc = PdfDocument.Open(F.Build(Back.StructureElement, "1.7"));
        doc.Pages.RemoveAt(1);
        var first = doc.SaveToBytes();
        AssertGone(first, F.StructActualText, F.StructTitle);
        var again = doc.SaveToBytes();
        AssertGone(again, F.StructActualText, F.StructTitle);
        SavedPdfLeakScanner.FindTerm(again, F.KeptStructText).Should().NotBeEmpty();

        doc.Pages.RemoveAt(0); // page 1, the one whose element carries KeptStructText
        var second = doc.SaveToBytes();
        foreach (var token in new[] { F.StructActualText, F.StructTitle, F.KeptStructText, F.KeptOne })
            SavedPdfLeakScanner.FindTerm(second, token).Should().BeEmpty($"{token} belongs to a removed page");
        SavedPdfLeakScanner.FindTerm(second, F.KeptThree).Should().NotBeEmpty("page 3 is kept");
        using var reopened = PdfDocument.Open(second);
        Kids(reopened, Root(reopened)).Should().ContainSingle("page 3's element stays");
        NumsKeys(reopened, Root(reopened)).Should().Equal(2);
    }

    [Fact]
    public void ResaveOfTheSavedFile_StaysClean()
    {
        var saved = RemoveAndSave(F.Build(Back.StructureElement, "1.7"), 1);
        using var reopened = PdfDocument.Open(saved);
        AssertGone(reopened.SaveToBytes(), F.StructActualText, F.StructAlt, F.StructExpansion, F.StructTitle);
    }

    // ── Fixture: three tagged pages, MCID 0 on each; objects 10+ are the caller's, 10 the root ──

    private static byte[] Tagged(params string[] structObjects) => Tagged(structObjects, "");

    private static byte[] Tagged(string[] structObjects, string page2Annots)
    {
        static string Stream(string data) => $"<< /Length {data.Length} >>\nstream\n{data}\nendstream";
        static string Text(string t) => F.Tagged(true, $"BT /F1 12 Tf 72 700 Td ({t}) Tj ET");
        static string Page(int contents, int key, string extra = "") =>
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> "
            + $"/Contents {contents} 0 R /StructParents {key} {extra} >>";
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 10 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            Page(6, 0), Page(7, 1, page2Annots), Page(8, 2),
            Stream(Text(F.KeptOne)), Stream(Text(F.PageText)), Stream(Text(F.KeptThree)),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        objs.AddRange(structObjects);
        return F.Assemble(objs, "1.7");
    }

    private static byte[] RemoveAndSave(byte[] source, int index)
    {
        using var doc = PdfDocument.Open(source);
        doc.Pages.RemoveAt(index);
        return doc.SaveToBytes();
    }

    private static void AssertGone(byte[] saved, params string[] tokens)
    {
        SavedPdfLeakScanner.FindTerm(saved, F.PageText).Should().BeEmpty("the removed page's content goes");
        foreach (var token in tokens)
            SavedPdfLeakScanner.FindTerm(saved, token).Should().BeEmpty($"{token} belongs to the removed page");
        SavedPdfLeakScanner.FindTerm(saved, F.KeptOne).Should().NotBeEmpty("page 1 is kept");
    }

    private static PdfObject Resolve(PdfDocument doc, PdfObject o) => doc.Resolve(o);

    private static PdfDictionary Root(PdfDocument doc) =>
        (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("StructTreeRoot")!);

    private static List<PdfObject> Kids(PdfDocument doc, PdfDictionary node) =>
        node.GetOptional("K") is not { } k ? []
        : doc.Resolve(k) is PdfArray a ? a.ToList()
        : [k];

    private static List<long> NumsKeys(PdfDocument doc, PdfDictionary root)
    {
        if (root.GetOptional("ParentTree") is not { } pt || doc.Resolve(pt) is not PdfDictionary tree)
            return [];
        var nums = (PdfArray)doc.Resolve(tree.GetOptional("Nums")!);
        return nums.Where((_, i) => i % 2 == 0).Select(n => ((PdfInteger)n).Value).ToList();
    }
}
