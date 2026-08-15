using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Rendering.Tests;

public sealed class ImageColorConverterTests
{
    [Fact]
    public void GetImageColorConverter_DeviceGray_UsesExactByteTable()
    {
        var converter = RenderContext.GetImageColorConverterForTests(PdfColorSpace.DeviceGray);

        converter.Should().NotBeNull();
        converter!.Strategy.Should().Be("OneComponentExactByteTable");
        converter.ToRgb([0.5]).Should().Be(((byte)128, (byte)128, (byte)128));
    }

    [Fact]
    public void GetImageColorConverter_DeviceRgb_UsesContinuous3DLattice()
    {
        var converter = RenderContext.GetImageColorConverterForTests(PdfColorSpace.DeviceRGB);

        converter.Should().NotBeNull();
        converter!.Strategy.Should().Be("Continuous3DLattice");
        converter.ToRgb([1.0, 0.0, 0.5]).Should().Be(((byte)255, (byte)0, (byte)127));
    }

    [Fact]
    public void GetImageColorConverter_DeviceCmyk_UsesContinuous4DLattice()
    {
        var converter = RenderContext.GetImageColorConverterForTests(PdfColorSpace.DeviceCMYK);

        converter.Should().NotBeNull();
        converter!.Strategy.Should().Be("Continuous4DLattice");
        var rgb = converter.ToRgb([0.0, 1.0, 1.0, 0.0]);
        rgb.R.Should().BeGreaterThan(180);
        rgb.G.Should().BeLessThan(80);
        rgb.B.Should().BeLessThan(80);
    }

    [Fact]
    public void GetImageColorConverter_Indexed_UsesExactPaletteTable()
    {
        using var doc = PdfDocument.Open(CreateMinimalPdf());
        var colorSpace = PdfColorSpace.Parse(
            new PdfArray(
                new PdfName("Indexed"),
                new PdfName("DeviceRGB"),
                new PdfInteger(1),
                new PdfString(new byte[] { 0, 0, 255, 255, 0, 0 })),
            doc);

        var converter = RenderContext.GetImageColorConverterForTests(colorSpace);

        converter.Should().NotBeNull();
        converter!.Strategy.Should().Be("IndexedExactByteTable");
        converter.ToRgb(0).Should().Be(((byte)0, (byte)0, (byte)255));
        converter.ToRgb(1).Should().Be(((byte)255, (byte)0, (byte)0));
    }

    [Fact]
    public void GetImageColorConverter_Lab_LeavesDirectPathInPlace()
    {
        using var doc = PdfDocument.Open(CreateMinimalPdf());
        var lab = PdfColorSpace.Parse(
            new PdfArray(
                new PdfName("Lab"),
                new PdfDictionary
                {
                    [new PdfName("WhitePoint")] = new PdfArray(new PdfInteger(1), new PdfInteger(1), new PdfInteger(1))
                }),
            doc);

        RenderContext.GetImageColorConverterForTests(lab).Should().BeNull();
    }

    [Fact]
    public void GetImageColorConverter_ReusesConverterForColorSpaceInstance()
    {
        var first = RenderContext.GetImageColorConverterForTests(PdfColorSpace.DeviceRGB);
        var second = RenderContext.GetImageColorConverterForTests(PdfColorSpace.DeviceRGB);

        second.Should().BeSameAs(first);
    }

    private static byte[] CreateMinimalPdf()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        var o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1 1] >>\nendobj\n");
        var xrefPos = sb.Length;
        sb.Append("xref\n0 4\n");
        sb.Append("0000000000 65535 f \n");
        sb.Append($"{o1:D10} 00000 n \n");
        sb.Append($"{o2:D10} 00000 n \n");
        sb.Append($"{o3:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xrefPos}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
