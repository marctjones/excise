using Excise.Core.ColorSpaces;

namespace Excise.Rendering;

/// <summary>
/// Cached, context-free color conversion for raw image samples. The cache is
/// weakly keyed by the resolved PDF color-space instance, so it cannot extend
/// a document lifetime.
/// </summary>
internal sealed class ImageColorConverter
{
    private const int LatticeSize = 17;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfColorSpace, ConverterBox>
        Converters = new();

    private readonly PdfColorSpace _colorSpace;
    private readonly byte[]? _byteTable;
    private readonly float[]? _lattice;
    private readonly int _components;

    private ImageColorConverter(
        PdfColorSpace colorSpace,
        string strategy,
        int components,
        byte[]? byteTable,
        float[]? lattice)
    {
        _colorSpace = colorSpace;
        Strategy = strategy;
        _components = components;
        _byteTable = byteTable;
        _lattice = lattice;
    }

    internal string Strategy { get; }

    internal static ImageColorConverter? For(PdfColorSpace colorSpace)
        => Converters.GetValue(
            colorSpace,
            static cs => new ConverterBox(Create(cs))).Converter;

    internal (byte R, byte G, byte B) ToRgb(ReadOnlySpan<double> values)
    {
        if (_byteTable != null)
        {
            var sample = _colorSpace.Type == PdfColorSpaceType.Indexed
                ? (int)Math.Round(values.Length > 0 ? values[0] : 0)
                : UnitToByte(values.Length > 0 ? values[0] : 0);
            return LookupByteTable(_byteTable, sample);
        }

        if (_lattice != null)
            return LatticeToRgb(_lattice, _components, values);

        var (r, g, b) = _colorSpace.ToRgb(values.ToArray());
        return ToByteRgb(r, g, b);
    }

    internal (byte R, byte G, byte B) ToRgb(int sample)
    {
        if (_byteTable != null)
            return LookupByteTable(_byteTable, sample);

        return ToRgb([sample / 255.0]);
    }

    internal (byte R, byte G, byte B) ToRgb(
        byte first,
        byte second,
        byte third,
        byte fourth)
    {
        Span<double> values = stackalloc double[4]
        {
            first / 255.0,
            second / 255.0,
            third / 255.0,
            fourth / 255.0
        };
        return ToRgb(values);
    }

    private static ImageColorConverter? Create(PdfColorSpace colorSpace)
    {
        if (colorSpace.Type == PdfColorSpaceType.Lab)
            return null;

        if (colorSpace.Type == PdfColorSpaceType.Indexed)
        {
            return new ImageColorConverter(
                colorSpace,
                "IndexedExactByteTable",
                1,
                BuildIndexedByteTable(colorSpace),
                null);
        }

        return colorSpace.Components switch
        {
            1 => new ImageColorConverter(
                colorSpace,
                "OneComponentExactByteTable",
                1,
                BuildOneComponentByteTable(colorSpace),
                null),
            3 => new ImageColorConverter(
                colorSpace,
                "Continuous3DLattice",
                3,
                null,
                BuildContinuousLattice(colorSpace, 3)),
            4 => new ImageColorConverter(
                colorSpace,
                "Continuous4DLattice",
                4,
                null,
                BuildContinuousLattice(colorSpace, 4)),
            _ => null
        };
    }

    private static byte[] BuildIndexedByteTable(PdfColorSpace colorSpace)
    {
        var table = new byte[256 * 3];
        var destination = 0;
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = colorSpace.LookupIndexed(i);
            var rgb = ToByteRgb(r, g, b);
            table[destination++] = rgb.R;
            table[destination++] = rgb.G;
            table[destination++] = rgb.B;
        }

        return table;
    }

    private static byte[] BuildOneComponentByteTable(PdfColorSpace colorSpace)
    {
        var table = new byte[256 * 3];
        var values = new double[1];
        var destination = 0;
        for (var i = 0; i < 256; i++)
        {
            values[0] = colorSpace.DecodeSampleByte(0, (byte)i);
            var (r, g, b) = colorSpace.ToRgb(values);
            var rgb = ToByteRgb(r, g, b);
            table[destination++] = rgb.R;
            table[destination++] = rgb.G;
            table[destination++] = rgb.B;
        }

        return table;
    }

    private static float[] BuildContinuousLattice(PdfColorSpace colorSpace, int components)
    {
        var count = 1;
        for (var i = 0; i < components; i++)
            count *= LatticeSize;

        var lattice = new float[count * 3];
        var values = new double[components];
        var destination = 0;
        for (var i = 0; i < count; i++)
        {
            var remainder = i;
            for (var component = components - 1; component >= 0; component--)
            {
                values[component] = (remainder % LatticeSize) / (double)(LatticeSize - 1);
                remainder /= LatticeSize;
            }

            var (r, g, b) = colorSpace.ToRgb(values);
            lattice[destination++] = (float)r;
            lattice[destination++] = (float)g;
            lattice[destination++] = (float)b;
        }

        return lattice;
    }

    private static (byte R, byte G, byte B) LatticeToRgb(
        float[] lattice,
        int components,
        ReadOnlySpan<double> values)
    {
        Span<int> index = stackalloc int[4];
        Span<double> fractions = stackalloc double[4];
        for (var component = 0; component < components; component++)
        {
            var value = component < values.Length ? Math.Clamp(values[component], 0, 1) : 0;
            var scaled = value * (LatticeSize - 1);
            var lower = (int)scaled;
            var fraction = scaled - lower;
            if (lower >= LatticeSize - 1)
            {
                lower = LatticeSize - 2;
                fraction = 1;
            }

            index[component] = lower;
            fractions[component] = fraction;
        }

        double r = 0, g = 0, b = 0;
        var corners = 1 << components;
        for (var mask = 0; mask < corners; mask++)
        {
            var weight = 1.0;
            var offset = 0;
            for (var component = 0; component < components; component++)
            {
                var high = (mask & (1 << component)) != 0;
                weight *= high ? fractions[component] : 1 - fractions[component];
                offset = (offset * LatticeSize) + index[component] + (high ? 1 : 0);
            }

            if (weight == 0)
                continue;

            offset *= 3;
            r += weight * lattice[offset];
            g += weight * lattice[offset + 1];
            b += weight * lattice[offset + 2];
        }

        return ToByteRgb(r, g, b);
    }

    private static (byte R, byte G, byte B) LookupByteTable(byte[] table, int sample)
    {
        var offset = Math.Clamp(sample, 0, 255) * 3;
        return (table[offset], table[offset + 1], table[offset + 2]);
    }

    private static byte UnitToByte(double value)
        => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);

    private static (byte R, byte G, byte B) ToByteRgb(double r, double g, double b)
        => ((byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));

    private sealed class ConverterBox
    {
        internal ConverterBox(ImageColorConverter? converter) => Converter = converter;

        internal ImageColorConverter? Converter { get; }
    }
}
