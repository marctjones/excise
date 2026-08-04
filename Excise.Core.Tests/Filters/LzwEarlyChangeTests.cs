using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Filters;

/// <summary>
/// §7.4.4.3 <c>/EarlyChange</c>. The PDF default is <b>1</b>: the LZW code
/// width grows one code EARLIER than the plain LZW rule, compensating for the
/// fact that a decoder's table always lags the encoder's by one entry.
/// <c>LzwFilterDecoder</c> hard-coded the <c>EarlyChange = 0</c> behaviour and
/// never read the parameter at all, so every conforming stream desynchronised
/// at the first code-width boundary.
///
/// WHY THIS TESTS THE REAL FILE RATHER THAN A GENERATED ONE
///
/// Two synthetic approaches were written and discarded, and the reason is
/// worth keeping:
///
///  1. A ROUND-TRIP against a hand-rolled encoder. An encoder written by the
///     same hand that just changed the decoder is not independent evidence,
///     and getting its width rule wrong makes the test fail for reasons that
///     have nothing to do with the product — which is exactly what happened,
///     because encoder and decoder hold different nextCode values at the same
///     instant, the very thing EarlyChange reconciles.
///  2. An arbitrary byte pattern, asserting the two settings DIFFER. Random
///     bytes are not a valid LZW stream: it desynchronised and threw the same
///     error under both settings, so it discriminated nothing.
///
/// The real 2700-byte stream does discriminate, and it is what the fix was
/// diagnosed on: the old behaviour threw "Invalid LZW code: 704" after
/// emitting 359 of 5805 bytes.
/// </summary>
public class LzwEarlyChangeTests
{
    private const string Fixture =
        "Isartor testsuite/PDFA-1b/6.1 File structure/6.1.10 Filters/isartor-6-1-10-t01-fail-a.pdf";

    /// <summary>
    /// The whole image decodes, not the 6% the EarlyChange=0 rule managed
    /// before the stream desynchronised. 215 x 27 at 8bpc indexed = 5805 bytes.
    /// </summary>
    [Fact]
    public void RealLzwImageStream_DecodesCompletely()
    {
        var path = FindCorpusFile(Fixture);
        Assert.SkipWhen(path == null, "gitignored Isartor corpus not present (scripts/download-test-pdfs.sh)."); // [requires: corpus:isartor]

        using var doc = PdfDocument.Open(path!);
        var image = FindLzwImage(doc);
        image.Should().NotBeNull("the fixture carries an /LZWDecode image XObject");

        image!.DecodedData.Length.Should().Be(215 * 27,
            "the full 215x27 8-bpc indexed image must decode — the EarlyChange=0 rule " +
            "desynchronised and threw after 359 bytes, leaving the page blank");
    }

    private static PdfStream? FindLzwImage(PdfDocument doc)
    {
        for (int n = 1; n < 40; n++)
        {
            PdfObject obj;
            try { obj = doc.GetObject(n); } catch { continue; }
            if (obj is not PdfStream s) continue;
            var filter = doc.Resolve(s.GetOptional("Filter") ?? PdfNull.Instance);
            var name = filter switch
            {
                PdfName pn => pn.Value,
                PdfArray a when a.Count > 0 && doc.Resolve(a[0]) is PdfName an => an.Value,
                _ => null,
            };
            if (name is "LZWDecode" or "LZW") return s;
        }
        return null;
    }

    private static string? FindCorpusFile(string relative)
    {
        var root = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", "isartor"));
        if (!Directory.Exists(root)) return null;
        var full = Path.Combine(root, relative);
        return File.Exists(full) ? full : null;
    }
}
