using BenchmarkDotNet.Attributes;
using Pdfe.Core.Authoring;
using Pdfe.Core.Document;
using Pdfe.Core.Text;
using Pdfe.Rendering;

namespace Pdfe.Benchmarks;

/// <summary>
/// Core pdfe pipeline benchmarks (#344): parse, render, and text-extract on a
/// representative generated document. Run via <c>scripts/run-benchmarks.sh</c>.
/// These track pdfe-vs-pdfe regressions over time; see the project file for the
/// planned cross-library comparison.
/// </summary>
[MemoryDiagnoser]
public class PdfeBenchmarks
{
    [Params(1, 10)]
    public int PageCount;

    private byte[] _pdf = Array.Empty<byte>();

    [GlobalSetup]
    public void Setup()
    {
        var b = PdfDocumentBuilder.Create().Title("Benchmark");
        for (int i = 0; i < PageCount; i++)
        {
            b.Heading($"Section {i + 1}");
            b.Paragraph("The quick brown fox jumps over the lazy dog. " +
                        "Pack my box with five dozen liquor jugs. " +
                        "Sphinx of black quartz, judge my vow.");
        }
        _pdf = b.SaveToBytes();
    }

    [Benchmark]
    public int Parse()
    {
        using var doc = PdfDocument.Open(_pdf);
        return doc.PageCount;
    }

    [Benchmark]
    public int Render()
    {
        using var doc = PdfDocument.Open(_pdf);
        var renderer = new SkiaRenderer();
        using var bmp = renderer.RenderPage(doc.GetPage(1));
        return bmp.Width;
    }

    [Benchmark]
    public int ExtractText()
    {
        using var doc = PdfDocument.Open(_pdf);
        int total = 0;
        for (int p = 1; p <= doc.PageCount; p++)
            total += new TextExtractor(doc.GetPage(p)).ExtractText().Length;
        return total;
    }
}
