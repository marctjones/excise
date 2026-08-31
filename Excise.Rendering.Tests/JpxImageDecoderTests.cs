using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using Xunit;

namespace Excise.Rendering.Tests;

public sealed class JpxImageDecoderTests
{
    [Fact]
    public void MalformedPayloadIsRefusedWithoutAContext()
    {
        JpxImageDecoder.Decode(new JpxImageDecodeRequest(
            Bytes: new byte[] { 1, 2, 3, 4 },
            SourceWidth: 1,
            SourceHeight: 1,
            TargetWidth: 1,
            TargetHeight: 1,
            ColorSpace: PdfColorSpace.DeviceRGB,
            HasExternalSoftMask: false,
            MaximumPixels: 1024,
            CancellationToken: default)).Should().BeNull();
    }

    [Fact]
    public void CancellationIsPropagatedInsteadOfReportedAsMalformedData()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => JpxImageDecoder.Decode(new JpxImageDecodeRequest(
            Bytes: new byte[] { 1, 2, 3, 4 },
            SourceWidth: 1,
            SourceHeight: 1,
            TargetWidth: 1,
            TargetHeight: 1,
            ColorSpace: PdfColorSpace.DeviceRGB,
            HasExternalSoftMask: false,
            MaximumPixels: 1024,
            CancellationToken: cancellation.Token));

        act.Should().Throw<OperationCanceledException>();
    }
}
