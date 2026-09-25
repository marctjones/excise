using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;
using static Excise.Core.Tests.Text.Segmentation.FormXObjectRedactionTests;

namespace Excise.Core.Tests.Redaction;

/// <summary>
/// #1830: the three op-list consumers that ask "which marked-content spans
/// enclose this operator" (§14.6) against one page: nested <c>BDC</c>/<c>BMC</c>,
/// stray <c>EMC</c>s (no-ops), a carrier span only partly under the area and
/// continuing across the <c>/Contents</c> array seam (one stream for marked
/// content, §14.6), a carrier span inside an <c>/OC</c> span whose group is OFF,
/// a carrier reached through a property-list NAME, a named list with no carrier
/// shared by two spans, a carrier span that encloses only an inline image, and a
/// span never closed. Each consumer must implicate exactly the spans that
/// enclose the area; the scrubbers are judged on the saved bytes.
/// </summary>
public class MarkedContentSpanConsumerTests
{
    private const string InlineImage = "IMAGE";

    private static readonly string[] ContentCarriers =
    {
        "OUTERCARRIER", "INNERCARRIER", "PARTCARRIER", "LAYERCARRIER",
        "NAMEDCARRIER", "FIGURECARRIER", "OPENCARRIER",
    };

    private static readonly string[] StructureCarriers =
    {
        "STRUCTZERO", "STRUCTONE", "STRUCTTWO", "STRUCTTHREE", "STRUCTFOUR",
    };

    private const string FirstStream =
        "EMC\n" +                                                           // stray: nothing open
        "/Span <</MCID 0 /ActualText (OUTERCARRIER)>> BDC\n" +
        "/Artifact BMC\n" +
        "/Span <</MCID 1 /ActualText (INNERCARRIER)>> BDC\n" +
        "BT /F1 12 Tf 100 700 Td (NESTED) Tj ET\n" +
        "EMC\n" +
        "EMC\n" +
        "BT /F1 12 Tf 300 700 Td (OUTERONLY) Tj ET\n" +
        "EMC\n" +
        "EMC\n" +                                                           // stray again
        "/Span <</MCID 2 /ActualText (PARTCARRIER)>> BDC\n" +
        "BT /F1 12 Tf 100 600 Td (PARTNEAR) Tj ET\n";

    private const string SecondStream =
        "BT /F1 12 Tf 300 600 Td (PARTFAR) Tj ET\n" +                      // same span, next stream
        "EMC\n" +
        "/OC /OC1 BDC\n" +
        "/Span <</MCID 3 /ActualText (LAYERCARRIER)>> BDC\n" +
        "BT /F1 12 Tf 100 500 Td (LAYERED) Tj ET\n" +
        "EMC\n" +
        "EMC\n" +
        "/Span /P1 BDC BT /F1 12 Tf 100 400 Td (NAMEDTEXT) Tj ET EMC\n" +
        "/Span /P2 BDC BT /F1 12 Tf 100 350 Td (SHAREDA) Tj ET EMC\n" +
        "/Span /P2 BDC BT /F1 12 Tf 300 350 Td (SHAREDB) Tj ET EMC\n" +
        "BT /F1 12 Tf 100 300 Td (TOPLEVEL) Tj ET\n" +
        "/Figure <</Alt (FIGURECARRIER)>> BDC\n" +
        "q 40 0 0 40 400 100 cm BI /W 4 /H 1 /BPC 8 /CS /G /L 4 ID PIXL EI Q\n" +
        "EMC\n" +
        "/Span <</MCID 4 /ActualText (OPENCARRIER)>> BDC\n" +
        "BT /F1 12 Tf 100 200 Td (UNCLOSED) Tj ET\n";                      // never closed

