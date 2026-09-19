using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Filters;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// F1 of #1207: a deferred-decode Flate image tells its decoder how big it will
/// be, so the decoded array is allocated once at the right size instead of grown
/// as a chunk list and then copied into an exact-size result — which held both
/// live at the end and made a 92 MiB image cost ~184 MiB at its peak.
///
/// <para>The allocation assertion is the primary gate and it is deliberately
/// falsifiable: pass no hint and it goes red at ≥2×. The identity assertions are
/// the other half — a wrong hint may cost a copy and must never cost a byte.</para>
/// </summary>
public class FlateSizeHintTests
{
    private const long Max = StreamDecodeLimits.MaxDecompressedBytes;

    [Fact]
    public void ACorrectHint_AllocatesAboutOnceTheDecodedSize_AndNoHintAllocatesAtLeastTwice()
    {
        // Incompressible so the 64x-encoded clamp cannot bite and the hint is honoured.
        var decoded = Random32MiB();
        var encoded = Flate(decoded);

        var withHint = AllocatedBy(() => FlateFilterDecoder.DecodeFlateData(encoded, Max, decoded.Length));
        var withoutHint = AllocatedBy(() => FlateFilterDecoder.DecodeFlateData(encoded, Max));

        withHint.Should().BeLessThanOrEqualTo((long)(decoded.Length * 1.1),
            "an exact hint must allocate the result once and never copy it (the loop must probe EOF " +
            "before growing — the naive shape allocates a follow-on chunk and copies anyway)");
        withoutHint.Should().BeGreaterThanOrEqualTo(2L * decoded.Length,
            "without a hint the chunk list and the exact-size result are both live at the end — this is " +
            "the ≥2x the fix exists to remove, and it is what the assertion above would read if the hint were ignored");
    }

