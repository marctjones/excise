using System;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.TestSupport;
using Back = Excise.TestSupport.RemovedPageBackReference;
using F = Excise.TestSupport.RemovedPageFixtures;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #2058: putting a removed page back (the App's undo of Remove Page, the redo of
/// Add/Insert Pages) must put back the page the rest of the document points at,
/// so the save does not cut its bookmark, link, destination, open action,
/// structure elements or form field as references to a removed page (#2012).
/// Removing it again, or never putting it back, must still cut them.
/// Oracles: the saved bytes through <see cref="SavedPdfLeakScanner"/> (presence
/// of the restored page's tokens) and the reopened file's object graph read
/// entry by entry; the qpdf twin is
/// <c>Excise.Rendering.Tests.Differential.RemovedPageUndoOracleTests</c>.
/// </summary>
public sealed class RemovedPageUndoTests
{
    public static TheoryData<Back, string> Cases()
    {
        var data = new TheoryData<Back, string>();
        foreach (var back in Enum.GetValues<Back>())
        {
            data.Add(back, "1.4");
            data.Add(back, "1.7");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RemovePage_Undo_Save_EverythingThatPointedAtThePageStillDoes(Back back, string version)
    {
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(back, version)))
        {
            var captured = doc.GetPage(2);
            doc.Pages.RemoveAt(1);
            doc.Pages.Insert(1, captured);
            saved = doc.SaveToBytes();
        }

        AssertRestored(saved, back, $"{back}/{version}");
    }

