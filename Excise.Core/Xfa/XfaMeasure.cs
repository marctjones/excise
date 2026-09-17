using System.Globalization;

namespace Excise.Core.Xfa;

/// <summary>
/// XFA measurements ("0.25in", "12.7mm", "10pt") converted to PDF points.
/// </summary>
/// <remarks>
/// A number with no unit is in inches for lengths (XFA 3.3, "Measurements").
/// Font sizes pass <c>defaultUnit: "pt"</c>. pdf.js reads a bare number as
/// points everywhere; Designer always writes a unit, so the two agree on real
/// forms.
/// </remarks>
internal static class XfaMeasure
{
    /// <summary>Parse <paramref name="text"/>; null when absent or unparseable.</summary>
    public static double? Parse(string? text, string defaultUnit = "in")
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var s = text.Trim();
        int end = 0;
        if (end < s.Length && (s[end] == '+' || s[end] == '-'))
            end++;
        while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] == '.'))
            end++;

        if (!double.TryParse(s.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        var unit = s[end..].Trim();
        if (unit.Length == 0)
            unit = defaultUnit;

        double points = unit.ToLowerInvariant() switch
        {
            "in" => value * 72,
            "cm" => value / 2.54 * 72,
            "mm" => value / 25.4 * 72,
            "pt" => value,
            "px" => value,
            "mp" => value / 1000,
            _ => double.NaN,
        };

        if (double.IsNaN(points))
            return null;

        // A page is at most a few metres; anything beyond is hostile or broken.
        return Math.Clamp(points, -1_000_000, 1_000_000);
    }

    /// <summary>Parse <paramref name="text"/>, or <paramref name="fallback"/>.</summary>
    public static double Parse(string? text, double fallback, string defaultUnit = "in")
        => Parse(text, defaultUnit) ?? fallback;

    /// <summary>Parse a whitespace-separated list, e.g. <c>columnWidths</c>.</summary>
    public static List<double> ParseList(string? text)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Count >= 1000)
                break;
            result.Add(Parse(part) ?? -1);
        }
        return result;
    }
}
