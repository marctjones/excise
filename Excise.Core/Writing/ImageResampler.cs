namespace Excise.Core.Writing;

/// <summary>
/// Area-average downsampling of interleaved 8-bit image samples (#1550).
/// </summary>
/// <remarks>
/// Each destination sample is the coverage-weighted mean of the source samples
/// under it, computed separably (rows, then columns). Area averaging is the
/// right filter for reduction: it never invents a value outside the range of
/// the samples it covers, and a region of zeros — a redacted area of a scan —
/// stays zero wherever it fully covers a destination sample.
/// </remarks>
internal static class ImageResampler
{
    internal static byte[] AreaAverage(
        byte[] source,
        int width,
        int height,
        int components,
        int newWidth,
        int newHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0 || components <= 0 || newWidth <= 0 || newHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be positive.");
        if (newWidth > width || newHeight > height)
            throw new ArgumentOutOfRangeException(nameof(newWidth), "Only reduction is supported.");
        if (source.LongLength != (long)width * height * components)
            throw new ArgumentException("Sample count does not match the dimensions.", nameof(source));

        var columns = Weights(width, newWidth);
        var rows = Weights(height, newHeight);

        // Horizontal pass: width × height → newWidth × height.
        var horizontal = new float[checked((long)newWidth * height * components)];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = (long)y * width * components;
            var targetRow = (long)y * newWidth * components;
            for (var x = 0; x < newWidth; x++)
            {
                var (first, weights) = columns[x];
                for (var c = 0; c < components; c++)
                {
                    double sum = 0;
                    for (var k = 0; k < weights.Length; k++)
                        sum += weights[k] * source[sourceRow + (first + k) * components + c];
                    horizontal[targetRow + x * components + c] = (float)sum;
                }
            }
        }

        // Vertical pass: newWidth × height → newWidth × newHeight.
        var result = new byte[checked((long)newWidth * newHeight * components)];
        var stride = (long)newWidth * components;
        for (var y = 0; y < newHeight; y++)
        {
            var (first, weights) = rows[y];
            var targetRow = (long)y * stride;
            for (var i = 0; i < stride; i++)
            {
                double sum = 0;
                for (var k = 0; k < weights.Length; k++)
                    sum += weights[k] * horizontal[(first + k) * stride + i];
                result[targetRow + i] = (byte)Math.Clamp((int)Math.Round(sum), 0, 255);
            }
        }

        return result;
    }

    /// <summary>
    /// For each destination index, the first source index it covers and the
    /// normalized coverage of each covered source index.
    /// </summary>
    private static (int First, double[] Weights)[] Weights(int sourceLength, int targetLength)
    {
        var scale = (double)sourceLength / targetLength;
        var table = new (int, double[])[targetLength];
        for (var i = 0; i < targetLength; i++)
        {
            var start = i * scale;
            var end = Math.Min(sourceLength, (i + 1) * scale);
            var first = (int)Math.Floor(start);
            var last = Math.Min(sourceLength - 1, (int)Math.Ceiling(end) - 1);
            var weights = new double[last - first + 1];
            double total = 0;
            for (var j = first; j <= last; j++)
            {
                var coverage = Math.Min(end, j + 1) - Math.Max(start, j);
                weights[j - first] = Math.Max(0, coverage);
                total += weights[j - first];
            }

            for (var k = 0; k < weights.Length; k++)
                weights[k] /= total;
            table[i] = (first, weights);
        }

        return table;
    }
}
