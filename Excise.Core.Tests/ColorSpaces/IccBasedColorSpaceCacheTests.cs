using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.ColorSpaces;

/// <summary>
/// #1208: <see cref="PdfColorSpace.Parse"/> returns one instance per ICCBased
/// profile stream per document. Excise.Rendering's ImageColorConverter cache is
/// keyed by that instance's identity, so a fresh instance per call rebuilt the
/// 17^4 CMYK lattice on every render. Same contract as #1425's FromName cache.
/// </summary>
public class IccBasedColorSpaceCacheTests
{
    private static PdfStream IccStream(byte[] profile, int n)
        => new(new PdfDictionary { ["N"] = new PdfInteger(n) }, profile);

    [Fact]
    public void Parse_IccBased_ReturnsTheSameInstanceForTheSameProfileStream()
    {
        using var doc = PdfDocument.CreateNew();
        var reference = doc.AddIndirectObject(IccStream(PdfIccProfileTests.BuildLut16CmykProfile(), 4));

        var first = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), reference), doc);
        var second = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), reference), doc);
        var viaIndirectArray = PdfColorSpace.Parse(
            doc.AddIndirectObject(new PdfArray(new PdfName("ICCBased"), reference)),
            doc);

        first.Type.Should().Be(PdfColorSpaceType.ICCBased);
        first.Components.Should().Be(4);
        second.Should().BeSameAs(first, "two ICCBased arrays naming the same profile stream are the same colour space");
        viaIndirectArray.Should().BeSameAs(first);
    }

    [Fact]
    public void Parse_IccBased_DifferentProfileStreamsGetDifferentInstances()
    {
        using var doc = PdfDocument.CreateNew();
        var profile = PdfIccProfileTests.BuildLut16CmykProfile();

        var a = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), doc.AddIndirectObject(IccStream(profile, 4))), doc);
        var b = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), doc.AddIndirectObject(IccStream(profile, 4))), doc);

        b.Should().NotBeSameAs(a, "the cache is keyed by stream identity, not by profile bytes");
    }

    [Fact]
    public void Parse_IccBased_OneStreamObjectInTwoDocumentsGetsOneInstancePerDocument()
    {
        // The parsed space embeds the document's OutputIntent profile, so a
        // stream object reachable from two documents must not share an answer.
        var stream = IccStream(PdfIccProfileTests.BuildLut16CmykProfile(), 4);
        using var docA = PdfDocument.CreateNew();
        using var docB = PdfDocument.CreateNew();

        var csA = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), stream), docA);
        var csB = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), stream), docB);

        csA.Should().NotBeSameAs(csB);
        PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), stream), docA).Should().BeSameAs(csA);
    }

    [Theory]
    [InlineData(1, PdfColorSpaceType.DeviceGray, 1)]
    [InlineData(3, PdfColorSpaceType.DeviceRGB, 3)]
    [InlineData(4, PdfColorSpaceType.DeviceCMYK, 4)]
    public void Parse_IccBased_UnparseableProfileFallsBackAsBeforeAndIsCached(
        int n,
        PdfColorSpaceType expectedType,
        int expectedComponents)
    {
        using var doc = PdfDocument.CreateNew();
        var reference = doc.AddIndirectObject(IccStream(new byte[] { 1, 2, 3, 4, 5 }, n));

        var first = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), reference), doc);
        var second = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), reference), doc);

        first.Type.Should().Be(expectedType);
        first.Components.Should().Be(expectedComponents);
        second.Should().BeSameAs(first);
        switch (n)
        {
            case 1:
                first.Should().BeSameAs(PdfColorSpace.DeviceGray);
                break;
            case 3:
                first.Should().BeSameAs(PdfColorSpace.DeviceRGB);
                break;
            default:
                // The N=4 fallback keeps its reference-formula CMYK policy.
                first.ToRgb([0.2, 0.4, 0.6, 0.1]).Should().Be(
                    PdfColorConverter.CmykToRgb(0.2, 0.4, 0.6, 0.1, PdfColorConverter.CmykPolicy.ReferenceFormula));
                break;
        }
    }

    [Fact]
    public void Parse_IccBased_StreamWhoseBytesWereReplacedIsParsedAgain()
    {
        using var doc = PdfDocument.CreateNew();
        var stream = IccStream(PdfIccProfileTests.BuildLut16CmykProfile(), 4);
        var reference = doc.AddIndirectObject(stream);

        var before = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), reference), doc);
        before.Type.Should().Be(PdfColorSpaceType.ICCBased);

        stream.DecodedData = new byte[] { 9, 9, 9 };
        var after = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), reference), doc);

        after.Should().NotBeSameAs(before);
        after.Type.Should().Be(PdfColorSpaceType.DeviceCMYK, "the replaced bytes are no longer a parseable profile");
        PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), reference), doc).Should().BeSameAs(after);
    }

    [Fact]
    public void Parse_IccBased_ConcurrentCallersAllReceiveOneInstance()
    {
        using var doc = PdfDocument.CreateNew();
        var reference = doc.AddIndirectObject(IccStream(PdfIccProfileTests.BuildLut16CmykProfile(), 4));
        var results = new PdfColorSpace[64];

        Parallel.For(
            0,
            results.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i => results[i] = PdfColorSpace.Parse(new PdfArray(new PdfName("ICCBased"), reference), doc));

        results.Should().AllSatisfy(result => result.Should().BeSameAs(results[0]));
    }
}
