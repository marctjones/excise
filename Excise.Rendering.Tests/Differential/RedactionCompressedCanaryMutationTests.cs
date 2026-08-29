using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1199: a saved-file leak can be hidden in a Flate-compressed object stream.
/// A raw grep cannot see this mutation, so the decompression-aware saved-byte
/// oracle must reject it even when no PDF extractor has a reachable reference
/// to the injected object.
/// </summary>
public sealed class RedactionCompressedCanaryMutationTests
{
    private const string Canary = "COMPRESSEDCANARYV9";

    [Fact]
    public void SavedByteOracle_RejectsCanaryReinsertedIntoFlateCompressedObjectStream()
    {
        var input = BuildPagePdf();
        byte[] cleanOutput;
        using (var document = PdfDocument.Open(input))
        {
            document.RedactText(Canary).VerifiedRemovals.Should().Be(1);
            cleanOutput = document.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(cleanOutput, Canary).Should().BeEmpty(
            "guard: the genuine redacted output must be clean before the mutation is applied");

        var mutated = AppendCompressedObjectStream(cleanOutput, Canary);

        // This proves that the token is not merely rediscovered in a cleartext
        // dictionary or comment. The raw form is intentionally blind here.
        Encoding.Latin1.GetString(mutated).Should().NotContain(Canary,
            "the deliberate leak must exist only inside the Flate payload");

        var hits = SavedPdfLeakScanner.FindTerm(mutated, Canary);
        hits.Should().Contain(hit => hit.StartsWith("inflated stream", StringComparison.Ordinal),
            "the carrier-agnostic saved-byte oracle must inflate and reject a canary hidden in an /ObjStm");
    }

    private static byte[] AppendCompressedObjectStream(byte[] cleanOutput, string canary)
    {
        // A syntactically shaped object stream payload: object number/offset table
        // followed by an object body. It is appended after EOF deliberately — the
        // saved-byte security property is 'not recoverable anywhere in the file',
        // not merely 'not reachable through this reader's current xref'.
        var payload = Encoding.UTF8.GetBytes($"7 0 4 << /ActualText ({canary}) >>");
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(payload);
            compressed = output.ToArray();
        }

        using var mutated = new MemoryStream();
        mutated.Write(cleanOutput);
        var header = Encoding.ASCII.GetBytes(
            $"\n999 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
        mutated.Write(header);
        mutated.Write(compressed);
        mutated.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        return mutated.ToArray();
    }

    private static byte[] BuildPagePdf()
    {
        var content = Encoding.Latin1.GetBytes($"BT /F1 28 Tf 72 700 Td ({Canary}) Tj ET\n");
        using var stream = new MemoryStream();
        void Write(string value) => stream.Write(Encoding.Latin1.GetBytes(value));
        Write("%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
              "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        stream.Write(content);
        Write("endstream\nendobj\n");
        Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        Write("trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");
        return stream.ToArray();
    }
}
