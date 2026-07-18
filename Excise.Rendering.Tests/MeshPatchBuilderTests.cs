using AwesomeAssertions;
using Excise.Rendering.Shadings;
using SkiaSharp;

namespace Excise.Rendering.Tests;

public sealed class MeshPatchBuilderTests
{
    [Fact]
    public void ResolveCanonicalPatchPoints_Type7Flag0_ReordersPerimeterAndInteriorControls()
    {
        var source = Enumerable.Range(0, 16)
            .Select(i => new SKPoint(i, -i))
            .ToArray();

        var canonical = PdfMeshPatchBuilder.ResolveCanonicalPatchPoints(
            source,
            previous: null,
            flag: 0,
            tensorPatch: true);

        canonical.Select(p => (int)p.X).Should().Equal(
            0, 11, 10, 9,
            1, 12, 15, 8,
            2, 13, 14, 7,
            3, 4, 5, 6);
    }

    [Fact]
    public void ResolveCanonicalPatchColors_Flag0_ConvertsPdfCornerOrderToRendererOrder()
    {
        var source = new[]
        {
            new SKColor(10, 0, 0),
            new SKColor(20, 0, 0),
            new SKColor(30, 0, 0),
            new SKColor(40, 0, 0)
        };

        var canonical = PdfMeshPatchBuilder.ResolveCanonicalPatchColors(
            source,
            previous: null,
            flag: 0);

        canonical.Select(c => (int)c.Red).Should().Equal(10, 40, 30, 20);
    }

    [Fact]
    public void ResolveCanonicalPatchPoints_Type6Flag0_DerivesInteriorControls()
    {
        var source = Enumerable.Range(0, 12)
            .Select(i => new SKPoint(i, i * 2))
            .ToArray();

        var canonical = PdfMeshPatchBuilder.ResolveCanonicalPatchPoints(
            source,
            previous: null,
            flag: 0,
            tensorPatch: false);

        canonical.Should().HaveCount(16);
        canonical[0].Should().Be(new SKPoint(0, 0));
        canonical[3].Should().Be(new SKPoint(9, 18));
        canonical[12].Should().Be(new SKPoint(3, 6));
        canonical[15].Should().Be(new SKPoint(6, 12));
        canonical[5].Should().NotBe(default(SKPoint));
        canonical[6].Should().NotBe(default(SKPoint));
        canonical[9].Should().NotBe(default(SKPoint));
        canonical[10].Should().NotBe(default(SKPoint));
    }
}