    [Theory]
    [InlineData(Back.Outline)]
    [InlineData(Back.StructureElement)]
    [InlineData(Back.AcroFormFieldKids)]
    public void RemovePage_Undo_Redo_Save_RemovedPageIsNotInTheFile(Back back)
    {
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(back, "1.7")))
        {
            var captured = doc.GetPage(2);
            doc.Pages.RemoveAt(1);
            doc.Pages.Insert(1, captured);
            doc.Pages.RemoveAt(1);
            saved = doc.SaveToBytes();
        }

        foreach (var token in F.RemovedTokens(back))
            SavedPdfLeakScanner.FindTerm(saved, token).Should().BeEmpty($"{back}: redo removed the page again, {token} must go");
        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(2);
    }

    [Theory]
    [InlineData(Back.Outline)]
    [InlineData(Back.LinkOnKeptPage)]
    [InlineData(Back.StructureElement)]
    [InlineData(Back.AcroFormWidget)]
    [InlineData(Back.AcroFormFieldKids)]
    public void RemovePage_RepeatedUndoRedoCycles_ThenUndo_Save_EverythingStillResolves(Back back)
    {
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(back, "1.7")))
        {
            var captured = doc.GetPage(2);
            for (var i = 0; i < 3; i++)
            {
                doc.Pages.RemoveAt(1);
                doc.Pages.Insert(1, captured);
            }
            saved = doc.SaveToBytes();
        }

        AssertRestored(saved, back, $"{back} after three cycles");
    }

    [Fact]
    public void RemoveTwoPages_UndoInAscendingOrder_Save_BothPagesAndTheirReferencesComeBack()
    {
        // Remove Pages removes in descending order; undo re-inserts ascending.
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(Back.StructureElement, "1.7")))
        {
            var two = doc.GetPage(2);
            var three = doc.GetPage(3);
            doc.Pages.RemoveAt(2);
            doc.Pages.RemoveAt(1);
            doc.Pages.Insert(1, two);
            doc.Pages.Insert(2, three);
            saved = doc.SaveToBytes();
        }

        AssertRestored(saved, Back.StructureElement, "two pages");
        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(3).Text.Should().Contain(F.KeptThree);
        var element = StructElementByPage(reopened, 3);
        element.Should().NotBeNull("page 3's structure element must still name page 3");
    }

    [Fact]
    public void RemovePage_MoveAndRotateInBetween_Undo_Save_ReferencesResolve()
    {
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(Back.Outline, "1.4")))
        {
            var captured = doc.GetPage(2);
            doc.Pages.RemoveAt(1);
            doc.Pages.Move(0, 1);                 // [3, 1]
            doc.GetPage(1).Rotation = 90;
            doc.Pages.Insert(1, captured);        // [3, 2, 1]
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(3);
        reopened.GetPage(1).Text.Should().Contain(F.KeptThree);
        reopened.GetPage(2).Text.Should().Contain(F.PageText);
        reopened.GetPage(3).Text.Should().Contain(F.KeptOne);
        PdfOutlineParser.Parse(reopened).Single().PageNumber.Should().Be(2, "the bookmark follows the restored page");
    }

    [Fact]
    public void NestedPageTree_RemovePage_Undo_Save_BookmarkStillResolves()
    {
        // RemoveAt flattens a nested tree first; the relinked leaf hangs off the root.
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(Back.Outline, "1.4", nestPagesTwoAndThree: true)))
        {
            var captured = doc.GetPage(2);
            doc.Pages.RemoveAt(1);
            doc.Pages.Insert(1, captured);
            saved = doc.SaveToBytes();
        }

        AssertRestored(saved, Back.Outline, "nested tree");
        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(3).Text.Should().Contain(F.KeptThree);
    }

    [Fact]
    public void InsertPageFromAnotherDocument_IsACopy_AndRemovingTheSourcePageStillScrubsTheSource()
    {
        // A page of another document is copied (new object numbers), even when
        // that document removed the page itself.
        using var source = PdfDocument.Open(F.Build(Back.Outline, "1.7"));
        using var target = PdfDocument.Open(F.Build(Back.None, "1.7"));
        var page = source.GetPage(2);
        source.Pages.RemoveAt(1);
        target.Pages.Insert(0, page);

        target.Pages[0].Reference.Should().NotBeNull();
        target.PageCount.Should().Be(4);
        target.GetPage(1).Text.Should().Contain(F.PageText);

        var sourceSaved = source.SaveToBytes();
        foreach (var token in F.RemovedTokens(Back.Outline))
            SavedPdfLeakScanner.FindTerm(sourceSaved, token).Should().BeEmpty($"the source removed page 2: {token} must go");

        var targetSaved = target.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(targetSaved, F.PageText).Should().NotBeEmpty("the target holds a copy");
        using var reopened = PdfDocument.Open(targetSaved);
        reopened.PageCount.Should().Be(4);
    }

    [Fact]
    public void InsertPageOfThisDocumentStillInTheTree_IsACopy()
    {
        using var doc = PdfDocument.Open(F.Build(Back.Outline, "1.7"));
        var page = doc.GetPage(2);
        var original = doc.Pages[1].Reference!.ObjectNum;
        doc.Pages.Insert(3, page);

        doc.PageCount.Should().Be(4);
        doc.Pages[3].Reference!.ObjectNum.Should().NotBe(original, "a page still in the tree is duplicated, not moved");
        doc.Pages[1].Reference!.ObjectNum.Should().Be(original);
    }

    [Fact]
    public void RemovePage_Undo_RemovedPageIsTheSameObject()
    {
        using var doc = PdfDocument.Open(F.Build(Back.Outline, "1.7"));
        var captured = doc.GetPage(2);
        var original = doc.Pages[1].Reference!.ObjectNum;
        doc.Pages.RemoveAt(1);
        doc.Pages.Insert(1, captured);

        doc.Pages[1].Reference!.ObjectNum.Should().Be(original, "undo puts back the page, not a copy");
        doc.GetPage(2).Dictionary.Should().BeSameAs(captured.Dictionary);
    }

    [Fact]
    public void RemovePage_Save_ThenUndo_Save_PageComesBackValid()
    {
        // Core callers can save between remove and re-insert (the App clears its
        // history on save). The first save already cut the bookmark; the page
        // itself comes back and the file stays readable.
        using var doc = PdfDocument.Open(F.Build(Back.Outline, "1.7"));
        var captured = doc.GetPage(2);
        doc.Pages.RemoveAt(1);
        doc.SaveToBytes();
        doc.Pages.Insert(1, captured);
        var saved = doc.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(3);
        reopened.GetPage(2).Text.Should().Contain(F.PageText);
    }

    // ── Assertions ──────────────────────────────────────────────────────

    /// <summary>
    /// The restored page 2 carries every token it had, and the entry the
    /// <paramref name="back"/> variant uses to point at it names page 2's
    /// object in the reopened file.
    /// </summary>
    private static void AssertRestored(byte[] saved, Back back, string label)
    {
        foreach (var token in F.RemovedTokens(back))
            SavedPdfLeakScanner.FindTerm(saved, token).Should().NotBeEmpty($"{label}: the restored page's {token} is in the file");

        using var doc = PdfDocument.Open(saved);
        doc.PageCount.Should().Be(3, label);
        doc.GetPage(2).Text.Should().Contain(F.PageText, label);
        var pageTwo = doc.Pages[1].Reference!.ObjectNum;
        var catalog = doc.Catalog;

        switch (back)
        {
            case Back.Outline:
                PdfOutlineParser.Parse(doc).Single().PageNumber.Should().Be(2, $"{label}: the bookmark goes to page 2");
                break;
            case Back.NamedDestination:
                var names = Dict(doc, Dict(doc, catalog["Names"])["Dests"]).GetArray("Names");
                FirstRef(doc, names[1]).Should().Be(pageTwo, $"{label}: the named destination names page 2");
                break;
            case Back.LinkOnKeptPage:
                var link = Dict(doc, doc.Pages[0].Dictionary.GetArray("Annots")[0]);
                FirstRef(doc, link["Dest"]).Should().Be(pageTwo, $"{label}: the link goes to page 2");
                break;
            case Back.OpenAction:
                FirstRef(doc, catalog["OpenAction"]).Should().Be(pageTwo, $"{label}: the open action goes to page 2");
                break;
            case Back.StructureElement:
                var root = Dict(doc, catalog["StructTreeRoot"]);
                Arr(doc, root["K"]).Count.Should().Be(3, $"{label}: every page's element is in the tree");
                var element = StructElementByPage(doc, 2);
                element.Should().NotBeNull($"{label}: page 2's element names page 2 in /Pg");
                element!.GetStringOrNull("ActualText").Should().Be(F.StructActualText, label);
                doc.Pages[1].Dictionary.GetInt("StructParents").Should().Be(1, label);
                var nums = Arr(doc, Dict(doc, root["ParentTree"])["Nums"]);
                var entry = Enumerable.Range(0, nums.Count / 2)
                    .Where(i => doc.Resolve(nums[2 * i]) is PdfInteger { Value: 1 })
                    .Select(i => Arr(doc, nums[2 * i + 1]))
                    .Single();
                ReferenceEquals(Dict(doc, entry[0]), element).Should().BeTrue($"{label}: /ParentTree 1 names page 2's element");
                break;
            case Back.AcroFormWidget:
            case Back.AcroFormFieldKids:
                var fields = Arr(doc, Dict(doc, catalog["AcroForm"])["Fields"]);
                fields.Count.Should().Be(1, $"{label}: the field is still in /Fields");
                var field = Dict(doc, fields[0]);
                var widgetRef = back == Back.AcroFormWidget ? fields[0] : Arr(doc, field["Kids"])[0];
                var widgetNum = ((PdfReference)widgetRef).ObjectNum;
                RefNum(Dict(doc, widgetRef)["P"]).Should().Be(pageTwo, $"{label}: the widget's /P is page 2");
                Arr(doc, doc.Pages[1].Dictionary["Annots"]).OfType<PdfReference>()
                    .Select(r => r.ObjectNum).Should().Contain(widgetNum, $"{label}: page 2 lists the widget");
                doc.GetPage(2).GetFormFields().Should().ContainSingle(f => f.Value == F.FieldValue, label);
                break;
        }
    }

    private static PdfDictionary? StructElementByPage(PdfDocument doc, int pageNumber)
    {
        var pageNum = doc.Pages[pageNumber - 1].Reference!.ObjectNum;
        var root = Dict(doc, doc.Catalog["StructTreeRoot"]);
        return Arr(doc, root["K"]).Select(k => Dict(doc, k))
            .FirstOrDefault(e => e.GetOptional("Pg") is PdfReference pg && pg.ObjectNum == pageNum);
    }

    private static PdfDictionary Dict(PdfDocument doc, PdfObject value) => (PdfDictionary)doc.Resolve(value);

    private static PdfArray Arr(PdfDocument doc, PdfObject value) => (PdfArray)doc.Resolve(value);

    private static int? RefNum(PdfObject value) => value is PdfReference r ? r.ObjectNum : null;

    /// <summary>The object number a destination array's first element names.</summary>
    private static int? FirstRef(PdfDocument doc, PdfObject value)
        => doc.Resolve(value) is PdfArray { Count: > 0 } array && array[0] is PdfReference r ? r.ObjectNum : null;
}
