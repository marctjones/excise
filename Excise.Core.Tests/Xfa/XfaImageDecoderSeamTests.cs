using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #1575: the optional <see cref="IXfaImageDecoder"/> seam. Without a decoder, non-JPEG images
/// are reported exactly as before; with one, the header's pixel size is checked against
/// <see cref="XfaImage.MaxPixels"/> before a single pixel is decoded. What the decoded image
/// looks like is checked with mutool in Excise.App.Tests (XfaImageDecoderTests).
/// </summary>
public class XfaImageDecoderSeamTests
{
    private sealed class StubDecoder((int, int)? size) : IXfaImageDecoder
    {
        public int Decodes { get; private set; }

        public (int Width, int Height)? ReadSize(byte[] bytes) => size;

        public XfaDecodedImage? Decode(byte[] bytes)
        {
            Decodes++;
            return new XfaDecodedImage(2, 1, new byte[] { 255, 0, 0, 255, 0, 0 }, null);
        }
    }

    private static readonly byte[] Png = { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0 };

    private static XfaLayoutResult LayOut(IXfaImageDecoder? decoder)
    {
        var body = "<draw name=\"Logo\" x=\"1in\" y=\"1in\" w=\"1in\" h=\"1in\"><value>"
            + $"<image contentType=\"image/png\">{Convert.ToBase64String(Png)}</image></value></draw>";
        using var document = PdfDocument.Open(XfaTestForms.BuildPdf(XfaTestForms.Template(body, layout: "position")));
        var result = document.ApplyXfaLayout(
            new XfaLayoutOptions { ImageDecoder = decoder }, TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        return result;
    }

    [Fact]
    public void WithoutADecoder_NonJpegIsReportedAsBefore()
    {
        LayOut(null).Omissions.Should().Contain("PNG images not drawn (only JPEG is)");
    }

    [Fact]
    public void WithADecoder_TheImageIsDrawnAndNotReported()
    {
        var decoder = new StubDecoder((2, 1));
        LayOut(decoder).Omissions.Should().NotContain(n => n.Contains("PNG", StringComparison.Ordinal));
        decoder.Decodes.Should().Be(1);
    }

    [Fact]
    public void AnOversizedHeader_IsRefusedBeforeDecoding()
    {
        var decoder = new StubDecoder((10_000, 5_001));
        LayOut(decoder).Omissions.Should().Contain("images over 50 megapixels not drawn");
        decoder.Decodes.Should().Be(0, "the pixel cap is checked on the header, before any pixel is decoded");
    }

    [Fact]
    public void AnUnreadableHeader_IsReportedByFormat()
    {
        var decoder = new StubDecoder(null);
        LayOut(decoder).Omissions.Should().Contain("PNG images that could not be decoded not drawn");
        decoder.Decodes.Should().Be(0);
    }
}
