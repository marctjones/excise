using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// An encrypted file's stream bodies are random bytes. The scanner tries to inflate
/// every stream body, and zlib reports corrupt or non-zlib input as an exception of its
/// own (an IOException, not an InvalidDataException) for some byte patterns. One of those
/// escaped as a flaky failure in <c>XfaStaticDataSyncTests.Encrypted_RoundTrip_...</c>.
/// Seeded random bodies make that failure deterministic: a body that cannot be inflated
/// is "nothing to scan", never an exception.
/// </summary>
public class SavedPdfLeakScannerRandomStreamTests
{
    private static byte[] PdfWithStream(byte[] body)
    {
        using var file = new MemoryStream();
        void Write(byte[] b) => file.Write(b, 0, b.Length);
        void Ascii(string s) => Write(Encoding.Latin1.GetBytes(s));
        Ascii("%PDF-1.7\n4 0 obj\n<< /Length " + body.Length + " >>\nstream\n");
        Write(body);
        Ascii("\nendstream\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        return file.ToArray();
    }

    [Fact]
    public void RandomStreamBodies_AreNeverAnException_AndNeverAFalseHit()
    {
        const int Seeds = 4000;
        for (var seed = 0; seed < Seeds; seed++)
        {
            var rng = new System.Random(seed);
            var body = new byte[16 + rng.Next(0, 600)];
            rng.NextBytes(body);
            // About half of the seeds start like a zlib stream (a plausible CMF/FLG pair), the
            // case that gets past the header check and fails later inside the inflater.
            if (seed % 2 == 0)
            {
                body[0] = 0x78;
                body[1] = new byte[] { 0x01, 0x5E, 0x9C, 0xDA }[seed / 2 % 4];
            }

            var scan = () => SavedPdfLeakScanner.FindTerm(PdfWithStream(body), "ZEBRAQUASAR");

            scan.Should().NotThrow($"random stream body for seed {seed} must be skipped, not thrown");
            scan().Should().BeEmpty($"random bytes for seed {seed} do not contain the term");
        }
    }
}
