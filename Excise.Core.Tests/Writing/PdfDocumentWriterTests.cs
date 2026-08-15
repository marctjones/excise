using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Excise.Core.Security;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// TDD tests for PDF document writing - tests define expected behavior before implementation.
/// </summary>
public class PdfDocumentWriterTests
{
    #region Save API Tests

    [Fact]
    public void Save_ToStream_ProducesValidPdf()
    {
        // Arrange - Open a PDF
        var originalData = CreateSimplePdf("Hello World");
        using var doc = PdfDocument.Open(originalData);

        // Act - Save to a new stream
        using var outputStream = new MemoryStream();
        doc.Save(outputStream);
        var savedData = outputStream.ToArray();

        // Assert - Should produce valid PDF structure
        savedData.Should().NotBeEmpty();
        var header = System.Text.Encoding.ASCII.GetString(savedData, 0, 8);
        header.Should().StartWith("%PDF-");
    }

    [Fact]
    public void Save_ToBytes_ProducesValidPdf()
    {
        // Arrange
        var originalData = CreateSimplePdf("Test");
        using var doc = PdfDocument.Open(originalData);

        // Act
        var savedData = doc.SaveToBytes();

        // Assert
        savedData.Should().NotBeEmpty();
        savedData.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
    }

    [Fact]
    public void Save_PreservesPageCount()
    {
        // Arrange
        var originalData = CreateSimplePdf("Content");
        using var doc = PdfDocument.Open(originalData);
        var originalPageCount = doc.PageCount;

        // Act
        var savedData = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(savedData);

        // Assert
        reopened.PageCount.Should().Be(originalPageCount);
    }

    [Fact]
    public void Save_PreservesTextContent()
    {
        // Arrange
        var originalData = CreateSimplePdf("Preserved Text");
        using var doc = PdfDocument.Open(originalData);
        var originalText = doc.GetPage(1).Text;

        // Act
        var savedData = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(savedData);

        // Assert
        reopened.GetPage(1).Text.Should().Contain("Preserved");
    }

    #endregion

    #region Object Serialization Tests

    [Fact]
    public void Serialize_PdfNull_WritesCorrectly()
    {
        // Act
        var result = SerializeObject(PdfNull.Instance);

        // Assert
        result.Should().Be("null");
    }

    [Fact]
    public void Serialize_PdfBoolean_WritesCorrectly()
    {
        // Act & Assert
        SerializeObject(PdfBoolean.True).Should().Be("true");
        SerializeObject(PdfBoolean.False).Should().Be("false");
    }

    [Fact]
    public void Serialize_PdfInteger_WritesCorrectly()
    {
        // Act & Assert
        SerializeObject(new PdfInteger(42)).Should().Be("42");
        SerializeObject(new PdfInteger(-100)).Should().Be("-100");
        SerializeObject(new PdfInteger(0)).Should().Be("0");
    }

    [Fact]
    public void Serialize_PdfReal_WritesCorrectly()
    {
        // Act & Assert
        SerializeObject(new PdfReal(3.14)).Should().Be("3.14");
        SerializeObject(new PdfReal(-0.5)).Should().Be("-0.5");
        SerializeObject(new PdfReal(100.0)).Should().Be("100");
    }

    [Fact]
    public void Serialize_PdfName_WritesCorrectly()
    {
        // Act & Assert
        SerializeObject(new PdfName("Type")).Should().Be("/Type");
        SerializeObject(new PdfName("Page")).Should().Be("/Page");
    }

    [Fact]
    public void Serialize_PdfName_EscapesSpecialChars()
    {
        // Names with special characters need #XX encoding
        var name = new PdfName("Name With Space");
        var result = SerializeObject(name);
        result.Should().Contain("#20"); // Space is #20
    }

    [Fact]
    public void Serialize_PdfString_WritesLiteralCorrectly()
    {
        // Act
        var result = SerializeObject(PdfString.FromText("Hello"));

        // Assert - Should be literal string with parentheses
        result.Should().Be("(Hello)");
    }

    [Fact]
    public void Serialize_PdfString_EscapesParentheses()
    {
        // Act
        var result = SerializeObject(PdfString.FromText("(nested)"));

        // Assert - Parentheses should be escaped
        result.Should().Contain("\\(").And.Contain("\\)");
    }

    [Fact]
    public void Serialize_PdfArray_WritesCorrectly()
    {
        // Arrange
        var array = new PdfArray();
        array.Add((PdfObject)new PdfInteger(1));
        array.Add((PdfObject)new PdfInteger(2));
        array.Add((PdfObject)new PdfInteger(3));

        // Act
        var result = SerializeObject(array);

        // Assert
        result.Should().Be("[1 2 3]");
    }

    [Fact]
    public void Serialize_PdfDictionary_WritesCorrectly()
    {
        // Arrange
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["Count"] = new PdfInteger(1)
        };

        // Act
        var result = SerializeObject(dict);

