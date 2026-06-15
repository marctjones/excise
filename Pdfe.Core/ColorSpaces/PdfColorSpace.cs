namespace Pdfe.Core.ColorSpaces;

using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

/// <summary>
/// Resolved PDF color space (ISO 32000-2 §8.6).
/// Converts n-component color values to device RGB for rendering.
/// </summary>
public sealed class PdfColorSpace
{
    public PdfColorSpaceType Type { get; }
    public int Components { get; }

    private readonly PdfColorSpace? _indexedBase;
    private readonly byte[]? _indexedLookup;
    private readonly PdfColorSpace? _alternateSpace;
    private readonly PdfObject? _tintTransform;
    private readonly double[]? _whitePoint;
    private readonly double[]? _calGamma;
    private readonly double[]? _calMatrix;

    private PdfColorSpace(PdfColorSpaceType type, int components,
        PdfColorSpace? indexedBase = null, byte[]? indexedLookup = null,
        PdfColorSpace? alternateSpace = null, PdfObject? tintTransform = null,
        double[]? whitePoint = null, double[]? calGamma = null, double[]? calMatrix = null)
    {
        Type = type;
        Components = components;
        _indexedBase = indexedBase;
        _indexedLookup = indexedLookup;
        _alternateSpace = alternateSpace;
        _tintTransform = tintTransform;
        _whitePoint = whitePoint;
        _calGamma = calGamma;
        _calMatrix = calMatrix;
    }

    public static readonly PdfColorSpace DeviceGray = new(PdfColorSpaceType.DeviceGray, 1);
    public static readonly PdfColorSpace DeviceRGB = new(PdfColorSpaceType.DeviceRGB, 3);
    public static readonly PdfColorSpace DeviceCMYK = new(PdfColorSpaceType.DeviceCMYK, 4);

    public static PdfColorSpace FromName(string name) => name switch
    {
        "DeviceGray" or "G" => DeviceGray,
        "DeviceRGB" or "RGB" => DeviceRGB,
        "DeviceCMYK" or "CMYK" => DeviceCMYK,
        "CalGray" => new PdfColorSpace(PdfColorSpaceType.CalGray, 1),
        "CalRGB" => new PdfColorSpace(PdfColorSpaceType.CalRGB, 3),
        "Lab" => new PdfColorSpace(PdfColorSpaceType.Lab, 3),
        "Separation" => new PdfColorSpace(PdfColorSpaceType.Separation, 1),
        _ => new PdfColorSpace(PdfColorSpaceType.Unknown, 1)
    };

    /// <summary>
    /// Parse a color space from a PdfObject (name or array) using the document.
    /// </summary>
    public static PdfColorSpace Parse(PdfObject csObj, PdfDocument doc)
    {
        if (csObj is PdfName n)
            return FromName(n.Value);

        var resolved = doc.Resolve(csObj);
        if (resolved is PdfArray arr && arr.Count >= 1)
        {
            var typeName = (arr[0] as PdfName)?.Value ?? "";
            return typeName switch
            {
                "ICCBased" => ParseICCBased(arr, doc),
                "Indexed" => ParseIndexed(arr, doc),
                "CalGray" => ParseCalGray(arr),
                "CalRGB" => ParseCalRgb(arr),
                "Lab" => new PdfColorSpace(PdfColorSpaceType.Lab, 3),
                "Separation" => ParseSeparation(arr, doc),
                "DeviceN" => ParseDeviceN(arr, doc),
                "Pattern" => new PdfColorSpace(PdfColorSpaceType.Pattern, 0),
                "DeviceGray" => DeviceGray,
                "DeviceRGB" => DeviceRGB,
                "DeviceCMYK" => DeviceCMYK,
                _ => new PdfColorSpace(PdfColorSpaceType.Unknown, 1)
            };
        }

        return DeviceRGB;
    }

    private static PdfColorSpace ParseICCBased(PdfArray arr, PdfDocument doc)
    {
        if (arr.Count < 2) return DeviceRGB;
        var iccStream = doc.Resolve(arr[1]) as PdfStream;
        if (iccStream == null) return DeviceRGB;

        var n = iccStream.GetInt("N", 3);
        return n switch
        {
            1 => DeviceGray,
            4 => DeviceCMYK,
            _ => DeviceRGB
        };
    }

    private static PdfColorSpace ParseIndexed(PdfArray arr, PdfDocument doc)
    {
        if (arr.Count < 4) return DeviceRGB;

        var baseCs = Parse(arr[1], doc);
        var hival = (arr[2] as PdfInteger)?.Value ?? 255;

        byte[] lookup;
        var lookupObj = doc.Resolve(arr[3]);
        if (lookupObj is PdfString ps)
            lookup = System.Text.Encoding.Latin1.GetBytes(ps.Value);
        else if (lookupObj is PdfStream ls)
            lookup = ls.DecodedData ?? ls.EncodedData;
        else
            lookup = Array.Empty<byte>();

        return new PdfColorSpace(PdfColorSpaceType.Indexed, 1, baseCs, lookup);
    }

