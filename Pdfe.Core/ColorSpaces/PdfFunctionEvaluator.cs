using Pdfe.Core.Primitives;

namespace Pdfe.Core.ColorSpaces;

internal static class PdfFunctionEvaluator
{
    public static double[]? Evaluate(PdfObject? funcObj, double t)
    {
        if (funcObj == null)
            return null;

        if (funcObj is PdfArray arr)
        {
            var results = new List<double>();
            foreach (var item in arr)
            {
                var r = Evaluate(item, t);
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
            0 => EvaluateSampled(func, t),
            2 => EvaluateExponential(func, t),
            3 => EvaluateStitching(func, t),
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

    private static double[]? EvaluateStitching(PdfDictionary func, double t)
    {
        var functions = func.GetOptional("Functions") as PdfArray;
        var boundsArr = GetNumberArray(func, "Bounds") ?? Array.Empty<double>();
        var encodeArr = GetNumberArray(func, "Encode") ?? Array.Empty<double>();

        if (functions == null || functions.Count == 0)
            return null;

        int idx = 0;
        for (int i = 0; i < boundsArr.Length; i++)
        {
            if (t < boundsArr[i])
                break;
            idx = i + 1;
        }
        idx = Math.Clamp(idx, 0, functions.Count - 1);

        double tMin = idx == 0 ? 0 : boundsArr[idx - 1];
        double tMax = idx < boundsArr.Length ? boundsArr[idx] : 1;
        double e0 = encodeArr.Length > idx * 2 ? encodeArr[idx * 2] : 0;
        double e1 = encodeArr.Length > idx * 2 + 1 ? encodeArr[idx * 2 + 1] : 1;
        double tEnc = tMax > tMin ? e0 + (t - tMin) / (tMax - tMin) * (e1 - e0) : e0;

        return Evaluate(functions[idx], tEnc);
    }

    private static double[]? EvaluateSampled(PdfDictionary func, double t)
    {
        var sizeArr = GetNumberArray(func, "Size");
        if (sizeArr == null || sizeArr.Length == 0)
            return null;

        int n = (int)sizeArr[0];
        if (n <= 1)
            return null;

        var bps = func.GetInt("BitsPerSample", 8);
        var rangeArr = GetNumberArray(func, "Range");
        var streamData = (func as PdfStream)?.DecodedData;

        if (streamData == null)
            return null;

        int outComps = rangeArr != null ? rangeArr.Length / 2 : 1;
        int totalSamples = n * outComps;
        var samples = DecodeSamples(streamData, totalSamples, bps);

        double idx = t * (n - 1);
        int lo = (int)Math.Floor(idx);
        int hi = Math.Min(lo + 1, n - 1);
        double frac = idx - lo;

        var result = new double[outComps];
        for (int c = 0; c < outComps; c++)
        {
            double v0 = lo * outComps + c < samples.Length ? samples[lo * outComps + c] : 0;
            double v1 = hi * outComps + c < samples.Length ? samples[hi * outComps + c] : v0;
            result[c] = v0 + frac * (v1 - v0);

            if (rangeArr != null && c * 2 + 1 < rangeArr.Length)
                result[c] = result[c] * (rangeArr[c * 2 + 1] - rangeArr[c * 2]) + rangeArr[c * 2];
        }

        return result;
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