    /// <summary>
    /// The same property through the FILTER ENTRY rather than the decoder's
    /// internal overload: the hint lives on the <see cref="PdfStream"/> and
    /// <c>FlateFilterDecoder.Decode</c> must forward it. This is the test that
    /// goes red if that forwarding is dropped — the direct test above cannot
    /// see that layer, which is exactly how the first plant of this fix passed.
    /// </summary>
    [Fact]
    public void TheHintOnTheStream_ReachesTheDecoder_ThroughTheFilterPipeline()
    {
        var decoded = Random32MiB();
        var hinted = FlateImageStream(decoded);
        hinted.ExpectedDecodedLength = decoded.Length;
        var unhinted = FlateImageStream(decoded);

        var withHint = AllocatedBy(() => new Excise.Core.Parsing.StreamDecompressor().Decompress(hinted));
        var withoutHint = AllocatedBy(() => new Excise.Core.Parsing.StreamDecompressor().Decompress(unhinted));

        hinted.DecodedData.Should().Equal(decoded);
        withHint.Should().BeLessThanOrEqualTo((long)(decoded.Length * 1.1),
            "the object store's estimate is only useful if the filter entry hands it to the decoder");
        withoutHint.Should().BeGreaterThanOrEqualTo(2L * decoded.Length);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void AWrongHint_IsByteIdentical_AndCostsAtMostOneExtraCopy(double factor)
    {
        var decoded = RandomBytes(1 << 20);
        var encoded = Flate(decoded);
        var hint = (long)(decoded.Length * factor);

        byte[]? result = null;
        var allocated = AllocatedBy(() => result = FlateFilterDecoder.DecodeFlateData(encoded, Max, hint));

        result.Should().Equal(decoded, "a size hint may cost a copy, never a byte");

        // Over-hint: the hinted chunk under-fills at EOF, so one exact-size copy —
        // hint + decoded. Under-hint: the hinted chunk fills, the probe finds more,
        // a doubling chunk takes the rest, and the exact-size copy follows —
        // hint + ~decoded + decoded. Either way bounded and byte-identical; the
        // under-hint is ~0.5x worse than today's no-hint path, which is the price
        // of a dictionary that lied low, and why ClampHint never rounds a hint UP.
        var bound = factor < 1 ? hint + 2L * decoded.Length : hint + decoded.Length;
        allocated.Should().BeLessThanOrEqualTo(bound + (1 << 16),
            "a wrong hint costs the hinted chunk plus the fallback growth and one exact-size copy, nothing worse");
    }

    [Fact]
    public void TheHint_IsClampedAgainstAHostileDictionary()
    {
        // A dictionary claiming 40 GB over a 20-byte stream: allocate 1,280 bytes, not 40 GB.
        FlateFilterDecoder.ClampHint(40L * 1024 * 1024 * 1024, encodedLength: 20, Max)
            .Should().Be(20 * FlateFilterDecoder.MaxHintToEncodedRatio);
        FlateFilterDecoder.ClampHint(Max * 4, encodedLength: Max, Max).Should().Be(Max, "never past the decode ceiling");
        FlateFilterDecoder.ClampHint(0, 1000, Max).Should().Be(0);
        FlateFilterDecoder.ClampHint(1000, 0, Max).Should().Be(0);
    }

    [Theory]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8", "", 4 * 3 * 4)]
    [InlineData("/ColorSpace /DeviceCMYK /BitsPerComponent 8", "", 4 * 4 * 4)]
    [InlineData("/ColorSpace /DeviceGray /BitsPerComponent 1", "", 1 * 4)]
    [InlineData("/ImageMask true", "", 1 * 4)]
    [InlineData("/ColorSpace [/Indexed /DeviceRGB 1 <000000FFFFFF>] /BitsPerComponent 8", "", 4 * 1 * 4)]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8", "/DecodeParms << /Predictor 12 /Colors 3 /Columns 4 /BitsPerComponent 8 >>", (1 + 12) * 4)]
    [InlineData("/ColorSpace [/ICCBased 6 0 R] /BitsPerComponent 8", "", 4 * 4 * 4)]
    [InlineData("/ColorSpace /Pattern /BitsPerComponent 8", "", 0)]
    public void TheObjectStore_EstimatesTheInflatedSizeFromTheDictionary(string imageEntries, string extra, long expected)
    {
        using var doc = PdfDocument.Open(OneImageDocument(imageEntries, extra));
        var image = doc.GetPage(1).GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;

        image.IsDecoded.Should().BeFalse("the estimate is taken at resolve time, before any decode");
        image.ExpectedDecodedLength.Should().Be(expected);
    }

    [Fact]
    public void AClaimThatDoesNotFitAnArray_YieldsNoHint()
    {
        using var doc = PdfDocument.Open(OneImageDocument("/ColorSpace /DeviceCMYK /BitsPerComponent 8", "", width: 100_000, height: 100_000));
        var image = doc.GetPage(1).GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;
        image.ExpectedDecodedLength.Should().Be(0, "40 GB does not fit a byte[]; the decoder must fall back to chunked growth under its own ceiling");
    }

    // ---- helpers -------------------------------------------------------

    private static long AllocatedBy(Action action)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static byte[] Random32MiB() => RandomBytes(32 << 20);

    private static PdfStream FlateImageStream(byte[] decoded)
    {
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetName("Filter", "FlateDecode");
        return new PdfStream(dict, Flate(decoded));
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(1207).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }

    /// <summary>
    /// A one-page PDF with a 4x4 /FlateDecode image XObject (object 5) and, for the
    /// ICCBased case, an ICC stream at object 6 with /N 4. The image body is never
    /// decoded by these tests, so it need not be valid Flate.
    /// </summary>
    private static byte[] OneImageDocument(string imageEntries, string extra, int width = 4, int height = 4)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length 30 >>\nstream\nq 40 0 0 40 10 10 cm /Im0 Do Q\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} {imageEntries} /Filter /FlateDecode {extra} /Length 2 >>\nstream\nxx\nendstream",
            "<< /N 4 /Length 4 >>\nstream\nabcd\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
