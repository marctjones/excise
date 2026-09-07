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
        var converter = ImageColorConverter.For(PdfColorSpace.DeviceGray);

        converter.Should().NotBeNull();
        converter!.Strategy.Should().Be("OneComponentExactByteTable");
        converter.ToRgb([0.5]).Should().Be(((byte)128, (byte)128, (byte)128));
    }

    [Fact]
    public void GetImageColorConverter_DeviceRgb_UsesContinuous3DLattice()
    {
        var converter = ImageColorConverter.For(PdfColorSpace.DeviceRGB);

        converter.Should().NotBeNull();
        converter!.Strategy.Should().Be("Continuous3DLattice");
        converter.ToRgb([1.0, 0.0, 0.5]).Should().Be(((byte)255, (byte)0, (byte)127));
    }

    [Fact]
    public void GetImageColorConverter_DeviceCmyk_UsesContinuous4DLattice()
    {
        var converter = ImageColorConverter.For(PdfColorSpace.DeviceCMYK);

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

        var converter = ImageColorConverter.For(colorSpace);

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

        ImageColorConverter.For(lab).Should().BeNull();
    }

    [Fact]
    public void GetImageColorConverter_ReusesConverterForColorSpaceInstance()
    {
        var first = ImageColorConverter.For(PdfColorSpace.DeviceRGB);
        var second = ImageColorConverter.For(PdfColorSpace.DeviceRGB);

        second.Should().BeSameAs(first);
    }

    // Regression coverage for the lattice interpolation itself (#1350's perf fix).
    // Lattice3DToRgb/Lattice4DToRgb are fixed-arity specializations of LatticeGenericToRgb —
    // same weight products, same corner traversal order, just unrolled against a
    // compile-time-constant arity instead of a runtime one. This asserts they stay exactly
    // (bit-for-bit) equal to the untouched generic algorithm, which is the invariant a future
    // refactor must preserve: don't change the corner traversal order or the per-corner
    // weight multiplication order without also re-deriving this test.
    //
    // ⚠️ This test is WEAKER than it looks and must not be trusted as sufficient on its own.
    // A prior draft of this perf fix shared partial products across a differently-nested
    // loop, which reversed which axis varied fastest in the corner summation, and it
    // rendered 30 of ~1,000,000 pixels of the real Altona fixture one level off from the
    // pre-change baseline. That broken code was checked against this exact test — both with
    // an uncorrelated random-noise lattice AND with a lattice built from the real
    // BuildContinuousLattice(DeviceCMYK) transform, 200k trials each — and PASSED both times.
    // Floating-point summation reordering only became visible on the real fixture's actual
    // ICC-CLUT-backed color lattice (piecewise, quantized — not the smooth analytic DeviceCMYK
    // fallback this test can reach). The only thing that actually caught the bug was
    // rendering test-pdfs/altona/eci_altona-test-suite-v2_technical2_x4.pdf and diffing the
    // resulting PNG bytes against a pre-change baseline. If you touch the corner order or
    // weight order again, re-run that render and re-diff — this test passing is not evidence
    // that the output is unchanged.
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void LatticeSpecialization_MatchesGenericAlgorithmExactly(int components)
    {
        var colorSpace = components == 3 ? PdfColorSpace.DeviceRGB : PdfColorSpace.DeviceCMYK;
        var lattice = ImageColorConverter.BuildContinuousLattice(colorSpace, components);
        var rnd = new Random(20260907 + components);

        for (var trial = 0; trial < 200_000; trial++)
        {
            // Mix uniform-random points with exact grid points / slightly out-of-range values,
            // since those hit the boundary-clamp branch in LatticeAxis differently.
            double Pick() => rnd.Next(5) switch
            {
                0 => 0.0,
                1 => 1.0,
                2 => (rnd.NextDouble() * 2) - 0.5,
                _ => rnd.NextDouble()
            };

            var values = new double[components];
            for (var i = 0; i < components; i++)
                values[i] = Pick();

            var expected = ImageColorConverter.LatticeGenericToRgb(lattice, components, values);
            var actual = components == 3
                ? ImageColorConverter.Lattice3DToRgb(lattice, values)
                : ImageColorConverter.Lattice4DToRgb(lattice, values);

            actual.Should().Be(expected, $"trial {trial} values=[{string.Join(",", values)}]");
        }
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
