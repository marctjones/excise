using Pdfe.Core.Primitives;
using System.Runtime.CompilerServices;

namespace Pdfe.Core.ColorSpaces;

internal static class PdfFunctionEvaluator
{
    private static readonly ConditionalWeakTable<PdfDictionary, CachedFunctionData> s_functionData = new();

    private sealed class CachedFunctionData
    {
        public bool SampledInitialized { get; set; }
        public SampledFunctionData? Sampled { get; set; }
        public bool CalculatorInitialized { get; set; }
        public IReadOnlyList<string>? CalculatorTokens { get; set; }
    }

    private sealed record SampledFunctionData(
        int SampleCount,
        int OutputComponentCount,
        double[]? Range,
        double[] Samples);

    public static double[]? Evaluate(PdfObject? funcObj, double t)
        => Evaluate(funcObj, new[] { t });

    public static double[]? Evaluate(PdfObject? funcObj, double[] inputs)
        => Evaluate(funcObj, inputs, resolve: null);

    public static double[]? Evaluate(PdfObject? funcObj, double t, Func<PdfObject, PdfObject>? resolve)
        => Evaluate(funcObj, new[] { t }, resolve);

    public static double[]? Evaluate(PdfObject? funcObj, double[] inputs, Func<PdfObject, PdfObject>? resolve)
    {
        if (funcObj == null)
            return null;

        if (resolve != null)
            funcObj = resolve(funcObj);

        if (funcObj is PdfArray arr)
        {
            var results = new List<double>();
            foreach (var item in arr)
            {
                var r = Evaluate(item, inputs, resolve);
                if (r != null)
                    results.AddRange(r);
            }

            return results.Count > 0 ? results.ToArray() : null;
        }

        if (funcObj is not PdfDictionary func)
            return null;

        var funcType = func.GetInt("FunctionType", -1);
        return funcType switch
        {
            0 => EvaluateSampled(func, inputs.Length > 0 ? inputs[0] : 0),
            2 => EvaluateExponential(func, inputs.Length > 0 ? inputs[0] : 0),
            3 => EvaluateStitching(func, inputs.Length > 0 ? inputs[0] : 0, resolve),
            4 => EvaluateCalculator(func, inputs),
            _ => null
        };
    }

    private static double[] EvaluateExponential(PdfDictionary func, double t)
    {
        var n = func.GetNumber("N", 1.0);
        var c0 = GetNumberArray(func, "C0") ?? new[] { 0.0 };
        var c1 = GetNumberArray(func, "C1") ?? new[] { 1.0 };
        var len = Math.Max(c0.Length, c1.Length);
        var result = new double[len];

        for (int i = 0; i < len; i++)
        {
            var v0 = i < c0.Length ? c0[i] : 0;
            var v1 = i < c1.Length ? c1[i] : 1;
            result[i] = v0 + Math.Pow(t, n) * (v1 - v0);
        }

        return result;
    }

    private static double[]? EvaluateStitching(PdfDictionary func, double t, Func<PdfObject, PdfObject>? resolve)
    {
        var functions = func.GetOptional("Functions") as PdfArray;
        var boundsArr = GetNumberArray(func, "Bounds") ?? Array.Empty<double>();
        var encodeArr = GetNumberArray(func, "Encode") ?? Array.Empty<double>();

        if (functions == null || functions.Count == 0)
            return null;

        var domainArr = GetNumberArray(func, "Domain");
        var domainMin = domainArr is { Length: >= 2 } ? domainArr[0] : 0.0;
        var domainMax = domainArr is { Length: >= 2 } ? domainArr[1] : 1.0;
        t = Math.Clamp(t, Math.Min(domainMin, domainMax), Math.Max(domainMin, domainMax));

        int idx = 0;
        for (int i = 0; i < boundsArr.Length; i++)
        {
            if (t < boundsArr[i])
                break;
            idx = i + 1;
        }
        idx = Math.Clamp(idx, 0, functions.Count - 1);

        double tMin = idx == 0 ? domainMin : boundsArr[idx - 1];
        double tMax = idx < boundsArr.Length ? boundsArr[idx] : domainMax;
        double e0 = encodeArr.Length > idx * 2 ? encodeArr[idx * 2] : 0;
        double e1 = encodeArr.Length > idx * 2 + 1 ? encodeArr[idx * 2 + 1] : 1;
        double tEnc = tMax > tMin ? e0 + (t - tMin) / (tMax - tMin) * (e1 - e0) : e0;

        return Evaluate(functions[idx], tEnc, resolve);
    }

