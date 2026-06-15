using Pdfe.Core.Primitives;

namespace Pdfe.Core.Filters;

internal static class PdfPredictor
{
    public static byte[] ApplyIfNeeded(byte[] data, PdfDictionary? parms)
    {
        if (parms == null || !parms.ContainsKey("Predictor"))
            return data;

        int predictor = parms.GetInt("Predictor", 1);
        return predictor > 1 ? Apply(data, parms) : data;
    }

    private static byte[] Apply(byte[] data, PdfDictionary parms)
    {
        int predictor = parms.GetInt("Predictor", 1);
        int colors = parms.GetInt("Colors", 1);
        int bitsPerComponent = parms.GetInt("BitsPerComponent", 8);
        int columns = parms.GetInt("Columns", 1);

        int bytesPerPixel = (colors * bitsPerComponent + 7) / 8;
        int rowBytes = (colors * columns * bitsPerComponent + 7) / 8;

        if (predictor == 2)
            return ApplyTiffPredictor(data, colors, columns, bitsPerComponent);

        if (predictor >= 10 && predictor <= 15)
            return ApplyPngPredictor(data, rowBytes, bytesPerPixel);

        return data;
    }

    private static byte[] ApplyPngPredictor(byte[] data, int rowBytes, int bytesPerPixel)
    {
        int rowStride = rowBytes + 1;
        int rows = data.Length / rowStride;

        var output = new byte[rows * rowBytes];
        var prevRow = new byte[rowBytes];

        for (int row = 0; row < rows; row++)
        {
            int srcOffset = row * rowStride;
            int dstOffset = row * rowBytes;

            int filter = data[srcOffset];
            var currentRow = new byte[rowBytes];

            for (int i = 0; i < rowBytes; i++)
            {
                byte raw = data[srcOffset + 1 + i];
                byte left = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : (byte)0;
                byte up = prevRow[i];
                byte upLeft = i >= bytesPerPixel ? prevRow[i - bytesPerPixel] : (byte)0;

                currentRow[i] = filter switch
                {
                    0 => raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + (left + up) / 2),
                    4 => (byte)(raw + PaethPredictor(left, up, upLeft)),
                    _ => raw
                };

                output[dstOffset + i] = currentRow[i];
            }

            Array.Copy(currentRow, prevRow, rowBytes);
        }

        return output;
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
            return a;
        if (pb <= pc)
            return b;
        return c;
    }

    private static byte[] ApplyTiffPredictor(byte[] data, int colors, int columns, int bitsPerComponent)
    {
        if (bitsPerComponent != 8)
            return data;

        int bytesPerRow = colors * columns;
        int rows = data.Length / bytesPerRow;
        var output = new byte[data.Length];

        for (int row = 0; row < rows; row++)
        {
            int rowOffset = row * bytesPerRow;

            for (int col = 0; col < columns; col++)
            {
                for (int comp = 0; comp < colors; comp++)
                {
                    int idx = rowOffset + col * colors + comp;
                    if (col == 0)
                    {
                        output[idx] = data[idx];
                    }
                    else
                    {
                        int prevIdx = rowOffset + (col - 1) * colors + comp;
                        output[idx] = (byte)(data[idx] + output[prevIdx]);
                    }
                }
            }
        }

        return output;
    }
}
