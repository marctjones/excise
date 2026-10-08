using System.Text;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1848 — the needle search folds must agree with the needle redaction folds.
/// Redaction folds and TRIMS the caller's term (<c>Fold(term).Trim()</c>, and an
/// empty result matches nothing); search used to fold only, so
/// (1) a term that folds to nothing (a lone zero-width space) spun forever in the
/// page path and produced one empty hit per character in the annotation path, and
/// (2) a trailing space made search show fewer hits than redaction would remove.
/// </summary>
public sealed class SearchNeedleNormalizationTests : IDisposable
{
    private const string Line = "Lee met Lee and Lee.";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-needle-norm-{Guid.NewGuid():N}");

    public SearchNeedleNormalizationTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private static PdfSearchService Service() => new(NullLogger<PdfSearchService>.Instance);

    private static bool InAnnotation(SearchMatch m) =>
        m.Context.EndsWith("[in annotation]", StringComparison.Ordinal);

    [Theory]
    [InlineData("​")]            // zero-width space folds to nothing
    [InlineData("​​")]
    [InlineData("­")]            // soft hyphen
    [InlineData("  ")]                // whitespace only
    public void NeedleThatFoldsToNothing_TerminatesAndMatchesNothing(string needle)
    {
        using var doc = PdfDocument.Open(Fixture());
        var service = Service();

        List<SearchMatch>? hits = null;
        var worker = Task.Run(() => hits = service.SearchInPage(doc.GetPage(1), needle));

        worker.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("search must terminate on an empty folded needle");
        hits.Should().BeEmpty("redaction matches nothing for this needle, so search must show nothing");
    }

    [Theory]
    [InlineData("Lee ")]
    [InlineData(" Lee")]
    [InlineData("  Lee  ")]
    public void SurroundingWhitespace_DoesNotChangeWhatSearchFinds(string needle)
    {
        using var doc = PdfDocument.Open(Fixture());
        var service = Service();

        var hits = service.SearchInPage(doc.GetPage(1), needle, caseSensitive: true);

        hits.Count(m => !InAnnotation(m)).Should().Be(3, "page: the same three hits as the bare term");
        hits.Count(InAnnotation).Should().Be(3, "annotation: the same three hits as the bare term");
    }

    [Fact]
    public void TrailingSpaceNeedle_SearchAndRedactionAgree_AndAnIndependentReaderConfirms()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var pdf = Fixture();
        var inputPath = Path.Combine(_tempDir, "input.pdf");
        File.WriteAllBytes(inputPath, pdf);
        MutoolTextExtractor.ExtractPage(inputPath, 1)!.Split("Lee").Length.Should().Be(4,
            "input-side control: mutool reads three occurrences");

        using var searchDoc = PdfDocument.Open(pdf);
        var searched = Service().SearchInPage(searchDoc.GetPage(1), "Lee ", caseSensitive: true)
            .Count(m => !InAnnotation(m));

        using var redactDoc = PdfDocument.Open(pdf);
        var report = redactDoc.RedactText("Lee ", RedactionOptions.Default with
        {
            CaseSensitive = true,
            StripDocumentMetadata = false,
        });
        report.MatchesLocated.Should().Be(searched, "search showed N hits, redaction must remove exactly N");

        var outputPath = Path.Combine(_tempDir, "output.pdf");
        File.WriteAllBytes(outputPath, redactDoc.SaveToBytes());
        MutoolTextExtractor.ExtractPage(outputPath, 1)!.Should().NotContain("Lee",
            "mutool is the independent reader of the redacted page");
    }

    private static byte[] Fixture()
    {
        var body = $"BT /F1 12 Tf 72 700 Td ({Line}) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [6 0 R] " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {body.Length} >>\nstream\n{body}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /Annot /Subtype /Text /Rect [500 100 520 120] /Contents ({Line}) >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
