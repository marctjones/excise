using AwesomeAssertions;
using Excise.Core.Filters;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Filters;

/// <summary>
/// #1677: <see cref="PdfPredictor.TryOpenDecodeStream"/> undoes a /Predictor one row
/// at a time so a subsampled image decode never holds the whole unfiltered array.
/// The oracle is the array path, <see cref="PdfPredictor.ApplyIfNeeded"/>, which every
/// materialised Flate/LZW decode already runs: for every COMPLETE predictor row the
/// stream must emit exactly its bytes, and a partial trailing row must end the stream
/// (the array path's tail quirks stay with the array path).
/// </summary>
public class PredictorDecodeStreamTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var predictor in new[] { 2, 10, 11, 12, 13, 14, 15 })
        foreach (var colors in new[] { 1, 3, 4 })
        foreach (var bpc in new[] { 1, 2, 4, 8, 16 })
        foreach (var columns in new[] { 1, 7, 13 })
            yield return new object[] { predictor, colors, bpc, columns };
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void TheStream_EmitsExactlyTheArrayPathsBytes_RowForRow(int predictor, int colors, int bpc, int columns)
    {
        var parms = Parms(predictor, colors, bpc, columns);
        var (encoded, _, rowBytes) = RandomFilteredRows(predictor, colors, bpc, columns, rows: 9,
            seed: predictor * 10000 + colors * 1000 + bpc * 100 + columns);

        var expected = PdfPredictor.ApplyIfNeeded(encoded, parms);
        var streamed = ReadAllInOddChunks(PdfPredictor.TryOpenDecodeStream(new MemoryStream(encoded), parms)!);

        streamed.Should().Equal(expected,
            $"predictor={predictor} colors={colors} bpc={bpc} columns={columns}: the row-by-row unfilter must " +
            "produce the array path's bytes exactly, including Up/Average/Paeth's dependence on the previous row");
        streamed.Length.Should().Be(rowBytes * 9);
    }

    [Theory]
    [InlineData(2, 3, 8)]
    [InlineData(2, 1, 16)]
    [InlineData(10, 3, 8)]
    [InlineData(15, 1, 4)]
    public void APartialTrailingRow_EndsTheStream_BeforeThatRow(int predictor, int colors, int bpc)
    {
        var parms = Parms(predictor, colors, bpc, columns: 11);
        var (encoded, encodedRowBytes, rowBytes) = RandomFilteredRows(predictor, colors, bpc, 11, rows: 5, seed: 42);
        var truncated = encoded.AsSpan(0, encoded.Length - encodedRowBytes / 2).ToArray();

        var streamed = ReadAllInOddChunks(PdfPredictor.TryOpenDecodeStream(new MemoryStream(truncated), parms)!);

        streamed.Length.Should().Be(rowBytes * 4, "only complete predictor rows are emitted");
        streamed.Should().Equal(PdfPredictor.ApplyIfNeeded(encoded, parms).AsSpan(0, rowBytes * 4).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    public void NoOrANoOpPredictor_ReturnsTheInflatedStreamItself(int? predictor)
    {
        var parms = predictor is { } p ? Parms(p, 1, 8, 4) : null;
        var inner = new MemoryStream(new byte[] { 1, 2, 3 });
        PdfPredictor.TryOpenDecodeStream(inner, parms).Should().BeSameAs(inner,
            "the array path returns these bytes unchanged, so the stream must too");
    }

    [Fact]
    public void TiffAtADepthTheArrayPathDoesNotUnfilter_PassesThrough()
    {
        var inner = new MemoryStream(new byte[] { 1, 2, 3 });
        PdfPredictor.TryOpenDecodeStream(inner, Parms(2, 1, 4, 4)).Should().BeSameAs(inner);
    }

    [Theory]
    [InlineData(0, 8, 4)]
    [InlineData(1, 0, 4)]
    [InlineData(1, 8, 0)]
    [InlineData(1, 8, int.MaxValue)]
    public void GeometryTheStreamDoesNotTakeOn_IsRefused_SoTheCallerUsesTheArrayPath(int colors, int bpc, int columns)
        => PdfPredictor.TryOpenDecodeStream(new MemoryStream(), Parms(12, colors, bpc, columns)).Should().BeNull();

    private static PdfDictionary Parms(int predictor, int colors, int bpc, int columns)
    {
        var parms = new PdfDictionary();
        parms.SetInt("Predictor", predictor);
        parms.SetInt("Colors", colors);
        parms.SetInt("BitsPerComponent", bpc);
        parms.SetInt("Columns", columns);
        return parms;
    }

    /// <summary>
    /// Random rows; for PNG each row's filter byte cycles 0..5 so every filter type,
    /// and one unknown type (passes raw), follows every other.
    /// </summary>
    private static (byte[] Encoded, int EncodedRowBytes, int RowBytes) RandomFilteredRows(
        int predictor, int colors, int bpc, int columns, int rows, int seed)
    {
        var rowBytes = (colors * columns * bpc + 7) / 8;
        var png = predictor >= 10;
        var encodedRowBytes = png ? rowBytes + 1 : rowBytes;
        var encoded = new byte[encodedRowBytes * rows];
        new Random(seed).NextBytes(encoded);
        if (png)
        {
            for (var r = 0; r < rows; r++)
                encoded[r * encodedRowBytes] = (byte)(r % 6);
        }

        return (encoded, encodedRowBytes, rowBytes);
    }

    private static byte[] ReadAllInOddChunks(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[7];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);
        stream.Read(buffer, 0, buffer.Length).Should().Be(0, "an ended stream stays ended");
        return output.ToArray();
    }
}