    private static double[]? EvaluateSampled(PdfDictionary func, double t)
    {
        var sampled = GetSampledFunctionData(func);
        if (sampled == null)
            return null;

        double idx = t * (sampled.SampleCount - 1);
        int lo = (int)Math.Floor(idx);
        int hi = Math.Min(lo + 1, sampled.SampleCount - 1);
        double frac = idx - lo;

        var result = new double[sampled.OutputComponentCount];
        for (int c = 0; c < sampled.OutputComponentCount; c++)
        {
            double v0 = lo * sampled.OutputComponentCount + c < sampled.Samples.Length
                ? sampled.Samples[lo * sampled.OutputComponentCount + c]
                : 0;
            double v1 = hi * sampled.OutputComponentCount + c < sampled.Samples.Length
                ? sampled.Samples[hi * sampled.OutputComponentCount + c]
                : v0;
            result[c] = v0 + frac * (v1 - v0);

            if (sampled.Range != null && c * 2 + 1 < sampled.Range.Length)
                result[c] = result[c] * (sampled.Range[c * 2 + 1] - sampled.Range[c * 2]) + sampled.Range[c * 2];
        }

        return result;
    }

    private static SampledFunctionData? GetSampledFunctionData(PdfDictionary func)
    {
        var cached = s_functionData.GetOrCreateValue(func);
        if (cached.SampledInitialized)
            return cached.Sampled;

        lock (cached)
        {
            if (cached.SampledInitialized)
                return cached.Sampled;

            var sizeArr = GetNumberArray(func, "Size");
            if (sizeArr == null || sizeArr.Length == 0)
            {
                cached.SampledInitialized = true;
                return null;
            }

            int n = (int)sizeArr[0];
            if (n <= 1)
            {
                cached.SampledInitialized = true;
                return null;
            }

            var bps = func.GetInt("BitsPerSample", 8);
            var rangeArr = GetNumberArray(func, "Range");
            var streamData = (func as PdfStream)?.DecodedData;
            if (streamData == null)
            {
                cached.SampledInitialized = true;
                return null;
            }

            int outComps = rangeArr != null ? rangeArr.Length / 2 : 1;
            int totalSamples = n * outComps;
            cached.Sampled = new SampledFunctionData(
                n,
                outComps,
                rangeArr,
                DecodeSamples(streamData, totalSamples, bps));
            cached.SampledInitialized = true;
            return cached.Sampled;
        }
    }

    private static double[] DecodeSamples(byte[] streamData, int totalSamples, int bitsPerSample)
    {
        var samples = new double[totalSamples];
        if (bitsPerSample <= 0)
            return samples;

        double maxVal = (1 << Math.Min(bitsPerSample, 16)) - 1;
        if (bitsPerSample == 8)
        {
            for (int i = 0; i < Math.Min(totalSamples, streamData.Length); i++)
                samples[i] = streamData[i] / maxVal;
            return samples;
        }

        if (bitsPerSample == 16)
        {
            var sampleIndex = 0;
            for (var i = 0; i + 1 < streamData.Length && sampleIndex < totalSamples; i += 2)
            {
                var value = (streamData[i] << 8) | streamData[i + 1];
                samples[sampleIndex++] = value / maxVal;
            }
            return samples;
        }

        var bitIndex = 0;
        for (var sampleIndex = 0; sampleIndex < totalSamples; sampleIndex++)
        {
            uint value = 0;
            for (var bit = 0; bit < bitsPerSample; bit++)
            {
                var byteIndex = bitIndex >> 3;
                if (byteIndex >= streamData.Length)
                    break;
                var bitOffset = 7 - (bitIndex & 7);
                var bitValue = (streamData[byteIndex] >> bitOffset) & 1;
                value = (value << 1) | (uint)bitValue;
                bitIndex++;
            }
            samples[sampleIndex] = value / maxVal;
        }

        return samples;
    }

