using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Deterministic coverage for <see cref="PdfOutlineParser"/> destination
/// resolution. The pre-existing outline tests load a real book from a
/// hard-coded local path and silently return when it is absent (so they never
/// run on CI — a #619-style invisible coverage loss); this fixture builds the
/// outline tree in-memory and exercises every destination encoding: a direct
/// <c>/Dest</c> array, an <c>/A</c> GoTo action, the PDF 1.2+
/// <c>/Names/Dests</c> name tree (branch → leaf, with a <c>{/D […]}</c> leaf
/// value), the older <c>/Catalog/Dests</c> dictionary, nesting, the
/// no-destination case, and a non-GoTo action.
/// </summary>
public class PdfOutlineParserDestinationTests
{
    private static PdfReference Intern(PdfDocument doc, PdfObject obj) => doc.AddIndirectObject(obj);

    private static PdfDictionary Item(string title)
    {
        var d = new PdfDictionary();
        d["Title"] = new PdfString(title);
        return d;
    }

    private static PdfArray DestArray(PdfReference page) =>
        new PdfArray(page, new PdfName("Fit"));

    [Fact]
    public void NoOutline_YieldsEmptyList()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        PdfOutlineParser.Parse(doc).Should().BeEmpty();
    }

    [Fact]
    public void Parse_ResolvesEveryDestinationEncodingAndNesting()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.Pages.AddBlank();

        var pagesDict = (PdfDictionary)doc.Resolve(doc.Catalog["Pages"]);
        var kids = (PdfArray)doc.Resolve(pagesDict["Kids"]);
        var page1 = (PdfReference)kids[0];
        var page2 = (PdfReference)kids[^1];
        int lastPageNo = kids.Count; // 1-based, kids in order

        // Named destinations: PDF 1.2+ /Names/Dests name tree (branch → leaf,
        // one entry a {/D [...]} dict) AND the older /Catalog/Dests dictionary.
        var ntDestDict = new PdfDictionary();
        ntDestDict["D"] = DestArray(page1);
        var ntLeaf = new PdfDictionary();
        ntLeaf["Names"] = new PdfArray(new PdfString("nt_dest"), Intern(doc, ntDestDict));
        var ntRoot = new PdfDictionary();
        ntRoot["Kids"] = new PdfArray(Intern(doc, ntLeaf));
        var names = new PdfDictionary();
        names["Dests"] = Intern(doc, ntRoot);
        doc.Catalog["Names"] = Intern(doc, names);

        var oldDests = new PdfDictionary();
        oldDests["old_dest"] = DestArray(page2);
        doc.Catalog["Dests"] = Intern(doc, oldDests);

        // Children of "Chapter 1".
        var sectionNamed = Item("Section via name tree");
        sectionNamed["Dest"] = new PdfString("nt_dest"); // string key → name tree
        var sectionOld = Item("Section via old dests");
        sectionOld["Dest"] = new PdfName("old_dest");    // name key → /Catalog/Dests
        var sectionNoDest = Item("Section without a destination");
        var sectionNonGoto = Item("Section with a non-GoTo action");
        var uriAction = new PdfDictionary();
        uriAction["S"] = new PdfName("URI");
        sectionNonGoto["A"] = uriAction;

        var sNamedRef = Intern(doc, sectionNamed);
        var sOldRef = Intern(doc, sectionOld);
        var sNoRef = Intern(doc, sectionNoDest);
        var sNonGotoRef = Intern(doc, sectionNonGoto);
        sectionNamed["Next"] = sOldRef;
        sectionOld["Next"] = sNoRef;
        sectionNoDest["Next"] = sNonGotoRef;

        // Top-level items.
        var chapter1 = Item("Chapter 1");
        chapter1["Dest"] = DestArray(page1);            // direct /Dest array
        chapter1["First"] = sNamedRef;
        var chapter2 = Item("Chapter 2");
        var gotoAction = new PdfDictionary();           // /A GoTo action
        gotoAction["S"] = new PdfName("GoTo");
        gotoAction["D"] = DestArray(page2);
        chapter2["A"] = gotoAction;

        var c1Ref = Intern(doc, chapter1);
        var c2Ref = Intern(doc, chapter2);
        chapter1["Next"] = c2Ref;

        var outlines = new PdfDictionary();
        outlines.SetName("Type", "Outlines");
        outlines["First"] = c1Ref;
        outlines["Last"] = c2Ref;
        doc.Catalog["Outlines"] = Intern(doc, outlines);

        var tree = PdfOutlineParser.Parse(doc);

        tree.Should().HaveCount(2);
        tree[0].Title.Should().Be("Chapter 1");
        tree[0].PageNumber.Should().Be(1, "direct /Dest array points at page 1");
        tree[1].Title.Should().Be("Chapter 2");
        tree[1].PageNumber.Should().Be(lastPageNo, "the GoTo action points at the last page");

        var children = tree[0].Children;
        children.Should().HaveCount(4);
        children.Single(c => c.Title == "Section via name tree").PageNumber.Should().Be(1);
        children.Single(c => c.Title == "Section via old dests").PageNumber.Should().Be(lastPageNo);
        children.Single(c => c.Title == "Section without a destination").PageNumber.Should().BeNull();
        children.Single(c => c.Title == "Section with a non-GoTo action").PageNumber.Should().BeNull();
    }
}
