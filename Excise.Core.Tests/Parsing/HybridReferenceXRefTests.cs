using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// Hybrid-reference files (PDF 32000-1 §7.5.8.4) carry both a classic xref
/// table and a cross-reference stream for the same revision, with the trailer
/// pointing at the stream via /XRefStm.
///
/// WHY THIS IS NOT A COSMETIC PARSER DETAIL
/// ----------------------------------------
/// Ignoring /XRefStm does not fail. It SUCCEEDS at reading the wrong revision:
/// the reader falls through to /Prev and resolves superseded object definitions
/// as if they were current, with no warning and no visual artifact. excise is a
/// redaction tool, so its users are reviewing documents someone else produced —
/// showing them content the author replaced, and letting them redact against
/// it, is a document-identity failure rather than a rendering one (#872).
///
/// The fixture below was found by the PDFium corpus scan (#862); the same page
/// is the only one in 3,915 where excise disagreed with all five reference
/// renderers for a structural rather than an image-decoding reason.
/// </summary>
public class HybridReferenceXRefTests
{
    /// <summary>
    /// pixel/bug_1484283.pdf is 1,399 bytes and does exactly one thing: an
    /// incremental update whose trailer is
    ///
    ///     trailer &lt;&lt; /Prev 466 /Root 1 0 R /Size 7 /XRefStm 1036 &gt;&gt;
    ///
    /// The classic `xref 4 3` table covers objects 4, 5 and 6 only. Object 2
    /// (the /Pages node, carrying /MediaBox) is updated 300 -> 350 and lives
    /// inside an ObjStm, reachable ONLY through the cross-reference stream at
    /// offset 1036.
    ///
    /// So the height is a single-number oracle for whether /XRefStm was
    /// honoured: 350 means yes, 300 means the parser silently served the
    /// superseded revision.
    /// </summary>
    [Fact]
    public void HybridReferenceFile_ResolvesObjectUpdatedOnlyViaXRefStm()
    {
        var path = FindFixture("pixel/bug_1484283.pdf");
        Assert.SkipWhen(path == null,
            "PDFium corpus not present — run scripts/download-pdfium-corpus.sh");

        using var doc = PdfDocument.Open(path!);
        var page = doc.GetPage(1);

        page.Height.Should().Be(350,
            "object 2's /MediaBox is updated to [0 0 200 350] in an ObjStm reachable only " +
            "through the /XRefStm cross-reference stream. A height of 300 means the parser " +
            "ignored /XRefStm, fell through to /Prev, and resolved the SUPERSEDED revision " +
            "as current — silently, which is the whole danger of #872");

        page.Width.Should().Be(200, "the width is unchanged by the update, so it should not move");
    }

    /// <summary>
    /// The content stream is updated in the same revision, by the classic table
    /// rather than the stream. Asserting it separately keeps the two mechanisms
    /// from vouching for each other: a fix that wired up /XRefStm but broke
    /// ordinary /Prev precedence would still pass the MediaBox assertion alone.
    /// </summary>
    [Fact]
    public void HybridReferenceFile_StillHonoursTheClassicTableUpdate()
    {
        var path = FindFixture("pixel/bug_1484283.pdf");
        Assert.SkipWhen(path == null,
            "PDFium corpus not present — run scripts/download-pdfium-corpus.sh");

        using var doc = PdfDocument.Open(path!);
        var text = System.Text.Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());

        // The update recolours the rectangles: blue/green/red become
        // yellow/magenta/cyan. "1 1 0 rg" appears only in the NEW object 4.
        text.Should().Contain("1 1 0 rg",
            "the updated content stream sets yellow; seeing only the original " +
            "blue/green/red means the newer object 4 was not picked up");
        text.Should().NotContain("0 0 1 rg",
            "0 0 1 rg (blue) belongs to the superseded content stream only");
    }

    private static string? FindFixture(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test-pdfs", "pdfium",
                                         relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
