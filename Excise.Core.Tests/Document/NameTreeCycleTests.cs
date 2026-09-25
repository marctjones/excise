using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Redaction;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1833: a /Kids cycle in a name tree (§7.9.6) or number tree (§7.9.7) was an
/// uncatchable StackOverflow in four walkers. Each fixture below has a root and
/// a child whose /Kids point at the child itself and back at the root, and one
/// real entry in each node; the walk must end and report each entry once.
/// </summary>
public class NameTreeCycleTests
{
    /// <summary>Two pages, then <paramref name="objects"/> as objects 4, 5, ...</summary>
    private static byte[] Pdf(string catalogEntries, params string[] objects)
    {
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogEntries} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };
        bodies.AddRange(objects);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer << /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private const string Dest = "[3 0 R /Fit]";

    [Fact(Timeout = 30_000)]
    public void EmbeddedFiles_KidsCycle_TerminatesAndFindsEachFileOnce()
    {
        using var doc = PdfDocument.Open(EmbeddedFilesCycle("payload"));

        doc.GetEmbeddedFiles().Select(f => f.Name).Should().Equal("a.txt", "b.txt");
    }

    [Fact(Timeout = 30_000)]
    public void EmbeddedFiles_KidsCycle_ScrubbingTheTermRemovesTheFileAndLeaksNothing()
    {
        using var doc = PdfDocument.Open(EmbeddedFilesCycle("SecretMarker"));

        PdfDocumentSanitizer.ScrubTerms(doc, new[] { "SecretMarker" }, caseSensitive: true, RedactionCarriers.EmbeddedFiles)
            .Should().BeTrue();

        doc.GetEmbeddedFiles().Select(f => f.Name).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "SecretMarker").Should().BeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public void Dests_KidsCycle_TerminatesAndFindsEachDestinationOnce()
    {
        var pdf = Pdf("/Names << /Dests 4 0 R >>",
            $"<< /Names [(a) {Dest}] /Kids [5 0 R] >>",
            $"<< /Names [(b) {Dest}] /Kids [5 0 R 4 0 R] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.GetNamedDestinations().Keys.Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact(Timeout = 30_000)]
    public void JavaScript_KidsCycle_TerminatesAndFindsEachScriptOnce()
    {
        var pdf = Pdf("/Names << /JavaScript 4 0 R >>",
            "<< /Names [(a) << /S /JavaScript /JS (1;) >>] /Kids [5 0 R] >>",
            "<< /Names [(b) << /S /JavaScript /JS (2;) >>] /Kids [5 0 R 4 0 R] >>");
        using var doc = PdfDocument.Open(pdf);

        doc.DocumentJavaScriptActions.Keys.Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact(Timeout = 30_000)]
    public void PageLabels_KidsCycle_TerminatesAndReadsBothLeaves()
    {
        var pdf = Pdf("/PageLabels 4 0 R",
            "<< /Nums [0 << /S /r >>] /Kids [5 0 R] >>",
            "<< /Nums [1 << /S /D /P (P-) >>] /Kids [5 0 R 4 0 R] >>");
        using var doc = PdfDocument.Open(pdf);

        PdfPageLabelParser.ParsePageLabels(doc).Keys.Should().BeEquivalentTo(new[] { 0, 1 });
    }

    [Fact(Timeout = 30_000)]
    public void PageLabels_ChainDeeperThanTheBound_StopsAtDepthSixtyFour()
    {
        var chain = Enumerable.Range(0, 70)
            .Select(i => $"<< /Nums [{i} << /S /D >>]{(i < 69 ? $" /Kids [{i + 5} 0 R]" : "")} >>")
            .ToArray();
        using var doc = PdfDocument.Open(Pdf("/PageLabels 4 0 R", chain));

        var labels = PdfPageLabelParser.ParsePageLabels(doc);

        labels.Keys.Should().BeEquivalentTo(Enumerable.Range(0, 65), "the root is depth 0 and depth 64 is the last one read");
    }

    private static byte[] EmbeddedFilesCycle(string payload) => Pdf("/Names << /EmbeddedFiles 4 0 R >>",
        "<< /Names [(a.txt) 6 0 R] /Kids [5 0 R] >>",
        "<< /Names [(b.txt) 7 0 R] /Kids [5 0 R 4 0 R] >>",
        "<< /Type /Filespec /F (a.txt) /EF << /F 8 0 R >> >>",
        "<< /Type /Filespec /F (b.txt) /EF << /F 8 0 R >> >>",
        $"<< /Type /EmbeddedFile /Length {payload.Length} >>\nstream\n{payload}\nendstream");
}
