using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1833 item 1: every destination names the page by the same leaf rule
/// <see cref="PageCollection"/> uses. The second leaf carries <c>/Type /Template</c>
/// (pdfium's bad_page_type.pdf shape), which the page list recovers as page 2.
/// A second walk that skipped it without counting numbered every later
/// destination one short: object 5 is <c>Pages[2]</c>, but outlines, links,
/// widgets, named destinations and actions called it page 2.
/// </summary>
public class PageCollectionPageNumberTests
{
    private const int TargetPage = 3;

    [Fact]
    public void Fixture_IndependentReaderShowsTheTargetObjectAsPageThree()
    {
        Assert.SkipUnless(MutoolStextOracle.IsAvailable, "mutool not on PATH");
        MutoolStextOracle.Words(WrongTypeLeafPdf(), TargetPage, 792).Select(w => w.Text)
            .Should().Contain("CHARLIE", "MuPDF numbers object 5, the only page drawing CHARLIE, as page 3");
    }

    [Fact]
    public void PageList_HoldsTheTargetObjectAtIndexTwo()
    {
        using var doc = PdfDocument.Open(WrongTypeLeafPdf());
        doc.PageCount.Should().Be(3);
        doc.GetPageReference(TargetPage).Should().Be(new PdfReference(5, 0));
        doc.TryGetPageNumber(new PdfReference(5, 0), out var byRef).Should().BeTrue();
        byRef.Should().Be(TargetPage);
        doc.TryGetPageNumber(doc.Pages[TargetPage - 1].Dictionary, out var byDict).Should().BeTrue();
        byDict.Should().Be(TargetPage);
        doc.TryGetPageNumber(new PdfReference(4, 0), out var template).Should().BeTrue();
        template.Should().Be(2, "the wrong-/Type leaf is a page, so it has a number");
    }

    [Fact]
    public void OutlineDestination_NamesTheTargetPage()
    {
        using var doc = PdfDocument.Open(WrongTypeLeafPdf());
        PdfOutlineParser.Parse(doc).Single().PageNumber.Should().Be(TargetPage);
    }

    [Fact]
    public void LinkAnnotationDestination_NamesTheTargetPage()
    {
        using var doc = PdfDocument.Open(WrongTypeLeafPdf());
        doc.GetPage(1).GetLinks().Single().DestinationPage.Should().Be(TargetPage);
        doc.GetPage(1).GetAnnotations().Single().DestinationPage.Should().Be(TargetPage);
    }

    [Fact]
    public void WidgetPageEntry_NamesTheTargetPage()
    {
        using var doc = PdfDocument.Open(WrongTypeLeafPdf());
        doc.GetAcroForm()!.Fields.Single().PageNumber.Should().Be(TargetPage);
    }

    [Fact]
    public void NamedDestinationAndOpenAction_NameTheTargetPage()
    {
        using var doc = PdfDocument.Open(WrongTypeLeafPdf());
        doc.GetNamedDestinations()["Target"].PageNumber.Should().Be(TargetPage);
        doc.OpenAction!.DestinationPage.Should().Be(TargetPage);
    }

    /// <summary>
    /// The unredact audit located a structure-tree carrier by comparing
    /// <see cref="PdfObject.ObjectNumber"/> of two <see cref="PdfReference"/>
    /// tokens. A reference token never has that set, so null equalled null and
    /// every carrier with a <c>/Pg</c> was reported on page 1.
    /// </summary>
    [Fact]
    public void StructureTreeCarrier_IsReportedOnThePageItsPgNames()
    {
        using var doc = PdfDocument.Open(WrongTypeLeafPdf());
        CarrierTextRecovery.Scan(doc, TestContext.Current.CancellationToken)
            .Single(f => f.Carrier == "structure-tree /ActualText" && f.Text == "PGTOKEN")
            .PageNumber.Should().Be(TargetPage);
    }

    /// <summary>
    /// Page 1 links to object 5; page 2 is <c>/Type /Template</c>; object 5
    /// draws CHARLIE and is the target of an outline item, a link, a widget's
    /// <c>/P</c>, a named destination, the open action and a structure
    /// element's <c>/Pg</c>.
    /// </summary>
    private static byte[] WrongTypeLeafPdf()
    {
        const string font = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        static string Content(string text)
        {
            var ops = $"BT /F1 24 Tf 72 700 Td ({text}) Tj ET";
            return $"<< /Length {ops.Length} >>\nstream\n{ops}\nendstream";
        }

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 9 0 R /AcroForm << /Fields [12 0 R] >> " +
                "/OpenAction << /S /GoTo /D [5 0 R /Fit] >> /Dests << /Target [5 0 R /Fit] >> /StructTreeRoot 13 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {font} /Contents 6 0 R /Annots [11 0 R] >>",
            $"<< /Type /Template /Parent 2 0 R /MediaBox [0 0 612 792] {font} /Contents 7 0 R >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {font} /Contents 8 0 R /Annots [12 0 R] >>",
            Content("ALPHA"),
            Content("BRAVO"),
            Content("CHARLIE"),
            "<< /Type /Outlines /First 10 0 R /Last 10 0 R /Count 1 >>",
            "<< /Title (Go) /Parent 9 0 R /Dest [5 0 R /Fit] >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 600 200 620] /Dest [5 0 R /Fit] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (f) /V (v) /Rect [72 600 200 620] /P 5 0 R >>",
            "<< /Type /StructTreeRoot /K 14 0 R >>",
            "<< /Type /StructElem /S /Span /P 13 0 R /Pg 5 0 R /ActualText (PGTOKEN) >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
