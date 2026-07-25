using Excise.Core.Document;
using Excise.Core.Primitives;
using AwesomeAssertions;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// Guards the content-stream parse cache on the render hot path (#598).
/// A Form XObject invoked N times on a page must be content-stream-parsed
/// once, while every invocation still executes under its own graphics
/// state (CTM, fill colour, ...) — the cache holds the PARSE, never the
/// drawn result.
/// </summary>
public class FormXObjectParseCacheTests
{
    [Fact]
    public void FormInvokedManyTimes_ParsesOnce_AndHonorsPerInvocationState()
    {
        // Form paints the unit-square-ish rect WITHOUT setting a colour, so
        // each invocation inherits the fill colour in force at the Do site.
        const string formContent = "0 0 40 40 re f";

        // Nine invocations of the same form at distinct translations with
        // distinct fill colours: red / green / blue repeating.
        var content = string.Join(" ",
            Invocation(60, 700, "1 0 0 rg"),
            Invocation(160, 700, "0 1 0 rg"),
            Invocation(260, 700, "0 0 1 rg"),
            Invocation(60, 600, "1 0 0 rg"),
            Invocation(160, 600, "0 1 0 rg"),
            Invocation(260, 600, "0 0 1 rg"),
            Invocation(60, 500, "1 0 0 rg"),
            Invocation(160, 500, "0 1 0 rg"),
            Invocation(260, 500, "0 0 1 rg"));

        var pdfData = FormPdfBuilder.Build(content, formContent);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        RenderContext.ContentStreamParseCacheHits = 0;
        using var bitmap = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });

        // 9 invocations of the same decoded form bytes → 1 parse, 8 cache hits.
        RenderContext.ContentStreamParseCacheHits.Should().BeGreaterThanOrEqualTo(8,
            "a form invoked N times must be content-stream-parsed once");

        // Per-invocation graphics state must still apply: each instance sits
        // at its own translation and carries its own fill colour.
        // Dpi 72 → 1 unit = 1 px; bitmap y = pageHeight (792) - pdf y.
        PixelAt(bitmap, 80, 720).Should().Be(new SKColor(255, 0, 0), "first column is red");
        PixelAt(bitmap, 180, 720).Should().Be(new SKColor(0, 255, 0), "second column is green");
        PixelAt(bitmap, 280, 720).Should().Be(new SKColor(0, 0, 255), "third column is blue");
        PixelAt(bitmap, 80, 520).Should().Be(new SKColor(255, 0, 0), "cached parse must not freeze first-invocation state");
        PixelAt(bitmap, 280, 520).Should().Be(new SKColor(0, 0, 255), "blue column repeats");

        // And outside every instance the page stays white.
        PixelAt(bitmap, 400, 720).Should().Be(SKColors.White);
    }

    [Fact]
    public void FormStreamMutatedBetweenRenders_RendersNewContent()
    {
        const string formContent = "0 g 0 0 40 40 re f";
        var content = "q 1 0 0 1 100 700 cm /Fm1 Do Q";
        var pdfData = FormPdfBuilder.Build(content, formContent);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        using (var before = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 }))
        {
            PixelAt(before, 120, 720).Should().Be(SKColors.Black, "original form paints black at 100..140 x 700..740");
        }

        // Replace the form's content: same box, moved 200pt right. The
        // DecodedData setter installs a NEW byte[] instance, so no cached
        // parse (reference-keyed) can ever serve the old operators.
        var form = ResolveForm(doc);
        form.DecodedData = System.Text.Encoding.ASCII.GetBytes("0 g 200 0 40 40 re f");

        using var after = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });
        PixelAt(after, 120, 720).Should().Be(SKColors.White, "old form content must not survive the mutation");
        PixelAt(after, 320, 720).Should().Be(SKColors.Black, "new form content must render");
    }

    private static PdfStream ResolveForm(PdfDocument doc)
    {
        var resources = doc.GetPage(1).Resources!;
        var xobjects = (PdfDictionary)doc.Resolve(resources.GetOptional("XObject")!)!;
        return (PdfStream)doc.Resolve(xobjects.GetOptional("Fm1")!)!;
    }

    private static string Invocation(int tx, int ty, string fill) =>
        $"q {fill} 1 0 0 1 {tx} {ty} cm /Fm1 Do Q";

    private static SKColor PixelAt(SKBitmap bitmap, int pdfX, int pdfY) =>
        bitmap.GetPixel(pdfX, 792 - pdfY);

    private static class FormPdfBuilder
    {
        public static byte[] Build(string content, string formContent)
        {
            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
            writer.NewLine = "\n";

            writer.WriteLine("%PDF-1.4");
            writer.Flush();

            var offsets = new long[6];

            offsets[1] = ms.Position;
            writer.WriteLine("1 0 obj");
            writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
            writer.WriteLine("endobj");
            writer.Flush();

            offsets[2] = ms.Position;
            writer.WriteLine("2 0 obj");
            writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
            writer.WriteLine("endobj");
            writer.Flush();

            offsets[3] = ms.Position;
            writer.WriteLine("3 0 obj");
            writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
            writer.WriteLine("   /Contents 4 0 R");
            writer.WriteLine("   /Resources << /XObject << /Fm1 5 0 R >> >> >>");
            writer.WriteLine("endobj");
            writer.Flush();

            offsets[4] = ms.Position;
            writer.WriteLine("4 0 obj");
            writer.WriteLine($"<< /Length {content.Length} >>");
            writer.WriteLine("stream");
            writer.Write(content);
            writer.WriteLine();
            writer.WriteLine("endstream");
            writer.WriteLine("endobj");
            writer.Flush();

            offsets[5] = ms.Position;
            writer.WriteLine("5 0 obj");
            writer.WriteLine("<< /Type /XObject /Subtype /Form /BBox [0 0 612 792]");
            writer.WriteLine($"   /Matrix [1 0 0 1 0 0] /Length {formContent.Length} >>");
            writer.WriteLine("stream");
            writer.Write(formContent);
            writer.WriteLine();
            writer.WriteLine("endstream");
            writer.WriteLine("endobj");
            writer.Flush();

            long xrefPos = ms.Position;
            writer.WriteLine("xref");
            writer.WriteLine("0 6");
            writer.WriteLine("0000000000 65535 f ");
            for (int i = 1; i <= 5; i++)
                writer.WriteLine($"{offsets[i]:D10} 00000 n ");
            writer.Flush();

            writer.WriteLine("trailer");
            writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
            writer.WriteLine("startxref");
            writer.WriteLine(xrefPos.ToString());
            writer.WriteLine("%%EOF");
            writer.Flush();

            return ms.ToArray();
        }
    }
}
