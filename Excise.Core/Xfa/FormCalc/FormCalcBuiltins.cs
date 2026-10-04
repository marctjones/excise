using System.Globalization;
using System.Text;

namespace Excise.Core.Xfa.FormCalc;

internal delegate object? FcBuiltin(FormCalcInterpreter interpreter, IReadOnlyList<object?> args);

/// <summary>
/// The pure FormCalc built-in functions: arithmetic, logical and string. Dates, times, financial and
/// picture-clause functions are in the other parts of this class. Names are case-insensitive.
/// Deliberately absent (not stubbed): Get, Post and Put, and every host-application function.
/// </summary>
internal static partial class FormCalcBuiltins
{
    private static readonly Dictionary<string, FcBuiltin> Table = new(StringComparer.OrdinalIgnoreCase);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool TryGet(string name, out FcBuiltin builtin) => Table.TryGetValue(name, out builtin!);

    public static IReadOnlyCollection<string> Names => Table.Keys;

    static FormCalcBuiltins()
    {
        RegisterCore();
        RegisterDatesAndTimes();
        RegisterFinancial();
        RegisterPictures();
    }

    private static void Add(string name, int min, int max, Func<FormCalcInterpreter, IReadOnlyList<object?>, object?> body) =>
        Table[name] = (interp, args) =>
        {
            if (args.Count < min || args.Count > max)
                throw new FormCalcRuntimeException($"{name} takes {(min == max ? min.ToString(Inv) : $"{min} to {(max == int.MaxValue ? "many" : max.ToString(Inv))}")} argument(s), not {args.Count}.");
            interp.Tick();
            interp.CheckTimeAndCancellation();
            var result = body(interp, args);
            interp.CheckTimeAndCancellation();
            if (result is string text) interp.ChargeString(text);
            return result;
        };

    private static double N(IReadOnlyList<object?> a, int i, double dflt = 0) =>
        i < a.Count && !FormCalcValue.IsNull(a[i]) ? FormCalcValue.ToNumber(a[i]) : dflt;

    private static string S(IReadOnlyList<object?> a, int i) => i < a.Count ? FormCalcValue.ToText(a[i]) : "";

    /// <summary>Numbers from the arguments: a list (<c>a[*]</c>) expands, null and non-numbers are skipped.</summary>
    private static IEnumerable<double> Numbers(FormCalcInterpreter interp, IReadOnlyList<object?> args)
    {
        foreach (var arg in args)
        {
            if (arg is FcNodeList list)
            {
                foreach (var o in list)
                {
                    interp.Tick();
                    if (FormCalcValue.Scalar(o) is { } v && IsNumeric(v)) yield return FormCalcValue.ToNumber(v);
                }
            }
            else if (FormCalcValue.Scalar(arg) is { } v && IsNumeric(v))
                yield return FormCalcValue.ToNumber(v);
        }
    }

    private static bool IsNumeric(object v) => v is double || (v is string s && FormCalcValue.LooksNumeric(s));

    private static int ToInt(double d) => double.IsNaN(d) ? 0 : (int)Math.Clamp(d, int.MinValue, int.MaxValue);

    /// <summary>Linear ordinal UTF-16 search with bounded work between deadline checks (#1923).</summary>
    private sealed class OrdinalSearch
    {
        private readonly FormCalcInterpreter _interpreter;
        private readonly string _text;
        private readonly string _needle;
        private readonly int[] _prefix;
        private int _work;

        public OrdinalSearch(FormCalcInterpreter interpreter, string text, string needle)
        {
            _interpreter = interpreter;
            _text = text;
            _needle = needle;
            interpreter.CheckTimeAndCancellation();
            _prefix = new int[needle.Length];
            for (int position = 1, matched = 0; position < needle.Length; position++)
            {
                Checkpoint();
                while (matched > 0 && needle[position] != needle[matched])
                {
                    matched = _prefix[matched - 1];
                    Checkpoint();
                }
                if (needle[position] == needle[matched]) matched++;
                _prefix[position] = matched;
            }
        }

