using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Core.Writing;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1550 — Reduce File Size judged by tools that are not excise: mutool renders
/// and extracts, qpdf checks structure. Lossless must be pixel-identical; the
/// lossy presets must stay close; a redacted file must stay redacted.
/// </summary>
public sealed class ReduceFileSizeOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-1550-oracle-{Guid.NewGuid():N}");

    public ReduceFileSizeOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static string? SmokeFile(string name) => TestRepoLayout.FindFile("test-pdfs", "smoke", name);

    private static string RequireSmokeFile(string name)
    {
        var path = SmokeFile(name);
        Assert.SkipWhen(path is null, TestRepoLayout.AbsenceReason($"smoke corpus file {name}", $"test-pdfs/smoke/{name}"));
        return path!;
    }

    private string Optimize(string sourcePath, PdfOptimizationPreset preset, string label)
    {
        byte[] saved;
        using (var document = PdfDocument.Open(sourcePath))
            saved = document.SaveToBytes();
        var output = Path.Combine(_dir, $"{label}-{preset}.pdf");
        PdfDocumentOptimizer.SaveOptimizedCopy(
            saved, output, PdfOptimizationOptions.ForPreset(preset, PdfImageCodecs.CreateJpegCodec()),
            cancellationToken: TestContext.Current.CancellationToken);
        return output;
    }

    /// <summary>
    /// Lossless changes only how bytes are stored, so mutool must draw exactly
    /// the same pixels from the original file and the reduced copy.
    /// </summary>
    [Theory]
    [InlineData("irs-w4.pdf")]
    [InlineData("irs-1040.pdf")]
    [InlineData("cdc-vis-covid-19.pdf")]
    [InlineData("scotus-trump-v-anderson.pdf")]
    [InlineData("state-ds11-passport.pdf")]
    public void Lossless_RendersPixelIdenticalToTheOriginal(string fileName)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var source = RequireSmokeFile(fileName);
        var reduced = Optimize(source, PdfOptimizationPreset.Lossless, Path.GetFileNameWithoutExtension(fileName));

        // Size is not asserted here: the writer keeps form-field and font
        // dictionaries out of object streams on purpose (#1431), which can cost
        // more than Lossless saves on a small form. Sizes are measured and
        // reported per file in the #1550 report instead.
        if (QpdfReferenceTool.IsAvailable)
        {
            var check = QpdfReferenceTool.Check(reduced);
            check.Should().NotBeNull();
            check!.Value.Success.Should().BeTrue(check.Value.Output);
        }

        var pages = Math.Min(2, QpdfReferenceTool.PageCount(source) ?? 1);
        for (var page = 1; page <= pages; page++)
        {
            using var before = MutoolReferenceRenderer.RenderPage(source, page, dpi: 72);
            using var after = MutoolReferenceRenderer.RenderPage(reduced, page, dpi: 72);
            before.Should().NotBeNull();
            after.Should().NotBeNull();
            CountDifferentPixels(before!, after!).Should().Be(0,
                $"{fileName} page {page}: a lossless rewrite must not change a pixel");
        }
    }

    /// <summary>
    /// A 1200-pixel JPEG drawn two inches wide is a 600 dpi image: Standard
    /// resamples it to 300 pixels, and mutool must draw nearly the same page.
    /// </summary>
    [Fact]
    public void Standard_DownsampledJpeg_RendersCloseToTheOriginal()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var codec = PdfImageCodecs.CreateJpegCodec();
        var source = Path.Combine(_dir, "photo.pdf");
        File.WriteAllBytes(source, JpegImagePage(codec, pixels: 1200, placedPoints: 144));

        byte[] saved;
        using (var document = PdfDocument.Open(source))
            saved = document.SaveToBytes();
        var reduced = Path.Combine(_dir, "photo-standard.pdf");
        var result = PdfDocumentOptimizer.SaveOptimizedCopy(
            saved, reduced, PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Standard, codec),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ImagesDownsampled.Should().Be(1);
        result.OutputSizeBytes.Should().BeLessThan(new FileInfo(source).Length / 2);
        using (var reopened = PdfDocument.Open(reduced))
        {
            var image = (Excise.Core.Primitives.PdfStream)reopened.GetPage(1).GetXObject("Im1")!;
            image.GetInt("Width").Should().Be(300);
            image.Filters.Should().Equal("DCTDecode");
        }

        using var before = MutoolReferenceRenderer.RenderPage(source, 1, dpi: 72);
        using var after = MutoolReferenceRenderer.RenderPage(reduced, 1, dpi: 72);
        before.Should().NotBeNull();
        after.Should().NotBeNull();
        MeanAbsoluteDifference(before!, after!).Should().BeLessThan(2.0,
            "at 72 dpi a 300-pixel copy of a smooth 2-inch image is visually the same page");
        // The image spans x 40..184 and y 456..600 in PDF space; its centre is
        // (112, 528), which is row 792 - 528 = 264 of a 72 dpi render.
        after!.GetPixel(112, 264).Should().NotBe(SKColors.White,
            "the image must still be drawn");
    }

    /// <summary>
    /// A real government form, redacted, then reduced with a lossy preset: an
    /// independent extractor must not find the term in either file, and must
    /// still find the rest of the form.
    /// </summary>
    [Theory]
    [InlineData(PdfOptimizationPreset.Lossless)]
    [InlineData(PdfOptimizationPreset.Screen)]
    public void RedactionThenReduceFileSize_KeepsTheTermGone(PdfOptimizationPreset preset)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        const string term = "Employee";
        var source = RequireSmokeFile("irs-w4.pdf");

        byte[] redacted;
        int pageCount;
        using (var document = PdfDocument.Open(source))
        {
            document.RedactText(term).VerifiedRemovals.Should().BeGreaterThan(0);
            redacted = document.SaveToBytes();
            pageCount = document.PageCount;
        }

        var redactedPath = Path.Combine(_dir, "w4-redacted.pdf");
        File.WriteAllBytes(redactedPath, redacted);
        var reduced = Path.Combine(_dir, $"w4-redacted-{preset}.pdf");
        PdfDocumentOptimizer.SaveOptimizedCopy(
            redacted, reduced, PdfOptimizationOptions.ForPreset(preset, PdfImageCodecs.CreateJpegCodec()),
            cancellationToken: TestContext.Current.CancellationToken);

        var beforePages = MutoolTextExtractor.ExtractAllPages(redactedPath, pageCount);
        var afterPages = MutoolTextExtractor.ExtractAllPages(reduced, pageCount);
        beforePages.Should().NotBeNull("mutool must read the redacted file");
        afterPages.Should().NotBeNull("mutool must read the reduced file");
        var beforeText = string.Join("\n", beforePages!);
        var afterText = string.Join("\n", afterPages!);
        beforeText.Should().NotContain(term, "precondition: the redaction is clean before reducing");
        afterText.Should().NotContain(term);
        afterText.Should().Contain("Withholding", "the rest of the form must survive");
        // The saved-bytes verdict is relative: whatever the redaction itself
        // left (a carrier outside its scope) is not this optimizer's to judge,
        // but the reduced copy must hold no more than the redacted file did.
        var hitsBefore = SavedPdfLeakScanner.FindTerm(redacted, term);
        var hitsAfter = SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(reduced), term);
        if (hitsBefore.Count == 0)
            hitsAfter.Should().BeEmpty();
        else
            hitsAfter.Count.Should().BeLessThanOrEqualTo(hitsBefore.Count, string.Join("; ", hitsAfter));
    }

    private static int CountDifferentPixels(SKBitmap a, SKBitmap b)
    {
        a.Width.Should().Be(b.Width);
        a.Height.Should().Be(b.Height);
        var different = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    different++;
        return different;
    }

    private static double MeanAbsoluteDifference(SKBitmap a, SKBitmap b)
    {
        a.Width.Should().Be(b.Width);
        a.Height.Should().Be(b.Height);
        double total = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                total += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
            }
        }

        return total / (a.Width * a.Height * 3.0);
    }

    private static byte[] JpegImagePage(IPdfJpegCodec codec, int pixels, double placedPoints)
    {
        var samples = new byte[pixels * pixels * 3];
        for (var y = 0; y < pixels; y++)
            for (var x = 0; x < pixels; x++)
            {
                var i = (y * pixels + x) * 3;
                samples[i] = (byte)(x * 255 / pixels);
                samples[i + 1] = (byte)(y * 255 / pixels);
                samples[i + 2] = (byte)(128 + 100 * Math.Sin((x + y) / 40.0));
            }

        var jpeg = codec.Encode(samples, pixels, pixels, 3, quality: 92)!;
        var content = Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"q {placedPoints} 0 0 {placedPoints} 40 {600 - placedPoints} cm /Im1 Do Q\n"));

        using var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) => file.Write(Encoding.Latin1.GetBytes(s));
        void Object(string dictionary, byte[]? stream = null)
        {
            offsets.Add(file.Position);
            Ascii($"{offsets.Count} 0 obj\n{dictionary}");
            if (stream != null)
            {
                Ascii($"\nstream\n");
                file.Write(stream);
                Ascii("\nendstream");
            }

            Ascii("\nendobj\n");
        }

        Ascii("%PDF-1.7\n");
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>");
        Object($"<< /Type /XObject /Subtype /Image /Width {pixels} /Height {pixels} /ColorSpace /DeviceRGB " +
               $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>", jpeg);
        Object($"<< /Length {content.Length} >>", content);
        var xref = file.Position;
        Ascii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Ascii($"{offset:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }
}