    private static double[]? EvaluateCalculator(PdfDictionary func, double[] inputs)
    {
        if (func is not PdfStream stream)
            return null;

        var tokens = GetCalculatorTokens(stream);
        if (tokens == null)
            return null;
        var stack = new Stack<double>();

        for (int i = 0; i < inputs.Length; i++)
            stack.Push(inputs[i]);

        if (!ExecuteCalculatorTokens(tokens, stack))
            return null;

        var range = GetNumberArray(func, "Range");
        int outComps = range is { Length: > 0 } ? range.Length / 2 : stack.Count;
        if (outComps <= 0 || stack.Count == 0)
            return null;

        var values = stack.Reverse().TakeLast(outComps).ToArray();
        if (range != null)
        {
            for (int i = 0; i < values.Length && i * 2 + 1 < range.Length; i++)
                values[i] = Math.Clamp(values[i], range[i * 2], range[i * 2 + 1]);
        }

        return values;
    }

    private static IReadOnlyList<string>? GetCalculatorTokens(PdfStream stream)
    {
        var cached = s_functionData.GetOrCreateValue(stream);
        if (cached.CalculatorInitialized)
            return cached.CalculatorTokens;

        lock (cached)
        {
            if (cached.CalculatorInitialized)
                return cached.CalculatorTokens;

            var data = stream.DecodedData ?? stream.EncodedData;
            var program = System.Text.Encoding.Latin1.GetString(data).Trim();
            if (program.StartsWith("{", StringComparison.Ordinal) &&
                program.EndsWith("}", StringComparison.Ordinal))
                program = program.Substring(1, program.Length - 2);
            cached.CalculatorTokens = TokenizeCalculator(program);
            cached.CalculatorInitialized = true;
            return cached.CalculatorTokens;
        }
    }