        public int Find(int start)
        {
            _interpreter.CheckTimeAndCancellation();
            if (start > _text.Length - _needle.Length) return -1;
            for (int position = start, matched = 0; position < _text.Length; position++)
            {
                Checkpoint();
                while (matched > 0 && _text[position] != _needle[matched])
                {
                    matched = _prefix[matched - 1];
                    Checkpoint();
                }
                if (_text[position] == _needle[matched]) matched++;
                if (matched == _needle.Length) return position - matched + 1;
            }
            return -1;
        }

        private void Checkpoint()
        {
            if ((++_work & 0x3FF) == 0)
                _interpreter.CheckTimeAndCancellation();
        }
    }

    private static void RegisterCore()
    {
        // Arithmetic
        Add("Abs", 1, 1, (_, a) => Math.Abs(N(a, 0)));
        Add("Avg", 1, int.MaxValue, (i, a) =>
        {
            var v = Numbers(i, a).ToList();
            if (v.Count == 0) throw new FormCalcRuntimeException("Avg of no numbers.");
            return v.Sum() / v.Count;
        });
        Add("Ceil", 1, 1, (_, a) => Math.Ceiling(N(a, 0)));
        Add("Count", 1, int.MaxValue, (i, a) =>
        {
            double n = 0;
            foreach (var arg in a)
            {
                if (arg is FcNodeList list) { foreach (var o in list) { i.Tick(); if (!FormCalcValue.IsNull(o)) n++; } }
                else if (!FormCalcValue.IsNull(arg)) n++;
            }
            return n;
        });
        Add("Floor", 1, 1, (_, a) => Math.Floor(N(a, 0)));
        Add("Max", 1, int.MaxValue, (i, a) => { var v = Numbers(i, a).ToList(); return v.Count == 0 ? 0d : v.Max(); });
        Add("Min", 1, int.MaxValue, (i, a) => { var v = Numbers(i, a).ToList(); return v.Count == 0 ? 0d : v.Min(); });
        Add("Mod", 2, 2, (_, a) =>
        {
            var d = N(a, 1);
            if (d == 0) throw new FormCalcRuntimeException("Mod by zero.");
            return N(a, 0) % d;
        });
        Add("Round", 1, 2, (_, a) =>
        {
            var digits = Math.Clamp(ToInt(N(a, 1)), 0, 15);
            return Math.Round(N(a, 0), digits, MidpointRounding.AwayFromZero);
        });
        Add("Sum", 1, int.MaxValue, (i, a) => Numbers(i, a).Sum());

        // Logical
        Add("Choose", 2, int.MaxValue, (_, a) =>
        {
            var index = ToInt(Math.Floor(N(a, 0)));
            return index >= 1 && index < a.Count ? FormCalcValue.Scalar(a[index]) : "";
        });
        Add("Exists", 1, 1, (_, a) => FormCalcValue.Bool(a[0] switch
        {
            FcNodeList l => l.Count > 0,
            IFcObject => true,
            _ => false,
        }));
        Add("HasValue", 1, 1, (_, a) => FormCalcValue.Bool(FormCalcValue.Scalar(a[0]) switch
        {
            null => false,
            string s => s.Trim().Length > 0,
            _ => true,
        }));
        Add("Oneof", 2, int.MaxValue, (i, a) =>
        {
            var probe = a[0];
            for (var k = 1; k < a.Count; k++)
            {
                if (a[k] is FcNodeList list)
                {
                    foreach (var o in list) if (FormCalcValue.Compare(probe, o) == 0) return 1d;
                }
                else if (FormCalcValue.Compare(probe, a[k]) == 0) return 1d;
            }
            return 0d;
        });
        Add("Within", 3, 3, (_, a) =>
            FormCalcValue.Bool(FormCalcValue.Compare(a[0], a[1]) >= 0 && FormCalcValue.Compare(a[0], a[2]) <= 0));
        Add("If", 3, 3, (_, a) => FormCalcValue.IsTrue(a[0]) ? FormCalcValue.Scalar(a[1]) : FormCalcValue.Scalar(a[2]));
        Add("Null", 0, 0, (_, _) => null);
        Add("Eval", 1, 1, (i, a) => i.RunNested(S(a, 0)));

        // String
        Add("At", 2, 2, (i, a) =>
        {
            var text = S(a, 0);
            var sub = S(a, 1);
            return sub.Length == 0 || sub.Length > text.Length ? 0d : new OrdinalSearch(i, text, sub).Find(0) + 1d;
        });
        Add("Concat", 0, int.MaxValue, (i, a) =>
        {
            var sb = new StringBuilder();
            foreach (var arg in a)
            {
                i.CheckTimeAndCancellation();
                sb.Append(FormCalcValue.ToText(arg));
                i.CheckLength(sb.Length);
            }
            return sb.ToString();
        });
        Add("Left", 2, 2, (_, a) => { var s = S(a, 0); var n = Math.Clamp(ToInt(N(a, 1)), 0, s.Length); return s[..n]; });
        Add("Right", 2, 2, (_, a) => { var s = S(a, 0); var n = Math.Clamp(ToInt(N(a, 1)), 0, s.Length); return s[(s.Length - n)..]; });
        Add("Len", 1, 1, (_, a) => (double)S(a, 0).Length);
        Add("Lower", 1, 2, (_, a) => S(a, 0).ToLowerInvariant());
        Add("Upper", 1, 2, (_, a) => S(a, 0).ToUpperInvariant());
        Add("Ltrim", 1, 1, (_, a) => S(a, 0).TrimStart());
        Add("Rtrim", 1, 1, (_, a) => S(a, 0).TrimEnd());
        Add("Replace", 2, 3, (i, a) =>
        {
            var s = S(a, 0);
            var old = S(a, 1);
            if (old.Length == 0) return s;
            var replacement = S(a, 2);
            if (old.Length > s.Length) return s;
            var search = new OrdinalSearch(i, s, old);
            long matches = 0;
            for (var at = search.Find(0); at >= 0; at = search.Find(at + old.Length))
                matches++;
            if (matches == 0) return s;
            // Preflight before allocating output; scans are cancellable and linear (#1923).
            var length = s.Length + matches * (replacement.Length - old.Length);
            if (length > i.Limits.MaxStringLength)
                throw new FormCalcRuntimeException($"A string grew past {i.Limits.MaxStringLength} characters.");
            var output = new StringBuilder((int)length);
            var position = 0;
            for (var at = search.Find(0); at >= 0; at = search.Find(position))
            {
                i.CheckTimeAndCancellation();
                output.Append(s, position, at - position).Append(replacement);
                position = at + old.Length;
            }
            return output.Append(s, position, s.Length - position).ToString();
        });
        Add("Space", 1, 1, (i, a) =>
        {
            var n = ToInt(N(a, 0));
            if (n < 0) throw new FormCalcRuntimeException("Space of a negative count.");
            if (n > i.Limits.MaxStringLength) throw new FormCalcRuntimeException("Space is too long.");
            return new string(' ', n);
        });
        Add("Substr", 3, 3, (_, a) =>
        {
            var s = S(a, 0);
            var start = ToInt(Math.Floor(N(a, 1)));
            var count = ToInt(Math.Floor(N(a, 2)));
            if (start < 1) { count += start - 1; start = 1; }
            if (count <= 0 || start > s.Length) return "";
            return s.Substring(start - 1, Math.Min(count, s.Length - start + 1));
        });
        Add("Str", 1, 3, (_, a) =>
        {
            var width = Math.Clamp(ToInt(N(a, 1, 10)), 0, 1000);
            var precision = Math.Clamp(ToInt(N(a, 2, 0)), 0, 15);
            var rounded = Math.Round(N(a, 0), precision, MidpointRounding.AwayFromZero);
            var text = rounded.ToString("F" + precision.ToString(Inv), Inv);
            return text.Length > width ? new string('*', width) : text.PadLeft(width);
        });
        Add("Uuid", 0, 1, (_, _) => Guid.NewGuid().ToString());
        Add("WordNum", 1, 3, (_, a) => WordNum(N(a, 0), ToInt(N(a, 1, 0))));
        Add("Encode", 2, 2, (_, a) => Encode(S(a, 0), S(a, 1)));
        Add("Decode", 2, 2, (_, a) => Decode(S(a, 0), S(a, 1)));
        Add("UnitValue", 1, 2, (_, a) => UnitValue(S(a, 0), a.Count > 1 ? S(a, 1) : null));
        Add("UnitType", 1, 1, (_, a) => UnitType(S(a, 0)));
    }

