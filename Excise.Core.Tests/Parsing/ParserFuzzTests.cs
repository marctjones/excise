using System;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// Property-based / fuzz coverage for the parsers (#352): on hostile or
/// malformed bytes the parser must either parse them or fail with a *typed*
/// PDF exception — never a raw CLR crash (NullReference, IndexOutOfRange,
/// ArgumentOutOfRange, Overflow, …) that signals a missing bounds/null check,
/// and never hang (the #346/#347 guards + a hard time budget enforce this).
/// Seeds are fixed for reproducibility.
/// </summary>
public class ParserFuzzTests
{
    // Exception types that indicate a *handled* malformed-input condition.
    private static bool IsGraceful(Exception ex) =>
        ex is PdfParseException
        || ex is PdfEncryptionNotSupportedException
        || ex is NotSupportedException          // e.g. unknown stream filter
        || ex is System.IO.EndOfStreamException; // truncated stream

    // The raw CLR types that would indicate a parser bug (missing guard).
    private static void AssertGraceful(Exception ex, byte[] input, int seed, int iter)
    {
        if (IsGraceful(ex)) return;
        throw new Xunit.Sdk.XunitException(
            $"seed={seed} iter={iter} len={input.Length}: parser threw a raw " +
            $"{ex.GetType().Name} (\"{ex.Message}\") instead of a typed PdfParseException. " +
            $"This is a missing guard — fix the parser. First bytes: " +
            BitConverter.ToString(input, 0, Math.Min(32, input.Length)) +
            "\nSTACK:\n" + ex.StackTrace);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void PdfDocument_Open_RandomBytes_FailsGracefullyOrParses(int seed)
    {
        var rng = new Random(seed);
        for (int iter = 0; iter < 400; iter++)
        {
            var bytes = new byte[rng.Next(0, 6000)];
            rng.NextBytes(bytes);
            try
            {
                using var doc = PdfDocument.Open(bytes);
                _ = doc.PageCount; // touch the structure
            }
            catch (Exception ex) { AssertGraceful(ex, bytes, seed, iter); }
        }
    }

    [Theory]
    [InlineData(11)] [InlineData(22)] [InlineData(33)]
    public void PdfDocument_Open_MutatedValidPdf_FailsGracefullyOrParses(int seed)
    {
        var valid = MinimalValidPdf();
        var rng = new Random(seed);
        for (int iter = 0; iter < 400; iter++)
        {
            var bytes = (byte[])valid.Clone();
            // Apply a handful of random single-byte mutations.
            int muts = rng.Next(1, 12);
            for (int m = 0; m < muts; m++)
                bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            try
            {
                using var doc = PdfDocument.Open(bytes);
                _ = doc.PageCount;
                if (doc.PageCount > 0) { _ = doc.GetPage(1).GetContentStreamBytes(); }
            }
            catch (Exception ex) { AssertGraceful(ex, bytes, seed, iter); }
        }
    }

    [Theory]
    [InlineData(101)] [InlineData(202)] [InlineData(303)]
    public void ContentStreamParser_RandomBytes_FailsGracefullyOrParses(int seed)
    {
        var rng = new Random(seed);
        for (int iter = 0; iter < 600; iter++)
        {
            var bytes = new byte[rng.Next(0, 4000)];
            rng.NextBytes(bytes);
            try
            {
                _ = new ContentStreamParser(bytes).Parse();
            }
            catch (Exception ex) { AssertGraceful(ex, bytes, seed, iter); }
        }
    }

    [Fact]
    public void ContentStreamParser_MutatedValidContent_FailsGracefullyOrParses()
    {
        var valid = Encoding.ASCII.GetBytes(
            "q 1 0 0 1 0 0 cm BT /F1 12 Tf 100 700 Td (hello [world]) Tj ET " +
            "0 0 100 100 re f [1 [2 3] 4] TJ BI /W 1 /H 1 /BPC 8 /CS /G ID \xFF EI Q");
        var rng = new Random(777);
        for (int iter = 0; iter < 600; iter++)
        {
            var bytes = (byte[])valid.Clone();
            int muts = rng.Next(1, 10);
            for (int m = 0; m < muts; m++)
                bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            try
            {
                _ = new ContentStreamParser(bytes).Parse();
            }
            catch (Exception ex) { AssertGraceful(ex, bytes, 777, iter); }
        }
    }

    private static byte[] MinimalValidPdf()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new long[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
