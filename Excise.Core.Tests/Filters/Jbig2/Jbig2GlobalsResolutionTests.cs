using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Filters.Jbig2;

/// <summary>
/// ISO 32000-2 §7.3.8 requires every stream to be an indirect object, so a
/// conforming <c>/DecodeParms &lt;&lt; /JBIG2Globals n 0 R &gt;&gt;</c> entry
/// is ALWAYS a reference. Before #874 the filter tested the entry with
/// <c>is PdfStream</c>, which inspected the reference and failed — JBIG2
/// globals therefore never worked on any conforming file. PdfDocument now
/// resolves the reference in place before the filter pipeline runs.
/// </summary>
public class Jbig2GlobalsResolutionTests
{
    private const string Bug631912 = "../../../../test-pdfs/pdfium/bug_631912.pdf";
    private const string Bug1087 = "../../../../test-pdfs/pdfium/pixel/bug_1087.pdf";

    [Fact]
    public void Open_IndirectJbig2Globals_ResolvesReferenceToStream()
    {
        var pdf = BuildPdfWithJbig2Image(globalsTarget: 4);
        using var doc = PdfDocument.Open(pdf);

        var image = doc.GetObject(5).Should().BeOfType<PdfStream>().Subject;
        var parms = image.GetOptional("DecodeParms").Should().BeOfType<PdfDictionary>().Subject;
        parms.GetOptional("JBIG2Globals").Should().BeOfType<PdfStream>(
            "the always-indirect globals stream must be materialized before the " +
            "/JBIG2Decode filter runs, or the decoder never sees its symbol dictionary");
    }

    [Fact]
    public void Open_SelfReferentialJbig2Globals_TerminatesWithoutRecursion()
    {
        // Hostile shape: the image's globals reference points at the image
        // object itself. Resolution must not recurse without limit; the open
        // and the fetch must complete.
        var pdf = BuildPdfWithJbig2Image(globalsTarget: 5);
        using var doc = PdfDocument.Open(pdf);
        var image = doc.GetObject(5).Should().BeOfType<PdfStream>().Subject;
        image.GetOptional("DecodeParms").Should().BeOfType<PdfDictionary>();
    }

    // ── Corpus regressions (gitignored fixtures; see skip-allowlist) ────────

    [Fact]
    public void Bug631912_JbigImageWithGlobals_DecodesFullImage()
    {
        Assert.SkipWhen(!File.Exists(Bug631912), "pdfium corpus fixture not available");
        AssertJbig2ImageDecodes(Bug631912, imageObjectNumber: 5, width: 1152, height: 720,
            // mutool draw measures 0.0007 dark on this page (thin handwritten
            // "Test"); before the fix the decode failed outright and — before
            // #878 — painted the page ~100% black. Both failure directions
            // are outside these bounds.
            minDarkFraction: 0.0001, maxDarkFraction: 0.01);
    }

    [Fact]
    public void Bug1087_JbigImageWithGlobals_DecodesFullImage()
    {
        Assert.SkipWhen(!File.Exists(Bug1087), "pdfium corpus fixture not available");
        AssertJbig2ImageDecodes(Bug1087, imageObjectNumber: 5, width: 548, height: 238,
            // mutool: 0.0016 dark.
            minDarkFraction: 0.0001, maxDarkFraction: 0.01);
    }

    [Fact]
    public void Bug1087_MaskStreamWithShortIndirectLength_IsReadable()
    {
        // The #874 "reverted fix" mystery: object 9 (/Mask of the JBIG2
        // image) declares /Length 89 via 10 0 R but carries 94 payload bytes,
        // and byte 89 is '^' — untokenizable, so parsing it used to throw
        // "Unexpected character '^' (0x5E) at position 1243". Nothing touched
        // object 9 until the base image started decoding, which made the
        // globals fix LOOK like the culprit. mutool warns "PDF stream Length
        // incorrect" on this same file and renders it.
        Assert.SkipWhen(!File.Exists(Bug1087), "pdfium corpus fixture not available");
        using var doc = PdfDocument.Open(File.ReadAllBytes(Bug1087));

        var mask = doc.GetObject(9).Should().BeOfType<PdfStream>().Subject;
        mask.EncodedData.Length.Should().Be(94, "the wrong declared length must fall back to the endstream marker scan");
    }

    private static void AssertJbig2ImageDecodes(
        string path, int imageObjectNumber, int width, int height,
        double minDarkFraction, double maxDarkFraction)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var image = doc.GetObject(imageObjectNumber).Should().BeOfType<PdfStream>().Subject;

        image.IsDecoded.Should().BeTrue("the /JBIG2Decode filter must succeed once globals resolve");
        int expectedBytes = ((width + 7) / 8) * height;
        image.DecodedData.Length.Should().Be(expectedBytes);

        // 1-bpc DeviceGray: 0 = black. Count ink and compare against the
        // independently measured mutool render, so neither an all-white nor an
        // all-black buffer can pass.
        long dark = 0;
        foreach (var b in image.DecodedData)
            dark += 8 - System.Numerics.BitOperations.PopCount(b);
        double darkFraction = (double)dark / (expectedBytes * 8L);
        darkFraction.Should().BeInRange(minDarkFraction, maxDarkFraction);
    }

    /// <summary>
    /// Minimal PDF with a JBIG2 image XObject (object 5) whose /DecodeParms
    /// carries an indirect /JBIG2Globals reference, and a globals stream
    /// (object 4). Payloads are inert bytes — these tests exercise reference
    /// resolution, not JBIG2 entropy decoding.
    /// </summary>
    private static byte[] BuildPdfWithJbig2Image(int globalsTarget)
    {
        var buffer = new System.IO.MemoryStream();
        void Write(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));

        var offsets = new long[6];
        Write("%PDF-1.4\n");
        offsets[1] = buffer.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = buffer.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = buffer.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
              "/Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");
        offsets[4] = buffer.Position;
        Write("4 0 obj\n<< /Length 8 >>\nstream\n");
        buffer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Write("\nendstream\nendobj\n");
        offsets[5] = buffer.Position;
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 8 /Height 8 " +
              $"/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode " +
              $"/DecodeParms << /JBIG2Globals {globalsTarget} 0 R >> /Length 4 >>\nstream\n");
        buffer.Write(new byte[] { 9, 9, 9, 9 });
        Write("\nendstream\nendobj\n");

        long xrefPos = buffer.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");

        return buffer.ToArray();
    }
}
