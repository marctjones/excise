using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #2077: a page's annotation and link parsers build the catalog's named-destination map only when a link names
/// its destination (it walks the whole name tree; counting redaction marks over a 455-page book built it 455
/// times). The laziness must not change what a named link resolves to.
/// </summary>
public class LazyNamedDestinationTests
{
    private static PdfDocument Open(string page1Annots)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Dests << /Chap2 [4 0 R /Fit] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {page1Annots} >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(sb.ToString()));
    }

    [Fact]
    public void ALinkThatNamesItsDestination_StillResolvesToThePage()
    {
        var doc = Open("/Annots [<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /Dest /Chap2 >>]");

        var links = doc.GetPage(1).GetLinks();

        links.Should().ContainSingle().Which.DestinationPage.Should().Be(2);
    }

    [Fact]
    public void AnnotationsWithNoNamedLink_ParseWithoutTheMap()
    {
        var doc = Open("/Annots [<< /Type /Annot /Subtype /Text /Rect [10 10 20 20] /Contents (note) >> << /Type /Annot /Subtype /Redact /Rect [30 30 60 60] >>]");

        var annots = doc.GetPage(1).GetAnnotations();

        annots.Should().HaveCount(2);
        annots.Count(a => a.Subtype == PdfAnnotationSubtype.Redact).Should().Be(1);
    }
}
