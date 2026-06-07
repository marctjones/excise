using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Pdfe.Core.Authoring;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Pdfe.Core.Text;
using Xunit;

namespace Pdfe.Core.Tests.Authoring;

/// <summary>Tests for bulleted/numbered lists in the builder (#407).</summary>
public class ListTests
{
    private static List<string> StructTypes(PdfDocument doc)
    {
        var types = new List<string>();
        var rootObj = doc.Catalog.GetOptional("StructTreeRoot");
        if (rootObj == null) return types;
        void Walk(PdfObject? k)
        {
            if (k == null) return;
            var r = doc.Resolve(k);
            if (r is PdfArray arr) { foreach (var x in arr) Walk(x); return; }
            if (r is PdfDictionary d)
            {
                var s = d.GetNameOrNull("S");
                if (s != null) types.Add(s);
                if (d.GetOptional("K") is { } kids) Walk(kids);
            }
        }
        Walk((doc.Resolve(rootObj) as PdfDictionary)!.GetOptional("K"));
        return types;
    }

    private static string Extract(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => new TextExtractor(p).ExtractText()));
    }

    [Fact]
    public void BulletList_RendersItems()
    {
        var pdf = PdfDocumentBuilder.Create()
            .BulletList(new[] { "First item", "Second item", "Third item" })
            .SaveToBytes();
        var text = Extract(pdf);
        foreach (var s in new[] { "First item", "Second item", "Third item" })
            text.Should().Contain(s);
    }

    [Fact]
    public void NumberedList_RendersMarkers()
    {
        var pdf = PdfDocumentBuilder.Create()
            .NumberedList(new[] { "alpha", "beta" })
            .SaveToBytes();
        var text = Extract(pdf);
        text.Should().Contain("1.");
        text.Should().Contain("2.");
        text.Should().Contain("alpha");
    }

    [Fact]
    public void Tagged_List_EmitsLAndLi()
    {
        var pdf = PdfDocumentBuilder.Create().Tagged()
            .BulletList(new[] { "one", "two" })
            .SaveToBytes();
        using var doc = PdfDocument.Open(pdf);
        var types = StructTypes(doc);
        types.Should().Contain("L");
        types.Count(t => t == "LI").Should().Be(2);
    }

    [Fact]
    public void List_LongItem_WrapsAndPaginates()
    {
        var longItem = string.Join(" ", Enumerable.Repeat("word", 400));
        var pdf = PdfDocumentBuilder.Create()
            .BulletList(new[] { longItem, "short" })
            .SaveToBytes();
        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().BeGreaterThanOrEqualTo(1);
        Extract(pdf).Should().Contain("short");
    }
}
