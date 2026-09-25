using System.Text;
using System.Text.RegularExpressions;
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
/// #1834 item 2: one whole-word rule. A user who sees N whole-word search hits
/// and then redacts whole-word must lose exactly those N occurrences, on the
/// page and in the annotation. The expected counts are fixed by construction
/// of the fixture; the redaction side is read back by mutool (page) and qpdf
/// (annotation), never by excise itself.
/// </summary>
public sealed class WholeWordSearchRedactionParityTests : IDisposable
{
    private static readonly string[] PageLines =
    {
        "Lee met Leeward and Lee",
        "top secret plan, stop secret, top secretary, top secret.",
        "KEY_1 KEY 2KEY KEY9 (KEY) KEY-x",
        "Smith's car, O'Smith, Smith.",
        "Omega begins; the end is Omega",
        "xOmega Omegas Omega2",
    };

    private const string AnnotationText =
        "Lee met Leeward and Lee; top secret plan, stop secret, top secretary, top secret. " +
        "KEY_1 KEY 2KEY KEY9 (KEY) KEY-x; Smith's car, O'Smith, Smith. " +
        "Omega begins; the end is Omega; xOmega Omegas Omega2";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-wholeword-parity-{Guid.NewGuid():N}");

    public WholeWordSearchRedactionParityTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    // term, whole-word occurrences, substring occurrences (page and annotation each)
    [Theory]
    [InlineData("Lee", 2, 3)]                // line start and end; "Leeward" is not whole
    [InlineData("top secret", 2, 4)]         // a two-word term; "stop secret" and "top secretary" are not whole
    [InlineData("KEY", 3, 6)]                // '_', digit before and after are word characters; '(' and '-' are not
    [InlineData("Smith", 3, 3)]              // apostrophes are not word characters
    [InlineData("Omega", 2, 5)]              // line start and end; xOmega, Omegas, Omega2 are not whole
    public void WholeWordSearch_FindsExactlyWhatWholeWordRedactionRemoves(
        string term, int whole, int substring)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var pdf = Fixture();

        // Search: both paths, both modes, one fixture.
        using var searchDoc = PdfDocument.Open(pdf);
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        List<SearchMatch> Search(bool wholeWord) =>
            service.SearchInPage(searchDoc.GetPage(1), term, caseSensitive: true, wholeWordsOnly: wholeWord);
        static bool InAnnotation(SearchMatch m) => m.Context.EndsWith("[in annotation]", StringComparison.Ordinal);

        var wholeHits = Search(wholeWord: true);
        wholeHits.Count(m => !InAnnotation(m)).Should().Be(whole, "page search, whole word");
        wholeHits.Count(InAnnotation).Should().Be(whole, "annotation search, whole word");
        var substringHits = Search(wholeWord: false);
        substringHits.Count(m => !InAnnotation(m)).Should().Be(substring, "page search, substring");
        substringHits.Count(InAnnotation).Should().Be(substring, "annotation search, substring");

        // Redaction: what it reports, then what an independent reader still finds.
        var inputPath = Path.Combine(_tempDir, "input.pdf");
        File.WriteAllBytes(inputPath, pdf);
        MutoolTextExtractor.ExtractPage(inputPath, 1).Should().NotBeNull();
        CountOccurrences(MutoolTextExtractor.ExtractPage(inputPath, 1)!, term)
            .Should().Be(substring, "input-side control: mutool reads every substring occurrence");

        using var redactDoc = PdfDocument.Open(pdf);
        var report = redactDoc.RedactText(term, RedactionOptions.Default with
        {
            WholeWord = true,
            CaseSensitive = true,
            StripDocumentMetadata = false,
        });
        report.MatchesLocated.Should().Be(whole, "redaction locates what the search showed");

        var outputPath = Path.Combine(_tempDir, "output.pdf");
        File.WriteAllBytes(outputPath, redactDoc.SaveToBytes());

        var pageText = MutoolTextExtractor.ExtractPage(outputPath, 1);
        pageText.Should().NotBeNull("mutool is the independent reader of the redacted page");
        CountOccurrences(pageText!, term).Should().Be(substring - whole,
            "every whole-word occurrence is gone from the page and every other one is untouched");

        var annotations = QpdfReferenceTool.ListAnnotations(outputPath);
        annotations.Should().NotBeNull("qpdf is the independent reader of the annotation");
        CountOccurrences(annotations!.Single(a => a.Subtype == "Text").Contents ?? "", term)
            .Should().Be(substring - whole,
                "the annotation loses the same whole-word occurrences the search showed");
    }

    private static int CountOccurrences(string text, string term) =>
        Regex.Matches(text, Regex.Escape(term), RegexOptions.CultureInvariant).Count;

    private static byte[] Fixture()
    {
        var content = new StringBuilder();
        for (var i = 0; i < PageLines.Length; i++)
            content.Append($"BT /F1 12 Tf 72 {700 - 24 * i} Td ({PageLines[i]}) Tj ET\n");
        var body = content.ToString();

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [6 0 R] " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {body.Length} >>\nstream\n{body}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /Annot /Subtype /Text /Rect [500 100 520 120] /Contents ({AnnotationText}) >>",
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