    private static bool ExecuteCalculatorTokens(IReadOnlyList<string> tokens, Stack<double> stack)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "{" || token == "}")
                continue;

            if (double.TryParse(token, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                stack.Push(number);
                continue;
            }

            switch (token)
            {
                case "pop":
                    if (stack.Count < 1) return false;
                    stack.Pop();
                    break;
                case "dup":
                    if (stack.Count < 1) return false;
                    stack.Push(stack.Peek());
                    break;
                case "exch":
                    if (stack.Count < 2) return false;
                    var a = stack.Pop();
                    var b = stack.Pop();
                    stack.Push(a);
                    stack.Push(b);
                    break;
                case "add":
                    if (!Binary(stack, (x, y) => x + y)) return false;
                    break;
                case "sub":
                    if (!Binary(stack, (x, y) => x - y)) return false;
                    break;
                case "mul":
                    if (!Binary(stack, (x, y) => x * y)) return false;
                    break;
                case "div":
                    if (!Binary(stack, (x, y) => y == 0 ? 0 : x / y)) return false;
                    break;
                case "abs":
                    if (!Unary(stack, Math.Abs)) return false;
                    break;
                case "sqrt":
                    if (!Unary(stack, x => Math.Sqrt(Math.Max(0, x)))) return false;
                    break;
                case "floor":
                    if (!Unary(stack, Math.Floor)) return false;
                    break;
                case "sin":
                    if (!Unary(stack, x => Math.Sin(x * Math.PI / 180.0))) return false;
                    break;
                case "cos":
                    if (!Unary(stack, x => Math.Cos(x * Math.PI / 180.0))) return false;
                    break;
                case "atan":
                    if (stack.Count < 2) return false;
                    var denominator = stack.Pop();
                    var numerator = stack.Pop();
                    var degrees = Math.Atan2(numerator, denominator) * 180.0 / Math.PI;
                    if (degrees < 0) degrees += 360.0;
                    stack.Push(degrees);
                    break;
                case "lt":
                    if (!Binary(stack, (x, y) => x < y ? 1 : 0)) return false;
                    break;
                case "mod":
                    if (!Binary(stack, (x, y) => Math.Abs(y) < double.Epsilon ? 0 : x % y)) return false;
                    break;
                case "copy":
                    if (!Copy(stack)) return false;
                    break;
                case "roll":
                    if (!Roll(stack)) return false;
                    break;
                case "if":
                    if (stack.Count < 1 || i < 1) return false;
                    var condition = stack.Pop();
                    var proc = tokens[i - 1];
                    if (!proc.StartsWith("{", StringComparison.Ordinal) ||
                        !proc.EndsWith("}", StringComparison.Ordinal))
                        return false;
                    if (Math.Abs(condition) > double.Epsilon)
                    {
                        var inner = TokenizeCalculator(proc.Substring(1, proc.Length - 2));
                        if (!ExecuteCalculatorTokens(inner, stack)) return false;
                    }
                    break;
                default:
                    if (token.StartsWith("{", StringComparison.Ordinal) &&
                        token.EndsWith("}", StringComparison.Ordinal))
                        break;
                    return false;
            }
        }

        return true;
    }

    private static bool Copy(Stack<double> stack)
    {
        if (stack.Count < 1)
            return false;

        var countValue = stack.Pop();
        if (countValue < 0 || Math.Abs(countValue - Math.Round(countValue)) > double.Epsilon)
            return false;

        var count = (int)Math.Round(countValue);
        if (stack.Count < count)
            return false;

        var values = stack.Take(count).Reverse().ToArray();
        foreach (var value in values)
            stack.Push(value);

        return true;
    }

    private static bool Roll(Stack<double> stack)
    {
        if (stack.Count < 2)
            return false;

        var shiftValue = stack.Pop();
        var countValue = stack.Pop();
        if (countValue < 0 ||
            Math.Abs(countValue - Math.Round(countValue)) > double.Epsilon ||
            Math.Abs(shiftValue - Math.Round(shiftValue)) > double.Epsilon)
            return false;

        var count = (int)Math.Round(countValue);
        var shift = (int)Math.Round(shiftValue);
        if (count == 0)
            return true;
        if (stack.Count < count)
            return false;

        var values = stack.Take(count).Reverse().ToList();
        for (var i = 0; i < count; i++)
            stack.Pop();

        shift %= count;
        if (shift < 0)
            shift += count;
        if (shift != 0)
        {
            var split = count - shift;
            values = values.Skip(split).Concat(values.Take(split)).ToList();
        }

        foreach (var value in values)
            stack.Push(value);

        return true;
    }

    private static bool Unary(Stack<double> stack, Func<double, double> op)
    {
        if (stack.Count < 1) return false;
        stack.Push(op(stack.Pop()));
        return true;
    }

    private static bool Binary(Stack<double> stack, Func<double, double, double> op)
    {
        if (stack.Count < 2) return false;
        var b = stack.Pop();
        var a = stack.Pop();
        stack.Push(op(a, b));
        return true;
    }

    private static List<string> TokenizeCalculator(string program)
    {
        var tokens = new List<string>();
        for (int i = 0; i < program.Length;)
        {
            if (char.IsWhiteSpace(program[i]))
            {
                i++;
                continue;
            }

            if (program[i] == '{')
            {
                int depth = 1;
                int start = i++;
                while (i < program.Length && depth > 0)
                {
                    if (program[i] == '{') depth++;
                    else if (program[i] == '}') depth--;
                    i++;
                }
                tokens.Add(program.Substring(start, i - start));
                continue;
            }

            int tokenStart = i;
            while (i < program.Length && !char.IsWhiteSpace(program[i]) &&
                   program[i] != '{' && program[i] != '}')
                i++;
            tokens.Add(program.Substring(tokenStart, i - tokenStart));
        }

        return tokens;
    }

    private static double[]? GetNumberArray(PdfDictionary dict, string key)
    {
        var obj = dict.GetOptional(key);
        if (obj is PdfArray arr)
        {
            var result = new double[arr.Count];
            for (int i = 0; i < arr.Count; i++)
                result[i] = arr.GetNumber(i);
            return result;
        }

        return null;
    }
}
