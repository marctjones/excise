using System.Linq;
using System.Text;
using Avalonia;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1450: the GUI's click-to-redact area path
/// (<see cref="RedactionService.RedactArea(PdfPage, Rect, int)"/>) used to
/// append its visual black-box marker through an untracked
/// <c>page.GetContentStream()</c> / <c>page.SetContentStream(new
/// ContentStream(ops))</c> — no <c>SourceBytes</c>, so the append
/// re-serialized the WHOLE page a second time even though
/// <c>PdfPageRedactionExtensions.RedactArea</c> just did the #1093/#1449
/// hardened splice a few lines earlier. It now calls Core's own
/// <c>PdfDocumentRedactionExtensions.AppendBlackRectangle</c> instead of
/// keeping a duplicate.
///
/// <para>These tests pin the property that duplicate existed to violate: on a
/// multi-stream <c>/Contents</c> array, a GUI area redaction of one stream's
/// text must not silently delete the OTHER array elements (the pre-#1449
/// behaviour every untracked re-serialize still falls back to), and the
/// redacted term must be gone from the SAVED bytes, not merely from
/// <c>page.Text</c> — CLAUDE.md's standing rule that a redaction test may
/// never rely on <c>ExtractAllText</c> alone.</para>
/// </summary>
public class RedactionServiceMultiStreamContentsTests
{
    private static string StreamText(string body, int y) =>
        $"BT\n/F1 12 Tf\n72 {y} Td\n({body}) Tj\nET\n";

    /// <summary>
    /// A one-page PDF whose <c>/Contents</c> is an ARRAY of streams (ISO
    /// 32000-2 §7.7.3.3), mirroring
    /// <c>Excise.Core.Tests.Content.MultiStreamContentsArrayTests.BuildMultiStreamPdf</c>.
    /// </summary>
    private static byte[] BuildMultiStreamPdf(params string[] streamContents)
    {
        var bodies = streamContents.Select(Encoding.Latin1.GetBytes).ToArray();
        using var ms = new System.IO.MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");

        var offset1 = ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var offset2 = ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        const int firstStreamObj = 4;
        var fontObj = firstStreamObj + bodies.Length;
        var streamRefs = string.Join(" ",
            System.Linq.Enumerable.Range(0, bodies.Length).Select(i => $"{firstStreamObj + i} 0 R"));

        var offset3 = ms.Position;
        W($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
          + $"/Contents [{streamRefs}] /Resources << /Font << /F1 {fontObj} 0 R >> >> >>\nendobj\n");

        var streamOffsets = new long[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            streamOffsets[i] = ms.Position;
            W($"{firstStreamObj + i} 0 obj\n<< /Length {bodies[i].Length} >>\nstream\n");
            ms.Write(bodies[i]);
            W("\nendstream\nendobj\n");
        }

        var fontOffset = ms.Position;
        W($"{fontObj} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var size = fontObj + 1;
        var xref = ms.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        W($"{offset1:D10} 00000 n \n");
        W($"{offset2:D10} 00000 n \n");
        W($"{offset3:D10} 00000 n \n");
        foreach (var so in streamOffsets)
            W($"{so:D10} 00000 n \n");
        W($"{fontOffset:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    [Fact]
    public void RedactArea_OnFirstStream_KeepsTheSecondStreamAsASeparateArrayElement()
    {
        var stream0 = StreamText("SECRET", 720);
        var stream1 = StreamText("Innocuous", 500);
        using var doc = PdfDocument.Open(BuildMultiStreamPdf(stream0, stream1));
        var page = doc.GetPage(1);

        var service = new RedactionService(
            NullLogger<RedactionService>.Instance, new NullLoggerFactory());

        // PdfPageRect.FromContentPoints takes content-stream (bottom-left
        // origin) coordinates directly, sidestepping the visual/DPI
        // conversion the Rect overload does — a rect over ONLY the first
        // (y=720) line's text, well clear of the second (y=500). This calls
        // the exact RedactArea(PdfPage, PdfPageRect) overload whose
        // AppendBlackRectangle call #1450 changed.
        service.RedactArea(page, PdfPageRect.FromContentPoints(
            page.PageNumber, new PdfRectangle(60, 710, 180, 740)));

        var array = doc.Resolve(page.Dictionary["Contents"]) as PdfArray;
        array.Should().NotBeNull();
        array!.Count.Should().Be(2,
            "the GUI area-redaction path must not collapse an untouched multi-stream " +
            "/Contents array to one element — the exact defect #1450 fixes");

        var second = doc.Resolve(array[1]) as PdfStream;
        second.Should().NotBeNull();
        Encoding.Latin1.GetString(second!.DecodedData).Should().StartWith(stream1,
            "the second stream's own operators were never in the redaction area and must " +
            "keep their exact original bytes, not be silently deleted by a fallback collapse");

        // CLAUDE.md: a redaction test may not rely on page.Text/ExtractAllText
        // alone. Carrier-agnostic scan of the SAVED bytes.
        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, "SECRET").Should().BeEmpty(
            "the redacted term must be gone from every carrier in the saved file");
    }
}
