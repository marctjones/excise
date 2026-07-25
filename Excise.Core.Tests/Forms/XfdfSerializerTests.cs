using System.IO;
using System.Xml;
using System.Xml.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Forms;
using Xunit;

namespace Excise.Core.Tests.Forms;

/// <summary>
/// XFDF annotation import/export round-trip (#626).
///
/// <para>
/// Oracle note (no-self-oracle): the expected XFDF grammar in these tests —
/// element names, attribute names, separators, color/date formats — is
/// hand-derived from the Adobe XFDF 3.0 specification and from the shape of
/// real Acrobat "Export comments as data file" output, not from excise's own
/// serializer. <see cref="Import_AcrobatStyleSample_ParsesAllSupportedSubtypes"/>
/// feeds a fixture written in that external dialect (including elements excise
/// never emits, like <c>&lt;f&gt;</c>, <c>&lt;ids&gt;</c> and
/// <c>contents-richtext</c>) through the importer.
/// </para>
/// </summary>
public class XfdfSerializerTests
{
    private const string SampleDate = "D:20260725093000+00'00'";

    // ── Export ────────────────────────────────────────────────────────────────

    [Fact]
    public void Export_ProducesWellFormedXfdfInAdobeNamespace()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddSquareAnnotation(1, new PdfRectangle(100, 500, 200, 560),
            contents: "Check this", author: "Reviewer A",
            red: 1, green: 0, blue: 0, borderWidth: 2,
            interiorRed: 0, interiorGreen: 1, interiorBlue: 0);

        var xfdf = XfdfSerializer.ExportAnnotations(doc);

        var parsed = XDocument.Parse(xfdf);
        parsed.Root!.Name.Should().Be(XName.Get("xfdf", XfdfSerializer.XfdfNamespace));

        var annots = parsed.Root.Element(XName.Get("annots", XfdfSerializer.XfdfNamespace));
        annots.Should().NotBeNull();

