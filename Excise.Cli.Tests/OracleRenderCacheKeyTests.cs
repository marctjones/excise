using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

using RenderProgram = Excise.RenderTools.Program;

namespace Excise.Cli.Tests;

/// <summary>
/// #1385: the oracle render cache's key did not include the arguments an
/// oracle is actually invoked with, so changing a renderer's flags (#1380
/// added -cropbox to pdftocairo) left every pre-change cached render valid
/// as far as the cache was concerned -- a rescan reported "changed nothing"
/// because it never re-rendered anything. These tests pin the acceptance
/// criterion directly: two different invocation signatures over the same
/// (oracle, path, page, dpi, password) must both miss the cache, not the
/// second one silently reusing the first's render.
/// </summary>
public class OracleRenderCacheKeyTests
{
    private static string NewCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-oracle-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string NewFakePdf()
    {
        var path = Path.Combine(Path.GetTempPath(), "excise-oracle-cache-test-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public void GetOrRender_DifferentInvocationSignature_MissesTheCacheBothTimes()
    {
        var cacheDir = NewCacheDir();
        var pdfPath = NewFakePdf();
        try
        {
            var cache = new RenderProgram.OracleRenderCache(cacheDir);
            var renderCalls = 0;
            ReferenceRenderResult Render() { renderCalls++; return new ReferenceRenderResult(null, "TOOL_UNAVAILABLE", "test", 0); }

            cache.GetOrRender("mutool", pdfPath, 1, 150, null, "draw -F png", Render);
            cache.GetOrRender("mutool", pdfPath, 1, 150, null, "draw -F png -cropbox", Render);

            renderCalls.Should().Be(2,
                "two different invocation signatures over the same (oracle, path, page, dpi, password) " +
                "must both be treated as cache misses -- the second must not silently reuse the first's render");
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void GetOrRender_SameInvocationSignature_IsStillCacheableOnSuccess()
    {
        // The other side of #1385's fix: the signature must not become part
        // of an unconditional cache-buster -- an unchanged signature over an
        // unchanged (oracle, path, page, dpi, password) still benefits from
        // caching once a render actually succeeds.
        var cacheDir = NewCacheDir();
        var pdfPath = NewFakePdf();
        try
        {
            var cache = new RenderProgram.OracleRenderCache(cacheDir);
            var renderCalls = 0;
            ReferenceRenderResult Render()
            {
                renderCalls++;
                using var bmp = new SkiaSharp.SKBitmap(4, 4);
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                return new ReferenceRenderResult(SkiaSharp.SKBitmap.Decode(img.Encode()), "OK", null, 1);
            }

            var first = cache.GetOrRender("mutool", pdfPath, 1, 150, null, "draw -F png", Render);
            var second = cache.GetOrRender("mutool", pdfPath, 1, 150, null, "draw -F png", Render);

            renderCalls.Should().Be(1, "the second call has the same signature and should hit the cache");
            first.CacheHit.Should().BeFalse();
            second.CacheHit.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
            File.Delete(pdfPath);
        }
    }
}
