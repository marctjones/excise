using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;
namespace Excise.Rendering.Tests.Corpus;

/// <summary>
/// Smoke tests over a curated corpus of public-domain US government PDFs.
/// Unlike the pixel-perfect tests in <see cref="Visual.RenderingVisualTests"/>,
/// these assert only non-crash behavior and minimum render viability:
///   - Document opens
///   - First page renders without throwing
///   - Bitmap is reasonably sized
///   - At least some non-white content made it onto the canvas
///
/// Populate the corpus with <c>scripts/download-smoke-corpus.sh</c>. If the
/// corpus directory is empty or missing, the test degenerates to a single
/// skipped case rather than failing.
/// </summary>
public class SmokeCorpusTests
{
    private readonly ITestOutputHelper _output;

    public SmokeCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void SmokeCorpus_FirstPage_RendersWithoutCrashing(string pdfPath)
    {
        if (pdfPath == SentinelNoCorpus)
        {
            _output.WriteLine(
                "No smoke corpus found at test-pdfs/smoke/. " +
                "Run scripts/download-smoke-corpus.sh to populate it.");
            return;
        }

        var name = Path.GetFileName(pdfPath);
        var sw = Stopwatch.StartNew();

        using var doc = PdfDocument.Open(pdfPath);
        doc.PageCount.Should().BeGreaterThan(0, $"{name} should have at least one page");

        var page = doc.GetPage(1);
        page.Width.Should().BeGreaterThan(0);
        page.Height.Should().BeGreaterThan(0);

        // 72 DPI keeps the full run fast; at 200 DPI a multi-megabyte instructions
        // PDF alone takes several seconds. Smoke goal is "does it crash," not fidelity.
        var renderer = new SkiaRenderer();
        using var bitmap = renderer.RenderPage(page, new RenderOptions { Dpi = 72 });

        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(50, $"{name} rendered absurdly narrow");
        bitmap.Height.Should().BeGreaterThan(50, $"{name} rendered absurdly short");

        HasNonTrivialContent(bitmap).Should().BeTrue(
            $"{name} rendered as a blank (all-white) bitmap — renderer likely threw " +
            "silently or produced no draw calls");

        sw.Stop();
        _output.WriteLine(
            $"{name}: page 1 ({page.Width:0}x{page.Height:0} pt) → " +
            $"{bitmap.Width}x{bitmap.Height} px in {sw.ElapsedMilliseconds} ms " +
            $"({doc.PageCount} total pages)");
    }

    /// <summary>
    /// Samples ~1000 pixels on a sparse grid and returns true if any are not
    /// near-white. Avoids scanning every pixel (slow on 2MP bitmaps) and the
    /// tolerance handles anti-aliased greyscale text on a white background.
    /// </summary>
    private static bool HasNonTrivialContent(SKBitmap bitmap)
    {
        const int samples = 32;
        var stepX = Math.Max(1, bitmap.Width / samples);
        var stepY = Math.Max(1, bitmap.Height / samples);

        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red < 240 || p.Green < 240 || p.Blue < 240)
                    return true;
            }
        }
        return false;
    }

    public static IEnumerable<object[]> CorpusFiles()
    {
        var dir = ResolveCorpusDir();
        if (dir == null || !Directory.Exists(dir))
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        var files = Directory.GetFiles(dir, "*.pdf");
        if (files.Length == 0)
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var f in files)
            yield return new object[] { f };
    }

    private static string? ResolveCorpusDir()
    {
        // Walk up from bin/Debug/netX to the repo root and look for test-pdfs/smoke.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test-pdfs", "smoke");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private const string SentinelNoCorpus = "<no-corpus-downloaded>";
}
