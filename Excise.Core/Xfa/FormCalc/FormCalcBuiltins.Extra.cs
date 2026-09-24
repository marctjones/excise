using System.Globalization;
using System.Text;

namespace Excise.Core.Xfa.FormCalc;

// Dates and times, financial functions and picture clauses. Dates count days from 1 = 1900-01-01
// (no 1900 leap-day quirk) and times count milliseconds; the locale is fixed to en-US, so the locale
// arguments are accepted and ignored.
internal static partial class FormCalcBuiltins
{
    private static readonly DateTime DayZero = new(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly string[] MonthNames = CultureInfo.GetCultureInfo("en-US").DateTimeFormat.MonthNames[..12];
    private static readonly string[] MonthAbbr = CultureInfo.GetCultureInfo("en-US").DateTimeFormat.AbbreviatedMonthNames[..12];
    private static readonly string[] DayNames = CultureInfo.GetCultureInfo("en-US").DateTimeFormat.DayNames;
    private static readonly string[] DayAbbr = CultureInfo.GetCultureInfo("en-US").DateTimeFormat.AbbreviatedDayNames;

    private static readonly string[] DateStyles = { "M/D/YY", "M/D/YY", "MMM D, YYYY", "MMMM D, YYYY", "EEEE, MMMM D, YYYY" };
    private static readonly string[] TimeStyles = { "h:MM A", "h:MM A", "h:MM:SS A", "h:MM:SS A Z", "h:MM:SS A Z" };

    private static DateTime NumToDate(double n)
    {
        if (double.IsNaN(n) || n < 1 || n > 2_958_465) throw new FormCalcRuntimeException("A date number is out of range.");
        return DayZero.AddDays(Math.Floor(n));
    }

    private static double DateToNum(DateTime d) => (d.Date - DayZero).Days;

    private static void RegisterDatesAndTimes()
    {
        Add("Date", 0, 0, (_, _) => DateToNum(DateTime.Now));
        Add("Time", 0, 0, (_, _) => Math.Floor(DateTime.UtcNow.TimeOfDay.TotalMilliseconds));
        Add("DateFmt", 0, 2, (_, a) => DateStyles[Math.Clamp(ToInt(N(a, 0, 0)), 0, 4)]);
        Add("LocalDateFmt", 0, 2, (_, a) => DateStyles[Math.Clamp(ToInt(N(a, 0, 0)), 0, 4)]);
        Add("TimeFmt", 0, 2, (_, a) => TimeStyles[Math.Clamp(ToInt(N(a, 0, 0)), 0, 4)]);
        Add("LocalTimeFmt", 0, 2, (_, a) => TimeStyles[Math.Clamp(ToInt(N(a, 0, 0)), 0, 4)]);
        Add("Num2Date", 1, 3, (_, a) => FormatDate(NumToDate(N(a, 0)), a.Count > 1 ? S(a, 1) : DateStyles[0]));
        Add("Date2Num", 1, 3, (_, a) =>
            ParseDate(S(a, 0), a.Count > 1 ? S(a, 1) : DateStyles[0]) is { } d ? DateToNum(d) : throw new FormCalcRuntimeException("Date2Num: not a date in that picture."));
        Add("IsoDate2Num", 1, 1, (_, a) =>
            ParseDate(S(a, 0), S(a, 0).Contains('-', StringComparison.Ordinal) ? "YYYY-MM-DD" : "YYYYMMDD") is { } d
                ? DateToNum(d) : throw new FormCalcRuntimeException("IsoDate2Num: not an ISO date."));
        Add("Num2Time", 1, 3, (_, a) => FormatTime(TimeSpan.FromMilliseconds(Math.Clamp(N(a, 0), 0, 86_399_999)), a.Count > 1 ? S(a, 1) : TimeStyles[0]));
        Add("Num2GMTime", 1, 3, (_, a) => FormatTime(TimeSpan.FromMilliseconds(Math.Clamp(N(a, 0), 0, 86_399_999)), a.Count > 1 ? S(a, 1) : TimeStyles[0]));
        Add("Time2Num", 1, 3, (_, a) =>
            ParseTime(S(a, 0), a.Count > 1 ? S(a, 1) : TimeStyles[0]) is { } t ? t.TotalMilliseconds : throw new FormCalcRuntimeException("Time2Num: not a time in that picture."));
        Add("IsoTime2Num", 1, 1, (_, a) =>
            ParseTime(S(a, 0).TrimEnd('Z'), S(a, 0).Contains(':', StringComparison.Ordinal) ? "HH:MM:SS" : "HHMMSS") is { } t
                ? t.TotalMilliseconds : throw new FormCalcRuntimeException("IsoTime2Num: not an ISO time."));
    }

    /// <summary>Date picture: D DD DDD DDDD, M MM MMM MMMM, YY YYYY, E EEE EEEE (weekday), quoted text is literal.</summary>
    private static string FormatDate(DateTime d, string picture)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < picture.Length;)
        {
            var c = picture[i];
            if (c == '\'')
            {
                var end = picture.IndexOf('\'', i + 1);
                if (end < 0) end = picture.Length;
                sb.Append(picture, i + 1, end - i - 1);
                i = end + 1;
                continue;
            }
            var run = 1;
            while (i + run < picture.Length && picture[i + run] == c) run++;
            switch (c)
            {
                case 'D':
                    sb.Append(run switch
                    {
                        1 => d.Day.ToString(Inv),
                        2 => d.Day.ToString("00", Inv),
                        3 => d.DayOfYear.ToString(Inv),
                        _ => d.DayOfYear.ToString("000", Inv),
                    });
                    break;
                case 'M':
                    sb.Append(run switch
                    {
                        1 => d.Month.ToString(Inv),
                        2 => d.Month.ToString("00", Inv),
                        3 => MonthAbbr[d.Month - 1],
                        _ => MonthNames[d.Month - 1],
                    });
                    break;
                case 'Y':
                    sb.Append(run <= 2 ? (d.Year % 100).ToString("00", Inv) : d.Year.ToString("0000", Inv));
                    break;
                case 'E':
                    sb.Append(run switch
                    {
                        1 => ((int)d.DayOfWeek + 1).ToString(Inv),
                        <= 3 => DayAbbr[(int)d.DayOfWeek],
                        _ => DayNames[(int)d.DayOfWeek],
                    });
                    break;
                case 'G':
                    sb.Append("AD");
                    break;
                default:
                    sb.Append(c, run);
                    break;
            }
            i += run;
        }
        return sb.ToString();
    }

    /// <summary>Time picture: h hh (1-12), H HH (0-23), M MM, S SS, FFF, A (AM/PM), Z (GMT).</summary>
    private static string FormatTime(TimeSpan t, string picture)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < picture.Length;)
        {
            var c = picture[i];
            if (c == '\'')
            {
                var end = picture.IndexOf('\'', i + 1);
                if (end < 0) end = picture.Length;
                sb.Append(picture, i + 1, end - i - 1);
                i = end + 1;
                continue;
            }
            var run = 1;
            while (i + run < picture.Length && picture[i + run] == c) run++;
            var hour12 = t.Hours % 12 == 0 ? 12 : t.Hours % 12;
            switch (c)
            {
                case 'h': sb.Append(run == 1 ? hour12.ToString(Inv) : hour12.ToString("00", Inv)); break;
                case 'H': sb.Append(run == 1 ? t.Hours.ToString(Inv) : t.Hours.ToString("00", Inv)); break;
                case 'M': sb.Append(run == 1 ? t.Minutes.ToString(Inv) : t.Minutes.ToString("00", Inv)); break;
                case 'S': sb.Append(run == 1 ? t.Seconds.ToString(Inv) : t.Seconds.ToString("00", Inv)); break;
                case 'F': sb.Append(t.Milliseconds.ToString("000", Inv)); break;
                case 'A': sb.Append(t.Hours < 12 ? "AM" : "PM"); break;
                case 'Z': sb.Append("GMT"); break;
                default: sb.Append(c, run); break;
            }
            i += run;
        }
        return sb.ToString();
    }

    private static DateTime? ParseDate(string text, string picture)
    {
        int year = 1900, month = 1, day = 1, pos = 0;
        try
        {
            for (var i = 0; i < picture.Length;)
            {
                var c = picture[i];
                var run = 1;
                while (i + run < picture.Length && picture[i + run] == c) run++;
                switch (c)
                {
                    case 'D' when run <= 2: day = ReadInt(text, ref pos, run == 2 ? 2 : 2, run == 2); break;
                    case 'M' when run <= 2: month = ReadInt(text, ref pos, 2, run == 2); break;
                    case 'M':
                    {
                        var names = run == 3 ? MonthAbbr : MonthNames;
                        var found = Array.FindIndex(names, n => string.CompareOrdinal(text, pos, n, 0, n.Length) == 0);
                        if (found < 0) return null;
                        month = found + 1;
                        pos += names[found].Length;
                        break;
                    }
                    case 'Y':
                        year = ReadInt(text, ref pos, run <= 2 ? 2 : 4, true);
                        if (run <= 2) year += year < 30 ? 2000 : 1900;
                        break;
                    default:
                        for (var k = 0; k < run; k++)
                        {
                            if (pos >= text.Length || text[pos] != c) return null;
                            pos++;
                        }
                        break;
                }
                i += run;
            }
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    private static TimeSpan? ParseTime(string text, string picture)
    {
        int hour = 0, minute = 0, second = 0, ms = 0, pos = 0;
        bool? pm = null;
        try
        {
            for (var i = 0; i < picture.Length;)
            {
                var c = picture[i];
                var run = 1;
                while (i + run < picture.Length && picture[i + run] == c) run++;
                switch (c)
                {
                    case 'h': case 'H': hour = ReadInt(text, ref pos, 2, run == 2); break;
                    case 'M': minute = ReadInt(text, ref pos, 2, run == 2); break;
                    case 'S': second = ReadInt(text, ref pos, 2, run == 2); break;
                    case 'F': ms = ReadInt(text, ref pos, 3, true); break;
                    case 'A':
                        if (pos + 2 > text.Length) return null;
                        pm = string.Compare(text, pos, "PM", 0, 2, StringComparison.OrdinalIgnoreCase) == 0;
                        pos += 2;
                        break;
                    case 'Z':
                        pos = Math.Min(text.Length, pos + 3);
                        break;
                    default:
                        for (var k = 0; k < run; k++)
                        {
                            if (pos >= text.Length || text[pos] != c) return null;
                            pos++;
                        }
                        break;
                }
                i += run;
            }
            if (pm == true && hour < 12) hour += 12;
            if (pm == false && hour == 12) hour = 0;
            if (hour > 23 || minute > 59 || second > 59) return null;
            return new TimeSpan(0, hour, minute, second, ms);
        }
        catch (FormatException) { return null; }
    }

    private static int ReadInt(string text, ref int pos, int maxDigits, bool exact)
    {
        var start = pos;
        while (pos < text.Length && pos - start < maxDigits && char.IsAsciiDigit(text[pos])) pos++;
        if (pos == start || (exact && pos - start < maxDigits && maxDigits > 2 && pos - start < 2))
            throw new FormatException();
        return int.Parse(text.AsSpan(start, pos - start), NumberStyles.None, Inv);
    }

    // Financial ---------------------------------------------------------------------------

    private static double Pow(double b, double e) => Math.Pow(b, e);

    private static void RegisterFinancial()
    {
        Add("FV", 3, 3, (_, a) =>
        {
            var pmt = N(a, 0); var rate = N(a, 1); var n = N(a, 2);
            if (rate == 0) return pmt * n;
            return pmt * (Pow(1 + rate, n) - 1) / rate;
        });
        Add("PV", 3, 3, (_, a) =>
        {
            var pmt = N(a, 0); var rate = N(a, 1); var n = N(a, 2);
            if (rate == 0) return pmt * n;
            return pmt * (1 - Pow(1 + rate, -n)) / rate;
        });
        Add("Pmt", 3, 3, (_, a) =>
        {
            var principal = N(a, 0); var rate = N(a, 1); var n = N(a, 2);
            if (n == 0) throw new FormCalcRuntimeException("Pmt over no periods.");
            if (rate == 0) return principal / n;
            return principal * rate / (1 - Pow(1 + rate, -n));
        });
        Add("Term", 3, 3, (_, a) =>
        {
            var pmt = N(a, 0); var rate = N(a, 1); var fv = N(a, 2);
            if (pmt == 0 || rate <= -1) throw new FormCalcRuntimeException("Term is undefined for those values.");
            return Math.Log(1 + fv * rate / pmt) / Math.Log(1 + rate);
        });
        Add("CTerm", 3, 3, (_, a) =>
        {
            var rate = N(a, 0); var fv = N(a, 1); var pv = N(a, 2);
            if (pv == 0 || rate <= -1 || fv / pv <= 0) throw new FormCalcRuntimeException("CTerm is undefined for those values.");
            return Math.Log(fv / pv) / Math.Log(1 + rate);
        });
        Add("Rate", 3, 3, (_, a) =>
        {
            var fv = N(a, 0); var pv = N(a, 1); var n = N(a, 2);
            if (pv == 0 || n == 0 || fv / pv <= 0) throw new FormCalcRuntimeException("Rate is undefined for those values.");
            return Pow(fv / pv, 1 / n) - 1;
        });
        Add("NPV", 2, int.MaxValue, (i, a) =>
        {
            var rate = N(a, 0);
            if (rate <= -1) throw new FormCalcRuntimeException("NPV needs a rate above -1.");
            double total = 0;
            var period = 1;
            foreach (var cash in Numbers(i, a.Skip(1).ToList()))
                total += cash / Pow(1 + rate, period++);
            return total;
        });
        Add("Apr", 3, 3, (_, a) =>
        {
            var principal = N(a, 0); var pmt = N(a, 1); var n = N(a, 2);
            if (principal <= 0 || pmt <= 0 || n <= 0 || pmt * n <= principal)
                throw new FormCalcRuntimeException("Apr is undefined for those values.");
            double lo = 0, hi = 1;
            for (var k = 0; k < 200; k++)
            {
                var mid = (lo + hi) / 2;
                var payment = mid == 0 ? principal / n : principal * mid / (1 - Pow(1 + mid, -n));
                if (payment > pmt) hi = mid; else lo = mid;
            }
            return (lo + hi) / 2 * 12;
        });
        Add("IPmt", 5, 5, (i, a) => Amortise(i, a, interest: true));
        Add("PPmt", 5, 5, (i, a) => Amortise(i, a, interest: false));
    }

    /// <summary>Interest (or principal) paid from period <c>first</c> for <c>count</c> periods.</summary>
    private static double Amortise(FormCalcInterpreter interp, IReadOnlyList<object?> a, bool interest)
    {
        var balance = N(a, 0); var rate = N(a, 1); var payment = N(a, 2);
        var first = ToInt(N(a, 3)); var count = ToInt(N(a, 4));
        if (first < 1 || count < 0 || first + count > 100_000) throw new FormCalcRuntimeException("Amortisation periods are out of range.");
        double interestPaid = 0, principalPaid = 0;
        for (var k = 1; k < first + count; k++)
        {
            interp.Tick();
            var intr = balance * rate;
            var princ = payment - intr;
            balance -= princ;
            if (k >= first) { interestPaid += intr; principalPaid += princ; }
        }
        return interest ? interestPaid : principalPaid;
    }

    // Pictures -------------------------------------------------------------------------------

    private static void RegisterPictures()
    {
        Add("Format", 2, int.MaxValue, (_, a) => FormatPicture(S(a, 0), a[1]));
        Add("Parse", 2, 2, (_, a) => ParsePicture(S(a, 0), S(a, 1)));
    }

    private static (string Category, string Body) SplitPicture(string picture)
    {
        var open = picture.IndexOf('{', StringComparison.Ordinal);
        if (open > 0 && picture.EndsWith('}'))
            return (picture[..open].ToLowerInvariant(), picture[(open + 1)..^1]);
        return ("", picture);
    }

    private static object FormatPicture(string picture, object? value)
    {
        var (category, body) = SplitPicture(picture);
        switch (category)
        {
            case "date": return FormatDate(NumToDate(FormCalcValue.ToNumber(value)), body);
            case "time": return FormatTime(TimeSpan.FromMilliseconds(Math.Clamp(FormCalcValue.ToNumber(value), 0, 86_399_999)), body);
            case "text": return FormatText(body, FormCalcValue.ToText(value));
            case "num": return FormatNumber(body, FormCalcValue.ToNumber(value));
            default:
                return FormCalcValue.Scalar(value) is double d ? FormatNumber(body, d) : FormatText(body, FormCalcValue.ToText(value));
        }
    }

    /// <summary>Text picture: 9 a digit, A a letter, X any character, anything else literal.</summary>
    private static string FormatText(string picture, string text)
    {
        var sb = new StringBuilder();
        var t = 0;
        foreach (var c in picture)
        {
            if (c is '9' or 'A' or 'X' or 'a' or 'x' or '0' or 'O' or '?')
            {
                if (t >= text.Length) break;
                sb.Append(text[t++]);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Number picture: 9 a required digit, z or 8 a digit that is blank when it would be a leading zero,
    /// '.' the decimal point, ',' grouping (shown only next to digits), other characters literal.
    /// </summary>
    private static string FormatNumber(string picture, double value)
    {
        var point = picture.IndexOf('.', StringComparison.Ordinal);
        var intPart = point < 0 ? picture : picture[..point];
        var fracPart = point < 0 ? "" : picture[(point + 1)..];
        var decimals = fracPart.Count(c => c is '9' or 'z' or '8' or 'Z');
        var rounded = Math.Round(Math.Abs(value), Math.Min(decimals, 15), MidpointRounding.AwayFromZero);
        var digits = rounded.ToString("F" + Math.Min(decimals, 15).ToString(Inv), Inv);
        var dot = digits.IndexOf('.', StringComparison.Ordinal);
        var intDigits = dot < 0 ? digits : digits[..dot];
        var fracDigits = dot < 0 ? "" : digits[(dot + 1)..];

        var right = new StringBuilder();
        var idx = intDigits.Length - 1;
        var leadingSuppressed = false;
        for (var k = intPart.Length - 1; k >= 0; k--)
        {
            var c = intPart[k];
            if (c is '9' or 'z' or 'Z' or '8')
            {
                if (idx >= 0) right.Append(intDigits[idx--]);
                else if (c == '9') right.Append('0');
                else { right.Append(' '); leadingSuppressed = true; }
            }
            else if (c == ',' && (idx >= 0 || !leadingSuppressed && k > 0 && idx >= 0)) right.Append(',');
            else if (c != ',' && (idx >= 0 || !leadingSuppressed)) right.Append(c);
        }
        var chars = right.ToString().Reverse().ToArray();
        var whole = new string(chars).TrimStart();
        var result = whole;
        if (decimals > 0)
        {
            var fracOut = new StringBuilder();
            var fi = 0;
            foreach (var c in fracPart)
            {
                if (c is '9' or 'z' or 'Z' or '8') fracOut.Append(fi < fracDigits.Length ? fracDigits[fi++] : '0');
                else fracOut.Append(c);
            }
            result += "." + fracOut;
        }
        else if (point >= 0 && fracPart.Length > 0) result += "." + fracPart;
        return value < 0 ? "-" + result : result;
    }

    private static object ParsePicture(string picture, string text)
    {
        var (category, body) = SplitPicture(picture);
        switch (category)
        {
            case "date":
                return ParseDate(text, body) is { } d ? DateToNum(d) : throw new FormCalcRuntimeException("Parse: not a date in that picture.");
            case "time":
                return ParseTime(text, body) is { } t ? t.TotalMilliseconds : throw new FormCalcRuntimeException("Parse: not a time in that picture.");
            default:
                var cleaned = new string(text.Where(c => char.IsAsciiDigit(c) || c is '.' or '-').ToArray());
                return category == "num" && double.TryParse(cleaned, NumberStyles.Float, Inv, out var n) ? n : (object)text;
        }
    }
}
