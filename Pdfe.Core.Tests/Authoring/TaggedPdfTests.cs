using System.Linq;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Authoring;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Authoring;

/// <summary>
/// Tests for tagged-PDF (logical structure tree) authoring via the builder (#275).
/// </summary>
public class TaggedPdfTests
{
    private static System.Collections.Generic.List<string> StructTypes(PdfDocument doc)
    {
        var types = new System.Collections.Generic.List<string>();
        var rootObj = doc.Catalog.GetOptional("StructTreeRoot");
        if (rootObj == null) return types;
        var root = doc.Resolve(rootObj) as PdfDictionary;
        // root /K -> Document elem(s) -> /K -> content elems
        void Walk(PdfObject? k)
        {
            if (k == null) return;
            var resolved = doc.Resolve(k);
            if (resolved is PdfArray arr) { foreach (var x in arr) Walk(x); return; }
            if (resolved is PdfDictionary d)
            {
                var s = d.GetNameOrNull("S");
                if (s != null) types.Add(s);
                if (d.GetOptional("K") is { } kids) Walk(kids);
            }
        }
        Walk(root!.GetOptional("K"));
        return types;
    }

    [Fact]
    public void Tagged_SetsMarkInfoStructTreeAndViewerPrefs()
    {
        var pdf = PdfDocumentBuilder.Create().Tagged().Title("Doc").Language("en-US")
            .Heading("Title").Paragraph("Body").SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        var markInfo = doc.Resolve(doc.Catalog.GetOptional("MarkInfo")!) as PdfDictionary;
        (markInfo!.GetOptional("Marked") as PdfBoolean)!.Value.Should().BeTrue();
        doc.Catalog.GetOptional("StructTreeRoot").Should().NotBeNull();
        doc.Resolve(doc.Catalog.GetOptional("ViewerPreferences")!).Should().BeOfType<PdfDictionary>();
    }

    [Fact]
    public void Tagged_PdfInfoReportsStructureForHeadingsParagraphsTables()
    {
        var pdf = PdfDocumentBuilder.Create().Tagged()
            .Heading("H one", 1)
            .Heading("H two", 2)
            .Paragraph("para")
            .KeyValue("k", "v")
            .Table(new[] { new[] { "a", "b" }, new[] { "c", "d" } })
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        var types = StructTypes(doc);
        types.Should().Contain("Document");
        types.Should().Contain("H1");
        types.Should().Contain("H2");
        types.Should().Contain("P");
        types.Should().Contain("Table");
    }

    [Fact]
    public void Tagged_ContentStreamHasMarkedContent()
    {
        var pdf = PdfDocumentBuilder.Create().Tagged().Paragraph("hello").SaveToBytes();
        using var doc = PdfDocument.Open(pdf);
        var content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().Contain("BDC");
        content.Should().Contain("EMC");
        content.Should().Contain("/MCID");
    }

    [Fact]
    public void Tagged_TableNestsTableTrTdTh()
    {
        var pdf = PdfDocumentBuilder.Create().Tagged()
            .Table(new[] { new[] { "H1", "H2" }, new[] { "a", "b" } }, headerRow: true)
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        var types = StructTypes(doc);
        types.Should().Contain("Table");
        types.Should().Contain("TR");
        types.Should().Contain("TH", "header cells should be TH");
        types.Should().Contain("TD", "body cells should be TD");
    }

    [Fact]
    public void Tagged_DecorativeContentIsMarkedAsArtifact()
    {
        var pdf = PdfDocumentBuilder.Create().Tagged()
            .Paragraph("text")
            .HorizontalRule()
            .TextField("Name", "name")   // draws a box border
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        var content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().Contain("/Artifact BMC", "rules/borders must be artifacts under PDF/UA");
    }

    [Fact]
    public void Tagged_FormFields_AppearInStructureTreeAsForm()
    {
        var pdf = PdfDocumentBuilder.Create().Tagged()
            .TextField("Name", "name")
            .CheckBox("Agree", "agree")
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        StructTypes(doc).Should().Contain("Form", "form widgets must be in the structure tree (PDF/UA)");
        // The widget annotation gets a /StructParent linking it into the ParentTree.
        var field = doc.GetAcroForm()!.FindField("name")!;
        field.RawDictionary.GetOptional("StructParent").Should().NotBeNull();
    }

    [Fact]
    public void NotTagged_HasNoStructTree()
    {
        var pdf = PdfDocumentBuilder.Create().Paragraph("hello").SaveToBytes();
        using var doc = PdfDocument.Open(pdf);
        doc.Catalog.GetOptional("StructTreeRoot").Should().BeNull();
        var content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().NotContain("BDC");
    }

    [Fact]
    public void Tagged_MultiPageParagraph_SpansPagesWithStructParents()
    {
        var builder = PdfDocumentBuilder.Create().Tagged();
        // One very long paragraph that must flow across pages.
        builder.Paragraph(string.Join(" ", Enumerable.Repeat("word", 1200)));
        using var doc = PdfDocument.Open(builder.SaveToBytes());

        doc.PageCount.Should().BeGreaterThan(1);
        // Every page that carries tagged content gets a /StructParents key.
        foreach (var p in doc.GetPages())
            p.Dictionary.GetOptional("StructParents").Should().NotBeNull();
        // The structure tree still parses and contains the paragraph.
        StructTypes(doc).Should().Contain("P");
    }
}