        // Assert
        result.Should().Contain("<<");
        result.Should().Contain("/Type /Page");
        result.Should().Contain("/Count 1");
        result.Should().Contain(">>");
    }

    [Fact]
    public void Serialize_PdfReference_WritesCorrectly()
    {
        // Act
        var result = SerializeObject(new PdfReference(5, 0));

        // Assert
        result.Should().Be("5 0 R");
    }

    #endregion

    #region XRef Table Tests

    [Fact]
    public void XRefTable_HasCorrectFormat()
    {
        // Arrange
        var originalData = CreateSimplePdf("Test");
        using var doc = PdfDocument.Open(originalData);

        // Act
        var savedData = doc.SaveToBytes();
        var content = System.Text.Encoding.ASCII.GetString(savedData);

        // Assert - Should have xref table
        content.Should().Contain("xref");
        content.Should().Contain("startxref");
        content.Should().Contain("%%EOF");
    }

    [Fact]
    public void Trailer_HasRequiredKeys()
    {
        // Arrange
        var originalData = CreateSimplePdf("Test");
        using var doc = PdfDocument.Open(originalData);

        // Act
        var savedData = doc.SaveToBytes();
        var content = System.Text.Encoding.ASCII.GetString(savedData);

        // Assert - Trailer must have /Root and /Size
        content.Should().Contain("trailer");
        content.Should().Contain("/Root");
        content.Should().Contain("/Size");
    }

    [Fact]
    public void Pdf15Save_PacksSmallObjectsIntoObjectStreamAndReopens()
    {
        var originalData = CreateSimplePdf("Compressed Round Trip", version: "1.5");
        using var doc = PdfDocument.Open(originalData);

        var savedData = doc.SaveToBytes();
        var content = System.Text.Encoding.Latin1.GetString(savedData);

        content.Should().Contain("/Type /ObjStm", "PDF 1.5+ saves should use object-level compression (#923)");
        content.Should().Contain("/Type /XRef", "compressed objects require type-2 xref stream entries");
        content.Should().NotContain("\ntrailer\n", "xref streams carry the trailer dictionary themselves");

        using var reopened = PdfDocument.Open(savedData);
        reopened.PageCount.Should().Be(1);
        reopened.GetPage(1).Text.Should().Contain("Compressed Round Trip");
    }

    [Fact]
    public void Pdf15Save_XRefStreamContainsType2EntriesForPackedObjects()
    {
        var originalData = CreateSimplePdf("Compressed XRef Entries", version: "1.5");
        using var doc = PdfDocument.Open(originalData);

        var savedData = doc.SaveToBytes();
        using var stream = new MemoryStream(savedData);
        var parser = new XRefParser(stream);
        var (_, xref) = parser.ParseXRef(parser.FindStartXRef());

        xref.Values.Count(e => e.IsCompressed).Should().BeGreaterThan(0,
            "the writer must emit type-2 xref entries for objects packed into /ObjStm");
        xref.Values.Where(e => e.IsCompressed).Should().OnlyContain(e =>
            e.ObjectStreamNumber.HasValue && e.IndexInStream.HasValue,
            "each compressed entry must identify its object stream and slot");
    }

    [Fact]
    public void Pdf15Save_LargePackableSetUsesMultipleObjectStreams()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("multi object stream content", version: "1.5"));
        var packableReferences = new PdfArray();
        for (var i = 0; i < 120; i++)
        {
            var marker = new PdfDictionary
            {
                ["Marker"] = new PdfInteger(i),
            };
            packableReferences.Add(doc.AddIndirectObject(marker));
        }

        doc.Catalog["TestObjects"] = packableReferences;

        var savedData = doc.SaveToBytes();
        var content = Encoding.Latin1.GetString(savedData);
        using var stream = new MemoryStream(savedData);
        var parser = new XRefParser(stream);
        var (_, xref) = parser.ParseXRef(parser.FindStartXRef());
        var compressedObjectStreamNumbers = xref.Values
            .Where(e => e.IsCompressed)
            .Select(e => e.ObjectStreamNumber!.Value)
            .Distinct()
            .ToList();

        CountOccurrences(content, "/Type /ObjStm").Should().BeGreaterThan(1);
        compressedObjectStreamNumbers.Should().HaveCountGreaterThan(1,
            "large documents should be partitioned across multiple object streams rather than one unbounded stream");

        using var reopened = PdfDocument.Open(savedData);
        reopened.PageCount.Should().Be(1);
        reopened.GetPage(1).Text.Should().Contain("multi object stream content");
    }

    [Fact]
    public void Pdf15Save_CompressedObjectStreamNumberAboveByteBoundaryReopens()
    {
        var originalData = CreatePdf(
            Enumerable.Range(1, 260).Select(i => $"compressed boundary page {i}").ToArray(),
            version: "1.5",
            includeOutline: false);
        using var doc = PdfDocument.Open(originalData);

        var savedData = doc.SaveToBytes();
        using var stream = new MemoryStream(savedData);
        var parser = new XRefParser(stream);
        var (_, xref) = parser.ParseXRef(parser.FindStartXRef());

        xref.Values.Where(e => e.IsCompressed)
            .Select(e => e.ObjectStreamNumber!.Value)
            .Max()
            .Should().BeGreaterThan(255,
                "the compatibility suite should exercise type-2 xref field widths beyond a single byte");

        using var reopened = PdfDocument.Open(savedData);
        reopened.PageCount.Should().Be(260);
        reopened.GetPage(260).Text.Should().Contain("compressed boundary page 260");
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSurvivesRepeatedSaveReopenCycles()
    {
        byte[] current = CreateSimplePdf("Repeated Compressed Save", version: "1.5");

        for (var i = 0; i < 3; i++)
        {
            using var doc = PdfDocument.Open(current);
            current = doc.SaveToBytes();
            Encoding.Latin1.GetString(current).Should().Contain("/Type /ObjStm",
                $"save cycle {i + 1} should remain on the compressed writer path");
        }

        using var final = PdfDocument.Open(current);
        final.PageCount.Should().Be(1);
        final.GetPage(1).Text.Should().Contain("Repeated Compressed Save");
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsPageTreeMutationAfterReopen()
    {
        using var source = PdfDocument.Open(CreateSimplePdf("Mutable Compressed", version: "1.5"));
        var compressed = source.SaveToBytes();

        using var doc = PdfDocument.Open(compressed);
        doc.Pages.AddBlank();
        doc.Pages.Move(1, 0);
        doc.Pages.RemoveAt(0);
        var edited = doc.SaveToBytes();

        Encoding.Latin1.GetString(edited).Should().Contain("/Type /ObjStm",
            "page-tree edits should not force eligible PDF 1.5+ documents off the compressed writer path");
        using var reopened = PdfDocument.Open(edited);
        reopened.PageCount.Should().Be(1);
        reopened.GetPage(1).Text.Should().Contain("Mutable Compressed");
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsRedactionAfterReopen()
    {
        using var source = PdfDocument.Open(CreateSimplePdf("SECRET remains visible", version: "1.5"));
        var compressed = source.SaveToBytes();

        using var doc = PdfDocument.Open(compressed);
        doc.RedactText("SECRET", drawBlackRect: false).Should().Be(1);
        var redacted = doc.SaveToBytes();

        Encoding.Latin1.GetString(redacted).Should().Contain("/Type /ObjStm",
            "redacted PDF 1.5+ output should still use the compressed writer path when eligible");
        using var reopened = PdfDocument.Open(redacted);
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
        reopened.GetPage(1).Text.Should().Contain("remains visible");
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsAreaRedactionAfterReopen()
    {
        using var source = PdfDocument.Open(CreateSimplePdf("AREASECRET remains visible", version: "1.5"));
        var compressed = source.SaveToBytes();

        using var doc = PdfDocument.Open(compressed);
        doc.GetPage(1).RedactArea(new PdfRectangle(95, 690, 180, 720));
        var redacted = doc.SaveToBytes();

        Encoding.Latin1.GetString(redacted).Should().Contain("/Type /ObjStm");
        using var reopened = PdfDocument.Open(redacted);
        reopened.GetPage(1).Text.Should().NotContain("AREASECRET");
        reopened.GetPage(1).Text.Should().Contain("remains visible");
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsAnnotationAddEditRemoveAfterReopen()
    {
        using var source = PdfDocument.Open(CreateSimplePdf("Annotation workflow", version: "1.5"));
        var compressed = source.SaveToBytes();

        using var doc = PdfDocument.Open(compressed);
        doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "keep");
        var remove = doc.AddTextAnnotation(1, new PdfRectangle(120, 700, 156, 736), "remove");
        doc.RemoveAnnotation(1, remove).Should().BeTrue();
        doc.GetPage(1).GetAnnotations()[0].SetAnnotationContents("updated keep");
        var edited = doc.SaveToBytes();

        Encoding.Latin1.GetString(edited).Should().Contain("/Type /ObjStm");
        using var reopened = PdfDocument.Open(edited);
        var annotations = reopened.GetPage(1).GetAnnotations();
        annotations.Should().ContainSingle();
        annotations[0].Contents.Should().Be("updated keep");
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsFormFillAndFlattenAfterReopen()
    {
        using var source = PdfDocument.Open(CreateSimplePdf("Form workflow", version: "1.5"));
        var compressed = source.SaveToBytes();

        byte[] filled;
        using (var doc = PdfDocument.Open(compressed))
        {
            var field = doc.AddTextField(1, new PdfRectangle(72, 640, 260, 662), "Name");
            field.SetValue("Alice");
            filled = doc.SaveToBytes();
        }

        Encoding.Latin1.GetString(filled).Should().Contain("/Type /ObjStm");
        using (var reopened = PdfDocument.Open(filled))
        {
            reopened.GetAcroForm()!.FindField("Name")!.Value.Should().Be("Alice");
            reopened.GetAcroForm()!.FindField("Name")!.SetValue("Bob");
            reopened.FlattenAcroForm();
            var flattened = reopened.SaveToBytes();

            Encoding.Latin1.GetString(flattened).Should().Contain("/Type /ObjStm");
            using var final = PdfDocument.Open(flattened);
            final.GetAcroForm().Should().BeNull();
            final.GetPage(1).Text.Should().Contain("Bob");
        }
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsMetadataEditAfterReopen()
    {
        using var source = PdfDocument.Open(CreateSimplePdf("Metadata workflow", version: "1.5"));
        var compressed = source.SaveToBytes();

        using var doc = PdfDocument.Open(compressed);
        doc.SetTitle("Edited compressed title");
        doc.SetAuthor("Edited compressed author");
        var edited = doc.SaveToBytes();

        Encoding.Latin1.GetString(edited).Should().Contain("/Type /ObjStm");
        using var reopened = PdfDocument.Open(edited);
        reopened.Title.Should().Be("Edited compressed title");
        reopened.Author.Should().Be("Edited compressed author");
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsOutlinesAndDestinationsAfterReopen()
    {
        using var source = PdfDocument.Open(CreateTwoPagePdfWithOutline(version: "1.5"));
        var compressed = source.SaveToBytes();

        Encoding.Latin1.GetString(compressed).Should().Contain("/Type /ObjStm");
        using var reopened = PdfDocument.Open(compressed);
        var outline = PdfOutlineParser.Parse(reopened);

        outline.Should().HaveCount(2);
        outline[0].Title.Should().Be("First Chapter");
        outline[0].PageNumber.Should().Be(1);
        outline[1].Title.Should().Be("Second Chapter");
        outline[1].PageNumber.Should().Be(2);
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsSplitAndMergeAfterReopen()
    {
        using var source = PdfDocument.Open(CreateTwoPagePdf("Split merge one", "Split merge two", version: "1.5"));
        var compressed = source.SaveToBytes();

        using var reopened = PdfDocument.Open(compressed);
        using var fragments = new DisposableDocuments(PdfDocumentSplitter.SplitToSinglePages(reopened));
        fragments.Documents.Should().HaveCount(2);

        using var merged = PdfDocumentMerger.Merge([
            (fragments.Documents[0], new[] { 0 }),
            (fragments.Documents[1], new[] { 0 }),
        ]);
        var saved = merged.SaveToBytes();

        Encoding.Latin1.GetString(saved).Should().Contain("/Type /ObjStm");
        using var final = PdfDocument.Open(saved);
        final.PageCount.Should().Be(2);
        final.GetPage(1).Text.Should().Contain("Split merge one");
        final.GetPage(2).Text.Should().Contain("Split merge two");
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsSearchByExtractedTextAfterReopen()
    {
        using var source = PdfDocument.Open(CreateSimplePdf("Searchable compressed content", version: "1.5"));
        var compressed = source.SaveToBytes();

        using var reopened = PdfDocument.Open(compressed);

        reopened.GetPage(1).Text.IndexOf("compressed", StringComparison.OrdinalIgnoreCase)
            .Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsEmbeddedFilesAfterReopen()
    {
        using var source = PdfDocument.Open(CreatePdfWithEmbeddedFile("payload.txt", "attachment payload", version: "1.5"));
        var compressed = source.SaveToBytes();

        Encoding.Latin1.GetString(compressed).Should().Contain("/Type /ObjStm");
        using var reopened = PdfDocument.Open(compressed);
        reopened.HasEmbeddedFiles.Should().BeTrue();
        var file = reopened.GetEmbeddedFiles().Should().ContainSingle().Subject;
        file.FileName.Should().Be("payload.txt");
        Encoding.UTF8.GetString(file.Bytes!).Should().Be("attachment payload");

        reopened.ScrubEmbeddedFiles();
        var scrubbed = reopened.SaveToBytes();
        using var final = PdfDocument.Open(scrubbed);
        final.GetEmbeddedFiles().Should().BeEmpty();
        final.HasEmbeddedFiles.Should().BeFalse();
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsImageXObjectsAfterReopen()
    {
        using var source = PdfDocument.Open(CreatePdfWithImageXObject(version: "1.5"));
        var compressed = source.SaveToBytes();

        Encoding.Latin1.GetString(compressed).Should().Contain("/Type /ObjStm");
        using var reopened = PdfDocument.Open(compressed);
        reopened.PageCount.Should().Be(1);
        Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes()).Should().Contain("/Im1 Do");

        var image = reopened.GetPage(1).GetXObject("Im1").Should().BeOfType<PdfStream>().Subject;
        image.GetName("Subtype").Should().Be("Image");
        image.DecodedData.Should().Equal(new byte[] { 0x5A });
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsFilteredImageXObjectsAfterReopen()
    {
        using var source = PdfDocument.Open(CreatePdfWithImageXObject(version: "1.5", flateImage: true));
        var compressed = source.SaveToBytes();

        Encoding.Latin1.GetString(compressed).Should().Contain("/Type /ObjStm");
        using var reopened = PdfDocument.Open(compressed);
        var image = reopened.GetPage(1).GetXObject("Im1").Should().BeOfType<PdfStream>().Subject;

        image.GetName("Filter").Should().Be("FlateDecode");
        image.DecodedData.Should().Equal(new byte[] { 0x5A });
    }

    [Fact]
    public void Pdf15Save_CompressedOutputSupportsCidCjkFixtureAfterReopen()
    {
        const string fixture = "../../../../test-pdfs/sample-pdfs/multilingual-noto-cjk.pdf";
        Assert.SkipWhen(!File.Exists(fixture), "CJK fixture not available");

        using var source = PdfDocument.Open(fixture);
        source.PageCount.Should().BeGreaterThan(0);

        var compressed = source.SaveToBytes();

        Encoding.Latin1.GetString(compressed).Should().Contain("/Type /ObjStm");
        using var reopened = PdfDocument.Open(compressed);
        reopened.PageCount.Should().Be(source.PageCount);
        reopened.GetPage(1).Resources.Should().NotBeNull();
        reopened.GetPage(1).Letters.Should().NotBeEmpty();
    }

    [Fact]
    public void Pdf15Save_IncrementalUpdateInputSavesToCompressedOutput()
    {
        using var source = PdfDocument.Open(CreateIncrementallyUpdatedPdf(version: "1.5"));

        source.Title.Should().Be("Incremental Title");
        var compressed = source.SaveToBytes();

        var saved = Encoding.Latin1.GetString(compressed);
        saved.Should().Contain("/Type /ObjStm");
        saved.Should().Contain("/Type /XRef");
        saved.Should().NotContain("/Prev", "fresh full saves should not retain old incremental xref chains");

        using var reopened = PdfDocument.Open(compressed);
        reopened.GetPage(1).Text.Should().Contain("Original revision text");
        reopened.Title.Should().Be("Incremental Title");
    }

    [Fact]
    public void Pdf15Save_LinearizedInputSavesToCompressedOutput()
    {
        const string fixture = "../../../../test-pdfs/smoke/irs-w9.pdf";
        Assert.SkipWhen(!File.Exists(fixture), "linearized smoke fixture not available");

        using var source = PdfDocument.Open(fixture);
        source.PageCount.Should().BeGreaterThan(0);

        var compressed = source.SaveToBytes();

        Encoding.Latin1.GetString(compressed).Should().Contain("/Type /ObjStm");
        using var reopened = PdfDocument.Open(compressed);
        reopened.PageCount.Should().Be(source.PageCount);
        reopened.GetPage(1).Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Pdf15Save_SmokeCorpusCompressedOutputStaysUnderSourceSizeBudget()
    {
        var fixtures = new[]
        {
            "../../../../test-pdfs/smoke/irs-w4.pdf",
            "../../../../test-pdfs/smoke/irs-w9.pdf",
            "../../../../test-pdfs/smoke/scotus-trump-v-anderson.pdf",
        };

        foreach (var fixture in fixtures)
        {
            Assert.SkipWhen(!File.Exists(fixture), $"smoke fixture not available: {fixture}");
            using var source = PdfDocument.Open(fixture);

            var saved = source.SaveToBytes();
            var ratio = saved.Length / (double)new FileInfo(fixture).Length;

            Encoding.Latin1.GetString(saved).Should().Contain("/Type /ObjStm");
            ratio.Should().BeLessThan(1.20,
                $"{Path.GetFileName(fixture)} should not regress toward the pre-#923 daily-driver file-size inflation");
        }
    }

    [Theory]
    [InlineData("1.4")]
    [InlineData("1.5")]
    public void Save_OpenSaveLatencyForPhysicalFormatStaysWithinSmokeBudget(string version)
    {
        var source = CreatePdf(
            Enumerable.Range(1, 30).Select(i => $"latency budget page {i}").ToArray(),
            version,
            includeOutline: true);

        var openTimer = Stopwatch.StartNew();
        using var doc = PdfDocument.Open(source);
        openTimer.Stop();

        var saveTimer = Stopwatch.StartNew();
        var saved = doc.SaveToBytes();
        saveTimer.Stop();

        openTimer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            $"{version} open latency should stay within the smoke budget for generated format fixtures");
        saveTimer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            $"{version} save latency should stay within the smoke budget for generated format fixtures");

        using var reopened = PdfDocument.Open(saved);
        reopened.PageCount.Should().Be(30);
    }

    [Fact]
    public void Pdf15Save_QpdfAcceptsCompressedOutput_WhenAvailable()
    {
        var qpdf = FindOnPath("qpdf");
        Assert.SkipWhen(qpdf is null, "qpdf not on PATH");

        using var doc = PdfDocument.Open(CreateSimplePdf("QPDF Compressed Check", version: "1.5"));
        var path = Path.Combine(Path.GetTempPath(), $"excise-compressed-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, doc.SaveToBytes());
        try
        {
            var (exitCode, output) = RunProcess(qpdf!, "--check", path);
            exitCode.Should().Be(0, $"qpdf should accept the compressed writer output:\n{output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pdf15Save_MutoolExtractsCompressedOutput_WhenAvailable()
    {
        var mutool = FindOnPath("mutool");
        Assert.SkipWhen(mutool is null, "mutool not on PATH");

        var path = WriteCompressedTempPdf("MuPDF compressed text");
        try
        {
            var (exitCode, output) = RunProcess(mutool!, "draw", "-F", "txt", "-o", "-", path, "1");
            exitCode.Should().Be(0, $"mutool should extract text from compressed writer output:\n{output}");
            output.Should().Contain("MuPDF compressed text");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pdf15Save_PdfinfoReadsCompressedOutput_WhenAvailable()
    {
        var pdfinfo = FindOnPath("pdfinfo");
        Assert.SkipWhen(pdfinfo is null, "pdfinfo not on PATH");

        var path = WriteCompressedTempPdf("Poppler compressed text");
        try
        {
            var (exitCode, output) = RunProcess(pdfinfo!, path);
            exitCode.Should().Be(0, $"pdfinfo should read compressed writer output:\n{output}");
            output.Should().Contain("Pages:           1");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pdf15Save_GhostscriptRendersCompressedOutput_WhenAvailable()
    {
        var gs = FindOnPath("gs");
        Assert.SkipWhen(gs is null, "Ghostscript not on PATH");

        var path = WriteCompressedTempPdf("Ghostscript compressed text");
        try
        {
            var (exitCode, output) = RunProcess(gs!, "-q", "-dNOPAUSE", "-dBATCH", "-sDEVICE=nullpage", path);
            exitCode.Should().Be(0, $"Ghostscript should render compressed writer output:\n{output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pdf15Save_PdfcpuValidatesCompressedOutput_WhenAvailable()
    {
        var pdfcpu = FindOnPath("pdfcpu");
        Assert.SkipWhen(pdfcpu is null, "pdfcpu not on PATH");

        var path = WriteCompressedTempPdf("pdfcpu compressed text");
        try
        {
            var (exitCode, output) = RunProcess(pdfcpu!, "validate", path);
            exitCode.Should().Be(0, $"pdfcpu should validate compressed writer output:\n{output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData("1.2")]
    [InlineData("1.3")]
    [InlineData("1.4")]
    [InlineData("1.5")]
    [InlineData("1.6")]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void VersionMatrix_QpdfAcceptsSavedOutput_WhenAvailable(string version)
    {
        var qpdf = FindOnPath("qpdf");
        Assert.SkipWhen(qpdf is null, "qpdf not on PATH");

        using var doc = PdfDocument.Open(CreateSimplePdf($"QPDF {version}", version));
        var path = Path.Combine(Path.GetTempPath(), $"excise-{version}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, doc.SaveToBytes());
        try
        {
            var (exitCode, output) = RunProcess(qpdf!, "--check", path);
            exitCode.Should().Be(0, $"qpdf should accept saved PDF {version} output:\n{output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pdf14Save_UsesClassicXRefTable()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("Classic Version", version: "1.4"));

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().Contain("\nxref\n");
        saved.Should().Contain("\ntrailer\n");
        saved.Should().NotContain("/Type /ObjStm");
        saved.Should().NotContain("/Type /XRef");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData("1.2")]
    [InlineData("1.3")]
    [InlineData("1.4")]
    public void PrePdf15Saves_UseClassicXRefTable(string version)
    {
        using var doc = PdfDocument.Open(CreateSimplePdf($"Classic {version}", version));

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().StartWith($"%PDF-{version}");
        saved.Should().Contain("\nxref\n");
        saved.Should().Contain("\ntrailer\n");
        saved.Should().NotContain("/Type /ObjStm");
        saved.Should().NotContain("/Type /XRef");
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1.6")]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void Pdf15AndLaterSaves_UseCompressedObjectFormat(string version)
    {
        using var doc = PdfDocument.Open(CreateSimplePdf($"Compressed {version}", version));

        var savedBytes = doc.SaveToBytes();
        var saved = Encoding.Latin1.GetString(savedBytes);

        saved.Should().StartWith($"%PDF-{version}");
        saved.Should().Contain("/Type /ObjStm");
        saved.Should().Contain("/Type /XRef");
        saved.Should().NotContain("\ntrailer\n");

        using var reopened = PdfDocument.Open(savedBytes);
        reopened.GetPage(1).Text.Should().Contain($"Compressed {version}");
    }

    [Fact]
    public void EncryptedSave_UsesClassicXRefTableEvenForPdf15()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("Encrypted Classic", version: "1.5"));

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes(new PdfEncryptionOptions()));

        saved.Should().Contain("\nxref\n");
        saved.Should().Contain("\ntrailer\n");
        saved.Should().Contain("/Encrypt");
        saved.Should().NotContain("/Type /ObjStm");
        saved.Should().NotContain("/Type /XRef");
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    public void EncryptedSave_UsesClassicXRefTableForEverySupportedAlgorithm(PdfEncryptionAlgorithm algorithm)
    {
        using var doc = PdfDocument.Open(CreateSimplePdf($"Encrypted {algorithm}", version: "1.5"));

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes(new PdfEncryptionOptions
        {
            Algorithm = algorithm,
            UserPassword = "user",
            OwnerPassword = "owner",
        }));

        saved.Should().Contain("\nxref\n");
        saved.Should().Contain("\ntrailer\n");
        saved.Should().Contain("/Encrypt");
        saved.Should().NotContain("/Type /ObjStm");
        saved.Should().NotContain("/Type /XRef");
    }

    [Fact]
    public void PdfA1MarkedSave_UsesClassicXRefTable()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("PDF/A-1 Classic", version: "1.7"));
        AddPdfAMetadata(doc, part: 1);

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().Contain("\nxref\n");
        saved.Should().Contain("\ntrailer\n");
        saved.Should().Contain("pdfaid:part>1");
        saved.Should().NotContain("/Type /ObjStm");
        saved.Should().NotContain("/Type /XRef");
    }

    [Fact]
    public void PdfA2MarkedSave_CanUseCompressedXRefStreams()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("PDF/A-2 Compressed", version: "1.7"));
        AddPdfAMetadata(doc, part: 2);

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().Contain("/Type /ObjStm");
        saved.Should().Contain("/Type /XRef");
        saved.Should().Contain("pdfaid:part>2");
    }

    [Fact]
    public void DocumentCarrierDictionariesStayTopLevelInCompressedSaves()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("Carrier Visible", version: "1.5"));
        doc.SetTitle("SECRETNAME in Info title");
        doc.SetAuthor("SECRETNAME in Info author");

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().Contain("/Type /ObjStm", "the file should still use compressed object storage");
        saved.Should().Contain("SECRETNAME in Info title",
            "document-level text carriers are intentionally left top-level for redaction/audit behavior");
        saved.Should().Contain("SECRETNAME in Info author",
            "document-level text carriers are intentionally left top-level for redaction/audit behavior");
    }

    #endregion

    #region Stream Tests

    [Fact]
    public void Stream_PreservesData()
    {
        // Arrange
        var originalData = CreateSimplePdf("Stream Test");
        using var doc = PdfDocument.Open(originalData);

        // Act
        var savedData = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(savedData);

        // Assert - Content stream should be preserved
        var page = reopened.GetPage(1);
        var contentBytes = page.GetContentStreamBytes();
        contentBytes.Should().NotBeEmpty();
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void RoundTrip_SimplePdf_PreservesStructure()
    {
        // Arrange
        var originalData = CreateSimplePdf("Round Trip Test");

        // Act - Multiple round trips
        byte[] currentData = originalData;
        for (int i = 0; i < 3; i++)
        {
            using var doc = PdfDocument.Open(currentData);
            currentData = doc.SaveToBytes();
        }

        // Assert - Should still be valid after 3 round trips
        using var final = PdfDocument.Open(currentData);
        final.PageCount.Should().Be(1);
        final.GetPage(1).Text.Should().Contain("Round Trip");
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateSimplePdf(string text, string version = "1.4")
        => CreatePdf([text], version, includeOutline: false);

    private static byte[] CreateTwoPagePdf(string firstPageText, string secondPageText, string version = "1.4")
        => CreatePdf([firstPageText, secondPageText], version, includeOutline: false);

    private static byte[] CreateTwoPagePdfWithOutline(string version = "1.4")
        => CreatePdf(["Outlined first page", "Outlined second page"], version, includeOutline: true);

    private static byte[] CreatePdfWithEmbeddedFile(string fileName, string content, string version)
    {
        var sb = new StringBuilder();
        var offsets = new long[8];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append($"%PDF-{version}\n");
        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");
        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");
        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");
        Mark(5);
        sb.Append($"5 0 obj <</Names[({fileName}) 6 0 R]>> endobj\n");
        Mark(6);
        sb.Append($"6 0 obj <</Type/Filespec/F({fileName})/EF<</F 7 0 R>>>> endobj\n");
        Mark(7);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        sb.Append("7 0 obj <</Type/EmbeddedFile/Length ").Append(fileBytes.Length).Append(">>\nstream\n");
        sb.Append(content);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        for (var i = 1; i <= 7; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 8/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] CreatePdfWithImageXObject(string version, bool flateImage = false)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine($"%PDF-{version}");
        writer.Flush();

        var offsets = new long[7];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im1 6 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        const string contentStream = "q 100 0 0 100 50 550 cm /Im1 Do Q";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentStream.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentStream);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var imageBytes = flateImage ? FlateCompress([0x5A]) : new byte[] { 0x5A };
        var filter = flateImage ? " /Filter /FlateDecode" : "";

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8{filter} /Length {imageBytes.Length} >>");
        writer.WriteLine("stream");
        writer.Flush();
        ms.Write(imageBytes);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (var i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] FlateCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] CreateIncrementallyUpdatedPdf(string version)
    {
        var sb = new StringBuilder();
        var offsets = new long[7];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append($"%PDF-{version}\n");
        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 5 0 R>>>>/Contents 4 0 R>> endobj\n");
        Mark(4);
        const string content = "BT /F1 12 Tf 100 700 Td (Original revision text) Tj ET";
        sb.Append("4 0 obj <</Length ").Append(content.Length).Append(">>\nstream\n")
            .Append(content).Append("\nendstream endobj\n");
        Mark(5);
        sb.Append("5 0 obj <</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>> endobj\n");

        var firstXRef = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 6/Root 1 0 R>>\nstartxref\n")
            .Append(firstXRef).Append("\n%%EOF\n");

        Mark(6);
        sb.Append("6 0 obj <</Title (Incremental Title)>> endobj\n");
        var secondXRef = sb.Length;
        sb.Append("xref\n6 1\n").Append(offsets[6].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 7/Root 1 0 R/Info 6 0 R/Prev ")
            .Append(firstXRef).Append(">>\nstartxref\n")
            .Append(secondXRef).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] CreatePdf(IReadOnlyList<string> pageTexts, string version, bool includeOutline)
    {
        var objectCount = 2 + (pageTexts.Count * 2) + 1 + (includeOutline ? 3 : 0);
        var fontObj = 2 + (pageTexts.Count * 2) + 1;
        var outlineRootObj = fontObj + 1;
        var firstOutlineObj = outlineRootObj + 1;
        var secondOutlineObj = firstOutlineObj + 1;

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine($"%PDF-{version}");
        writer.Flush();

        var offsets = new long[objectCount + 1];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine(includeOutline
            ? $"<< /Type /Catalog /Pages 2 0 R /Outlines {outlineRootObj} 0 R >>"
            : "<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var kids = string.Join(" ", Enumerable.Range(0, pageTexts.Count).Select(i => $"{3 + (i * 2)} 0 R"));
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine($"<< /Type /Pages /Kids [{kids}] /Count {pageTexts.Count} >>");
        writer.WriteLine("endobj");
        writer.Flush();

        for (var i = 0; i < pageTexts.Count; i++)
        {
            var pageObj = 3 + (i * 2);
            var contentsObj = pageObj + 1;
            var content = $"BT /F1 12 Tf 100 700 Td ({pageTexts[i]}) Tj ET";

            offsets[pageObj] = ms.Position;
            writer.WriteLine($"{pageObj} 0 obj");
            writer.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentsObj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
            writer.WriteLine("endobj");
            writer.Flush();

            offsets[contentsObj] = ms.Position;
            writer.WriteLine($"{contentsObj} 0 obj");
            writer.WriteLine($"<< /Length {content.Length} >>");
            writer.WriteLine("stream");
            writer.Write(content);
            writer.WriteLine();
            writer.WriteLine("endstream");
            writer.WriteLine("endobj");
            writer.Flush();
        }

        offsets[fontObj] = ms.Position;
        writer.WriteLine($"{fontObj} 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        if (includeOutline)
        {
            offsets[outlineRootObj] = ms.Position;
            writer.WriteLine($"{outlineRootObj} 0 obj");
            writer.WriteLine($"<< /Type /Outlines /First {firstOutlineObj} 0 R /Last {secondOutlineObj} 0 R /Count 2 >>");
            writer.WriteLine("endobj");
            writer.Flush();

            offsets[firstOutlineObj] = ms.Position;
            writer.WriteLine($"{firstOutlineObj} 0 obj");
            writer.WriteLine($"<< /Title (First Chapter) /Parent {outlineRootObj} 0 R /Next {secondOutlineObj} 0 R /Dest [3 0 R /XYZ 0 792 0] >>");
            writer.WriteLine("endobj");
            writer.Flush();

            offsets[secondOutlineObj] = ms.Position;
            writer.WriteLine($"{secondOutlineObj} 0 obj");
            writer.WriteLine($"<< /Title (Second Chapter) /Parent {outlineRootObj} 0 R /Prev {firstOutlineObj} 0 R /Dest [5 0 R /XYZ 0 792 0] >>");
            writer.WriteLine("endobj");
            writer.Flush();
        }

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {objectCount + 1}");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= objectCount; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Root 1 0 R /Size {objectCount + 1} >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static string SerializeObject(PdfObject obj)
    {
        // This will call the PdfObjectWriter.Serialize method when implemented
        return Excise.Core.Writing.PdfObjectWriter.Serialize(obj);
    }

    private static void AddPdfAMetadata(PdfDocument doc, int part)
    {
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">" +
            $"<pdfaid:part>{part}</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var bytes = Encoding.UTF8.GetBytes(xmp);
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("Metadata"),
            ["Subtype"] = new PdfName("XML"),
            ["Length"] = new PdfInteger(bytes.Length),
        };
        doc.Catalog["Metadata"] = doc.AddIndirectObject(new PdfStream(dict, bytes));
    }

    private static string WriteCompressedTempPdf(string text)
    {
        using var doc = PdfDocument.Open(CreateSimplePdf(text, version: "1.5"));
        var path = Path.Combine(Path.GetTempPath(), $"excise-compressed-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, doc.SaveToBytes());
        return path;
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var path = Path.Combine(dir, executable);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static (int ExitCode, string Output) RunProcess(string executable, params string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000).Should().BeTrue($"{executable} should exit within 30 seconds");
        return (proc.ExitCode, stdout + stderr);
    }

    private sealed class DisposableDocuments : IDisposable
    {
        public DisposableDocuments(IReadOnlyList<PdfDocument> documents)
        {
            Documents = documents;
        }

        public IReadOnlyList<PdfDocument> Documents { get; }

        public void Dispose()
        {
            foreach (var document in Documents)
                document.Dispose();
        }
    }

    #endregion

    #region Cross-reference plumbing not re-emitted (#359)

    [Fact]
    public void Save_DropsUnreachableObjectsAndXRefPlumbing_SoFreedObjectsCannotLeak()
    {
        // The writer emits a classic xref table and writes object-stream members
        // as standalone objects. Re-emitting the /ObjStm (or /XRef) container is
        // redundant AND a leak path: a redacted Form XObject that was inlined and
        // freed (RemoveObject) would still ship inside the container's bytes. (#359)
        // It also garbage-collects normal unreachable objects so stale previous
        // revisions and scrubbed attachment/metadata streams do not survive a save.
        using var doc = PdfDocument.Open(CreateSimplePdf("Hi"));

        var keep = new PdfStream(System.Text.Encoding.ASCII.GetBytes("KEEPME_DATA"));
        doc.AddIndirectObject(keep);

        var objStm = new PdfStream(System.Text.Encoding.ASCII.GetBytes("SECRET_IN_OBJSTM"));
        objStm["Type"] = new PdfName("ObjStm");
        doc.AddIndirectObject(objStm);

        var xref = new PdfStream(System.Text.Encoding.ASCII.GetBytes("SECRET_IN_XREF"));
        xref["Type"] = new PdfName("XRef");
        doc.AddIndirectObject(xref);

        var saved = System.Text.Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().NotContain("KEEPME_DATA", "unreachable normal objects must not be re-emitted");
        saved.Should().NotContain("SECRET_IN_OBJSTM", "/ObjStm containers must not be re-emitted");
        saved.Should().NotContain("SECRET_IN_XREF", "/XRef streams must not be re-emitted");
    }

    #endregion
}
