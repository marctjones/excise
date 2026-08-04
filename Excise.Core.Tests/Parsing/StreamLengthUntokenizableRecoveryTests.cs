using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// A wrong /Length can land the post-stream-data token read on a byte that
/// cannot BEGIN any PDF token, in which case the lexer throws instead of
/// returning a wrong token. That must reach the same marker-scan recovery as
/// a wrong-but-tokenizable landing byte, not fail the object (#874).
///
/// Real-world shape: pdfium's pixel/bug_1087.pdf declares /Length 89
/// (indirect, via <c>10 0 R</c>) on a 94-byte CCITT payload whose byte 89 is
/// '^' (0x5E) — mutool warns "PDF stream Length incorrect" on the same file
/// and recovers. Before this fix the throw made the whole /Mask object
/// unreadable, which surfaced as a hard render failure the moment the JBIG2
/// base image started decoding (the reverted-fix mystery in #874: the
/// exception was never caused by resolving /JBIG2Globals, it was this latent
/// parser bug on a neighboring object that only globals-resolved rendering
/// reaches).
/// </summary>
public class StreamLengthUntokenizableRecoveryTests
{
    [Fact]
    public void ShortLength_LandingOnUntokenizableByte_RecoversByMarkerScan()
    {
        var pdf = BuildPdfWithShortLengthStream(out var truePayloadLength);
        using var doc = PdfDocument.Open(pdf);

        var stream = doc.GetObject(4).Should().BeOfType<PdfStream>().Subject;
        stream.EncodedData.Length.Should().Be(truePayloadLength,
            "a declared /Length that lands on an untokenizable byte must fall back " +
            "to the endstream marker scan, like any other wrong /Length");
    }

    [Fact]
    public void ShortLength_LandingOnUntokenizableByte_DoesNotPoisonDocumentOpen()
    {
        var pdf = BuildPdfWithShortLengthStream(out _);
        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().Be(1);
    }

    /// <summary>
    /// Minimal one-page PDF whose object 4 is a stream with a 24-byte binary
    /// payload but a declared /Length of 20 — and byte 20 of the payload is
    /// '^' (0x5E), which no PDF token can start with. Mirrors bug_1087's
    /// producer bug without needing the corpus.
    /// </summary>
    private static byte[] BuildPdfWithShortLengthStream(out int truePayloadLength)
    {
        // 24 payload bytes; the byte at the declared-length landing position
        // (offset 20) is '^'.
        var payload = new byte[24];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(0xF0 + (i % 8));
        payload[20] = (byte)'^';
        payload[21] = (byte)'@';
        payload[22] = (byte)'^';
        payload[23] = (byte)'D';
        truePayloadLength = payload.Length;

        var buffer = new System.IO.MemoryStream();
        void Write(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));

        var offsets = new long[6];
        Write("%PDF-1.4\n");
        offsets[1] = buffer.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = buffer.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = buffer.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>\nendobj\n");
        offsets[4] = buffer.Position;
        Write("4 0 obj\n<< /Length 20 >>\nstream\n");
        buffer.Write(payload);
        Write("\nendstream\nendobj\n");

        long xrefPos = buffer.Position;
        Write("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i <= 4; i++)
            Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");

        return buffer.ToArray();
    }
}
