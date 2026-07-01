namespace Pdfe.Core.ColorSpaces;

/// <summary>
/// Minimal ICC v2 evaluator for renderer preview. Supports matrix/TRC RGB
/// profiles and lut16 (mft2) A2B printer profiles, which covers the PDF/X
/// ICC profiles in the print conformance corpus without introducing a native
/// color-management dependency.
/// </summary>
internal sealed class PdfIccProfile
{
    private readonly string _colorSpace;
    private readonly MatrixProfile? _matrixProfile;
    private readonly Lut16Profile? _aToBLut16Profile;
    private readonly Lut16Profile? _bToALut16Profile;
    private readonly Dictionary<IccCacheKey, (double R, double G, double B)> _cache = new();
    private readonly object _cacheLock = new();

    private const int MaxCacheEntries = 8192;
    private static readonly double[] D50WhitePoint = { 0.96422, 1.0, 0.82521 };

    private PdfIccProfile(
        string colorSpace,
        MatrixProfile? matrixProfile,
        Lut16Profile? aToBLut16Profile,
        Lut16Profile? bToALut16Profile)
    {
        _colorSpace = colorSpace;
        _matrixProfile = matrixProfile;
        _aToBLut16Profile = aToBLut16Profile;
        _bToALut16Profile = bToALut16Profile;
    }

