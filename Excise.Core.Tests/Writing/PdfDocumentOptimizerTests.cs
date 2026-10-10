using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Security;
using Excise.Core.Writing;
using Excise.TestSupport;
using Xunit;
using static Excise.Core.Tests.Writing.OptimizerFixtures;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// #1550 — Reduce File Size. What each pass changes, and what it must leave
/// alone. Renders are compared against independent renderers in
/// <c>Excise.Rendering.Tests/Differential/ReduceFileSizeOracleTests</c>; the
/// redaction safety tests are <see cref="ReduceFileSizeRedactionTests"/>.
/// </summary>
public sealed class PdfDocumentOptimizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-1550-{Guid.NewGuid():N}");

    public PdfDocumentOptimizerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static PdfOptimizationOptions Lossless => PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Lossless);

    private static (PdfOptimizationResult Result, byte[] Saved) OptimizeBytes(byte[] source, PdfOptimizationOptions options)
    {
        byte[] saved;
        using (var original = PdfDocument.Open(source))
            saved = original.SaveToBytes();
        using var copy = PdfDocument.Open(saved);
        var result = PdfDocumentOptimizer.Optimize(copy, options, TestContext.Current.CancellationToken);
        return (result, copy.SaveToBytes());
    }

    private static PdfStream ImageNamed(PdfDocument doc, string name)
        => doc.GetPage(1).GetXObject(name).Should().BeOfType<PdfStream>().Subject;

    private static PdfReference ImageReference(PdfDocument doc, string name)
        => doc.GetPage(1).Resources!.ResolveDictionary(doc, "XObject")!
            .GetOptional(name).Should().BeOfType<PdfReference>().Subject;

    // ── Lossless ──────────────────────────────────────────────────────────

    [Fact]
    public void Lossless_CompressesAnUncompressedContentStream_AndKeepsItsBytes()
    {
        var source = UncompressedTextPageWithThumbnail();
        using var plain = PdfDocument.Open(source);
        var plainSaved = plain.SaveToBytes();
        var originalContent = plain.GetPage(1).GetContentStreamBytes();

        var (result, optimized) = OptimizeBytes(source, Lossless);

        result.StreamsRecompressed.Should().BeGreaterThan(0);
        optimized.Length.Should().BeLessThan(plainSaved.Length,
            "an uncompressed 60-line content stream is the easiest win there is");
        using var reopened = PdfDocument.Open(optimized);
        reopened.GetPage(1).GetContentStreamBytes().Should().Equal(originalContent,
            "Lossless must not change a single decoded byte of page content");
        var contents = reopened.Resolve(reopened.GetPage(1).Dictionary.GetOptional("Contents")!)
            .Should().BeOfType<PdfStream>().Subject;
        contents.Filters.Should().Equal("FlateDecode");
        Inflate(contents.EncodedData).Should().Equal(originalContent,
            "an independent inflater must read back the same bytes");
    }

    [Fact]
    public void Lossless_RemovesThumbnailsAndPrivateApplicationData()
    {
        var (result, optimized) = OptimizeBytes(UncompressedTextPageWithThumbnail(), Lossless);

        result.ThumbnailsRemoved.Should().Be(1);
        result.PrivateDataEntriesRemoved.Should().Be(2, "one on the catalog, one on the page");
        using var reopened = PdfDocument.Open(optimized);
        reopened.GetPage(1).Dictionary.ContainsKey("Thumb").Should().BeFalse();
        reopened.GetPage(1).Dictionary.ContainsKey("PieceInfo").Should().BeFalse();
        reopened.Catalog.ContainsKey("PieceInfo").Should().BeFalse();
        System.Text.Encoding.Latin1.GetString(optimized).Should().NotContain("editing data",
            "the private-data dictionary is unreachable once removed, so the writer drops it");
    }

    [Fact]
    public void Lossless_LeavesImagesAtTheirOriginalResolution()
    {
        var (result, optimized) = OptimizeBytes(ImagePage(pixels: 600), Lossless);

        result.ImagesDownsampled.Should().Be(0);
        using var reopened = PdfDocument.Open(optimized);
        ImageNamed(reopened, "Im1").GetInt("Width").Should().Be(600);
    }

    [Fact]
    public void Recompress_LeavesAFlateStreamAloneWhenItWouldNotShrink()
    {
        var source = UncompressedTextPageWithThumbnail(compressContent: true);
        using var original = PdfDocument.Open(source);
        var originalEncoded = ((PdfStream)original.Resolve(original.GetPage(1).Dictionary.GetOptional("Contents")!)).EncodedData;

        var (_, optimized) = OptimizeBytes(source, Lossless with { RemoveThumbnails = false });

        using var reopened = PdfDocument.Open(optimized);
        var contents = (PdfStream)reopened.Resolve(reopened.GetPage(1).Dictionary.GetOptional("Contents")!);
        contents.EncodedData.Length.Should().BeLessThanOrEqualTo(originalEncoded.Length,
            "a re-encode is kept only when it is smaller");
    }

    // ── Deduplication ─────────────────────────────────────────────────────

    [Fact]
    public void Deduplicate_PointsTwoIdenticalImagesAtOneObject()
    {
        var (result, optimized) = OptimizeBytes(TwoIdenticalImages(), Lossless);

        result.StreamsDeduplicated.Should().Be(1);
        using var reopened = PdfDocument.Open(optimized);
        ImageReference(reopened, "ImA").ObjectNum.Should().Be(ImageReference(reopened, "ImB").ObjectNum);
    }

    /// <summary>
    /// The planted-defect check for deduplication. Same samples, different
    /// <c>/Decode</c> — the second image is the photographic negative of the
    /// first. A key built from the stream bytes alone would merge them and
    /// silently turn one picture into the other.
    /// </summary>
    [Fact]
    public void Deduplicate_DoesNotMergeImagesThatDifferOnlyInTheirDictionary()
    {
        var (result, optimized) = OptimizeBytes(TwoIdenticalImages(secondDecode: "[1 0 1 0 1 0]"), Lossless);

        result.StreamsDeduplicated.Should().Be(0);
        using var reopened = PdfDocument.Open(optimized);
        ImageReference(reopened, "ImA").ObjectNum.Should().NotBe(ImageReference(reopened, "ImB").ObjectNum);
        ImageNamed(reopened, "ImB").ContainsKey("Decode").Should().BeTrue();
        ImageNamed(reopened, "ImA").ContainsKey("Decode").Should().BeFalse();
    }

    // ── Image downsampling ────────────────────────────────────────────────

    [Fact]
    public void Standard_DownsamplesAn600DpiImageTo150Dpi()
    {
        // 600 px drawn at 72 pt = one inch = 600 dpi; Standard's target is 150.
        var (result, optimized) = OptimizeBytes(
            ImagePage(pixels: 600),
            PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Standard));

        result.ImagesDownsampled.Should().Be(1);
        result.ImagesSkipped.Should().BeEmpty();
        using var reopened = PdfDocument.Open(optimized);
        var image = ImageNamed(reopened, "Im1");
        image.GetInt("Width").Should().Be(150);
        image.GetInt("Height").Should().Be(150);
        image.Filters.Should().Equal("FlateDecode");
        image.ContainsKey("DecodeParms").Should().BeFalse();
        Inflate(image.EncodedData).Length.Should().Be(150 * 150 * 3,
            "the stored samples must match the new /Width and /Height");
    }

    [Fact]
    public void Standard_DownsamplesATypical200DpiScan()
    {
        // 600 px across 216 pt (3 in) = 200 dpi — the resolution most office
        // scanners default to, and the case Standard exists for.
        var (result, optimized) = OptimizeBytes(
            ImagePage(pixels: 600, placements: new[] { 216.0 }),
            PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Standard));

        result.ImagesDownsampled.Should().Be(1);
        using var reopened = PdfDocument.Open(optimized);
        ImageNamed(reopened, "Im1").GetInt("Width").Should().Be(450);
    }

    [Fact]
    public void Standard_UsesTheLargestPlacementOfASharedImage()
    {
        // Drawn at 1 inch (600 dpi) AND at 4 inches (150 dpi): the 4-inch copy
        // must keep its detail, so the image is below the 188 dpi threshold.
        var (result, optimized) = OptimizeBytes(
            ImagePage(pixels: 600, placements: new[] { 72.0, 288.0 }),
            PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Standard));

        result.ImagesDownsampled.Should().Be(0);
        using var reopened = PdfDocument.Open(optimized);
        ImageNamed(reopened, "Im1").GetInt("Width").Should().Be(600);
    }

    [Fact]
    public void Standard_LeavesAnImageDrawnInsideAFormUntouched()
    {
        var (result, optimized) = OptimizeBytes(
            ImagePage(pixels: 600, viaForm: true),
            PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Standard));

        // The form's /Matrix and the CTM at its Do are not composed by this
        // pass, so the image's displayed size is unknown and it is not touched.
        result.ImagesDownsampled.Should().Be(0);
        result.ImagesSkipped.Should().ContainKey("drawn inside a form XObject")
            .WhoseValue.Should().Be(1, "an untouched image must be reported, not silently left whole");
        System.Text.Encoding.Latin1.GetString(optimized).Should().Contain("/Width 600");
    }

    [Fact]
    public void Standard_SkipsAMaskedImage_AndReportsTheReason()
    {
        var pdf = ImagePage(pixels: 600, extraImageEntries: "/Mask [0 10 0 10 0 10] ");

        var (result, optimized) = OptimizeBytes(pdf, PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Standard));

        result.ImagesDownsampled.Should().Be(0);
        result.ImagesSkipped.Should().ContainKey("has a transparency mask")
            .WhoseValue.Should().Be(1);
        using var reopened = PdfDocument.Open(optimized);
        ImageNamed(reopened, "Im1").GetInt("Width").Should().Be(600);
    }

    [Fact]
    public void Screen_UsesTheCodecForALosslessImage_OnlyWhenJpegIsMuchSmaller()
    {
        var codec = new RecordingJpegCodec(encodedLength: 10);
        var (result, optimized) = OptimizeBytes(
            ImagePage(pixels: 600),
            PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Screen, codec));

        result.ImagesDownsampled.Should().Be(1);
        codec.Encodes.Should().Be(1);
        using var reopened = PdfDocument.Open(optimized);
        var image = ImageNamed(reopened, "Im1");
        image.Filters.Should().Equal("DCTDecode");
        image.GetInt("Width").Should().Be(96);
    }

    // ── Save, encryption, signatures ──────────────────────────────────────

    [Fact]
    public void SaveOptimizedCopy_WritesOnlyTheOutputFile_AndReportsItsSize()
    {
        var output = Path.Combine(_dir, "out.pdf");
        using var source = PdfDocument.Open(UncompressedTextPageWithThumbnail());

        var result = PdfDocumentOptimizer.SaveOptimizedCopy(
            source.SaveToBytes(), output, Lossless, cancellationToken: TestContext.Current.CancellationToken);

        result.OutputSizeBytes.Should().Be(new FileInfo(output).Length);
        Directory.GetFiles(_dir).Should().Equal(new[] { output }, "the temporary file must be moved into place");
    }

    [Fact]
    public void SaveOptimizedCopy_KeepsAnEncryptedSourceEncrypted()
    {
        const string password = "s3cret";
        var encryptedPath = Path.Combine(_dir, "encrypted.pdf");
        using (var plain = PdfDocument.Open(UncompressedTextPageWithThumbnail()))
        {
            plain.Save(encryptedPath, new PdfEncryptionOptions
            {
                UserPassword = password,
                OwnerPassword = password,
                Algorithm = PdfEncryptionAlgorithm.Aes128,
            });
        }

        var output = Path.Combine(_dir, "encrypted-small.pdf");
        using (var source = PdfDocument.Open(encryptedPath, new PdfOpenOptions { UserPassword = password }))
        {
            PdfDocumentOptimizer.SaveOptimizedCopy(
                source.SaveToBytes(), output, Lossless, source.GetReEncryptionOptions(password),
                TestContext.Current.CancellationToken);
        }

        var qpdf = FindOnPath("qpdf");
        Assert.SkipWhen(qpdf is null, "qpdf not on PATH — the encryption verdict must come from an independent reader");
        var (exit, text) = Run(qpdf!, "--show-encryption", $"--password={password}", output);
        exit.Should().Be(0, text);
        text.Should().Contain("AESv2", "the source's AES-128 must be kept, not upgraded or dropped");
        var (checkExit, checkText) = Run(qpdf!, "--check", $"--password={password}", output);
        checkExit.Should().Be(0, checkText);
    }

    /// <summary>
    /// #2053 — the writer used to refuse object streams for any encrypted
    /// output, so Reduce File Size grew a field-heavy encrypted form
    /// (IRS W-4, AES-256: 188 KB to 657 KB). Object streams are legal in an
    /// encrypted file (§7.5.7; the container stream is encrypted, the
    /// objects inside are not encrypted separately, and the xref stream is
    /// never encrypted). The encrypted copy must stay close to the plain one
    /// AND still be encrypted, open with the same password, and read the
    /// same text in two independent tools.
    /// </summary>
    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes128, "AESv2")]
    [InlineData(PdfEncryptionAlgorithm.Aes256, "AESv3")]
    public void SaveOptimizedCopy_EncryptedOutputStaysCloseToThePlainOptimizedSize(
        PdfEncryptionAlgorithm algorithm, string qpdfName)
    {
        const string password = "s3cret";
        var plainSource = FormHeavyPage();
        var plainOut = Path.Combine(_dir, "plain-small.pdf");
        using (var plain = PdfDocument.Open(plainSource))
        {
            PdfDocumentOptimizer.SaveOptimizedCopy(
                plain.SaveToBytes(), plainOut, Lossless, cancellationToken: TestContext.Current.CancellationToken);
        }

        var encryptedPath = Path.Combine(_dir, "encrypted.pdf");
        using (var plain = PdfDocument.Open(plainSource))
        {
            plain.Save(encryptedPath, new PdfEncryptionOptions
            {
                UserPassword = password,
                OwnerPassword = "owner-" + password,
                Algorithm = algorithm,
            });
        }

        var encryptedOut = Path.Combine(_dir, "encrypted-small.pdf");
        using (var source = PdfDocument.Open(encryptedPath, new PdfOpenOptions { UserPassword = password }))
        {
            PdfDocumentOptimizer.SaveOptimizedCopy(
                source.SaveToBytes(), encryptedOut, Lossless, source.GetReEncryptionOptions(password),
                TestContext.Current.CancellationToken);
        }

        var plainSize = new FileInfo(plainOut).Length;
        var encryptedSize = new FileInfo(encryptedOut).Length;
        encryptedSize.Should().BeLessThan((long)(plainSize * 1.10),
            $"encrypted {encryptedSize} B vs plain {plainSize} B (the pre-fix writer gave ~3x)");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(encryptedOut), "Louise Anne")
            .Should().BeEmpty("the encrypted copy must not hold readable content");

        using (var reopened = PdfDocument.Open(encryptedOut, new PdfOpenOptions { UserPassword = password }))
            reopened.IsEncrypted.Should().BeTrue();

        var qpdf = FindOnPath("qpdf");
        var mutool = FindOnPath("mutool");
        Assert.SkipWhen(qpdf is null || mutool is null, "qpdf and mutool must be on PATH: the verdict comes from independent readers");
        var (exit, text) = Run(qpdf!, "--show-encryption", $"--password={password}", encryptedOut);
        exit.Should().Be(0, text);
        text.Should().Contain(qpdfName, "the source's security handler must be kept");
        var (checkExit, checkText) = Run(qpdf!, "--check", $"--password={password}", encryptedOut);
        checkExit.Should().Be(0, checkText);
        var (noPwExit, _) = Run(mutool!, "draw", "-F", "txt", "-o", "-", encryptedOut);
        noPwExit.Should().NotBe(0, "the output must still demand the password");

        var (plainTextExit, plainText) = Run(mutool!, "draw", "-F", "txt", "-o", "-", plainOut);
        var (encTextExit, encText) = Run(mutool!, "draw", "-p", password, "-F", "txt", "-o", "-", encryptedOut);
        plainTextExit.Should().Be(0, plainText);
        encTextExit.Should().Be(0, encText);
        // mutool prints the file path on its "page" header lines; drop them.
        static string BodyOnly(string t) => string.Join('\n', t.Split('\n').Where(l => !l.StartsWith("page ", StringComparison.Ordinal)));
        encText.Should().Contain("Louise Anne");
        BodyOnly(encText).Should().Be(BodyOnly(plainText));
    }

    /// <summary>A page with a compressed text stream and 400 widget fields: many small objects.</summary>
    private static byte[] FormHeavyPage()
    {
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var page = pdf.Reserve();
        var contents = pdf.Add("<< /Filter /FlateDecode >>", Deflate(TextContent()));
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var fields = Enumerable.Range(0, 400)
            .Select(i => pdf.Add(
                $"<< /FT /Tx /T (topmostSubform[0].Page1[0].f{i}[0]) /V (value {i}) /Subtype /Widget " +
                $"/Rect [{10 + i % 20 * 20} {10 + i / 20 * 12} {28 + i % 20 * 20} {20 + i / 20 * 12}] /F 4 /P {page} 0 R >>"))
            .ToArray();
        var refs = string.Join(' ', fields.Select(f => $"{f} 0 R"));
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /AcroForm << /Fields [{refs}] >> >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
            $"/Resources << /Font << /F1 {font} 0 R >> >> /Contents {contents} 0 R /Annots [{refs}] >>");
        return pdf.Build(catalog);
    }

    [Fact]
    public void Optimize_WarnsThatASignedDocumentsSignaturesWillNotValidate()
    {
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var page = pdf.Reserve();
        var sig = pdf.Add("<< /Type /Sig /Filter /Adobe.PPKLite /ByteRange [0 10 20 30] /Contents <00> >>");
        var field = pdf.Add($"<< /FT /Sig /T (Signature1) /V {sig} 0 R /Subtype /Widget /Rect [0 0 0 0] /P {page} 0 R >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /AcroForm << /Fields [{field} 0 R] /SigFlags 3 >> >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Annots [{field} 0 R] >>");

        var (result, _) = OptimizeBytes(pdf.Build(catalog), Lossless);

        result.Warnings.Should().ContainSingle().Which.Should().Contain("signatures will no longer validate");
    }

    /// <summary>
    /// An ordinary save keeps form-field dictionaries out of object streams so
    /// a raw byte scan can read them (#1431); the optimized copy packs them,
    /// because that is where a form-heavy file's bytes go. The field is still
    /// in the file — the inflating scanner must find it — and a signature
    /// dictionary is never packed.
    /// </summary>
    [Fact]
    public void SaveOptimizedCopy_PacksFieldDictionariesAnOrdinarySaveKeepsGreppable()
    {
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var page = pdf.Reserve();
        var field = pdf.Add($"<< /FT /Tx /T (ApplicantSurnameField) /V (x) /Subtype /Widget /Rect [10 10 100 30] /P {page} 0 R >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /AcroForm << /Fields [{field} 0 R] >> >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Annots [{field} 0 R] >>");

        byte[] ordinary;
        using (var doc = PdfDocument.Open(pdf.Build(catalog)))
            ordinary = doc.SaveToBytes();
        System.Text.Encoding.Latin1.GetString(ordinary).Should().Contain("ApplicantSurnameField",
            "precondition: an ordinary save keeps the field greppable (#1431)");

        var output = Path.Combine(_dir, "packed.pdf");
        PdfDocumentOptimizer.SaveOptimizedCopy(
            ordinary, output, Lossless, cancellationToken: TestContext.Current.CancellationToken);
        var packed = File.ReadAllBytes(output);

        System.Text.Encoding.Latin1.GetString(packed).Should().NotContain("ApplicantSurnameField",
            "the optimized copy packs the field dictionary into a compressed object stream");
        Excise.TestSupport.SavedPdfLeakScanner.FindTerm(packed, "ApplicantSurnameField").Should().NotBeEmpty(
            "the field is still there, and a leak scan that inflates must see it");
    }

    [Theory]
    [InlineData(PdfOptimizationPreset.Lossless)]
    [InlineData(PdfOptimizationPreset.Standard)]
    public void OptimizedFiles_PassQpdfCheck(PdfOptimizationPreset preset)
    {
        var qpdf = FindOnPath("qpdf");
        Assert.SkipWhen(qpdf is null, "qpdf not on PATH");

        foreach (var (name, bytes) in new[]
                 {
                     ("text", UncompressedTextPageWithThumbnail()),
                     ("image", ImagePage()),
                     ("dedupe", TwoIdenticalImages()),
                 })
        {
            // Through SaveOptimizedCopy, so the size-optimized writer mode
            // (packed carriers, 200-object streams, predicted xref) is what
            // qpdf reads.
            byte[] saved;
            using (var doc = PdfDocument.Open(bytes))
                saved = doc.SaveToBytes();
            var path = Path.Combine(_dir, $"{name}-{preset}.pdf");
            PdfDocumentOptimizer.SaveOptimizedCopy(
                saved, path, PdfOptimizationOptions.ForPreset(preset),
                cancellationToken: TestContext.Current.CancellationToken);
            using (var reopened = PdfDocument.Open(path))
                reopened.PageCount.Should().Be(1, $"{name}: excise must read its own predicted xref stream");
            var (exit, output) = Run(qpdf!, "--check", path);
            exit.Should().Be(0, $"{name}: {output}");
            output.Should().NotContain("WARNING", name);
        }
    }

    [Fact]
    public void TryParsePreset_AcceptsTheCliSpellings()
    {
        PdfOptimizationOptions.TryParsePreset("Screen", out var screen).Should().BeTrue();
        screen.Should().Be(PdfOptimizationPreset.Screen);
        PdfOptimizationOptions.TryParsePreset("lossless", out _).Should().BeTrue();
        PdfOptimizationOptions.TryParsePreset("tiny", out _).Should().BeFalse();
        PdfOptimizationOptions.TryParsePreset("7", out _).Should().BeFalse("a number is not a preset name");
        PdfOptimizationOptions.TryParsePreset(null, out _).Should().BeFalse();
    }

    // ── Resampler ─────────────────────────────────────────────────────────

    [Fact]
    public void AreaAverage_AveragesExactlyOnAnIntegerFactor()
    {
        var source = new byte[] { 0, 100, 200, 40, 10, 30, 50, 70 };
        var result = ImageResampler.AreaAverage(source, 4, 2, 1, 2, 1);
        // Left 2×2 block: (0+100+10+30)/4 = 35; right: (200+40+50+70)/4 = 90.
        result.Should().Equal(35, 90);
    }

    [Fact]
    public void AreaAverage_KeepsAFullyCoveredZeroRegionZero()
    {
        // A redacted (zeroed) 40×40 block inside a bright 100×100 image must
        // stay pure zero wherever it fully covers a destination pixel.
        var source = Enumerable.Repeat((byte)230, 100 * 100).ToArray();
        for (var y = 20; y < 60; y++)
            for (var x = 20; x < 60; x++)
                source[y * 100 + x] = 0;

        var result = ImageResampler.AreaAverage(source, 100, 100, 1, 30, 30);

        // Destination pixel (i) covers source [i*10/3, (i+1)*10/3): pixels 6..17
        // lie wholly inside 20..60.
        for (var y = 6; y <= 17; y++)
            for (var x = 6; x <= 17; x++)
                result[y * 30 + x].Should().Be(0, $"destination ({x},{y}) lies wholly inside the zeroed block");
        result[0].Should().Be(230);
    }

    [Fact]
    public void AreaAverage_HandlesNonIntegerFactorsWithoutLeavingTheInputRange()
    {
        var source = FromSeed();
        var result = ImageResampler.AreaAverage(source, 97, 61, 3, 29, 17);
        result.Length.Should().Be(29 * 17 * 3);

        static byte[] FromSeed() => NoisySamples(97, 61, 3, seed: 7);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private sealed class RecordingJpegCodec(int encodedLength) : IPdfJpegCodec
    {
        public int Encodes { get; private set; }

        public byte[]? Decode(byte[] jpeg, int width, int height, int components, int? colorTransform) => null;

        public byte[]? Encode(byte[] samples, int width, int height, int components, int quality)
        {
            Encodes++;
            var bytes = new byte[encodedLength];
            bytes[0] = 0xFF;
            bytes[1] = 0xD8;
            return bytes;
        }
    }

    internal static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    internal static (int ExitCode, string Output) Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{executable} did not finish within 60 s");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
