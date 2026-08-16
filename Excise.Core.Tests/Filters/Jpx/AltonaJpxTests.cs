using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Filters.Jpx;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Filters.Jpx;

/// <summary>
/// JPX decode regressions that read the 127MB Altona PDF/X-4 fixture.
///
/// THEY LIVE IN THEIR OWN CLASS ON PURPOSE. While they sat in
/// CodestreamParserTests, that class reported 13 of its 14 discovered tests in
/// a full run — deterministically one short, and check-test-count.sh (#894)
/// called it FATAL. Which one vanished MOVED when the file was edited: first
/// the (338, 16) row of a [Theory], then, after that Theory was split into two
/// [Fact]s, TryDecodeManaged_AltonaIndexedJpxDecodesSingleIndexPlane instead.
///
/// It is not the decoder and not the fixture. Driven out of process, the same
/// four decodes run back to back in ONE process — including opening the 127MB
/// file four times — and all survive at 65MB peak RSS. It is not staleness
/// either; a --no-incremental rebuild changes nothing.
///
/// Nor is it reachable by filter for diagnosis: `--filter FullyQualifiedName~`
/// is NOT a substring match under xunit v3 on Microsoft.Testing.Platform.
/// Measured on this class: ~Altona matches 3 and ~Indexed matches 10, but
/// ~AltonaI, ~aIndexed and ~IndexedJpx all match NOTHING. So the token has to
/// align; a filter spanning a word boundary silently selects no tests, which
/// is also why check-test-count's re-run reports "unreachable by filter" for a
/// test that runs perfectly well under a shorter filter.
///
/// Splitting the class is the same containment #985 used: the harness loses a
/// test, so give it a smaller partition to lose from. Same family as #894.
/// </summary>
public class AltonaJpxTests
{
    [Fact]
    public void TryDecodeManaged_AltonaIndexedJpxDecodesSingleIndexPlane()
    {
        var path = CodestreamParserTests.FindRepoFile(
            "test-pdfs",
            "altona",
            "eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf");
        Assert.SkipWhen(path == null,
            "No Altona PDF/X fixture found at test-pdfs/altona/eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf.");

        using var doc = PdfDocument.Open(path);
        var imageStream = (PdfStream)doc.GetObject(335);

        var image = JpxDecoder.TryDecodeManaged(imageStream.EncodedData, maxComponents: 1);

        image.Should().NotBeNull();
        image!.Width.Should().BeOneOf(0, 424);
        image.Height.Should().BeOneOf(0, 212);
        image.BitsPerComponent.Should().Be(8);
        image.ComponentData.Should().ContainSingle(
            "the Altona Indexed JPX has one color index component and no embedded alpha component");
        image.ComponentData[0].Should().HaveCount(424 * 212);
        image.ComponentData[0].Should().Contain(sample => sample < 64);
        image.ComponentData[0].Should().Contain(sample => sample > 180);
    }

    // Two [Fact]s rather than a [Theory] with two [InlineData] rows.
    //
    // As a Theory, the (objectNumber: 338, bitsPerComponent: 16) row was
    // DISCOVERED by --list-tests and then never executed: no pass, no fail, not
    // even a skip. It was unreachable by every filter form including its exact
    // display name, while the 8-bit row in the same Theory ran normally. #894's
    // check-test-count gate caught it and correctly called it FATAL — a case
    // that never reports cannot be reddened by reverting the fix it covers, so
    // it silently defeats mutation testing.
    //
    // It is NOT a decoder crash. Driven directly out of process, object 338
    // decodes in 0.19s with 62MB peak RSS and cleanly returns null from the
    // managed path — so the coverage loss was in the test harness, not in the
    // code under test. Splitting the rows into separate methods restores both
    // assertions. Same family as #985.
    [Fact]
    public void TryDecodeJpx_AltonaGray16BitJpxDecodesSingleColorPlane()
        => AssertAltonaGrayJpxDecodesSingleColorPlane(objectNumber: 338, bitsPerComponent: 16);

    [Fact]
    public void TryDecodeJpx_AltonaGray8BitJpxDecodesSingleColorPlane()
        => AssertAltonaGrayJpxDecodesSingleColorPlane(objectNumber: 341, bitsPerComponent: 8);

    private static void AssertAltonaGrayJpxDecodesSingleColorPlane(int objectNumber, int bitsPerComponent)
    {
        var path = CodestreamParserTests.FindRepoFile(
            "test-pdfs",
            "altona",
            "eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf");
        Assert.SkipWhen(path == null,
            "No Altona PDF/X fixture found at test-pdfs/altona/eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf.");

        using var doc = PdfDocument.Open(path);
        var imageStream = (PdfStream)doc.GetObject(objectNumber);

        var image = JpxDecoder.TryDecodeManaged(imageStream.EncodedData, maxComponents: 2)
                    ?? JpxDecoder.TryDecodeOpenJpegGray(imageStream.EncodedData);

        Assert.SkipWhen(image == null,
            "Neither managed JPX nor optional opj_decompress could decode the Altona grayscale JPX fixture.");
        image!.BitsPerComponent.Should().Be(bitsPerComponent);
        image.ComponentData.Should().ContainSingle(
            "single-component grayscale JPX images should not expose a bogus alpha component");
        image.ComponentData[0].Should().HaveCount(424 * 212);
        image.ComponentData[0].Should().Contain(sample => sample < (bitsPerComponent == 16 ? 16_384 : 64));
        image.ComponentData[0].Should().Contain(sample => sample > (bitsPerComponent == 16 ? 49_152 : 180));
    }
}