    public static PdfIccProfile? TryParse(byte[] data)
    {
        try
        {
            if (data.Length < 132 || ReadUInt32(data, 0) > data.Length)
                return null;

            var colorSpace = ReadSignature(data, 16);
            var tagCount = (int)ReadUInt32(data, 128);
            if (tagCount < 0 || 132 + tagCount * 12 > data.Length)
                return null;

            var tags = new Dictionary<string, (int Offset, int Size)>(StringComparer.Ordinal);
            for (var i = 0; i < tagCount; i++)
            {
                var offset = 132 + i * 12;
                var signature = ReadSignature(data, offset);
                var tagOffset = checked((int)ReadUInt32(data, offset + 4));
                var tagSize = checked((int)ReadUInt32(data, offset + 8));
                if (tagOffset >= 0 && tagSize >= 0 && tagOffset + tagSize <= data.Length)
                    tags[signature] = (tagOffset, tagSize);
            }

            if (colorSpace == "RGB " &&
                TryParseMatrixProfile(data, tags, out var matrixProfile))
            {
                return new PdfIccProfile(colorSpace, matrixProfile, null, null);
            }

            Lut16Profile? bToALut16Profile = null;
            if (tags.TryGetValue("B2A0", out var b2a0))
                TryParseLut16Profile(data, b2a0.Offset, b2a0.Size, out bToALut16Profile);

            if (tags.TryGetValue("A2B0", out var a2b0) &&
                TryParseLut16Profile(data, a2b0.Offset, a2b0.Size, out var lut16Profile))
            {
                return new PdfIccProfile(colorSpace, null, lut16Profile, bToALut16Profile);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public (double R, double G, double B)? ToRgb(double[] values)
    {
        if (values.Length == 0)
            return null;

        var key = CreateCacheKey(values);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        (double R, double G, double B)? color = null;
        if (ToPcsLab(values) is { } lab)
        {
            var xyz = LabD50ToXyz(lab.L, lab.A, lab.B);
            var adapted = AdaptD50ToD65(xyz.X, xyz.Y, xyz.Z);
            color = XyzD65ToSrgb(adapted.X, adapted.Y, adapted.Z);
        }

        if (color.HasValue)
        {
            lock (_cacheLock)
            {
                if (_cache.Count < MaxCacheEntries)
                    _cache[key] = color.Value;
            }
        }

        return color;
    }

    public (double R, double G, double B)? ToOutputIntentPreviewRgb(double[] values, PdfIccProfile? outputIntent)
    {
        if (outputIntent == null)
            return ToRgb(values);

        var lab = ToPcsLab(values);
        if (lab == null)
            return ToRgb(values);

        var outputValues = outputIntent.FromPcsLab(lab.Value.L, lab.Value.A, lab.Value.B);
        if (outputValues == null)
            return ToRgb(values);

        return outputValues.Length switch
        {
            >= 4 => PdfColorConverter.DeviceCmykProcessScreenPreviewToRgb(
                outputValues[0],
                outputValues[1],
                outputValues[2],
                outputValues[3]),
            >= 3 => (outputValues[0], outputValues[1], outputValues[2]),
            >= 1 => (outputValues[0], outputValues[0], outputValues[0]),
            _ => ToRgb(values)
        };
    }

    private (double L, double A, double B)? ToPcsLab(double[] values)
    {
        if (_matrixProfile is { } matrixProfile && _colorSpace == "RGB " && values.Length >= 3)
        {
            var xyz = EvaluateMatrixProfileToXyzD50(matrixProfile, values[0], values[1], values[2]);
            return XyzD50ToLab(xyz.X, xyz.Y, xyz.Z);
        }

        if (_aToBLut16Profile != null && values.Length >= _aToBLut16Profile.InputChannels)
        {
            var output = EvaluateLut16(_aToBLut16Profile, values);
            if (output.Length >= 3)
                return DecodeIccLab(output[0], output[1], output[2]);
        }

        return null;
    }

    private double[]? FromPcsLab(double l, double a, double b)
    {
        if (_bToALut16Profile == null || _bToALut16Profile.InputChannels < 3)
            return null;

        return EvaluateLut16(_bToALut16Profile, new[]
        {
            Math.Clamp(l / 100.0, 0, 1),
            Math.Clamp((a + 128.0) / 255.0, 0, 1),
            Math.Clamp((b + 128.0) / 255.0, 0, 1)
        });
    }

    private static bool TryParseMatrixProfile(
        byte[] data,
        Dictionary<string, (int Offset, int Size)> tags,
        out MatrixProfile profile)
    {
        profile = default;
        if (!TryReadXyzTag(data, tags, "rXYZ", out var r) ||
            !TryReadXyzTag(data, tags, "gXYZ", out var g) ||
            !TryReadXyzTag(data, tags, "bXYZ", out var b))
        {
            return false;
        }

        if (!TryReadCurveTag(data, tags, "rTRC", out var rCurve) ||
            !TryReadCurveTag(data, tags, "gTRC", out var gCurve) ||
            !TryReadCurveTag(data, tags, "bTRC", out var bCurve))
        {
            return false;
        }

        profile = new MatrixProfile(r, g, b, rCurve, gCurve, bCurve);
        return true;
    }

    private static bool TryParseLut16Profile(byte[] data, int offset, int size, out Lut16Profile profile)
    {
        profile = default!;
        if (size < 52 || offset + size > data.Length || ReadSignature(data, offset) != "mft2")
            return false;

        var inputChannels = data[offset + 8];
        var outputChannels = data[offset + 9];
        var gridPoints = data[offset + 10];
        if (inputChannels <= 0 || outputChannels <= 0 || gridPoints <= 1)
            return false;

        var inputEntries = ReadUInt16(data, offset + 48);
        var outputEntries = ReadUInt16(data, offset + 50);
        if (inputEntries <= 1 || outputEntries <= 1)
            return false;

        var cursor = offset + 52;
        var inputTables = new ushort[inputChannels][];
        for (var c = 0; c < inputChannels; c++)
        {
            inputTables[c] = ReadUInt16Table(data, cursor, inputEntries);
            cursor += inputEntries * 2;
        }

        var clutEntries = 1;
        for (var i = 0; i < inputChannels; i++)
            clutEntries = checked(clutEntries * gridPoints);
        var clut = ReadUInt16Table(data, cursor, checked(clutEntries * outputChannels));
        cursor += clut.Length * 2;

        var outputTables = new ushort[outputChannels][];
        for (var c = 0; c < outputChannels; c++)
        {
            outputTables[c] = ReadUInt16Table(data, cursor, outputEntries);
            cursor += outputEntries * 2;
        }

        profile = new Lut16Profile(
            inputChannels,
            outputChannels,
            gridPoints,
            inputTables,
            clut,
            outputTables);
        return true;
    }

    private static (double R, double G, double B) EvaluateMatrixProfile(
        MatrixProfile profile,
        double red,
        double green,
        double blue)
    {
        var xyz = EvaluateMatrixProfileToXyzD50(profile, red, green, blue);
        var adapted = AdaptD50ToD65(xyz.X, xyz.Y, xyz.Z);
        return XyzD65ToSrgb(adapted.X, adapted.Y, adapted.Z);
    }

    private static (double X, double Y, double Z) EvaluateMatrixProfileToXyzD50(
        MatrixProfile profile,
        double red,
        double green,
        double blue)
    {
        var r = profile.RedCurve.Evaluate(Math.Clamp(red, 0, 1));
        var g = profile.GreenCurve.Evaluate(Math.Clamp(green, 0, 1));
        var b = profile.BlueCurve.Evaluate(Math.Clamp(blue, 0, 1));

        var x = profile.Red.X * r + profile.Green.X * g + profile.Blue.X * b;
        var y = profile.Red.Y * r + profile.Green.Y * g + profile.Blue.Y * b;
        var z = profile.Red.Z * r + profile.Green.Z * g + profile.Blue.Z * b;
        return (x, y, z);
    }

    private static (double R, double G, double B) EvaluateLut16Profile(Lut16Profile profile, double[] values)
    {
        var output = EvaluateLut16(profile, values);
        if (output.Length >= 3)
        {
            var lab = DecodeIccLab(output[0], output[1], output[2]);
            var xyz = LabD50ToXyz(lab.L, lab.A, lab.B);
            var adapted = AdaptD50ToD65(xyz.X, xyz.Y, xyz.Z);
            return XyzD65ToSrgb(adapted.X, adapted.Y, adapted.Z);
        }

        var gray = output.Length > 0 ? output[0] : 0;
        return (gray, gray, gray);
    }

    private static double[] EvaluateLut16(Lut16Profile profile, double[] values)
    {
        Span<double> input = stackalloc double[profile.InputChannels];
        for (var c = 0; c < profile.InputChannels; c++)
            input[c] = InterpolateTable(profile.InputTables[c], Math.Clamp(values[c], 0, 1));

        Span<double> clutOutput = stackalloc double[profile.OutputChannels];
        InterpolateClut(profile, input, clutOutput);

        Span<double> output = stackalloc double[profile.OutputChannels];
        for (var c = 0; c < profile.OutputChannels; c++)
            output[c] = InterpolateTable(profile.OutputTables[c], clutOutput[c]);

        return output.ToArray();
    }

    private static void InterpolateClut(Lut16Profile profile, ReadOnlySpan<double> input, Span<double> output)
    {
        Span<int> low = stackalloc int[profile.InputChannels];
        Span<double> fraction = stackalloc double[profile.InputChannels];
        for (var c = 0; c < profile.InputChannels; c++)
        {
            var position = Math.Clamp(input[c], 0, 1) * (profile.GridPoints - 1);
            low[c] = Math.Clamp((int)Math.Floor(position), 0, profile.GridPoints - 2);
            fraction[c] = position - low[c];
        }

        output.Clear();
        var corners = 1 << profile.InputChannels;
        for (var corner = 0; corner < corners; corner++)
        {
            var weight = 1.0;
            var clutIndex = 0;
            for (var c = 0; c < profile.InputChannels; c++)
            {
                var high = ((corner >> c) & 1) != 0;
                var coord = low[c] + (high ? 1 : 0);
                weight *= high ? fraction[c] : 1 - fraction[c];
                clutIndex = clutIndex * profile.GridPoints + coord;
            }

            var offset = clutIndex * profile.OutputChannels;
            for (var c = 0; c < profile.OutputChannels; c++)
                output[c] += weight * (profile.Clut[offset + c] / 65535.0);
        }
    }

    private static double InterpolateTable(ushort[] table, double value)
    {
        value = Math.Clamp(value, 0, 1);
        var position = value * (table.Length - 1);
        var lower = Math.Clamp((int)Math.Floor(position), 0, table.Length - 1);
        var upper = Math.Min(table.Length - 1, lower + 1);
        var fraction = position - lower;
        return ((table[lower] * (1 - fraction)) + (table[upper] * fraction)) / 65535.0;
    }

    private static (double L, double A, double B) DecodeIccLab(double l, double a, double b)
        => (
            l * 100.0,
            a * 255.0 - 128.0,
            b * 255.0 - 128.0);

    private static (double X, double Y, double Z) LabD50ToXyz(double l, double a, double b)
    {
        var fy = (l + 16) / 116.0;
        var fx = a / 500.0 + fy;
        var fz = fy - b / 200.0;
        return (
            D50WhitePoint[0] * Fcube(fx),
            D50WhitePoint[1] * Fcube(fy),
            D50WhitePoint[2] * Fcube(fz));
    }

    private static (double L, double A, double B) XyzD50ToLab(double x, double y, double z)
    {
        var fx = LabF(x / D50WhitePoint[0]);
        var fy = LabF(y / D50WhitePoint[1]);
        var fz = LabF(z / D50WhitePoint[2]);
        return (
            116.0 * fy - 16.0,
            500.0 * (fx - fy),
            200.0 * (fy - fz));
    }

    private static double LabF(double t)
        => t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : 7.787 * t + 16.0 / 116.0;

    private static double Fcube(double t)
        => t > 0.206897 ? t * t * t : (t - 16.0 / 116) / 7.787;

    private static (double X, double Y, double Z) AdaptD50ToD65(double x, double y, double z)
    {
        // Bradford D50 -> D65.
        return (
             0.9555766 * x - 0.0230393 * y + 0.0631636 * z,
            -0.0282895 * x + 1.0099416 * y + 0.0210077 * z,
             0.0122982 * x - 0.0204830 * y + 1.3299098 * z);
    }

    private static (double R, double G, double B) XyzD65ToSrgb(double x, double y, double z)
    {
        double r =  3.2406 * x - 1.5372 * y - 0.4986 * z;
        double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        double b =  0.0557 * x - 0.2040 * y + 1.0570 * z;
        return (EncodeSrgb(r), EncodeSrgb(g), EncodeSrgb(b));
    }

    private static double EncodeSrgb(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value <= 0.0031308
            ? 12.92 * value
            : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
    }

    private static bool TryReadXyzTag(
        byte[] data,
        Dictionary<string, (int Offset, int Size)> tags,
        string tag,
        out Xyz xyz)
    {
        xyz = default;
        if (!tags.TryGetValue(tag, out var entry) ||
            entry.Size < 20 ||
            ReadSignature(data, entry.Offset) != "XYZ ")
        {
            return false;
        }

        xyz = new Xyz(
            ReadS15Fixed16(data, entry.Offset + 8),
            ReadS15Fixed16(data, entry.Offset + 12),
            ReadS15Fixed16(data, entry.Offset + 16));
        return true;
    }

    private static bool TryReadCurveTag(
        byte[] data,
        Dictionary<string, (int Offset, int Size)> tags,
        string tag,
        out ToneCurve curve)
    {
        curve = default;
        if (!tags.TryGetValue(tag, out var entry) ||
            entry.Size < 12 ||
            ReadSignature(data, entry.Offset) != "curv")
        {
            return false;
        }

        var count = (int)ReadUInt32(data, entry.Offset + 8);
        if (count == 0)
        {
            curve = ToneCurve.Linear;
            return true;
        }

        if (count == 1)
        {
            curve = ToneCurve.FromGamma(ReadUInt16(data, entry.Offset + 12) / 256.0);
            return true;
        }

        if (entry.Size < 12 + count * 2)
            return false;

        curve = ToneCurve.FromTable(ReadUInt16Table(data, entry.Offset + 12, count));
        return true;
    }

    private static ushort[] ReadUInt16Table(byte[] data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count * 2 > data.Length)
            throw new InvalidDataException("ICC table exceeds profile bounds.");

        var table = new ushort[count];
        for (var i = 0; i < count; i++)
            table[i] = ReadUInt16(data, offset + i * 2);
        return table;
    }

    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static double ReadS15Fixed16(byte[] data, int offset)
        => unchecked((int)ReadUInt32(data, offset)) / 65536.0;

    private static string ReadSignature(byte[] data, int offset)
        => System.Text.Encoding.ASCII.GetString(data, offset, 4);

    private static IccCacheKey CreateCacheKey(double[] values)
        => new(
            values.Length,
            values.Length > 0 ? Quantize(values[0]) : 0,
            values.Length > 1 ? Quantize(values[1]) : 0,
            values.Length > 2 ? Quantize(values[2]) : 0,
            values.Length > 3 ? Quantize(values[3]) : 0);

    private static int Quantize(double value)
        => (int)Math.Round(Math.Clamp(value, 0, 1) * 4095);

    private readonly record struct IccCacheKey(int Length, int V0, int V1, int V2, int V3);

    private readonly record struct Xyz(double X, double Y, double Z);

    private readonly record struct MatrixProfile(
        Xyz Red,
        Xyz Green,
        Xyz Blue,
        ToneCurve RedCurve,
        ToneCurve GreenCurve,
        ToneCurve BlueCurve);

    private sealed record Lut16Profile(
        int InputChannels,
        int OutputChannels,
        int GridPoints,
        ushort[][] InputTables,
        ushort[] Clut,
        ushort[][] OutputTables);

    private readonly record struct ToneCurve(double Gamma, ushort[]? Table)
    {
        public static ToneCurve Linear => new(1.0, null);

        public static ToneCurve FromGamma(double gamma) => new(gamma <= 0 ? 1.0 : gamma, null);

        public static ToneCurve FromTable(ushort[] table) => new(1.0, table);

        public double Evaluate(double value)
        {
            value = Math.Clamp(value, 0, 1);
            if (Table is not { Length: > 1 } table)
                return Math.Pow(value, Gamma);

            var position = value * (table.Length - 1);
            var lower = Math.Clamp((int)Math.Floor(position), 0, table.Length - 1);
            var upper = Math.Min(table.Length - 1, lower + 1);
            var fraction = position - lower;
            return ((table[lower] * (1 - fraction)) + (table[upper] * fraction)) / 65535.0;
        }
    }
}