    private static PdfColorSpace ParseCalGray(PdfArray arr)
    {
        var dict = arr.Count >= 2 ? arr[1] as PdfDictionary : null;
        if (dict == null)
            return new PdfColorSpace(PdfColorSpaceType.CalGray, 1);

        var whitePoint = GetNumberArray(dict, "WhitePoint", new[] { 1.0, 1.0, 1.0 }, expected: 3);
        var gamma = dict?.GetNumber("Gamma", 1.0) ?? 1.0;
        return new PdfColorSpace(PdfColorSpaceType.CalGray, 1,
            whitePoint: whitePoint,
            calGamma: new[] { gamma });
    }

    private static PdfColorSpace ParseCalRgb(PdfArray arr)
    {
        var dict = arr.Count >= 2 ? arr[1] as PdfDictionary : null;
        if (dict == null)
            return new PdfColorSpace(PdfColorSpaceType.CalRGB, 3);

        var whitePoint = GetNumberArray(dict, "WhitePoint", new[] { 1.0, 1.0, 1.0 }, expected: 3);
        var gamma = GetNumberArray(dict, "Gamma", new[] { 1.0, 1.0, 1.0 }, expected: 3);
        var matrix = GetNumberArray(dict, "Matrix",
            new[] { 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0 },
            expected: 9);
        return new PdfColorSpace(PdfColorSpaceType.CalRGB, 3,
            whitePoint: whitePoint,
            calGamma: gamma,
            calMatrix: matrix);
    }

    private static PdfColorSpace ParseSeparation(PdfArray arr, PdfDocument doc)
    {
        if (arr.Count < 4)
            return new PdfColorSpace(PdfColorSpaceType.Separation, 1);

        var alternateSpace = Parse(arr[2], doc);
        var tintTransform = doc.Resolve(arr[3]);
        return new PdfColorSpace(PdfColorSpaceType.Separation, 1, alternateSpace: alternateSpace, tintTransform: tintTransform);
    }

    private static PdfColorSpace ParseDeviceN(PdfArray arr, PdfDocument doc)
    {
        int n = 1;
        if (arr.Count >= 2 && arr[1] is PdfArray names)
            n = names.Count;

        PdfColorSpace? alternateSpace = null;
        if (arr.Count >= 3)
            alternateSpace = Parse(arr[2], doc);

        PdfObject? tintTransform = arr.Count >= 4 ? doc.Resolve(arr[3]) : null;
        return new PdfColorSpace(PdfColorSpaceType.DeviceN, n, alternateSpace: alternateSpace, tintTransform: tintTransform);
    }

    /// <summary>
    /// Convert n-component values (0..1 each) to (R, G, B) each 0..1.
    /// </summary>
    public (double R, double G, double B) ToRgb(double[] values)
    {
        if (values.Length == 0)
            return (0, 0, 0);

        return Type switch
        {
            PdfColorSpaceType.DeviceGray =>
                (values[0], values[0], values[0]),

            PdfColorSpaceType.CalGray =>
                HasCalGrayCalibration ? CalGrayToRgb(values[0]) : (values[0], values[0], values[0]),

            PdfColorSpaceType.DeviceRGB =>
                (values.Length >= 3) ? (values[0], values[1], values[2]) : (values[0], values[0], values[0]),

            PdfColorSpaceType.CalRGB =>
                values.Length >= 3
                    ? (HasCalRgbCalibration ? CalRgbToRgb(values[0], values[1], values[2]) : (values[0], values[1], values[2]))
                    : (values[0], values[0], values[0]),

            PdfColorSpaceType.DeviceCMYK =>
                (values.Length >= 4) ? CmykToRgb(values[0], values[1], values[2], values[3]) : (0, 0, 0),

            PdfColorSpaceType.Lab =>
                (values.Length >= 3) ? LabToRgb(values[0], values[1], values[2]) : (0, 0, 0),

            PdfColorSpaceType.ICCBased =>
                Components == 1 ? (values[0], values[0], values[0]) :
                Components == 4 ? (values.Length >= 4 ? CmykToRgb(values[0], values[1], values[2], values[3]) : (0, 0, 0)) :
                (values.Length >= 3) ? (values[0], values[1], values[2]) : (0, 0, 0),

            PdfColorSpaceType.Separation =>
                ResolveTintedColor(values),

            PdfColorSpaceType.DeviceN =>
                ResolveTintedColor(values),

            PdfColorSpaceType.Indexed =>
                (values.Length >= 1) ? LookupIndexed((int)Math.Round(values[0])) : (0, 0, 0),

            _ => (values.Length >= 3) ? (values[0], values[1], values[2]) :
                 (values.Length >= 1) ? (values[0], values[0], values[0]) : (0, 0, 0)
        };
    }

