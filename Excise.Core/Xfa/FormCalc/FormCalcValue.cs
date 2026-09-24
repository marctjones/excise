using System.Globalization;

namespace Excise.Core.Xfa.FormCalc;

/// <summary>
/// FormCalc's loose values: a value is a double, a string, null, an object or a list of objects.
/// Numbers and strings convert freely; a script object reads as its <c>rawValue</c>.
/// </summary>
internal static class FormCalcValue
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The scalar behind a value: a node reads as its rawValue, an empty list as null.</summary>
    public static object? Scalar(object? v)
    {
        switch (v)
        {
            case IFcObject o:
                return o.TryGetProperty("rawValue", out var raw) ? Scalar(raw) : null;
            case FcNodeList list:
                return list.Count == 0 ? null : Scalar(list[0]);
            case int i: return (double)i;
            case bool b: return b ? 1d : 0d;
            default: return v;
        }
    }

    public static double ToNumber(object? v)
    {
        switch (Scalar(v))
        {
            case double d: return d;
            case string s: return ParseNumber(s);
            default: return 0;
        }
    }

    /// <summary>A string that is not a number reads as 0.</summary>
    public static double ParseNumber(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        return double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : 0;
    }

    public static bool LooksNumeric(string s) =>
        s.Trim().Length > 0 && double.TryParse(s.Trim(), NumberStyles.Float, Inv, out _);

    public static string ToText(object? v)
    {
        switch (Scalar(v))
        {
            case double d: return NumberToText(d);
            case string s: return s;
            default: return "";
        }
    }

    public static string NumberToText(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return d.ToString("0", Inv);
        var s = d.ToString("G15", Inv);
        if (s.Contains('E', StringComparison.Ordinal))
            s = d.ToString("0.###############", Inv);
        return s;
    }

    public static bool IsTrue(object? v) => ToNumber(v) != 0;

    public static double Bool(bool b) => b ? 1 : 0;

    public static bool IsNull(object? v) => Scalar(v) == null;

    /// <summary>-1, 0, 1: two strings compare as text, anything else as numbers (null is 0).</summary>
    public static int Compare(object? a, object? b)
    {
        var x = Scalar(a);
        var y = Scalar(b);
        if (x is string sx && y is string sy)
            return Math.Sign(string.CompareOrdinal(sx, sy));
        return ToNumber(x).CompareTo(ToNumber(y));
    }
}
