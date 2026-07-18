using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

public sealed class IncrementalRedactionRegressionTests
{
    [Fact]
    public void RedactText_IncrementalPageContent_DoesNotSerializePreviousRevisionBytes()
    {
        var pdf = BuildIncrementalPdf(
            new Dictionary<int, string>
            {
                [1] = Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                [2] = Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                [3] = Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                          "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
                [4] = Stream("", "BT /F1 12 Tf 100 700 Td (OLDREVISIONSECRET) Tj ET"),
                [5] = Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            },
            new Dictionary<int, string>
            {
                [4] = Stream("", "BT /F1 12 Tf 100 700 Td (CURRENTREVISIONSECRET) Tj ET"),
            });

        Encoding.Latin1.GetString(pdf).Should().Contain("OLDREVISIONSECRET");
        Encoding.Latin1.GetString(pdf).Should().Contain("CURRENTREVISIONSECRET");

        using var doc = PdfDocument.Open(pdf);
        var beforeText = string.Concat(doc.GetPage(1).Letters.Select(l => l.Value));
        beforeText.Should().Contain("CURRENTREVISIONSECRET");
        beforeText.Should().NotContain("OLDREVISIONSECRET");

        doc.RedactText("CURRENTREVISIONSECRET", drawBlackRect: false).Should().Be(1);

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());
        saved.Should().NotContain("CURRENTREVISIONSECRET");
        saved.Should().NotContain("OLDREVISIONSECRET",
            "full-save redaction output must not copy older incremental revisions");
    }

    [Fact]
    public void RedactArea_IncrementalAnnotation_DropsPreviousRevisionAppearanceObject()
    {
        var pdf = BuildIncrementalPdf(
            new Dictionary<int, string>
            {
                [1] = Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                [2] = Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                [3] = Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                          "/Contents 4 0 R /Annots [5 0 R] >>"),
                [4] = Stream("", ""),
                [5] = Obj("<< /Type /Annot /Subtype /FreeText /Rect [100 650 260 680] " +
                          "/Contents (OLDANNOTREVISIONSECRET) /AP << /N 6 0 R >> >>"),
                [6] = Stream("/Type /XObject /Subtype /Form /BBox [0 0 160 30]",
                             "BT /F1 12 Tf 2 10 Td (OLDANNOTREVISIONSECRET) Tj ET"),
            },
            new Dictionary<int, string>
            {
                [5] = Obj("<< /Type /Annot /Subtype /FreeText /Rect [100 650 260 680] " +
                          "/Contents (CURRENTANNOTREVISIONSECRET) >>"),
            });

        Encoding.Latin1.GetString(pdf).Should().Contain("OLDANNOTREVISIONSECRET");
        Encoding.Latin1.GetString(pdf).Should().Contain("CURRENTANNOTREVISIONSECRET");

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).GetAnnotations().Should().ContainSingle(a => a.Contents == "CURRENTANNOTREVISIONSECRET");

        doc.GetPage(1).RedactArea(new PdfRectangle(95, 645, 265, 685));

        var saved = Encoding.Latin1.GetString(doc.SaveToBytes());
        saved.Should().NotContain("CURRENTANNOTREVISIONSECRET");
        saved.Should().NotContain("OLDANNOTREVISIONSECRET",
            "unreachable old annotation appearance objects must be garbage-collected on redaction save");
    }

    private static string Obj(string body) => body;

    private static string Stream(string dictExtra, string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< {dictExtra} /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }

    private static byte[] BuildIncrementalPdf(
        IReadOnlyDictionary<int, string> baseObjects,
        IReadOnlyDictionary<int, string> updatedObjects)
    {
        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var maxObject = Math.Max(baseObjects.Keys.Max(), updatedObjects.Keys.Max());
        var baseOffsets = new Dictionary<int, long>();
        foreach (var objectNumber in baseObjects.Keys.OrderBy(k => k))
        {
            baseOffsets[objectNumber] = ms.Position;
            Write($"{objectNumber} 0 obj\n{baseObjects[objectNumber]}\nendobj\n");
        }

        var baseXref = ms.Position;
        Write($"xref\n0 {maxObject + 1}\n0000000000 65535 f \n");
        for (var objectNumber = 1; objectNumber <= maxObject; objectNumber++)
        {
            if (baseOffsets.TryGetValue(objectNumber, out var offset))
                Write($"{offset:D10} 00000 n \n");
            else
                Write("0000000000 65535 f \n");
        }
        Write($"trailer\n<< /Root 1 0 R /Size {maxObject + 1} >>\nstartxref\n{baseXref}\n%%EOF\n");

        var updateOffsets = new Dictionary<int, long>();
        foreach (var objectNumber in updatedObjects.Keys.OrderBy(k => k))
        {
            updateOffsets[objectNumber] = ms.Position;
            Write($"{objectNumber} 0 obj\n{updatedObjects[objectNumber]}\nendobj\n");
        }

        var updateXref = ms.Position;
        Write("xref\n");
        foreach (var objectNumber in updateOffsets.Keys.OrderBy(k => k))
        {
            Write($"{objectNumber} 1\n");
            Write($"{updateOffsets[objectNumber]:D10} 00000 n \n");
        }
        Write($"trailer\n<< /Root 1 0 R /Size {maxObject + 1} /Prev {baseXref} >>\nstartxref\n{updateXref}\n%%EOF");

        return ms.ToArray();
    }
}
