using System.Globalization;

namespace Excise.Core.Primitives;

/// <summary>
/// PDF date strings, ISO 32000-2 §7.9.4: <c>D:YYYYMMDDHHmmSSOHH'mm</c>. Every field after the year is
/// optional, a missing offset means GMT, and PDF 1.7's trailing apostrophe is still accepted. Text the
/// grammar does not allow (ISO 8601 <c>2024-01-02</c>, <c>T</c> separators) is not a date.
/// </summary>
internal static class PdfDate
{
    /// <summary>The instant a PDF date names, or null when <paramref name="raw"/> holds no valid date.</summary>
    internal static DateTimeOffset? Parse(string? raw)
    {
        if (raw is null)
            return null;

        var i = raw.StartsWith("D:", StringComparison.Ordinal) ? 2 : 0;
        if (Digits(raw, ref i, 4) is not { } year)
            return null;
        var month = Digits(raw, ref i, 2) ?? 1;
        var day = Digits(raw, ref i, 2) ?? 1;
        var hour = Digits(raw, ref i, 2) ?? 0;
        var minute = Digits(raw, ref i, 2) ?? 0;
        var second = Digits(raw, ref i, 2) ?? 0;

        var offset = TimeSpan.Zero;
        if (i < raw.Length && raw[i] != 'Z')
        {
            if (raw[i] is not ('+' or '-'))
                return null;
            var negative = raw[i++] == '-';
            if (Digits(raw, ref i, 2) is not { } offsetHours || (i < raw.Length && raw[i++] != '\''))
                return null;
            offset = new TimeSpan(offsetHours, Digits(raw, ref i, 2) ?? 0, 0);
            if (negative)
                offset = -offset;
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The PDF 1.7 spelling, e.g. <c>D:20260725093000+05'30'</c>; the local time keeps its offset.</summary>
    internal static string Format(DateTimeOffset time)
    {
        var magnitude = time.Offset.Duration();
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{time:yyyyMMddHHmmss}{(time.Offset < TimeSpan.Zero ? '-' : '+')}{magnitude.Hours:D2}'{magnitude.Minutes:D2}'");
    }

    private static int? Digits(string s, ref int i, int count)
    {
        if (i + count > s.Length)
            return null;
        var value = 0;
        for (var k = i; k < i + count; k++)
        {
            if (!char.IsAsciiDigit(s[k]))
                return null;
            value = value * 10 + (s[k] - '0');
        }
        i += count;
        return value;
    }
}
