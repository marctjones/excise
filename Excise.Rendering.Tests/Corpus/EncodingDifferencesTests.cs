using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.Rendering.Tests.Corpus;

/// <summary>
/// Regression tests for the /Encoding dictionary + /Differences code path in
/// <see cref="SkiaRenderer"/>. These hand-craft tiny PDFs where a non-printable
/// byte (e.g. 0x01) is mapped to a visible glyph name like /A via the /Encoding
/// dictionary's /Differences array. Without the fix those bytes decode as
/// control characters and draw nothing; with it they draw 'A'.
/// </summary>
public class EncodingDifferencesTests
{
    [Fact]
    public void DifferencesArray_MapsSubsetCode_ToVisibleGlyph()
    {
        // Content stream uses byte 0x01 (unprintable in WinAnsi). The font's
        // /Encoding dict remaps byte 1 to the glyph /A via /Differences.
        var content = "BT /F1 48 Tf 100 700 Td (\x01) Tj ET";
        var pdf = BuildPdfWithDifferences(content, differenceStartCode: 1, glyphName: "A");

        using var doc = PdfDocument.Open(pdf);
        var renderer = new SkiaRenderer();
        using var bitmap = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 150 });

        // Sample a band where the glyph should draw. The 'A' is at PDF coords
        // roughly (100, 700); at 150 DPI on an 8.5x11 page that's ~ (208, 192px
        // from top). Sweep a generous region to stay robust against font
        // metrics.
        bool foundInk = false;
        for (int y = 140; y < 260 && !foundInk; y++)
        {
            for (int x = 180; x < 320 && !foundInk; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red < 200 && p.Green < 200 && p.Blue < 200)
                    foundInk = true;
            }
        }

        foundInk.Should().BeTrue(
            "byte 0x01 remapped via /Differences to /A should render a visible 'A' glyph");
    }

    [Fact]
    public void NamedEncoding_WithoutDifferences_StillRendersCorrectly()
    {
        // Regression guard: the named-encoding fast path must not be broken by
        // the new dictionary code.
        var content = "BT /F1 48 Tf 100 700 Td (A) Tj ET";
        var pdf = BuildPdfWithWinAnsi(content);

        using var doc = PdfDocument.Open(pdf);
        var renderer = new SkiaRenderer();
        using var bitmap = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 150 });

        bool foundInk = false;
        for (int y = 140; y < 260 && !foundInk; y++)
        {
            for (int x = 180; x < 320 && !foundInk; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red < 200 && p.Green < 200 && p.Blue < 200)
                    foundInk = true;
            }
        }

        foundInk.Should().BeTrue("a plain WinAnsi 'A' must still render");
    }

    private static byte[] BuildPdfWithDifferences(string content, int differenceStartCode, string glyphName)
    {
        // 6 objects: catalog, pages, page, contents, font, encoding-dict.
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1, leaveOpen: true) { NewLine = "\n" };

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];
        WriteObj(writer, ms, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObj(writer, ms, offsets, 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObj(writer, ms, offsets, 3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        WriteObj(writer, ms, offsets, 5,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding 6 0 R >>");
        WriteObj(writer, ms, offsets, 6,
            $"<< /Type /Encoding /BaseEncoding /WinAnsiEncoding " +
            $"/Differences [{differenceStartCode} /{glyphName}] >>");

        WriteXref(writer, ms, offsets, entryCount: 6);
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithWinAnsi(string content)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1, leaveOpen: true) { NewLine = "\n" };

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];
        WriteObj(writer, ms, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObj(writer, ms, offsets, 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObj(writer, ms, offsets, 3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        WriteObj(writer, ms, offsets, 5,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        WriteXref(writer, ms, offsets, entryCount: 5);
        return ms.ToArray();
    }

    private static void WriteObj(StreamWriter writer, MemoryStream ms, long[] offsets, int num, string body)
    {
        offsets[num] = ms.Position;
        writer.WriteLine($"{num} 0 obj");
        writer.WriteLine(body);
        writer.WriteLine("endobj");
        writer.Flush();
    }

    private static void WriteXref(StreamWriter writer, MemoryStream ms, long[] offsets, int entryCount)
    {
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {entryCount + 1}");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= entryCount; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Root 1 0 R /Size {entryCount + 1} >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();
    }
}
