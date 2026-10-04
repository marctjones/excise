using AwesomeAssertions;
using Excise.App.Services;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Rendering.Differential;
using Microsoft.Extensions.Logging.Abstractions;

namespace Excise.App.Tests.Unit;

/// <summary>GUI Extract must use the assembly catalog policy (#1948).</summary>
public sealed class ExtractPagesCatalogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"excise-extract-catalog-{Guid.NewGuid():N}");
    private readonly PdfDocumentService _service = new(NullLogger<PdfDocumentService>.Instance);

    public ExtractPagesCatalogTests() => Directory.CreateDirectory(_tempDir);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Extract_PreservesMetadataAndOutputIntentGraphs(bool metadata, bool outputIntents)
    {
        var sourcePath = CreateSource(metadata, outputIntents);
        using var source = PdfDocument.Open(sourcePath);
        _service.LoadDocument(sourcePath);
        var outputPath = Path.Combine(_tempDir, "extracted.pdf");

        _service.ExtractPagesToPdf(outputPath, [2, 0]);
        _service.CloseDocument(); // Output must own its streams, even after the source closes.

        using var output = PdfDocument.Open(outputPath);
        output.PageCount.Should().Be(2);
        output.GetPage(1).Text.Should().Contain("THREE");
        output.GetPage(2).Text.Should().Contain("ONE");
        output.Catalog.ContainsKey("Metadata").Should().Be(metadata);
        output.Catalog.ContainsKey("OutputIntents").Should().Be(outputIntents);
        if (metadata)
            output.GetXmpMetadata().Should().Equal(source.GetXmpMetadata()!);
        if (outputIntents)
        {
            var originalIntent = OutputIntent(source);
            var copiedIntent = OutputIntent(output);
            copiedIntent.GetNameOrNull("S").Should().Be(originalIntent.GetNameOrNull("S"));
            copiedIntent.GetStringOrNull("OutputConditionIdentifier")
                .Should().Be(originalIntent.GetStringOrNull("OutputConditionIdentifier"));
            Profile(output).DecodedData.ToArray().Should().Equal(Profile(source).DecodedData.ToArray());
        }
    }

    [Fact]
    public void Extract_OmitsPageDependentCatalogEntriesRatherThanCopyingStaleReferences()
    {
        var sourcePath = CreateSource(metadata: true, outputIntents: true);
        _service.LoadDocument(sourcePath);
        var outputPath = Path.Combine(_tempDir, "extracted.pdf");

        _service.ExtractPagesToPdf(outputPath, [2, 0]);

        using var output = PdfDocument.Open(outputPath);
        output.Catalog.ContainsKey("PageLabels").Should().BeFalse("labels must be remapped for the reordered subset");
        output.Catalog.ContainsKey("OpenAction").Should().BeFalse("the original destination names an omitted page");
        output.Catalog.GetStringOrNull("Lang").Should().Be("en-US", "page-independent entries follow the existing allowlist");
    }

    [Fact]
    public void Extract_IsReadableAndCarriesDecodedIdentityStreams_PerIndependentTools()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var sourcePath = CreateSource(metadata: true, outputIntents: true);
        _service.LoadDocument(sourcePath);
        var outputPath = Path.Combine(_tempDir, "extracted.pdf");
        _service.ExtractPagesToPdf(outputPath, [2, 0]);

        var check = QpdfReferenceTool.Check(outputPath);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue(check.Value.Output);
        QpdfReferenceTool.PageCount(outputPath).Should().Be(2);
        MutoolTextExtractor.ExtractPage(outputPath, 1).Should().Contain("THREE");
        MutoolTextExtractor.ExtractPage(outputPath, 2).Should().Contain("ONE");

        using var source = PdfDocument.Open(sourcePath);
        using var output = PdfDocument.Open(outputPath);
        var metadataRef = (PdfReference)output.Catalog["Metadata"];
        var metadata = QpdfReferenceTool.FilteredStreamData(outputPath, metadataRef.ObjectNum);
        metadata.IsOk.Should().BeTrue(metadata.Diagnostics);
        metadata.Bytes.Should().Equal(source.GetXmpMetadata()!);
        var profileRef = (PdfReference)OutputIntent(output)["DestOutputProfile"];
        var profile = QpdfReferenceTool.FilteredStreamData(outputPath, profileRef.ObjectNum);
        profile.IsOk.Should().BeTrue(profile.Diagnostics);
        profile.Bytes.Should().Equal(Profile(source).DecodedData.ToArray());
    }

    private string CreateSource(bool metadata, bool outputIntents)
    {
        // The authoring path supplies a real XMP packet and sRGB ICC profile.
        // These tests assert identity preservation and readability, not PDF/A conformance.
        var bytes = PdfDocumentBuilder.Create().PdfA().Language("en-US")
            .Paragraph("ONE").PageBreak().Paragraph("TWO").PageBreak().Paragraph("THREE")
            .SaveToBytes();
        using var document = PdfDocument.Open(bytes);
        if (!metadata) document.Catalog.Remove("Metadata");
        if (!outputIntents) document.Catalog.Remove("OutputIntents");
        document.Catalog["PageLabels"] = new PdfDictionary
        {
            ["Nums"] = new PdfArray(new PdfInteger(0), new PdfDictionary { ["S"] = new PdfName("r") }),
        };
        var omittedPage = document.GetPage(2).Dictionary;
        document.Catalog["OpenAction"] = new PdfArray(
            new PdfReference(omittedPage.ObjectNumber!.Value, omittedPage.GenerationNumber ?? 0), new PdfName("Fit"));
        var path = Path.Combine(_tempDir, "source.pdf");
        document.Save(path);
        return path;
    }

    private static PdfDictionary OutputIntent(PdfDocument document)
    {
        var intents = (PdfArray)document.Resolve(document.Catalog["OutputIntents"]);
        return (PdfDictionary)document.Resolve(intents[0]);
    }

    private static PdfStream Profile(PdfDocument document)
        => (PdfStream)document.Resolve(OutputIntent(document)["DestOutputProfile"]);

    public void Dispose()
    {
        _service.CloseDocument();
        Directory.Delete(_tempDir, recursive: true);
    }
}
