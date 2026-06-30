namespace Pdfe.Core.ColorSpaces;

using System.Runtime.CompilerServices;
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
    private readonly double[]? _labRange;
    private readonly PdfColorConverter.CmykPolicy _cmykPolicy;
    private readonly PdfIccProfile? _iccProfile;
    private readonly PdfIccProfile? _iccOutputIntentProfile;
    private Dictionary<TintColorCacheKey, (double R, double G, double B)>? _tintRgbCache;
    private static readonly ConditionalWeakTable<PdfDocument, OutputIntentProfileBox> OutputIntentProfiles = new();

    private const int MaxTintRgbCacheEntries = 4096;

    private readonly record struct TintColorCacheKey(int Length, long V0, long V1, long V2, long V3);

    private PdfColorSpace(PdfColorSpaceType type, int components,
        PdfColorSpace? indexedBase = null, byte[]? indexedLookup = null,
        PdfColorSpace? alternateSpace = null, PdfObject? tintTransform = null,
        double[]? whitePoint = null, double[]? calGamma = null, double[]? calMatrix = null,
        double[]? labRange = null,
        PdfIccProfile? iccProfile = null,
        PdfIccProfile? iccOutputIntentProfile = null,
        PdfColorConverter.CmykPolicy cmykPolicy = PdfColorConverter.CmykPolicy.ProcessScreenPreview)
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
        _labRange = labRange;
        _iccProfile = iccProfile;
        _iccOutputIntentProfile = iccOutputIntentProfile;
        _cmykPolicy = cmykPolicy;
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
        "Lab" => new PdfColorSpace(PdfColorSpaceType.Lab, 3,
            whitePoint: DefaultLabWhitePoint(),
            labRange: DefaultLabRange()),
        "Separation" => new PdfColorSpace(PdfColorSpaceType.Separation, 1),
        "Pattern" => new PdfColorSpace(PdfColorSpaceType.Pattern, 0),
        _ => new PdfColorSpace(PdfColorSpaceType.Unknown, 1)
    };

    internal static PdfColorSpace FromName(string name, PdfDocument doc)
    {
        if (name is "DeviceCMYK" or "CMYK" &&
            GetOutputIntentProfile(doc) is { } outputIntentProfile)
        {
            return new PdfColorSpace(
                PdfColorSpaceType.DeviceCMYK,
                4,
                iccProfile: outputIntentProfile);
        }

        return FromName(name);
    }

    /// <summary>
    /// Parse a color space from a PdfObject (name or array) using the document.
    /// </summary>
    public static PdfColorSpace Parse(PdfObject csObj, PdfDocument doc)
    {
        if (csObj is PdfName n)
            return FromName(n.Value, doc);

        var resolved = doc.Resolve(csObj);
        if (resolved is PdfName resolvedName)
            return FromName(resolvedName.Value, doc);

        if (resolved is PdfArray arr && arr.Count >= 1)
        {
            var typeName = (arr[0] as PdfName)?.Value ?? "";
            return typeName switch
            {
                "ICCBased" => ParseICCBased(arr, doc),
                "Indexed" => ParseIndexed(arr, doc),
                "CalGray" => ParseCalGray(arr),
                "CalRGB" => ParseCalRgb(arr),
                "Lab" => ParseLab(arr),
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
        var iccProfile = PdfIccProfile.TryParse(iccStream.DecodedData ?? iccStream.EncodedData);
        if (iccProfile != null)
            return new PdfColorSpace(
                PdfColorSpaceType.ICCBased,
                n,
                iccProfile: iccProfile,
                iccOutputIntentProfile: GetOutputIntentProfile(doc));

        return n switch
        {
            1 => DeviceGray,
            4 => new PdfColorSpace(PdfColorSpaceType.DeviceCMYK, 4,
                cmykPolicy: PdfColorConverter.CmykPolicy.ReferenceFormula),
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
            lookup = ps.Bytes;
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

    private static PdfColorSpace ParseLab(PdfArray arr)
    {
        var dict = arr.Count >= 2 ? arr[1] as PdfDictionary : null;
        var whitePoint = GetNumberArray(dict, "WhitePoint", DefaultLabWhitePoint(), expected: 3);
        var range = GetNumberArray(dict, "Range", DefaultLabRange(), expected: 4);
        return new PdfColorSpace(PdfColorSpaceType.Lab, 3, whitePoint: whitePoint, labRange: range);
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
                values.Length >= 4
                    ? (_iccProfile?.ToRgb(values) ?? CmykToRgb(values[0], values[1], values[2], values[3]))
                    : (0, 0, 0),

            PdfColorSpaceType.Lab =>
                (values.Length >= 3) ? LabToRgb(values[0], values[1], values[2]) : (0, 0, 0),

            PdfColorSpaceType.ICCBased =>
                _iccProfile?.ToOutputIntentPreviewRgb(values, _iccOutputIntentProfile) ??
                (Components == 1 ? (values[0], values[0], values[0]) :
                 Components == 4 ? (values.Length >= 4 ? CmykToRgb(values[0], values[1], values[2], values[3]) : (0, 0, 0)) :
                 (values.Length >= 3) ? (values[0], values[1], values[2]) : (0, 0, 0)),

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
            baseValues[i] = _indexedBase.DecodeSampleByte(i, _indexedLookup[offset + i]);

        return _indexedBase.ToRgb(baseValues);
    }

    internal double DecodeSampleByte(int componentIndex, byte sample)
    {
        if (Type != PdfColorSpaceType.Lab)
            return sample / 255.0;

        if (componentIndex == 0)
            return sample * (100.0 / 255.0);

        var range = _labRange ?? DefaultLabRange();
        var rangeIndex = componentIndex == 1 ? 0 : 2;
        var min = range[rangeIndex];
        var max = range[rangeIndex + 1];
        return min + (sample * ((max - min) / 255.0));
    }

    private (double R, double G, double B) ResolveTintedColor(double[] values)
    {
        if (TryCreateTintColorCacheKey(values, out var cacheKey) &&
            _tintRgbCache != null &&
            _tintRgbCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var color = ResolveTintedColorUncached(values);
        if (TryCreateTintColorCacheKey(values, out cacheKey))
        {
            _tintRgbCache ??= new Dictionary<TintColorCacheKey, (double R, double G, double B)>();
            if (_tintRgbCache.Count < MaxTintRgbCacheEntries)
                _tintRgbCache[cacheKey] = color;
        }

        return color;
    }

    private (double R, double G, double B) ResolveTintedColorUncached(double[] values)
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

    private static bool TryCreateTintColorCacheKey(double[] values, out TintColorCacheKey key)
    {
        if (values.Length is <= 0 or > 4)
        {
            key = default;
            return false;
        }

        key = new TintColorCacheKey(
            values.Length,
            values.Length > 0 ? BitConverter.DoubleToInt64Bits(values[0]) : 0,
            values.Length > 1 ? BitConverter.DoubleToInt64Bits(values[1]) : 0,
            values.Length > 2 ? BitConverter.DoubleToInt64Bits(values[2]) : 0,
            values.Length > 3 ? BitConverter.DoubleToInt64Bits(values[3]) : 0);
        return true;
    }

    private static PdfIccProfile? GetOutputIntentProfile(PdfDocument doc)
        => OutputIntentProfiles.GetValue(doc, CreateOutputIntentProfileBox).Profile;

    private static OutputIntentProfileBox CreateOutputIntentProfileBox(PdfDocument doc)
    {
        try
        {
            var outputIntentsObj = doc.Catalog.GetOptional("OutputIntents");
            if (outputIntentsObj == null || doc.Resolve(outputIntentsObj) is not PdfArray outputIntents)
                return new OutputIntentProfileBox(null);

            foreach (var item in outputIntents)
            {
                if (doc.Resolve(item) is not PdfDictionary intent)
                    continue;

                var profileObj = intent.GetOptional("DestOutputProfile");
                if (profileObj == null || doc.Resolve(profileObj) is not PdfStream profileStream)
                    continue;

                var profile = PdfIccProfile.TryParse(profileStream.DecodedData ?? profileStream.EncodedData);
                if (profile != null)
                    return new OutputIntentProfileBox(profile);
            }
        }
        catch
        {
            return new OutputIntentProfileBox(null);
        }

        return new OutputIntentProfileBox(null);
    }

    private sealed class OutputIntentProfileBox
    {
        public OutputIntentProfileBox(PdfIccProfile? profile) => Profile = profile;

        public PdfIccProfile? Profile { get; }
    }

    private (double R, double G, double B) CalGrayToRgb(double a)
    {
        var gamma = _calGamma is { Length: > 0 } ? _calGamma[0] : 1.0;
        var wp = _whitePoint ?? new[] { 1.0, 1.0, 1.0 };
        var y = Math.Pow(Math.Clamp(a, 0, 1), gamma);
        var (adaptedX, adaptedY, adaptedZ) = AdaptXyzToD65(
            wp[0] * y,
            wp[1] * y,
            wp[2] * y,
            wp);
        return XyzToRgb(adaptedX, adaptedY, adaptedZ);
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

        var (adaptedX, adaptedY, adaptedZ) = AdaptXyzToD65(
            x,
            y,
            z,
            wp);
        return XyzToRgb(adaptedX, adaptedY, adaptedZ);
    }

    internal static (double R, double G, double B) ConvertDeviceCmykToRgb(double c, double m, double y, double k)
        => PdfColorConverter.DeviceCmykProcessScreenPreviewToRgb(c, m, y, k);

    private (double R, double G, double B) CmykToRgb(double c, double m, double y, double k)
        => PdfColorConverter.CmykToRgb(c, m, y, k, _cmykPolicy);

    private static (double R, double G, double B) ConvertCmykReferenceFormulaToRgb(double c, double m, double y, double k)
        => PdfColorConverter.CmykReferenceFormulaToRgb(c, m, y, k);

    private static (double R, double G, double B) XyzToRgb(double x, double y, double z)
    {
        double r =  3.2406 * x - 1.5372 * y - 0.4986 * z;
        double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        double b =  0.0557 * x - 0.2040 * y + 1.0570 * z;
        return (EncodeSrgb(r), EncodeSrgb(g), EncodeSrgb(b));
    }

    private static (double X, double Y, double Z) AdaptXyzToD65(double x, double y, double z, double[] sourceWhite)
    {
        if (sourceWhite.Length < 3)
            return (x, y, z);

        const double d65X = 0.95047;
        const double d65Y = 1.0;
        const double d65Z = 1.08883;

        if (NearlyEqual(sourceWhite[0], d65X) &&
            NearlyEqual(sourceWhite[1], d65Y) &&
            NearlyEqual(sourceWhite[2], d65Z))
            return (x, y, z);

        var srcL = 0.8951 * sourceWhite[0] + 0.2664 * sourceWhite[1] - 0.1614 * sourceWhite[2];
        var srcM = -0.7502 * sourceWhite[0] + 1.7135 * sourceWhite[1] + 0.0367 * sourceWhite[2];
        var srcS = 0.0389 * sourceWhite[0] - 0.0685 * sourceWhite[1] + 1.0296 * sourceWhite[2];
        if (Math.Abs(srcL) < 1e-9 || Math.Abs(srcM) < 1e-9 || Math.Abs(srcS) < 1e-9)
            return (x, y, z);

        var dstL = 0.8951 * d65X + 0.2664 * d65Y - 0.1614 * d65Z;
        var dstM = -0.7502 * d65X + 1.7135 * d65Y + 0.0367 * d65Z;
        var dstS = 0.0389 * d65X - 0.0685 * d65Y + 1.0296 * d65Z;

        var l = 0.8951 * x + 0.2664 * y - 0.1614 * z;
        var m = -0.7502 * x + 1.7135 * y + 0.0367 * z;
        var s = 0.0389 * x - 0.0685 * y + 1.0296 * z;

        l *= dstL / srcL;
        m *= dstM / srcM;
        s *= dstS / srcS;

        return (
            0.9869929 * l - 0.1470543 * m + 0.1599627 * s,
            0.4323053 * l + 0.5183603 * m + 0.0492912 * s,
            -0.0085287 * l + 0.0400428 * m + 0.9684867 * s);
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 1e-5;

    private static double EncodeSrgb(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value <= 0.0031308
            ? 12.92 * value
            : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
    }

    private (double, double, double) LabToRgb(double L, double a, double b)
    {
        double fy = (L + 16) / 116.0;
        double fx = a / 500.0 + fy;
        double fz = fy - b / 200.0;
        var whitePoint = _whitePoint ?? DefaultLabWhitePoint();
        double x = whitePoint[0] * Fcube(fx);
        double y = whitePoint[1] * Fcube(fy);
        double z = whitePoint[2] * Fcube(fz);
        var (adaptedX, adaptedY, adaptedZ) = AdaptXyzToD65(x, y, z, whitePoint);
        return XyzToRgb(adaptedX, adaptedY, adaptedZ);
    }

    private static double Fcube(double t) => t > 0.206897 ? t * t * t : (t - 16.0 / 116) / 7.787;

    private static double[] DefaultLabWhitePoint() => new[] { 0.96422, 1.00000, 0.82521 };

    private static double[] DefaultLabRange() => new[] { -100.0, 100.0, -100.0, 100.0 };

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
