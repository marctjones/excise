using System.Runtime.CompilerServices;
using Excise.Core.Primitives;

namespace Excise.Core.Filters;

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
            var currentRow = output.AsSpan(row * rowBytes, rowBytes);
            UnfilterPngRow(data[srcOffset], data.AsSpan(srcOffset + 1, rowBytes), currentRow, prevRow, bytesPerPixel);
            currentRow.CopyTo(prevRow);
        }

        return output;
    }

    /// <summary>
    /// One PNG-predicted row (ISO 32000-2 §7.4.4.4, PNG filter types 0-4; any other
    /// filter byte passes the row through raw). Shared by the array path and
    /// <see cref="PredictorDecodeStream"/> so both run the same arithmetic (#1677).
    /// <paramref name="current"/> must not overlap <paramref name="raw"/> or <paramref name="prev"/>.
    /// AggressiveOptimization: the stream path calls this once per row, thousands of short
    /// calls, which under tiered JIT would run at tier 0 for most of a page render.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void UnfilterPngRow(int filter, ReadOnlySpan<byte> raw, Span<byte> current, ReadOnlySpan<byte> prev, int bytesPerPixel)
    {
        for (int i = 0; i < raw.Length; i++)
        {
            byte left = i >= bytesPerPixel ? current[i - bytesPerPixel] : (byte)0;
            byte up = prev[i];
            byte upLeft = i >= bytesPerPixel ? prev[i - bytesPerPixel] : (byte)0;

            current[i] = filter switch
            {
                0 => raw[i],
                1 => (byte)(raw[i] + left),
                2 => (byte)(raw[i] + up),
                3 => (byte)(raw[i] + (left + up) / 2),
                4 => (byte)(raw[i] + PaethPredictor(left, up, upLeft)),
                _ => raw[i]
            };
        }
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
        if (bitsPerComponent == 16)
            return ApplyTiffPredictor16Bit(data, colors, columns);

        if (bitsPerComponent != 8)
            return data;

        int bytesPerRow = colors * columns;
        int rows = data.Length / bytesPerRow;
        // A trailing partial row is left ZERO here (not copied) — long-standing
        // behaviour, kept; the stream path never emits a partial row (#1677).
        var output = new byte[data.Length];

        for (int row = 0; row < rows; row++)
        {
            var span = output.AsSpan(row * bytesPerRow, bytesPerRow);
            data.AsSpan(row * bytesPerRow, bytesPerRow).CopyTo(span);
            UnfilterTiff8Row(span, colors);
        }

        return output;
    }

    private static byte[] ApplyTiffPredictor16Bit(byte[] data, int colors, int columns)
    {
        int bytesPerSample = 2;
        int bytesPerRow = colors * columns * bytesPerSample;
        int rows = data.Length / bytesPerRow;
        var output = new byte[data.Length];

        for (int row = 0; row < rows; row++)
        {
            var span = output.AsSpan(row * bytesPerRow, bytesPerRow);
            data.AsSpan(row * bytesPerRow, bytesPerRow).CopyTo(span);
            UnfilterTiff16Row(span, colors);
        }

        if (rows * bytesPerRow < data.Length)
            Array.Copy(data, rows * bytesPerRow, output, rows * bytesPerRow, data.Length - (rows * bytesPerRow));

        return output;
    }

    /// <summary>TIFF predictor 2, 8 bpc, in place: each sample adds the same component of the pixel to its left.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void UnfilterTiff8Row(Span<byte> row, int colors)
    {
        for (int i = colors; i < row.Length; i++)
            row[i] = (byte)(row[i] + row[i - colors]);
    }

    /// <summary>TIFF predictor 2, 16 bpc big-endian, in place, modulo 2^16.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void UnfilterTiff16Row(Span<byte> row, int colors)
    {
        int samples = row.Length / 2;
        for (int s = colors; s < samples; s++)
        {
            int idx = s * 2;
            int prevIdx = (s - colors) * 2;
            int value = (((row[idx] << 8) | row[idx + 1]) + ((row[prevIdx] << 8) | row[prevIdx + 1])) & 0xFFFF;
            row[idx] = (byte)(value >> 8);
            row[idx + 1] = (byte)value;
        }
    }

    /// <summary>
    /// #1677: a forward-only view of <paramref name="inflated"/> with the predictor in
    /// <paramref name="parms"/> undone row by row, so a reader that wants a few rows
    /// of a large predicted image never holds the whole unfiltered array — only the
    /// previous row, which is all PNG Up/Average/Paeth need. Emits exactly the bytes
    /// <see cref="ApplyIfNeeded"/> would, for every COMPLETE predictor row; a partial
    /// trailing row ends the stream instead (the array path drops it for PNG,
    /// zero-fills it for TIFF 8 bpc, passes it through for TIFF 16 bpc — a reader
    /// that needs those bytes must fall back to the array path, where those quirks live).
    /// Returns <paramref name="inflated"/> itself when the predictor is a no-op there
    /// (none, 1, an unknown value, TIFF at a depth it does not handle), and null when
    /// the parameters are ones this view does not take on (non-positive or oversized
    /// geometry) — the caller then uses the array path.
    /// </summary>
    internal static Stream? TryOpenDecodeStream(Stream inflated, PdfDictionary? parms)
    {
        if (parms == null || !parms.ContainsKey("Predictor"))
            return inflated;
        int predictor = parms.GetInt("Predictor", 1);
        if (predictor <= 1)
            return inflated;

        int colors = parms.GetInt("Colors", 1);
        int bitsPerComponent = parms.GetInt("BitsPerComponent", 8);
        int columns = parms.GetInt("Columns", 1);
        if (colors <= 0 || bitsPerComponent <= 0 || columns <= 0)
            return null;

        // Same int arithmetic as Apply, refused if it would have overflowed there.
        long rowBits = (long)colors * columns * bitsPerComponent;
        if (rowBits + 7 > MaxStreamRowBytes * 8L)
            return null;
        int bytesPerPixel = (colors * bitsPerComponent + 7) / 8;
        int rowBytes = (int)((rowBits + 7) / 8);

        if (predictor == 2)
        {
            return bitsPerComponent switch
            {
                8 => new PredictorDecodeStream(inflated, PredictorDecodeStream.Kind.Tiff8, colors, rowBytes, 0),
                16 => new PredictorDecodeStream(inflated, PredictorDecodeStream.Kind.Tiff16, colors, rowBytes, 0),
                _ => inflated,
            };
        }

        if (predictor >= 10 && predictor <= 15)
            return new PredictorDecodeStream(inflated, PredictorDecodeStream.Kind.Png, colors, rowBytes, bytesPerPixel);

        return inflated;
    }

    /// <summary>A predictor row wider than this is left to the array path (a hostile /Columns).</summary>
    private const int MaxStreamRowBytes = 64 * 1024 * 1024;

    private sealed class PredictorDecodeStream : Stream
    {
        internal enum Kind { Png, Tiff8, Tiff16 }

        private readonly Stream _inner;
        private readonly Kind _kind;
        private readonly int _colors;
        private readonly int _bytesPerPixel;
        private readonly byte[] _encodedRow;
        private byte[] _row;
        private byte[] _prevRow;
        private int _rowPosition;
        private bool _ended;

        public PredictorDecodeStream(Stream inner, Kind kind, int colors, int rowBytes, int bytesPerPixel)
        {
            _inner = inner;
            _kind = kind;
            _colors = colors;
            _bytesPerPixel = bytesPerPixel;
            _encodedRow = new byte[kind == Kind.Png ? rowBytes + 1 : rowBytes];
            _row = new byte[rowBytes];
            _prevRow = kind == Kind.Png ? new byte[rowBytes] : Array.Empty<byte>();
            _rowPosition = rowBytes; // nothing buffered yet
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int written = 0;
            while (written < buffer.Length)
            {
                if (_rowPosition >= _row.Length)
                {
                    if (_ended || !NextRow())
                    {
                        _ended = true;
                        break;
                    }
                }

                int n = Math.Min(buffer.Length - written, _row.Length - _rowPosition);
                _row.AsSpan(_rowPosition, n).CopyTo(buffer.Slice(written));
                _rowPosition += n;
                written += n;
            }

            return written;
        }

        private bool NextRow()
        {
            if (!FillExactly(_encodedRow))
                return false;

            switch (_kind)
            {
                case Kind.Png:
                    (_prevRow, _row) = (_row, _prevRow);
                    UnfilterPngRow(_encodedRow[0], _encodedRow.AsSpan(1), _row, _prevRow, _bytesPerPixel);
                    break;
                case Kind.Tiff8:
                    _encodedRow.CopyTo(_row, 0);
                    UnfilterTiff8Row(_row, _colors);
                    break;
                case Kind.Tiff16:
                    _encodedRow.CopyTo(_row, 0);
                    UnfilterTiff16Row(_row, _colors);
                    break;
            }

            _rowPosition = 0;
            return true;
        }

        private bool FillExactly(byte[] buffer)
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                int read = _inner.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0)
                    return false;
                filled += read;
            }

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
