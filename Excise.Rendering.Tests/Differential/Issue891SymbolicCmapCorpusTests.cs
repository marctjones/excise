using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #891 on the real pdf.js corpus fixtures that motivated it. Both are
/// symbolic subset TrueType fonts whose ONLY built-in cmap subtable is format 4
/// or 6 — <see cref="Fonts.CmapFormat0Table"/> (format 0 only) couldn't read
/// either, so <c>RenderContext.ResolveByteCodeCmap</c> had no byte→GID route and
/// both pages rendered entirely blank (verified independently: mutool draws
/// "RTWABIGP" / "OPENMAGAZIN" from the embedded programs; excise drew nothing).
///
/// | fixture           | font                          | cmap that resolves it        |
/// |--------------------|-------------------------------|-------------------------------|
/// | bug1027533.pdf     | GRDDWT+Arial-BoldMT            | (1,0) format 6 / (3,0) format 4 (0xF000|code) |
/// | bug1151216.pdf     | MFKCOC+TTF9t00                 | (1,0) format 6 / (3,0) format 4 (0xF000|code) |
///
/// Both verified directly against the extracted <c>FontFile2</c> program: all
/// content-stream codes resolve to non-.notdef, outline-bearing glyph ids via
/// EITHER subtable.
/// </summary>
public sealed class Issue891SymbolicCmapCorpusTests
{
    private const int Dpi = 150;
    private readonly ITestOutputHelper _out;

    public Issue891SymbolicCmapCorpusTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Bug1027533_ArialBoldSubset_RendersInk_NotBlank()
    {
        AssertFixtureRendersInkMatchingOracle("bug1027533.pdf");
    }

    [Fact]
    public void Bug1151216_Ttf9t00Subset_RendersInk_NotBlank()
    {
        AssertFixtureRendersInkMatchingOracle("bug1151216.pdf");
    }

    private void AssertFixtureRendersInkMatchingOracle(string fileName)
    {
        var root = LocateRepoRoot();
        Assert.SkipWhen(root == null, "Could not find repo root.");
        var pdfPath = Path.Combine(root!, "test-pdfs", "pdfjs", fileName);
        Assert.SkipWhen(!File.Exists(pdfPath),
            $"pdf.js fixture not found at test-pdfs/pdfjs/{fileName}. Run scripts/download-pdfjs-corpus.sh.");

        double exciseInk;
        using (var doc = PdfDocument.Open(pdfPath))
        using (var bmp = new SkiaRenderer().RenderPage(
                   doc.GetPage(1), new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White }))
            exciseInk = InkFraction(bmp);
        _out.WriteLine($"{fileName}: excise ink = {exciseInk:P3}");

        exciseInk.Should().BeGreaterThan(0.002,
            $"{fileName}'s symbolic TrueType has a (1,0) format-6 / (3,0) format-4 cmap that " +
            "excise must now read via TrueTypeFontFile.GidForSymbolByte instead of drawing nothing");

        Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
        using var refBmp = MutoolReferenceRenderer.RenderPage(pdfPath, 1, Dpi);
        Assert.SkipWhen(refBmp == null, "mutool declined to render.");
        var refInk = InkFraction(refBmp!);
        _out.WriteLine($"{fileName}: mutool ink = {refInk:P3}");
        refInk.Should().BeGreaterThan(0.002,
            "the independent oracle (mutool, reading the same embedded FontFile2) must also paint ink");
    }

    private static bool IsInk(SKColor p) => p.Red < 200 || p.Green < 200 || p.Blue < 200;

    private static double InkFraction(SKBitmap b)
    {
        long ink = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (IsInk(b.GetPixel(x, y))) ink++;
        return (double)ink / (b.Width * (long)b.Height);
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
