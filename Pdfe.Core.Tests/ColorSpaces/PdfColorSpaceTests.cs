using System.Text;
using AwesomeAssertions;
using Pdfe.Core.ColorSpaces;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.ColorSpaces;

public class PdfColorSpaceTests
{
    private static PdfDocument CreateMinimalPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        long xrefPos = sb.Length;
        sb.Append("xref\n0 4\n");
        sb.Append("0000000000 65535 f \n");
        sb.Append($"{o1:D10} 00000 n \n");
        sb.Append($"{o2:D10} 00000 n \n");
        sb.Append($"{o3:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xrefPos}\n%%EOF\n");
        var bytes = Encoding.Latin1.GetBytes(sb.ToString());
        return PdfDocument.Open(new MemoryStream(bytes), ownsStream: false);
    }

    [Fact]
    public void DeviceRgb_ToRgb_IdentityMapping()
    {
        var cs = PdfColorSpace.DeviceRGB;
        var (r, g, b) = cs.ToRgb(new[] { 0.5, 0.5, 0.5 });
        r.Should().Be(0.5);
        g.Should().Be(0.5);
        b.Should().Be(0.5);
    }

    [Fact]
    public void DeviceCmyk_Black_ConvertsToBlack()
    {
        var cs = PdfColorSpace.DeviceCMYK;
        var (r, g, b) = cs.ToRgb(new[] { 0.0, 0.0, 0.0, 1.0 });
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void DeviceCmyk_White_ConvertsToWhite()
    {
        var cs = PdfColorSpace.DeviceCMYK;
        var (r, g, b) = cs.ToRgb(new[] { 0.0, 0.0, 0.0, 0.0 });
        r.Should().Be(1.0);
        g.Should().Be(1.0);
        b.Should().Be(1.0);
    }

    [Fact]
    public void DeviceCmyk_Yellow_ConvertsCorrectly()
    {
        var cs = PdfColorSpace.DeviceCMYK;
        var (r, g, b) = cs.ToRgb(new[] { 0.0, 0.0, 1.0, 0.0 });
        r.Should().BeApproximately(1.0, 0.01);
        g.Should().BeApproximately(1.0, 0.01);
        b.Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public void DeviceGray_Half_ConvertsToDarkGray()
    {
        var cs = PdfColorSpace.DeviceGray;
        var (r, g, b) = cs.ToRgb(new[] { 0.5 });
        r.Should().Be(0.5);
        g.Should().Be(0.5);
        b.Should().Be(0.5);
    }

    [Fact]
    public void FromName_DeviceRgb_ReturnsRgbColorSpace()
    {
        var cs = PdfColorSpace.FromName("DeviceRGB");
        cs.Type.Should().Be(PdfColorSpaceType.DeviceRGB);
        cs.Components.Should().Be(3);
    }

    [Fact]
    public void FromName_DeviceGray_ReturnsGrayColorSpace()
    {
        var cs = PdfColorSpace.FromName("DeviceGray");
        cs.Type.Should().Be(PdfColorSpaceType.DeviceGray);
        cs.Components.Should().Be(1);
    }

    [Fact]
    public void FromName_DeviceCmyk_ReturnsCmykColorSpace()
    {
        var cs = PdfColorSpace.FromName("DeviceCMYK");
        cs.Type.Should().Be(PdfColorSpaceType.DeviceCMYK);
        cs.Components.Should().Be(4);
    }

    [Fact]
    public void FromName_UnknownName_ReturnsUnknownColorSpace()
    {
        var cs = PdfColorSpace.FromName("UnknownColorSpace");
        cs.Type.Should().Be(PdfColorSpaceType.Unknown);
        cs.Components.Should().Be(1);
    }

    [Fact]
    public void DeviceGray_Zero_ConvertsToBlack()
    {
        var cs = PdfColorSpace.DeviceGray;
        var (r, g, b) = cs.ToRgb(new[] { 0.0 });
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void DeviceGray_One_ConvertsToWhite()
    {
        var cs = PdfColorSpace.DeviceGray;
        var (r, g, b) = cs.ToRgb(new[] { 1.0 });
        r.Should().Be(1.0);
        g.Should().Be(1.0);
        b.Should().Be(1.0);
    }

    [Fact]
    public void ToRgb_EmptyArray_ReturnsBlack()
    {
        var cs = PdfColorSpace.DeviceRGB;
        var (r, g, b) = cs.ToRgb(Array.Empty<double>());
        r.Should().Be(0);
        g.Should().Be(0);
        b.Should().Be(0);
    }

    [Fact]
    public void ToRgb_InsufficientComponents_UsesAvailable()
    {
        var cs = PdfColorSpace.DeviceRGB;
        var (r, g, b) = cs.ToRgb(new[] { 0.8 });
        r.Should().Be(0.8);
        g.Should().Be(0.8);
        b.Should().Be(0.8);
    }

    [Fact]
    public void CalGray_ToRgb_ConvertsLikeDeviceGray()
    {
        var calGray = PdfColorSpace.FromName("CalGray");
        calGray.Type.Should().Be(PdfColorSpaceType.CalGray);
        var (r, g, b) = calGray.ToRgb(new[] { 0.75 });
        r.Should().Be(0.75);
        g.Should().Be(0.75);
        b.Should().Be(0.75);
    }

    [Fact]
    public void CalGray_ParseArray_AppliesGamma()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(
            new PdfName("CalGray"),
            new PdfDictionary
            {
                ["WhitePoint"] = new PdfArray(new PdfReal(1), new PdfReal(1), new PdfReal(1)),
                ["Gamma"] = new PdfReal(2.0)
            });

        var cs = PdfColorSpace.Parse(arr, doc);
        var (r, g, b) = cs.ToRgb(new[] { 0.5 });

        r.Should().BeApproximately(0.5371, 0.0001);
        g.Should().BeApproximately(0.5371, 0.0001);
        b.Should().BeApproximately(0.5371, 0.0001);
    }

    [Fact]
    public void FromName_Separation_ReturnsSeparationColorSpace()
    {
        var cs = PdfColorSpace.FromName("Separation");
        cs.Type.Should().Be(PdfColorSpaceType.Separation);
        cs.Components.Should().Be(1);
    }

    [Fact]
    public void DeviceRgb_ExtraComponents_UsesFirst3()
    {
        var cs = PdfColorSpace.DeviceRGB;
        var (r, g, b) = cs.ToRgb(new[] { 0.2, 0.4, 0.6, 0.8, 1.0 });
        r.Should().Be(0.2);
        g.Should().Be(0.4);
        b.Should().Be(0.6);
    }

    [Fact]
    public void Separation_ToRgb_ApproximatesAsGrayscaleTint()
    {
        var sep = PdfColorSpace.FromName("Separation");
        sep.Type.Should().Be(PdfColorSpaceType.Separation);
        sep.Components.Should().Be(1);

        var (r, g, b) = sep.ToRgb(new[] { 0.5 });
        r.Should().Be(0.5);
        g.Should().Be(0.5);
        b.Should().Be(0.5);
    }

    [Fact]
    public void Separation_ZeroTint_FullColor()
    {
        var sep = PdfColorSpace.FromName("Separation");
        var (r, g, b) = sep.ToRgb(new[] { 0.0 });
        r.Should().Be(1.0);
        g.Should().Be(1.0);
        b.Should().Be(1.0);
    }

    [Fact]
    public void Separation_FullTint_Black()
    {
        var sep = PdfColorSpace.FromName("Separation");
        var (r, g, b) = sep.ToRgb(new[] { 1.0 });
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void DeviceN_Parse_MultipleComponents()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(
            new PdfName("DeviceN"),
            new PdfArray(new PdfName("Cyan"), new PdfName("Magenta")),
            new PdfName("DeviceRGB"),
            new PdfStream()
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.DeviceN);
        cs.Components.Should().Be(2);
    }

    [Fact]
    public void Lab_FromName_ThreeComponents()
    {
        var lab = PdfColorSpace.FromName("Lab");
        lab.Type.Should().Be(PdfColorSpaceType.Lab);
        lab.Components.Should().Be(3);
    }

    [Fact]
    public void Pattern_FromName_ZeroComponents()
    {
        var pattern = PdfColorSpace.FromName("Pattern");
        pattern.Type.Should().Be(PdfColorSpaceType.Pattern);
        pattern.Components.Should().Be(0);
    }

    [Fact]
    public void Lab_ToRgb_Mid_GrayValue()
    {
        var lab = PdfColorSpace.FromName("Lab");
        var (r, g, b) = lab.ToRgb(new[] { 50.0, 0.0, 0.0 });
        r.Should().BeGreaterThan(0);
        g.Should().BeGreaterThan(0);
        b.Should().BeGreaterThan(0);
        r.Should().BeLessThan(1);
        g.Should().BeLessThan(1);
        b.Should().BeLessThan(1);
    }

    [Fact]
    public void Lab_ToRgb_Low_AlmostBlack()
    {
        var lab = PdfColorSpace.FromName("Lab");
        var (r, g, b) = lab.ToRgb(new[] { 10.0, 0.0, 0.0 });
        r.Should().BeLessThan(0.1);
        g.Should().BeLessThan(0.1);
        b.Should().BeLessThan(0.1);
    }

    [Fact]
    public void Lab_ToRgb_High_AlmostWhite()
    {
        var lab = PdfColorSpace.FromName("Lab");
        var (r, g, b) = lab.ToRgb(new[] { 95.0, 0.0, 0.0 });
        r.Should().BeGreaterThan(0.85, "L*=95 neutral should produce a near-white RGB triple");
        g.Should().BeGreaterThan(0.85);
        b.Should().BeGreaterThan(0.85);
    }

    [Fact]
    public void ICCBased_N4_CMYK_ReturnsCMYKColorSpace()
    {
        using var doc = CreateMinimalPdf();
        var iccDict = new PdfDictionary();
        iccDict[new PdfName("N")] = new PdfInteger(4);
        var iccStream = new PdfStream(iccDict, Array.Empty<byte>());

        var arr = new PdfArray(new PdfName("ICCBased"), iccStream);
        var cs = PdfColorSpace.Parse(arr, doc);
        // Parser maps ICCBased N=4 to DeviceCMYK proxy for rendering compatibility
        cs.Type.Should().Be(PdfColorSpaceType.DeviceCMYK);
        cs.Components.Should().Be(4);
    }

    [Fact]
    public void ICCBased_N4_CMYK_ToRgb_Black()
    {
        using var doc = CreateMinimalPdf();
        var iccDict = new PdfDictionary();
        iccDict[new PdfName("N")] = new PdfInteger(4);
        var iccStream = new PdfStream(iccDict, Array.Empty<byte>());

        var arr = new PdfArray(new PdfName("ICCBased"), iccStream);
        var cs = PdfColorSpace.Parse(arr, doc);

        var (r, g, b) = cs.ToRgb(new[] { 0.0, 0.0, 0.0, 1.0 });
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void ICCBased_N4_CMYK_ToRgb_White()
    {
        using var doc = CreateMinimalPdf();
        var iccDict = new PdfDictionary();
        iccDict[new PdfName("N")] = new PdfInteger(4);
        var iccStream = new PdfStream(iccDict, Array.Empty<byte>());

        var arr = new PdfArray(new PdfName("ICCBased"), iccStream);
        var cs = PdfColorSpace.Parse(arr, doc);

        var (r, g, b) = cs.ToRgb(new[] { 0.0, 0.0, 0.0, 0.0 });
        r.Should().Be(1.0);
        g.Should().Be(1.0);
        b.Should().Be(1.0);
    }

    [Fact]
    public void ICCBased_N1_Gray()
    {
        using var doc = CreateMinimalPdf();
        var iccDict = new PdfDictionary();
        iccDict[new PdfName("N")] = new PdfInteger(1);
        var iccStream = new PdfStream(iccDict, Array.Empty<byte>());

        var arr = new PdfArray(new PdfName("ICCBased"), iccStream);
        var cs = PdfColorSpace.Parse(arr, doc);
        // Parser maps ICCBased N=1 to DeviceGray proxy
        cs.Type.Should().Be(PdfColorSpaceType.DeviceGray);
        cs.Components.Should().Be(1);
    }

    [Fact]
    public void Indexed_LookupTable_BlueAndRed()
    {
        using var doc = CreateMinimalPdf();
        byte[] lookupData = new byte[] { 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00 };
        var lookupString = new PdfString(System.Text.Encoding.Latin1.GetString(lookupData));

        var arr = new PdfArray(
            new PdfName("Indexed"),
            new PdfName("DeviceRGB"),
            new PdfInteger(1),
            lookupString
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.Indexed);
        cs.Components.Should().Be(1);

        var (r0, g0, b0) = cs.LookupIndexed(0);
        r0.Should().BeApproximately(0.0, 0.01);
        g0.Should().BeApproximately(0.0, 0.01);
        b0.Should().BeApproximately(1.0, 0.01);

        var (r1, g1, b1) = cs.LookupIndexed(1);
        r1.Should().BeApproximately(1.0, 0.01);
        g1.Should().BeApproximately(0.0, 0.01);
        b1.Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public void Indexed_ToRgb_UsesLookup()
    {
        using var doc = CreateMinimalPdf();
        byte[] lookupData = new byte[] { 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00 };
        var lookupString = new PdfString(System.Text.Encoding.Latin1.GetString(lookupData));

        var arr = new PdfArray(
            new PdfName("Indexed"),
            new PdfName("DeviceRGB"),
            new PdfInteger(1),
            lookupString
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        var (r, g, b) = cs.ToRgb(new[] { 1.0 });
        r.Should().BeApproximately(1.0, 0.01);
        g.Should().BeApproximately(0.0, 0.01);
        b.Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public void Indexed_OutOfBounds_ClampsToAvailableLookupRange()
    {
        using var doc = CreateMinimalPdf();
        byte[] lookupData = new byte[]
        {
            0x00, 0x00, 0xFF,
            0xFF, 0x00, 0x00,
        };
        var lookupString = new PdfString(System.Text.Encoding.Latin1.GetString(lookupData));

        var arr = new PdfArray(
            new PdfName("Indexed"),
            new PdfName("DeviceRGB"),
            new PdfInteger(1),
            lookupString
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        var (r, g, b) = cs.LookupIndexed(-17);
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().BeApproximately(1.0, 0.01);

        (r, g, b) = cs.LookupIndexed(999);
        r.Should().BeApproximately(1.0, 0.01);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void Pattern_FromArray_ZeroComponents()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(new PdfName("Pattern"));
        var pattern = PdfColorSpace.Parse(arr, doc);
        pattern.Type.Should().Be(PdfColorSpaceType.Pattern);
        pattern.Components.Should().Be(0);
    }

    [Fact]
    public void Unknown_FromName_Returns1Component()
    {
        var unknown = PdfColorSpace.FromName("Bogus");
        unknown.Type.Should().Be(PdfColorSpaceType.Unknown);
        unknown.Components.Should().Be(1);
    }

    [Fact]
    public void Parse_DirectName_CallsFromName()
    {
        using var doc = CreateMinimalPdf();
        var cs = PdfColorSpace.Parse(new PdfName("Lab"), doc);
        cs.Type.Should().Be(PdfColorSpaceType.Lab);
    }

    [Fact]
    public void Parse_ArrayUnknownType_ReturnsUnknown()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(new PdfName("UnknownCS"));
        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.Unknown);
    }

    [Fact]
    public void CMYK_Magenta()
    {
        var cs = PdfColorSpace.DeviceCMYK;
        var (r, g, b) = cs.ToRgb(new[] { 0.0, 1.0, 0.0, 0.0 });
        r.Should().BeApproximately(1.0, 0.01);
        g.Should().BeApproximately(0.0, 0.01);
        b.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void CMYK_InsufficientComponents_ReturnsBlack()
    {
        var cs = PdfColorSpace.DeviceCMYK;
        var (r, g, b) = cs.ToRgb(new[] { 0.5, 0.5 });
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void ToRgb_Lab_ConvertsToRgb()
    {
        var lab = PdfColorSpace.FromName("Lab");
        lab.Type.Should().Be(PdfColorSpaceType.Lab);
        var (r, g, b) = lab.ToRgb(new[] { 50.0, 0.0, 0.0 }); // Middle gray in Lab (converted to double)
        r.Should().BeGreaterThanOrEqualTo(0);
        r.Should().BeLessThanOrEqualTo(1);
        g.Should().BeGreaterThanOrEqualTo(0);
        g.Should().BeLessThanOrEqualTo(1);
        b.Should().BeGreaterThanOrEqualTo(0);
        b.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void DeviceCmyk_MixedColor_ConvertsCorrectly()
    {
        var cs = PdfColorSpace.DeviceCMYK;
        var (r, g, b) = cs.ToRgb(new[] { 0.5, 0.25, 0.75, 0.1 });
        r.Should().BeGreaterThan(0);
        r.Should().BeLessThan(1);
        g.Should().BeGreaterThan(0);
        g.Should().BeLessThan(1);
        b.Should().BeGreaterThan(0);
        b.Should().BeLessThan(1);
    }

    [Fact]
    public void FromName_CalRGB_ReturnsCalRGBColorSpace()
    {
        var cs = PdfColorSpace.FromName("CalRGB");
        cs.Type.Should().Be(PdfColorSpaceType.CalRGB);
        cs.Components.Should().Be(3);
    }

    [Fact]
    public void FromName_UnknownName_ReturnsUnknown()
    {
        var cs = PdfColorSpace.FromName("UnknownSpace");
        cs.Type.Should().Be(PdfColorSpaceType.Unknown);
    }

    [Fact]
    public void FromName_G_ShorthandForDeviceGray()
    {
        var cs = PdfColorSpace.FromName("G");
        cs.Type.Should().Be(PdfColorSpaceType.DeviceGray);
        cs.Components.Should().Be(1);
    }

    [Fact]
    public void FromName_RGB_ShorthandForDeviceRGB()
    {
        var cs = PdfColorSpace.FromName("RGB");
        cs.Type.Should().Be(PdfColorSpaceType.DeviceRGB);
        cs.Components.Should().Be(3);
    }

    [Fact]
    public void FromName_CMYK_ShorthandForDeviceCMYK()
    {
        var cs = PdfColorSpace.FromName("CMYK");
        cs.Type.Should().Be(PdfColorSpaceType.DeviceCMYK);
        cs.Components.Should().Be(4);
    }

    [Fact]
    public void CalRGB_ToRgb_Works()
    {
        var calRGB = PdfColorSpace.FromName("CalRGB");
        calRGB.Type.Should().Be(PdfColorSpaceType.CalRGB);
        var (r, g, b) = calRGB.ToRgb(new[] { 0.5, 0.5, 0.5 });
        r.Should().Be(0.5);
        g.Should().Be(0.5);
        b.Should().Be(0.5);
    }

    [Fact]
    public void CalRGB_InsufficientComponents_UsesAvailable()
    {
        var calRGB = PdfColorSpace.FromName("CalRGB");
        var (r, g, b) = calRGB.ToRgb(new[] { 0.3 });
        r.Should().Be(0.3);
        g.Should().Be(0.3);
        b.Should().Be(0.3);
    }

    [Fact]
    public void DeviceRGB_InsufficientComponents_UsesFirstComponent()
    {
        var cs = PdfColorSpace.DeviceRGB;
        var (r, g, b) = cs.ToRgb(new[] { 0.6, 0.7 });
        // With only 2 components, it falls back to using first component for all three
        r.Should().Be(0.6);
        g.Should().Be(0.6);
        b.Should().Be(0.6);
    }

    [Fact]
    public void DeviceCMYK_InsufficientComponents_ReturnsBlack()
    {
        var cs = PdfColorSpace.DeviceCMYK;
        var (r, g, b) = cs.ToRgb(new[] { 0.5, 0.5, 0.5 }); // Missing K
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void Lab_InsufficientComponents_ReturnsBlack()
    {
        var lab = PdfColorSpace.FromName("Lab");
        var (r, g, b) = lab.ToRgb(new[] { 50.0, 10.0 }); // Missing b
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void Separation_InsufficientComponents_ReturnsBlack()
    {
        var sep = PdfColorSpace.FromName("Separation");
        var (r, g, b) = sep.ToRgb(Array.Empty<double>());
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void Indexed_InsufficientComponents_ReturnsBlack()
    {
        using var doc = CreateMinimalPdf();
        byte[] lookupData = new byte[] { 0xFF, 0x00, 0x00 };
        var lookupString = new PdfString(System.Text.Encoding.Latin1.GetString(lookupData));

        var arr = new PdfArray(
            new PdfName("Indexed"),
            new PdfName("DeviceRGB"),
            new PdfInteger(0),
            lookupString
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        var (r, g, b) = cs.ToRgb(Array.Empty<double>());
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void ICCBased_MissingArray_ReturnsDeviceRGB()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(new PdfName("ICCBased"));
        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.DeviceRGB);
    }

    [Fact]
    public void ICCBased_MissingStream_ReturnsDeviceRGB()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(new PdfName("ICCBased"), new PdfName("NotAStream"));
        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.DeviceRGB);
    }

    [Fact]
    public void ICCBased_N3_RGB()
    {
        using var doc = CreateMinimalPdf();
        var iccDict = new PdfDictionary();
        iccDict[new PdfName("N")] = new PdfInteger(3);
        var iccStream = new PdfStream(iccDict, Array.Empty<byte>());

        var arr = new PdfArray(new PdfName("ICCBased"), iccStream);
        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.DeviceRGB);
        cs.Components.Should().Be(3);
    }

    [Fact]
    public void ICCBased_N3_ToRgb()
    {
        using var doc = CreateMinimalPdf();
        var iccDict = new PdfDictionary();
        iccDict[new PdfName("N")] = new PdfInteger(3);
        var iccStream = new PdfStream(iccDict, Array.Empty<byte>());

        var arr = new PdfArray(new PdfName("ICCBased"), iccStream);
        var cs = PdfColorSpace.Parse(arr, doc);
        var (r, g, b) = cs.ToRgb(new[] { 0.2, 0.4, 0.8 });
        r.Should().Be(0.2);
        g.Should().Be(0.4);
        b.Should().Be(0.8);
    }

    [Fact]
    public void ICCBased_N3_InsufficientComponents()
    {
        using var doc = CreateMinimalPdf();
        var iccDict = new PdfDictionary();
        iccDict[new PdfName("N")] = new PdfInteger(3);
        var iccStream = new PdfStream(iccDict, Array.Empty<byte>());

        var arr = new PdfArray(new PdfName("ICCBased"), iccStream);
        var cs = PdfColorSpace.Parse(arr, doc);
        var (r, g, b) = cs.ToRgb(new[] { 0.5 });
        r.Should().Be(0.5);
        g.Should().Be(0.5);
        b.Should().Be(0.5);
    }

    [Fact]
    public void Indexed_WithStreamLookup_DecodesCorrectly()
    {
        using var doc = CreateMinimalPdf();
        byte[] lookupData = new byte[] { 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF };
        var lookupStream = new PdfStream(new PdfDictionary(), lookupData);

        var arr = new PdfArray(
            new PdfName("Indexed"),
            new PdfName("DeviceRGB"),
            new PdfInteger(1),
            lookupStream
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.Indexed);

        var (r0, g0, b0) = cs.LookupIndexed(0);
        r0.Should().BeApproximately(0.0, 0.01);
        g0.Should().BeApproximately(1.0, 0.01);
        b0.Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public void Indexed_MissingLookup_ReturnsBlack()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(
            new PdfName("Indexed"),
            new PdfName("DeviceRGB"),
            new PdfInteger(0),
            new PdfName("NoString")
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        var (r, g, b) = cs.LookupIndexed(0);
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void Indexed_TooFewArrayElements_ReturnsDeviceRGB()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(
            new PdfName("Indexed"),
            new PdfName("DeviceRGB")
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.DeviceRGB);
    }

    [Fact]
    public void DeviceN_SingleComponent()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(
            new PdfName("DeviceN"),
            new PdfArray()
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.DeviceN);
        cs.Components.Should().Be(0);
    }

    [Fact]
    public void DeviceN_NoNameArray_DefaultsToOne()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(new PdfName("DeviceN"));

        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.DeviceN);
        cs.Components.Should().Be(1);
    }

    [Fact]
    public void DeviceN_ToRgb()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(
            new PdfName("DeviceN"),
            new PdfArray(new PdfName("Cyan"), new PdfName("Magenta"), new PdfName("Yellow")),
            new PdfName("DeviceRGB"),
            new PdfStream()
        );

        var cs = PdfColorSpace.Parse(arr, doc);
        var (r, g, b) = cs.ToRgb(new[] { 0.5, 0.5, 0.5 });
        // Unknown type defaults to RGB pass-through
        r.Should().Be(0.5);
        g.Should().Be(0.5);
        b.Should().Be(0.5);
    }

    [Fact]
    public void Parse_NonArrayNonName_ReturnsDeviceRGB()
    {
        using var doc = CreateMinimalPdf();
        var cs = PdfColorSpace.Parse(new PdfReal(123), doc);
        cs.Type.Should().Be(PdfColorSpaceType.DeviceRGB);
    }

    [Fact]
    public void Unknown_ToRgb_WithThreeComponents()
    {
        var cs = PdfColorSpace.FromName("UnknownSpace");
        var (r, g, b) = cs.ToRgb(new[] { 0.1, 0.2, 0.3 });
        r.Should().Be(0.1);
        g.Should().Be(0.2);
        b.Should().Be(0.3);
    }

    [Fact]
    public void Unknown_ToRgb_WithOneComponent()
    {
        var cs = PdfColorSpace.FromName("UnknownSpace");
        var (r, g, b) = cs.ToRgb(new[] { 0.7 });
        r.Should().Be(0.7);
        g.Should().Be(0.7);
        b.Should().Be(0.7);
    }

    [Fact]
    public void Unknown_ToRgb_WithNoComponents()
    {
        var cs = PdfColorSpace.FromName("UnknownSpace");
        var (r, g, b) = cs.ToRgb(Array.Empty<double>());
        r.Should().Be(0.0);
        g.Should().Be(0.0);
        b.Should().Be(0.0);
    }

    [Fact]
    public void Lab_LabToRgb_BoundaryConditions()
    {
        var lab = PdfColorSpace.FromName("Lab");
        // Test L=0 (black)
        var (r1, g1, b1) = lab.ToRgb(new[] { 0.0, 0.0, 0.0 });
        r1.Should().BeGreaterThanOrEqualTo(0);
        g1.Should().BeGreaterThanOrEqualTo(0);
        b1.Should().BeGreaterThanOrEqualTo(0);
        // Test L=100 (white)
        var (r2, g2, b2) = lab.ToRgb(new[] { 100.0, 0.0, 0.0 });
        r2.Should().BeLessThanOrEqualTo(1);
        g2.Should().BeLessThanOrEqualTo(1);
        b2.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Lab_LabToRgb_WithColorComponent()
    {
        var lab = PdfColorSpace.FromName("Lab");
        var (r, g, b) = lab.ToRgb(new[] { 50.0, 50.0, 50.0 });
        // Should produce valid RGB
        r.Should().BeGreaterThanOrEqualTo(0).And.BeLessThanOrEqualTo(1);
        g.Should().BeGreaterThanOrEqualTo(0).And.BeLessThanOrEqualTo(1);
        b.Should().BeGreaterThanOrEqualTo(0).And.BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Fcube_BelowThreshold()
    {
        var lab = PdfColorSpace.FromName("Lab");
        // t < 0.206897: Fcube(t) = (t - 16/116) / 7.787
        var (r, g, b) = lab.ToRgb(new[] { 10.0, 0.0, 0.0 }); // Low L*
        r.Should().BeLessThan(0.15);
        g.Should().BeLessThan(0.15);
        b.Should().BeLessThan(0.15);
    }

    [Fact]
    public void CalRGB_FromArray_ThreeComponents()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(new PdfName("CalRGB"));
        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.CalRGB);
        cs.Components.Should().Be(3);

        var (r, g, b) = cs.ToRgb(new[] { 0.5, 0.25, 0.75 });
        r.Should().Be(0.5);
        g.Should().Be(0.25);
        b.Should().Be(0.75);
    }

    [Fact]
    public void CalRGB_ParseArray_AppliesGammaAndMatrix()
    {
        using var doc = CreateMinimalPdf();
        var gamma1 = new PdfArray(
            new PdfName("CalRGB"),
            new PdfDictionary
            {
                ["WhitePoint"] = new PdfArray(new PdfReal(1), new PdfReal(1), new PdfReal(1)),
                ["Gamma"] = new PdfArray(new PdfReal(1), new PdfReal(1), new PdfReal(1)),
                ["Matrix"] = new PdfArray(
                    new PdfReal(1), new PdfReal(0), new PdfReal(0),
                    new PdfReal(0), new PdfReal(1), new PdfReal(0),
                    new PdfReal(0), new PdfReal(0), new PdfReal(1))
            });
        var gamma2 = new PdfArray(
            new PdfName("CalRGB"),
            new PdfDictionary
            {
                ["WhitePoint"] = new PdfArray(new PdfReal(1), new PdfReal(1), new PdfReal(1)),
                ["Gamma"] = new PdfArray(new PdfReal(2), new PdfReal(1), new PdfReal(1)),
                ["Matrix"] = new PdfArray(
                    new PdfReal(1), new PdfReal(0), new PdfReal(0),
                    new PdfReal(0), new PdfReal(1), new PdfReal(0),
                    new PdfReal(0), new PdfReal(0), new PdfReal(1))
            });

        var gamma1Cs = PdfColorSpace.Parse(gamma1, doc);
        var gamma2Cs = PdfColorSpace.Parse(gamma2, doc);
        var (gamma1R, _, _) = gamma1Cs.ToRgb(new[] { 0.5, 0.0, 0.0 });
        var (gamma2R, _, _) = gamma2Cs.ToRgb(new[] { 0.5, 0.0, 0.0 });

        gamma2R.Should().BeLessThan(gamma1R, "gamma 2 darkens the first component before XYZ-to-sRGB conversion");
    }

    [Fact]
    public void CalRGB_ParseArray_AdaptsSourceWhitePointToDisplayWhite()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(
            new PdfName("CalRGB"),
            new PdfDictionary
            {
                ["WhitePoint"] = new PdfArray(new PdfReal(0.2), new PdfReal(1), new PdfReal(0.2)),
                ["Gamma"] = new PdfArray(new PdfReal(1), new PdfReal(1), new PdfReal(1)),
                ["Matrix"] = new PdfArray(
                    new PdfReal(1), new PdfReal(0), new PdfReal(0),
                    new PdfReal(0), new PdfReal(1), new PdfReal(0),
                    new PdfReal(0), new PdfReal(0), new PdfReal(1))
            });

        var cs = PdfColorSpace.Parse(arr, doc);
        var (r, g, b) = cs.ToRgb(new[] { 1.0, 1.0, 1.0 });

        r.Should().BeApproximately(1.0, 0.0001);
        g.Should().BeApproximately(1.0, 0.0001);
        b.Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public void Lab_FromArray_ThreeComponents()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(new PdfName("Lab"));
        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.Lab);
        cs.Components.Should().Be(3);
    }

    [Fact]
    public void Separation_FromArray_OneComponent()
    {
        using var doc = CreateMinimalPdf();
        var arr = new PdfArray(new PdfName("Separation"));
        var cs = PdfColorSpace.Parse(arr, doc);
        cs.Type.Should().Be(PdfColorSpaceType.Separation);
        cs.Components.Should().Be(1);
    }
}
