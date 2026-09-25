using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

public sealed class RedactionWorkflowServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"excise-redaction-workflow-{Guid.NewGuid():N}");

    public RedactionWorkflowServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CaptureMark_ExtractsPreviewWithoutOwningPendingUiState()
    {
        var sourcePath = Path.Combine(_tempDir, "preview.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "PREVIEWSECRET1288");
        var pageArea = PdfPageRect.FromContentPoints(
            1,
            new PdfRectangle(0, 0, 612, 792));

        var result = CreateWorkflow().CaptureMark(
            new RedactionMarkRequest(sourcePath, pageArea));

        result.PageArea.Should().Be(pageArea);
        Assert.Contains("PREVIEWSECRET1288", result.PreviewText);
    }

    /// <summary>
    /// #1490: a mark on page 2 previews page 2's text. The page comes from the
    /// rectangle, so nothing the viewer is showing can redirect it.
    /// </summary>
    [Fact]
    public void CaptureMark_ReadsThePreviewFromTheRectanglesOwnPage()
    {
        var sourcePath = Path.Combine(_tempDir, "two-pages.pdf");
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 2);
        var pageTwo = PdfPageRect.FromContentPoints(2, new PdfRectangle(0, 0, 612, 792));

        var result = CreateWorkflow().CaptureMark(new RedactionMarkRequest(sourcePath, pageTwo));

        result.PageArea.Should().Be(pageTwo);
        result.PreviewText.Should().Contain("Secret on Page 2");
        result.PreviewText.Should().NotContain("Page 1");
    }

    /// <summary>
    /// #1834: the preview is the carrier-scrub term list, so it must be the
    /// text the viewer's selection engine reads for the same area, under the
    /// overlap rule the engine removes by. The area clips under half of the
    /// rightmost glyph; the Arabic line is painted in visual order; the Latin
    /// line is letter-spaced (4 pt Tc at 24 pt). Each term also sits in an
    /// outline title and an off-box comment, which only the term scrub reaches.
    /// </summary>
    [Theory]
    [InlineData("سلام", 700)]
    [InlineData("KESTREL", 600)]
    public void CaptureMark_PreviewIsTheViewerSelection_AndItsTermLeavesEveryCarrier(string term, int baseline)
    {
        var sourcePath = Path.Combine(_tempDir, "carriers.pdf");
        File.WriteAllBytes(sourcePath, RtlAndLetterSpacedFixture());

        PdfRectangle area;
        string viewerText;
        using (var source = PdfDocument.Open(sourcePath))
        {
            var letters = source.GetPage(1).Letters;
            var line = letters
                .Where(l => l.GlyphRectangle.Bottom < baseline + 12 && l.GlyphRectangle.Top > baseline)
                .OrderBy(l => l.GlyphRectangle.Left)
                .ToList();
            line.Should().HaveCount(term.Length, "sanity: the line holds the term and nothing else");
            var last = line[^1].GlyphRectangle;
            area = new PdfRectangle(
                line[0].GlyphRectangle.Left - 1,
                line.Min(l => l.GlyphRectangle.Bottom) - 1,
                last.Left + 0.3 * (last.Right - last.Left),
                line.Max(l => l.GlyphRectangle.Top) + 1);

            var reading = TextSelectionEngine.SortReadingOrder(letters);
            var midY = (area.Bottom + area.Top) / 2;
            viewerText = TextSelectionEngine.BuildSelection(
                reading,
                letters,
                TextSelectionEngine.HitTest(letters, area.Left, midY)!,
                TextSelectionEngine.HitTest(letters, area.Right, midY)!,
                TextSelectionEngine.EstimateColumnGap(reading)).Text;

        }
        viewerText.Should().Be(term, "sanity: a drag across the area selects the whole term");
        var before = RemoteCarriers(sourcePath);
        before.Comment.Should().Contain(term, "input-side control: qpdf reads the term in the comment");
        before.Titles.Should().Contain(term, "input-side control: the outline title carries the term");

        var pageArea = PdfPageRect.FromContentPoints(1, area);
        var mark = CreateWorkflow().CaptureMark(new RedactionMarkRequest(sourcePath, pageArea));

        mark.PreviewText.Should().Be(viewerText,
            "the preview must read the area the way the viewer's selection engine reads it");

        var outputPath = Path.Combine(_tempDir, "carriers-redacted.pdf");
        using (var document = PdfDocument.Open(sourcePath))
        {
            CreateWorkflow().CreateRedactedCopy(new RedactedCopyRequest(
                new RedactionApplicationRequest(
                    document,
                    new[] { new RedactionAreaTransaction(1, pageArea, mark.PreviewText) },
                    Array.Empty<PdfTypewriterTextOperation>(),
                    RedactionOptions.Default),
                outputPath,
                EncryptionOptions: null));
        }

        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), term).Should().BeEmpty(
            "no raw or inflated carrier keeps the term");
        var pageText = MutoolTextExtractor.ExtractPage(outputPath, 1);
        pageText.Should().NotBeNull("mutool is the independent reader of the redacted page");
        pageText!.Should().NotContainAny(term.Select(c => c.ToString()),
            "no glyph of the term survives on the page; letters, not the word, because mutool's RTL order differs by platform");
        var after = RemoteCarriers(outputPath);
        after.Comment.Should().NotContain(term, "the comment is scrubbed with the preview text")
            .And.Contain("and", "the scrub is surgical, so the term's absence is the preview's doing");
        after.Titles.Should().NotContain(term, "the outline title is scrubbed with the preview text")
            .And.Contain("chapter").And.Contain("briefing");
    }

    /// <summary>
    /// The comment as qpdf decodes it, and the outline titles: each carrier
    /// read on its own, so the surgical-scrub assertions can name it.
    /// </summary>
    private static (string Comment, string Titles) RemoteCarriers(string path)
    {
        var annotations = QpdfReferenceTool.ListAnnotations(path);
        annotations.Should().NotBeNull("qpdf is the independent reader of the comment");
        using var document = PdfDocument.Open(path);
        return (
            annotations!.Single(a => a.Subtype == "Text").Contents ?? string.Empty,
            string.Join(" | ", PdfOutlineParser.Parse(document).Select(o => o.Title)));
    }

    /// <summary>
    /// One page: سلام through a /ToUnicode font, painted left to right in
    /// visual order (codes DCBA), and KESTREL with 4 pt letter spacing. The
    /// outline and a comment away from both lines carry both terms.
    /// </summary>
    private static byte[] RtlAndLetterSpacedFixture()
    {
        const string content =
            "BT /F1 24 Tf 100 700 Td (DCBA) Tj ET\n" +
            "BT /F2 24 Tf 4 Tc 100 600 Td (KESTREL) Tj ET";
        const string cmap =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "4 beginbfchar\n<41> <0633>\n<42> <0644>\n<43> <0627>\n<44> <0645>\nendbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";
        static string Utf16(string s) =>
            "<FEFF" + Convert.ToHexString(System.Text.Encoding.BigEndianUnicode.GetBytes(s)) + ">";

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [10 0 R] " +
                "/Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 11 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Outlines /First 8 0 R /Last 9 0 R /Count 2 >>",
            $"<< /Title {Utf16("سلام chapter")} /Parent 7 0 R /Next 9 0 R >>",
            "<< /Title (KESTREL briefing) /Parent 7 0 R /Prev 8 0 R >>",
            $"<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] /Contents {Utf16("KESTREL and سلام")} >>",
            $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream",
        };

        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
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
        return System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void RequestCapture_CopiesMutablePendingState()
    {
        var pending = new PendingRedaction
        {
            PageNumber = 1,
            PageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(10, 20, 30, 40)),
            PreviewText = "original"
        };

        using var document = PdfDocument.CreateNew();
        document.Pages.AddBlank();
        var request = RedactionApplicationRequest.Capture(
            document,
            new[] { pending },
            Array.Empty<PdfTypewriterTextOperation>(),
            RedactionOptions.Default);
        pending.PageNumber = 2;
        pending.PreviewText = "changed";

        var transaction = request.Redactions.Single();
        transaction.PageNumber.Should().Be(1);
        Assert.Equal("original", transaction.PreviewText);
    }

    [Fact]
    public void ApplyToDocument_ReportsInvalidPagesInsteadOfIndexingOutsideDocument()
    {
        var sourcePath = Path.Combine(_tempDir, "invalid-page.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "SURVIVES");
        using var document = PdfDocument.Open(sourcePath);
        var invalid = new RedactionAreaTransaction(
            99,
            PdfPageRect.FromContentPoints(99, new PdfRectangle(0, 0, 100, 100)),
            "missing page");

        var result = CreateWorkflow().ApplyToDocument(
            new RedactionApplicationRequest(
                document,
                new[] { invalid },
                Array.Empty<PdfTypewriterTextOperation>(),
                RedactionOptions.Default));

        result.AppliedRedactionCount.Should().Be(0);
        result.SkippedRedactionCount.Should().Be(1);
        result.SafetyReport.Warnings.Should().ContainSingle(
            warning => warning.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #1834: the user's width policy reaches the area pass, and the engine
    /// draws the box, not the App: under CloseGap it draws none.
    /// </summary>
    [Theory]
    [InlineData(WidthPolicy.CollapsePreserveLayout, 1)]
    [InlineData(WidthPolicy.CloseGap, 0)]
    public void ApplyToDocument_RunsTheAreaPassUnderTheWidthPolicy(WidthPolicy width, int boxes)
    {
        var sourcePath = Path.Combine(_tempDir, "width.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "WIDTHSECRET1834");
        using var document = PdfDocument.Open(sourcePath);
        int Rectangles() => document.GetPage(1).GetContentStream().Operators.Count(op => op.Name == "re");
        var before = Rectangles();
        var area = PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, 612, 792));

        CreateWorkflow().ApplyToDocument(new RedactionApplicationRequest(
            document,
            new[] { new RedactionAreaTransaction(1, area, "WIDTHSECRET1834") },
            Array.Empty<PdfTypewriterTextOperation>(),
            RedactionOptions.Default with { Width = width }));

        Rectangles().Should().Be(before + boxes);
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), "WIDTHSECRET1834").Should().BeEmpty();
    }

    [Fact]
    public void CreateRedactedCopy_AppliesAreasAndPendingTypewriterBeforeSafeSave()
    {
        var sourcePath = Path.Combine(_tempDir, "source.pdf");
        var outputPath = Path.Combine(_tempDir, "redacted.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "REMOVESECRET1288");
        using var document = PdfDocument.Open(sourcePath);
        var redaction = new RedactionAreaTransaction(
            1,
            PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, 612, 792)),
            "REMOVESECRET1288");
        var typewriter = PdfTypewriterTextOperation.Create(
            1,
            new PdfRectangle(72, 620, 300, 660),
            "TYPEWRITER1288");

        var result = CreateWorkflow().CreateRedactedCopy(
            new RedactedCopyRequest(
                new RedactionApplicationRequest(
                    document,
                    new[] { redaction },
                    new[] { typewriter },
                    RedactionOptions.Default),
                outputPath,
                EncryptionOptions: null));

        result.Application.AppliedRedactionCount.Should().Be(1);
        result.Application.SkippedRedactionCount.Should().Be(0);
        result.Application.AppliedTypewriterOperationCount.Should().Be(1);
        File.Exists(outputPath).Should().BeTrue();
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(sourcePath), "REMOVESECRET1288")
            .Should().NotBeEmpty("the independent scanner's negative control must detect the source secret");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "REMOVESECRET1288")
            .Should().BeEmpty("no saved carrier may retain the redacted secret");
        using var reopened = PdfDocument.Open(outputPath);
        reopened.GetPage(1).Text.Should().NotContain("REMOVESECRET1288");
        reopened.GetPage(1).Text.Should().Contain("TYPEWRITER1288");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static RedactionWorkflowService CreateWorkflow()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        return new RedactionWorkflowService(
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            NullLogger<RedactionWorkflowService>.Instance);
    }
}