    // Strings ------------------------------------------------------------------------

    private static string Encode(string s, string kind) => kind.ToLowerInvariant() switch
    {
        "url" => Uri.EscapeDataString(s),
        "html" => System.Net.WebUtility.HtmlEncode(s),
        "xml" => System.Security.SecurityElement.Escape(s) ?? "",
        _ => s,
    };

    private static string Decode(string s, string kind)
    {
        switch (kind.ToLowerInvariant())
        {
            case "url": try { return Uri.UnescapeDataString(s); } catch (UriFormatException) { return s; }
            case "html": return System.Net.WebUtility.HtmlDecode(s);
            case "xml": return System.Net.WebUtility.HtmlDecode(s);
            default: return s;
        }
    }

    private static readonly string[] Ones =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve",
        "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    };
    private static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

    /// <summary>English words for a number. Flag 1 gives an ordinal, 2 a dollars-and-cents phrase.</summary>
    private static string WordNum(double value, int flag)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) >= 1e15) return "*";
        var negative = value < 0;
        var abs = Math.Abs(value);
        var whole = (long)Math.Floor(abs);
        var words = NumberWords(whole);
        if (flag == 2)
        {
            var cents = (int)Math.Round((abs - whole) * 100, MidpointRounding.AwayFromZero);
            words += $" And {NumberWords(cents)} Cents";
        }
        return (negative ? "Negative " : "") + words;
    }

    private static string NumberWords(long n)
    {
        if (n < 20) return Ones[n];
        if (n < 100) return Tens[n / 10] + (n % 10 != 0 ? " " + Ones[n % 10] : "");
        if (n < 1000) return Ones[n / 100] + " Hundred" + (n % 100 != 0 ? " " + NumberWords(n % 100) : "");
        string[] names = { "Thousand", "Million", "Billion", "Trillion" };
        long unit = 1000;
        for (var i = 0; i < names.Length; i++, unit *= 1000)
        {
            if (n < unit * 1000)
                return NumberWords(n / unit) + " " + names[i] + (n % unit != 0 ? " " + NumberWords(n % unit) : "");
        }
        return "*";
    }

    private static double PointsPer(string unit) => unit.ToLowerInvariant() switch
    {
        "in" => 72,
        "cm" => 72 / 2.54,
        "mm" => 72 / 25.4,
        "pt" => 1,
        "pc" or "p" => 12,
        _ => 0,
    };

    private static (double Value, string Unit)? SplitUnit(string s)
    {
        s = s.Trim();
        var i = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or '-' or '+')) i++;
        if (i == 0 || !double.TryParse(s[..i], NumberStyles.Float, Inv, out var v)) return null;
        return (v, s[i..].Trim());
    }

    private static object UnitType(string s) => SplitUnit(s) is { } u && PointsPer(u.Unit) > 0 ? u.Unit.ToLowerInvariant() : "";

    private static object UnitValue(string s, string? target)
    {
        if (SplitUnit(s) is not { } u) return 0d;
        var from = PointsPer(u.Unit);
        if (from == 0) return 0d;
        var to = target == null ? from : PointsPer(target);
        return to == 0 ? 0d : u.Value * from / to;
    }
}
