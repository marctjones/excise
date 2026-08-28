using System.Diagnostics;
using System.IO;
using AwesomeAssertions;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// JPX soft masks must not render transparent image regions as black
/// rectangles — run OUT OF PROCESS, deliberately (#985).
///
/// In-process, this assertion could not run at all. Rendering
/// test-pdfs/pdfjs/S2.pdf inside the test host makes the host vanish
/// SILENTLY: no result, no crash dump under --blame-crash, no hang (it
/// returns in ~2s), and vstest reports "No test matches the given testcase
/// filter". The test was DISCOVERED by --list-tests and never selected —
/// 236 discovered, 235 executed in its old class.
///
/// The bisect that established this, all on current develop:
///
///   trivial body                      -> runs
///   Open() only, no render            -> runs
///   Open + RenderPage on S2.pdf       -> host vanishes, "No test matches"
///   Open + RenderPage on another file -> runs (5s)
///   fixture ABSENT (SkipWhen fires)   -> runs, reports SKIP
///   moved to its own class / renamed  -> still vanishes
///   [Fact] vs [Theory], with/without Timeout -> no difference
///
/// The last two rows are the proof it is not discovery: with the file
/// missing the same test is selected and reports a skip, so selection works.
/// It is the RENDER that ends the process, quietly enough that no tooling
/// notices — the signature of a native exit rather than a fault.
///
/// Per project policy a defect originating in Skia/JPX is not ours to fix and
/// not something to compensate for, but a silent process exit still has to be
/// CONTAINED, because it deletes test coverage invisibly. Containment here is
/// the same mechanism the reference oracles already use: render in a child
/// process, assert on the bytes it produced. A child that dies takes nothing
/// with it, and its death is then observable.
/// </summary>
public class JpxSoftMaskRenderTests
{
    [Fact]
    public void JpxSoftMasks_ClearImageBackgrounds_RenderedOutOfProcess()
    {
        var fixture = SkiaRendererTests.FindRepoFile("test-pdfs", "pdfjs", "S2.pdf");
        Assert.SkipWhen(fixture == null,
            "No pdf.js S2 fixture at test-pdfs/pdfjs/S2.pdf.");

        // NOT a skip. The csproj carries a build-order ProjectReference to
        // Excise.Cli precisely so this cannot go missing; if it is missing
        // anyway, that is a broken build, and skipping would re-create the
        // silent coverage hole this test exists to close.
        var cli = FindCliAssembly();
        cli.Should().NotBeNull(
            "Excise.Cli must be built — Excise.Rendering.Tests.csproj references it for build order (#985)");

        var output = Path.Combine(Path.GetTempPath(), $"excise-jpx-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { cli!, "render", fixture!, "--page", "1", "--dpi", "72", "-o", output })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000).Should().BeTrue("the render must not hang");

            // The child's fate is the observation this test exists to make.
            proc.ExitCode.Should().Be(0,
                "rendering S2.pdf must not kill the process — that is #985's symptom, " +
                $"now visible instead of silent. stdout={stdout} stderr={stderr}");
            File.Exists(output).Should().BeTrue("the child must have written a raster");

            using var bitmap = SKBitmap.Decode(output);
            bitmap.Should().NotBeNull("the produced PNG must be decodable");

            var (whiteFraction, darkFraction) = SkiaRendererTests.MeasureWhiteAndDarkPixels(bitmap!);
            whiteFraction.Should().BeGreaterThan(0.30,
                "JPX soft masks should preserve the white page background around transparent image regions");
            darkFraction.Should().BeLessThan(0.18,
                "transparent JPX image regions should not render as black rectangles");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// #1196 — a JPEG 2000 image behind a filter CHAIN (generalized filters
    /// before the terminal JPXDecode) must decode, not render blank. The image
    /// in jpx_lzw.pdf is /Filter [/ASCIIHexDecode /LZWDecode /JPXDecode] and
    /// decodes (opj + mutool + poppler agree) to a flat value-76 full-page fill.
    /// Before the fix the codec was fed the raw ASCIIHex+LZW bytes and produced
    /// nothing, leaving a blank WHITE page. Rendered out of process, per #985.
    /// </summary>
    [Fact]
    public void JpxImageInFilterChain_Decodes_NotBlank()
    {
        var fixture = SkiaRendererTests.FindRepoFile("test-pdfs", "pdfium", "jpx_lzw.pdf");
        Assert.SkipWhen(fixture == null,
            "No pdfium jpx_lzw fixture at test-pdfs/pdfium/jpx_lzw.pdf [requires: corpus:pdfium].");

        var cli = FindCliAssembly();
        cli.Should().NotBeNull("Excise.Cli must be built (#985 build-order ProjectReference)");

        var output = Path.Combine(Path.GetTempPath(), $"excise-jpxchain-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { cli!, "render", fixture!, "--page", "1", "--dpi", "100", "-o", output })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000).Should().BeTrue("the render must not hang");
            proc.ExitCode.Should().Be(0, $"render must succeed. stdout={stdout} stderr={stderr}");
            File.Exists(output).Should().BeTrue("the child must have written a raster");

            using var bitmap = SKBitmap.Decode(output);
            bitmap.Should().NotBeNull("the produced PNG must be decodable");

            // The flat value-76 fill is neither "white" (>245) nor "dark" (<32),
            // so the regression signal is the WHITE fraction: a blank page (the
            // pre-fix bug) is ~all white; a decoded page is ~all filled gray.
            var (whiteFraction, _) = SkiaRendererTests.MeasureWhiteAndDarkPixels(bitmap!);
            whiteFraction.Should().BeLessThan(0.5,
                "the chained JPX image must decode and fill the page, not render blank white (#1196)");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    private static string? FindCliAssembly()
    {
        var dir = Directory.GetCurrentDirectory();
        for (var up = 0; up < 8 && dir != null; up++)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                // The CLI project emits "excise.dll" (AssemblyName), not
                // Excise.Cli.dll — an earlier version of this finder looked for
                // the project name and silently skipped the whole test.
                foreach (var assembly in new[] { "excise.dll", "Excise.Cli.dll" })
                {
                    var candidate = Path.Combine(dir, "Excise.Cli", "bin", config, "net10.0", assembly);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