    private bool HasCalRgbCalibration => _whitePoint != null || _calGamma != null || _calMatrix != null;

    private bool HasCalGrayCalibration => _whitePoint != null || _calGamma != null;

    /// <summary>Convert an Indexed color index to RGB using the lookup table.</summary>
    public (double R, double G, double B) LookupIndexed(int index)
    {
        if (_indexedBase == null || _indexedLookup == null)
            return (0, 0, 0);

        int baseComps = _indexedBase.Components;
        if (baseComps <= 0 || _indexedLookup.Length < baseComps)
            return (0, 0, 0);

        int maxIndex = (_indexedLookup.Length / baseComps) - 1;
        index = Math.Clamp(index, 0, maxIndex);
        int offset = index * baseComps;

        var baseValues = new double[baseComps];
        for (int i = 0; i < baseComps; i++)
            baseValues[i] = _indexedLookup[offset + i] / 255.0;

        return _indexedBase.ToRgb(baseValues);
    }

    private (double R, double G, double B) ResolveTintedColor(double[] values)
    {
        if (_alternateSpace == null)
            return (values.Length >= 1) ? (1 - values[0], 1 - values[0], 1 - values[0]) : (0, 0, 0);

        var t = values.Length > 0 ? values[0] : 0;
        var evaluated = PdfFunctionEvaluator.Evaluate(_tintTransform, values);
        if (evaluated == null || evaluated.Length == 0)
        {
            if (_alternateSpace.Components == values.Length && values.Length > 0)
                return _alternateSpace.ToRgb(values);

            if (_alternateSpace.Components == 1 && values.Length > 0)
                return _alternateSpace.ToRgb(new[] { values[0] });

            return _alternateSpace.ToRgb(new[] { t });
        }

        return _alternateSpace.ToRgb(evaluated);
    }

    private (double R, double G, double B) CalGrayToRgb(double a)
    {
        var gamma = _calGamma is { Length: > 0 } ? _calGamma[0] : 1.0;
        var y = Math.Pow(Math.Clamp(a, 0, 1), gamma);
        var encoded = EncodeSrgb(y);
        return (encoded, encoded, encoded);
    }

    private (double R, double G, double B) CalRgbToRgb(double a, double b, double c)
    {
        var gamma = _calGamma ?? new[] { 1.0, 1.0, 1.0 };
        var matrix = _calMatrix ?? new[] { 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0 };
        var wp = _whitePoint ?? new[] { 1.0, 1.0, 1.0 };

        double ag = Math.Pow(Math.Clamp(a, 0, 1), gamma[0]);
        double bg = Math.Pow(Math.Clamp(b, 0, 1), gamma[1]);
        double cg = Math.Pow(Math.Clamp(c, 0, 1), gamma[2]);

        double x = matrix[0] * ag + matrix[3] * bg + matrix[6] * cg;
        double y = matrix[1] * ag + matrix[4] * bg + matrix[7] * cg;
        double z = matrix[2] * ag + matrix[5] * bg + matrix[8] * cg;

        return XyzToRgb(wp[0] * x, wp[1] * y, wp[2] * z);
    }

    private static (double, double, double) CmykToRgb(double c, double m, double y, double k)
    {
        return ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
    }

    private static (double R, double G, double B) XyzToRgb(double x, double y, double z)
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

    private static (double, double, double) LabToRgb(double L, double a, double b)
    {
        double fy = (L + 16) / 116.0;
        double fx = a / 500.0 + fy;
        double fz = fy - b / 200.0;
        double x = 0.96422 * Fcube(fx);
        double y = 1.00000 * Fcube(fy);
        double z = 0.82521 * Fcube(fz);
        double r = 3.1338561 * x - 1.6168667 * y - 0.4906146 * z;
        double g = -0.9787684 * x + 1.9161415 * y + 0.0334540 * z;
        double bv = 0.0719453 * x - 0.2289914 * y + 1.4052427 * z;
        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(bv, 0, 1));
    }

    private static double Fcube(double t) => t > 0.206897 ? t * t * t : (t - 16.0 / 116) / 7.787;

    private static double[] GetNumberArray(
        PdfDictionary? dict,
        string key,
        double[] fallback,
        int expected)
    {
        if (dict?.GetOptional(key) is not PdfArray arr || arr.Count < expected)
            return fallback;

        var values = new double[expected];
        for (int i = 0; i < expected; i++)
            values[i] = arr.GetNumber(i);
        return values;
    }
}