    private static byte[] Fixture() => Build(
        Obj("<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 10 0 R /MarkInfo << /Marked true >> " +
            "/OCProperties << /OCGs [7 0 R] /D << /OFF [7 0 R] >> >> >>"),
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [4 0 R 5 0 R] /StructParents 0 " +
            "/Resources << /Font << /F1 6 0 R >> /Properties << /OC1 7 0 R /P1 8 0 R /P2 9 0 R >> >> >>"),
        Stream("", FirstStream),
        Stream("", SecondStream),
        Obj(HelveticaFont),
        Obj("<< /Type /OCG /Name (Hidden layer) >>"),
        Obj("<< /ActualText (NAMEDCARRIER) >>"),
        Obj("<< /Lang (en-US) >>"),
        Obj("<< /Type /StructTreeRoot /K [11 0 R] >>"),
        Obj("<< /Type /StructElem /S /Document /P 10 0 R /K [12 0 R 13 0 R 14 0 R 15 0 R 16 0 R] >>"),
        Obj("<< /Type /StructElem /S /Span /P 11 0 R /Pg 3 0 R /K 0 /ActualText (STRUCTZERO) >>"),
        Obj("<< /Type /StructElem /S /Span /P 11 0 R /Pg 3 0 R /K 1 /ActualText (STRUCTONE) >>"),
        Obj("<< /Type /StructElem /S /Span /P 11 0 R /Pg 3 0 R /K 2 /ActualText (STRUCTTWO) >>"),
        Obj("<< /Type /StructElem /S /Span /P 11 0 R /Pg 3 0 R /K 3 /ActualText (STRUCTTHREE) >>"),
        Obj("<< /Type /StructElem /S /Span /P 11 0 R /Pg 3 0 R /K 4 /ActualText (STRUCTFOUR) >>"));

    // ---- MarkedContentCarrierScrubber ----

    [Theory]
    [InlineData("NESTED", "OUTERCARRIER INNERCARRIER")]
    [InlineData("OUTERONLY", "OUTERCARRIER")]
    [InlineData("PARTNEAR", "PARTCARRIER")]
    [InlineData("PARTFAR", "PARTCARRIER")]
    [InlineData("LAYERED", "LAYERCARRIER")]
    [InlineData("NAMEDTEXT", "NAMEDCARRIER")]
    [InlineData("SHAREDA", "")]
    [InlineData("TOPLEVEL", "")]
    [InlineData(InlineImage, "FIGURECARRIER")]
    [InlineData("UNCLOSED", "OPENCARRIER")]
    public void CarrierScrubber_RemovesExactlyTheCarriersWhoseSpanEnclosesTheArea(string target, string removed)
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);
        var content = page.GetContentStream();

        var changed = MarkedContentCarrierScrubber.Scrub(
            content.Operators, page, LeftHalfOf(content.Operators, target), out var refused);
        page.SetContentStream(content);

        var expected = removed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        changed.Should().Be(expected.Length > 0);
        refused.Should().BeEmpty(
            "/P2 carries no text, so the span that survives beside it is no reason to report a carrier");
        AssertOnlyRemoved(Save(doc), ContentCarriers, expected);
    }

    // ---- StructureTreeRedactionScrubber ----

    [Theory]
    [InlineData("NESTED", "STRUCTZERO STRUCTONE")]
    [InlineData("OUTERONLY", "STRUCTZERO")]
    [InlineData("PARTNEAR", "STRUCTTWO")]
    [InlineData("PARTFAR", "STRUCTTWO")]
    [InlineData("LAYERED", "STRUCTTHREE")]
    [InlineData("NAMEDTEXT", "")]
    [InlineData("SHAREDA", "")]
    [InlineData("TOPLEVEL", "")]
    [InlineData(InlineImage, "")]
    [InlineData("UNCLOSED", "STRUCTFOUR")]
    public void StructureTreeScrubber_RemovesTheElementsOfEveryMcidEnclosingTheArea(string target, string removed)
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);

        var changed = StructureTreeRedactionScrubber.ScrubArea(
            page, LeftHalfOf(page.GetContentStream().Operators, target));

        var expected = removed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        changed.Should().Be(expected.Length > 0);
        AssertOnlyRemoved(Save(doc), StructureCarriers, expected);
    }

    // ---- MarkedContentTextRecovery ----

    [Fact]
    public void Recovery_ReportsEveryCarrierAtTheUnionOfEverythingItsSpanDrew()
    {
        using var doc = PdfDocument.Open(Fixture());
        var ops = doc.GetPage(1).GetContentStream().Operators;
        PdfRectangle Box(string target) => BoxOf(ops, target).Normalize();
        PdfRectangle Union(PdfRectangle a, PdfRectangle b) => new(
            Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom),
            Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top));

        var hits = MarkedContentTextRecovery.Scan(doc);

        hits.Select(h => h.Text).Should().BeEquivalentTo(ContentCarriers,
            "every carrier span is reported once, the unclosed one included; /OC1 and /P2 carry no text");
        var at = hits.ToDictionary(h => h.Text, h => h.Enclosed);
        at["OUTERCARRIER"].Should().Be(Union(Box("NESTED"), Box("OUTERONLY")),
            "an outer span's box includes what a nested span drew");
        at["INNERCARRIER"].Should().Be(Box("NESTED"));
        at["PARTCARRIER"].Should().Be(Union(Box("PARTNEAR"), Box("PARTFAR")),
            "the span continues across the /Contents array seam");
        at["LAYERCARRIER"].Should().Be(Box("LAYERED"));
        at["NAMEDCARRIER"].Should().Be(Box("NAMEDTEXT"));
        at["FIGURECARRIER"].Should().Be(Box(InlineImage));
        at["OPENCARRIER"].Should().Be(Box("UNCLOSED"));
        hits.Where(h => h.NamedPropertyList).Select(h => h.Text).Should().Equal("NAMEDCARRIER");
        hits.Select(h => h.Carrier).Distinct().Should().BeEquivalentTo(
            new[] { "marked-content /ActualText", "marked-content /Alt" });
    }

    // ---- helpers ----

    private static PdfRectangle BoxOf(IReadOnlyList<ContentOperator> ops, string target) =>
        (target == InlineImage
            ? ops.Single(o => o.Name == "BI")
            : ops.Single(o => o.TextContent == target)).BoundingBox!.Value;

    /// <summary>Only part of the target lies under the area.</summary>
    private static PdfRectangle LeftHalfOf(IReadOnlyList<ContentOperator> ops, string target)
    {
        var box = BoxOf(ops, target).Normalize();
        return new PdfRectangle(box.Left, box.Bottom, (box.Left + box.Right) / 2, box.Top);
    }

    private static void AssertOnlyRemoved(byte[] saved, IEnumerable<string> carriers, string[] removed)
    {
        foreach (var carrier in carriers)
        {
            if (removed.Contains(carrier))
                SavedPdfLeakScanner.FindTerm(saved, carrier).Should().BeEmpty(
                    $"{carrier}'s span encloses the area, so it restates what is being removed");
            else
                SavedPdfLeakScanner.FindTerm(saved, carrier).Should().NotBeEmpty(
                    $"{carrier}'s span does not enclose the area; removing it destroys unrelated text");
        }
    }

    private static byte[] Save(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }
}
