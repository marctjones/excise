using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// Guards the glyph-outline caches on the render text hot path (#598).
/// SKFont.GetGlyphPath / GetTextPath drive the platform font scaler to
/// tessellate a glyph outline on every call; the same glyph recurs thousands
/// of times per page of body text, so the caches must serve the repeats. The
/// caches hold the UNPOSITIONED outline only — every draw still transforms a
/// fresh copy to its own cursor/scale, which is what keeps the raster
/// byte-identical (see the pixel-identity Visual/Differential suites).
/// </summary>
public class GlyphOutlineCacheTests
{
    [Fact]
    public void RepeatedGlyphsAreTessellatedOnceAndReused()
    {
        var fixture = Path.Combine(
            FindRepoRoot(), "test-pdfs", "sample-pdfs", "multilingual-noto-cjk.pdf");
        if (!File.Exists(fixture))
            return; // fixture not present in this checkout — nothing to guard

        using var doc = PdfDocument.Open(File.ReadAllBytes(fixture));
        var renderer = new SkiaRenderer();

        RenderContext.GlyphOutlineCacheHits = 0;
        RenderContext.GlyphOutlineCacheMisses = 0;

        for (int p = 1; p <= doc.PageCount; p++)
            using (renderer.RenderPage(doc.GetPage(p), new RenderOptions { Dpi = 96 })) { }

        long hits = RenderContext.GlyphOutlineCacheHits;
        long misses = RenderContext.GlyphOutlineCacheMisses;

        misses.Should().BeGreaterThan(0,
            "the glyph-outline path must be exercised by this embedded-font fixture");
        hits.Should().BeGreaterThan(0,
            "recurring glyphs must be served from the outline cache, not re-tessellated");
        // This is a mechanism guard, not a pixel or ratio assertion: hit rate is
        // fixture-dependent (this multilingual sample has many unique CJK
        // glyphs). On Latin body text corpus-wide it exceeds 90% (#598).
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "excise.sln")))
            d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }
}