        var square = annots!.Elements().Should().ContainSingle().Subject;
        square.Name.LocalName.Should().Be("square");
        square.Attribute("page")!.Value.Should().Be("0");
        square.Attribute("rect")!.Value.Should().Be("100,500,200,560");
        square.Attribute("color")!.Value.Should().Be("#FF0000");
        square.Attribute("interior-color")!.Value.Should().Be("#00FF00");
        square.Attribute("title")!.Value.Should().Be("Reviewer A");
        square.Attribute("width")!.Value.Should().Be("2");
        square.Attribute("flags")!.Value.Should().Be("print");
        square.Elements().Single(e => e.Name.LocalName == "contents").Value.Should().Be("Check this");
    }

    [Fact]
    public void Export_EmptyDocument_YieldsEmptyAnnotsElement()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var parsed = XDocument.Parse(XfdfSerializer.ExportAnnotations(doc));
        var annots = parsed.Root!.Elements().Single(e => e.Name.LocalName == "annots");
        annots.Elements().Should().BeEmpty();
    }

    [Fact]
    public void Export_SkipsLinkWidgetAndPopupSubtypes()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "Keep me");

        // Hand-plant a Link annotation (no authoring method exports these).
        var link = new Excise.Core.Primitives.PdfDictionary();
        link.SetName("Type", "Annot");
        link.SetName("Subtype", "Link");
        link["Rect"] = Excise.Core.Primitives.PdfArray.FromRectangle(0, 0, 100, 20);
        var pageDict = doc.GetPage(1).Dictionary;
        pageDict.GetArrayOrNull("Annots")!.Add(link);

        var parsed = XDocument.Parse(XfdfSerializer.ExportAnnotations(doc));
        var annots = parsed.Root!.Elements().Single(e => e.Name.LocalName == "annots");
        annots.Elements().Should().ContainSingle(e => e.Name.LocalName == "text");
    }

    [Fact]
    public void Export_StreamOverload_WritesUtf8Bytes()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "Ünïcode ✓ note");

        using var ms = new MemoryStream();
        XfdfSerializer.ExportAnnotations(doc, ms);
        ms.Position = 0;

        var parsed = XDocument.Load(ms);
        parsed.Descendants().Single(e => e.Name.LocalName == "contents")
            .Value.Should().Be("Ünïcode ✓ note");
    }

    // ── Round-trip: authored → export → import → compare ─────────────────────

    [Fact]
    public void RoundTrip_AuthoredSubtypes_SurviveExportAndImport()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();

        source.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736),
            "Sticky note body", author: "Alice", open: true, iconName: "Comment");
        source.AddHighlightAnnotation(1, new PdfRectangle(100, 650, 300, 670),
            contents: "Highlighted claim", author: "Alice", red: 1, green: 0.92, blue: 0.2);
        source.AddSquareAnnotation(1, new PdfRectangle(50, 400, 150, 480),
            contents: "Box comment", author: "Bob",
            red: 0.8, green: 0.1, blue: 0.1, borderWidth: 3,
            interiorRed: 0.9, interiorGreen: 0.9, interiorBlue: 0.2);
        source.AddCircleAnnotation(1, new PdfRectangle(200, 400, 320, 480),
            contents: "Circle comment", author: "Bob",
            red: 0, green: 0, blue: 1, borderWidth: 1.5);
        source.AddFreeTextAnnotation(1, new PdfRectangle(72, 200, 400, 280),
            "Visible text box", author: "Carol", fontSize: 14,
            textRed: 0.2, textGreen: 0.2, textBlue: 0.8,
            quadding: PdfFreeTextQuadding.Centered, borderWidth: 1,
            backgroundRed: 1, backgroundGreen: 1, backgroundBlue: 0.8);
        source.AddInkAnnotation(1,
            new[]
            {
                (IReadOnlyList<(double, double)>)new[] { (100.0, 100.0), (140.0, 130.0), (180.0, 100.0) },
                new[] { (200.0, 100.0), (240.0, 140.0) }
            },
            contents: "Freehand mark", author: "Dave",
            red: 0.1, green: 0.6, blue: 0.1, borderWidth: 2.5);

        var xfdf = XfdfSerializer.ExportAnnotations(source);

        using var target = PdfDocument.CreateNew();
        target.Pages.AddBlank();
        var result = XfdfSerializer.ImportAnnotations(target, xfdf);

        result.Skipped.Should().BeEmpty();
        result.Imported.Should().HaveCount(6);

        var annotations = target.GetPage(1).GetAnnotations();
        annotations.Should().HaveCount(6);

        var sourceAnnotations = source.GetPage(1).GetAnnotations();
        foreach (var expected in sourceAnnotations)
        {
            var actual = annotations.Single(a => a.Subtype == expected.Subtype);

            actual.Rect.Left.Should().BeApproximately(expected.Rect.Left, 0.001);
            actual.Rect.Bottom.Should().BeApproximately(expected.Rect.Bottom, 0.001);
            actual.Rect.Right.Should().BeApproximately(expected.Rect.Right, 0.001);
            actual.Rect.Top.Should().BeApproximately(expected.Rect.Top, 0.001);
            actual.Contents.Should().Be(expected.Contents);
            actual.Author.Should().Be(expected.Author);
            actual.Name.Should().Be(expected.Name, "/NM must survive the round-trip");
            actual.Flags.Should().Be(expected.Flags);

            if (expected.Color is { } c)
            {
                actual.Color.Should().NotBeNull();
                // Colors pass through #RRGGBB — 8 bits per channel.
                actual.Color!.Value.R.Should().BeApproximately(c.R, 1 / 255.0 + 0.0001);
                actual.Color.Value.G.Should().BeApproximately(c.G, 1 / 255.0 + 0.0001);
                actual.Color.Value.B.Should().BeApproximately(c.B, 1 / 255.0 + 0.0001);
            }
        }

        // Subtype-specific geometry.
        var ink = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Ink);
        ink.InkStrokes.Should().NotBeNull().And.HaveCount(2);
        ink.InkStrokes![0].Should().HaveCount(3);
        ink.InkStrokes[0][1].X.Should().BeApproximately(140, 0.001);
        ink.InkStrokes[0][1].Y.Should().BeApproximately(130, 0.001);
        ink.BorderWidth.Should().Be(2.5);

        var highlight = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Highlight);
        highlight.QuadPoints.Should().NotBeNull().And.HaveCount(1);
        highlight.QuadPoints![0].Left.Should().BeApproximately(100, 0.001);
        highlight.QuadPoints[0].Top.Should().BeApproximately(670, 0.001);

        var freeText = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.FreeText);
        freeText.RawDictionary.GetInt("Q", -1).Should().Be(1, "centered quadding must survive");
        freeText.HasAppearance.Should().BeTrue("authoring-backed import bakes /AP");

        var sticky = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        sticky.IsOpen.Should().BeTrue();
        sticky.IconName.Should().Be("Comment");

        var square = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Square);
        square.BorderWidth.Should().Be(3);
        square.RawDictionary.GetArrayOrNull("IC").Should().NotBeNull("interior color must survive");
    }

    [Fact]
    public void RoundTrip_ModificationDates_PassThroughVerbatim()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();
        var authored = source.AddSquareAnnotation(1, new PdfRectangle(10, 10, 60, 60), borderWidth: 1);
        authored.RawDictionary.SetString("M", SampleDate);
        authored.RawDictionary.SetString("CreationDate", SampleDate);

        var xfdf = XfdfSerializer.ExportAnnotations(source);
        XDocument.Parse(xfdf).Descendants().Single(e => e.Name.LocalName == "square")
            .Attribute("date")!.Value.Should().Be(SampleDate);

        using var target = PdfDocument.CreateNew();
        target.Pages.AddBlank();
        var imported = XfdfSerializer.ImportAnnotations(target, xfdf).Imported.Single();

        imported.RawDictionary.GetStringOrNull("M").Should().Be(SampleDate);
        imported.RawDictionary.GetStringOrNull("CreationDate").Should().Be(SampleDate);
    }

    [Fact]
    public void RoundTrip_ImportedAnnotations_SurviveSaveAndReload()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();
        source.AddFreeTextAnnotation(1, new PdfRectangle(72, 500, 300, 560), "Persisted via XFDF");
        var xfdf = XfdfSerializer.ExportAnnotations(source);

        byte[] saved;
        using (var target = PdfDocument.CreateNew())
        {
            target.Pages.AddBlank();
            XfdfSerializer.ImportAnnotations(target, xfdf);
            saved = target.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annotation = reopened.GetPage(1).GetAnnotations().Should().ContainSingle().Subject;
        annotation.Subtype.Should().Be(PdfAnnotationSubtype.FreeText);
        annotation.Contents.Should().Be("Persisted via XFDF");
        annotation.HasAppearance.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_SpecialCharactersInContents_AreXmlSafe()
    {
        using var source = PdfDocument.CreateNew();
        source.Pages.AddBlank();
        const string tricky = "a < b && \"c\" > 'd' — æøå 中文 ✓";
        source.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), tricky);

        var xfdf = XfdfSerializer.ExportAnnotations(source);

        using var target = PdfDocument.CreateNew();
        target.Pages.AddBlank();
        var imported = XfdfSerializer.ImportAnnotations(target, xfdf).Imported.Single();
        imported.Contents.Should().Be(tricky);
    }

    [Fact]
    public void RoundTrip_GenericSubtypes_AreStableAcrossTwoPasses()
    {
        // line/polygon/underline have no authoring method: the importer builds
        // raw dictionaries. Import → export → import must be lossless.
        const string firstPass = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
              <annots>
                <line page="0" rect="50,50,250,120" color="#0000FF" start="60,60" end="240,110"
                      title="Eve" name="line-1" date="{SampleDate}"/>
                <polygon page="0" rect="300,300,420,400" color="#00AA00" name="poly-1"
                         vertices="300,300;420,300;360,400"/>
                <underline page="0" rect="100,600,300,615" color="#CC0000" name="ul-1"
                           coords="100,615,300,615,100,600,300,600">
                  <contents>Underlined claim</contents>
                </underline>
              </annots>
            </xfdf>
            """;

        using var doc1 = PdfDocument.CreateNew();
        doc1.Pages.AddBlank();
        var pass1 = XfdfSerializer.ImportAnnotations(doc1, firstPass);
        pass1.Skipped.Should().BeEmpty();
        pass1.Imported.Should().HaveCount(3);

        var exported = XfdfSerializer.ExportAnnotations(doc1);

        using var doc2 = PdfDocument.CreateNew();
        doc2.Pages.AddBlank();
        var pass2 = XfdfSerializer.ImportAnnotations(doc2, exported);
        pass2.Skipped.Should().BeEmpty();

        var annotations = doc2.GetPage(1).GetAnnotations();
        annotations.Should().HaveCount(3);

        var line = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Line);
        line.LineEndpoints.Should().Be((60d, 60d, 240d, 110d));
        line.Author.Should().Be("Eve");
        line.Name.Should().Be("line-1");
        line.RawDictionary.GetStringOrNull("M").Should().Be(SampleDate);

        var polygon = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Polygon);
        polygon.Vertices.Should().Equal((300d, 300d), (420d, 300d), (360d, 400d));

        var underline = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Underline);
        underline.Contents.Should().Be("Underlined claim");
        underline.QuadPoints.Should().ContainSingle();
        underline.QuadPoints![0].Should().Be(new PdfRectangle(100, 600, 300, 615));
    }

    // ── Interop: spec-derived external dialect ────────────────────────────────

    [Fact]
    public void Import_AcrobatStyleSample_ParsesAllSupportedSubtypes()
    {
        // Fixture written in the dialect real Acrobat emits (XFDF 3.0):
        // <f href>, <ids>, contents-richtext, fields section, popup elements.
        const string acrobatXfdf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
              <annots>
                <highlight color="#FFD100" creationdate="D:20260420101112-07'00'" date="D:20260420101112-07'00'"
                           flags="print" name="8b3f5f64-0f4c-4f7e-9d5a-2f1a3c4d5e6f" page="0"
                           coords="132.2,706.9,266.5,706.9,132.2,694.1,266.5,694.1,132.2,692.0,201.0,692.0,132.2,679.2,201.0,679.2"
                           rect="132.2,679.2,266.5,706.9" subject="Highlight" title="jdoe">
                  <contents-richtext><body xmlns="http://www.w3.org/1999/xhtml"><p>Rich text ignored</p></body></contents-richtext>
                  <contents>Two-quad highlight</contents>
                </highlight>
                <text color="#FFD100" creationdate="D:20260420101500-07'00'" date="D:20260420101500-07'00'"
                      flags="print,nozoom,norotate" icon="Comment" name="note-1" open="no" page="0"
                      rect="431.6,730.4,449.6,748.4" subject="Sticky Note" title="jdoe">
                  <contents>Please re-check the totals</contents>
                </text>
                <ink color="#FF0000" flags="print" name="ink-1" page="0" rect="187.3,477.9,364.5,538.1" title="jdoe" width="2.1">
                  <inklist>
                    <gesture>190.4,535.0;220.9,510.2;251.5,534.1;282.0,509.3</gesture>
                    <gesture>300.0,500.0;361.4,481.0</gesture>
                  </inklist>
                </ink>
                <freetext page="0" rect="72,72,288,130" flags="print" name="ft-1" title="jdoe" justification="centered">
                  <contents>Typed comment</contents>
                  <defaultappearance>0 0.4 0 rg /Helv 11 Tf</defaultappearance>
                </freetext>
                <strikeout color="#FF0000" page="0" rect="100,200,220,214" name="so-1"
                           coords="100,214,220,214,100,200,220,200" title="jdoe">
                  <contents>Delete this sentence</contents>
                </strikeout>
                <popup flags="print" name="popup-1" open="no" page="0" parent="note-1" rect="450,600,650,730"/>
                <fancyfuturetype page="0" rect="1,1,2,2" name="unknown-1"/>
              </annots>
              <f href="original-document.pdf"/>
              <ids original="F1E2D3C4B5A69788" modified="8877A695B4C3D2E1"/>
            </xfdf>
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var result = XfdfSerializer.ImportAnnotations(doc, acrobatXfdf);

        result.Imported.Should().HaveCount(5);
        result.Skipped.Should().HaveCount(2);
        result.Skipped.Should().Contain(s => s.Contains("popup"));
        result.Skipped.Should().Contain(s => s.Contains("fancyfuturetype"));

        var annotations = doc.GetPage(1).GetAnnotations();

        var highlight = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Highlight);
        highlight.Contents.Should().Be("Two-quad highlight");
        highlight.Author.Should().Be("jdoe");
        highlight.Name.Should().Be("8b3f5f64-0f4c-4f7e-9d5a-2f1a3c4d5e6f");
        highlight.QuadPoints.Should().HaveCount(2, "the two Acrobat quads must not collapse");
        highlight.Color!.Value.R.Should().BeApproximately(1.0, 0.005);
        highlight.Color.Value.G.Should().BeApproximately(0xD1 / 255.0, 0.005);
        highlight.RawDictionary.GetStringOrNull("Subj").Should().Be("Highlight");
        highlight.CreationDate.Should().NotBeNull();
        highlight.CreationDate!.Value.Offset.Should().Be(TimeSpan.FromHours(-7));

        var sticky = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        sticky.IconName.Should().Be("Comment");
        sticky.IsOpen.Should().BeFalse();
        sticky.Flags.Should().Be(
            PdfAnnotationFlags.Print | PdfAnnotationFlags.NoZoom | PdfAnnotationFlags.NoRotate);

        var ink = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Ink);
        ink.InkStrokes.Should().HaveCount(2);
        ink.InkStrokes![0].Should().HaveCount(4);
        ink.BorderWidth.Should().Be(2.1);
        ink.Color!.Value.R.Should().BeApproximately(1, 0.005);

        var freeText = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.FreeText);
        freeText.Contents.Should().Be("Typed comment");
        freeText.RawDictionary.GetInt("Q", -1).Should().Be(1);
        freeText.RawDictionary.GetStringOrNull("DA").Should().Contain("0 0.4 0 rg").And.Contain("11 Tf");

        var strikeout = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.StrikeOut);
        strikeout.Contents.Should().Be("Delete this sentence");
        strikeout.QuadPoints.Should().ContainSingle();
    }

    // ── Error handling and skips ──────────────────────────────────────────────

    [Fact]
    public void Import_MalformedXml_ThrowsXmlException()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var act = () => XfdfSerializer.ImportAnnotations(doc, "<xfdf><annots></xfdf>");
        act.Should().Throw<XmlException>();
    }

    [Fact]
    public void Import_NonXfdfRoot_ThrowsInvalidDataException()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var act = () => XfdfSerializer.ImportAnnotations(doc, "<html><body/></html>");
        act.Should().Throw<InvalidDataException>().WithMessage("*xfdf*");
    }

    [Fact]
    public void Import_PageOutOfRange_IsSkippedNotFatal()
    {
        const string xfdf = """
            <xfdf xmlns="http://ns.adobe.com/xfdf/">
              <annots>
                <square page="7" rect="10,10,50,50" color="#FF0000" name="lost-square"/>
                <square page="0" rect="10,10,50,50" color="#FF0000" name="kept-square"/>
              </annots>
            </xfdf>
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var result = XfdfSerializer.ImportAnnotations(doc, xfdf);

        result.Imported.Should().ContainSingle().Which.Name.Should().Be("kept-square");
        result.Skipped.Should().ContainSingle().Which.Should().Contain("lost-square").And.Contain("out of range");
    }

    [Fact]
    public void Import_MissingRect_IsSkippedNotFatal()
    {
        const string xfdf = """
            <xfdf xmlns="http://ns.adobe.com/xfdf/">
              <annots>
                <highlight page="0" name="no-rect"><contents>orphan</contents></highlight>
              </annots>
            </xfdf>
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var result = XfdfSerializer.ImportAnnotations(doc, xfdf);

        result.Imported.Should().BeEmpty();
        result.Skipped.Should().ContainSingle().Which.Should().Contain("no-rect").And.Contain("rect");
    }

    [Fact]
    public void Import_WithoutAnnotsSection_YieldsEmptyResult()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        var result = XfdfSerializer.ImportAnnotations(doc,
            """<xfdf xmlns="http://ns.adobe.com/xfdf/"><fields/></xfdf>""");

        result.Imported.Should().BeEmpty();
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Import_StreamOverload_MatchesStringOverload()
    {
        const string xfdf = """
            <xfdf xmlns="http://ns.adobe.com/xfdf/">
              <annots>
                <square page="0" rect="10,10,90,90" color="#336699" width="2" name="stream-square"/>
              </annots>
            </xfdf>
            """;

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xfdf));

        var result = XfdfSerializer.ImportAnnotations(doc, ms);
        var square = result.Imported.Should().ContainSingle().Subject;
        square.Subtype.Should().Be(PdfAnnotationSubtype.Square);
        square.Name.Should().Be("stream-square");
        square.Color!.Value.R.Should().BeApproximately(0x33 / 255.0, 0.005);
    }
}
