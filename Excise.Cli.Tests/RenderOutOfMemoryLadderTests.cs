using System.Diagnostics;
using AwesomeAssertions;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1679, end to end: <c>excise render</c> must never exit 0 with a PNG that
/// differs from the unconstrained render. Measured before the fix on the
/// Altona technical2 page: every <c>DOTNET_GCHeapHardLimit</c> below the
/// render's need produced exit 0, "Saved to:", and a DIFFERENT picture — the
/// out-of-memory was swallowed into "no image drawn".
///
/// <para>A LADDER of limits rather than one, deliberately. A single fixed cap
/// pins a moving target: 192 MiB sat inside the silent window when this was
/// written (160–288 MiB), but every memory fix below moves the need, and a rung
/// that no longer triggers an OOM passes vacuously. The invariant holds at every
/// rung regardless: either the render succeeds byte-identically, or it fails
/// with a non-zero exit. The injected-OOM unit test in Excise.Rendering.Tests is
/// the durable primary gate; this is the ratchet that proves the CLI honours it.</para>
/// </summary>
public class RenderOutOfMemoryLadderTests
{
    // Hex WITHOUT 0x: the runtime reads DOTNET_GC* values as hex on every
    // shape, and Native AOT silently ignores a 0x prefix (measured 2026-09-19).
    private static readonly (string Label, string Hex)[] Rungs =
    {
        ("48 MiB", "3000000"),
        ("96 MiB", "6000000"),
        ("160 MiB", "A000000"),
        ("224 MiB", "E000000"),
    };

    [Fact]
    public void ACappedRender_NeverExitsZeroWithADifferentPicture()
    {
        var altona = TestRepoLayout.FindDirectory("test-pdfs", "altona");
        var pdf = altona == null ? null : Path.Combine(altona, "eci_altona-test-suite-v2_technical2_x4.pdf");
        Assert.SkipWhen(pdf == null || !File.Exists(pdf),
            TestRepoLayout.AbsenceReason("the Altona test suite", "test-pdfs/altona"));

        var work = Directory.CreateTempSubdirectory("excise-oom-ladder-");
        try
        {
            var baselinePng = Path.Combine(work.FullName, "baseline.png");
            var (baselineExit, baselineOut) = Render(pdf!, baselinePng, heapLimitHex: null);
            baselineExit.Should().Be(0, "the unconstrained render is the reference: " + baselineOut);
            var baseline = File.ReadAllBytes(baselinePng);

            foreach (var (label, hex) in Rungs)
            {
                var png = Path.Combine(work.FullName, $"cap-{hex}.png");
                var (exit, output) = Render(pdf!, png, hex);

                if (exit == 0)
                {
                    File.Exists(png).Should().BeTrue($"exit 0 at {label} must mean a file was written: {output}");
                    File.ReadAllBytes(png).Should().Equal(baseline,
                        $"at a {label} heap cap the CLI exited 0 — so the picture must be the SAME picture. " +
                        "A different one means an OutOfMemoryException was swallowed into a blank image (#1679). " +
                        "Output: " + output);
                }
                else
                {
                    (output.Contains("Error:") || exit == TimedOut).Should().BeTrue(
                        $"a failed render at {label} must say why, not just exit non-zero: {output}");
                }
            }
        }
        finally
        {
            try { work.Delete(recursive: true); } catch { }
        }
    }

    /// <summary>
    /// F2's ratchet (#1207/#1677): with the source array gone, these caps — which
    /// produced exit 1 (or, before #1679, a silently wrong PNG) — must now render
    /// byte-identically. one-patch p1 under 80 MiB, x4 p1 under 160 MiB. Pinned as
    /// exact-equality rather than "no worse", so a regression that re-materialises
    /// the source array reads as red, not as a shrug.
    /// </summary>
    [Theory]
    // Measured 2026-09-19 on the F2 NativeAOT binary by bisecting DOTNET_GCHeapHardLimit
    // with exit 0 + identical PNG as the criterion: one-patch p1 fits at 32 MiB (F1 needed
    // ~150), x4 p1 fits at 192 and fails cleanly at 160 (F1 needed ~300). Pinned with
    // headroom above the measured minimum, below the previous need, so a regression that
    // re-materialises the source array reads as red and GC variance does not.
    [InlineData("eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf", "3000000", "48 MiB")]
    [InlineData("eci_altona-test-suite-v2_technical2_x4.pdf", "E000000", "224 MiB")]
    public void AfterF2_TheAltonaPagesRenderIdentically_UnderTheseCaps(string file, string hex, string label)
    {
        var altona = TestRepoLayout.FindDirectory("test-pdfs", "altona");
        var pdf = altona == null ? null : Path.Combine(altona, file);
        Assert.SkipWhen(pdf == null || !File.Exists(pdf),
            TestRepoLayout.AbsenceReason("the Altona test suite", "test-pdfs/altona"));

        var work = Directory.CreateTempSubdirectory("excise-f2-ratchet-");
        try
        {
            var baseline = Path.Combine(work.FullName, "baseline.png");
            Render(pdf!, baseline, null).Exit.Should().Be(0);
            var capped = Path.Combine(work.FullName, "capped.png");
            var (exit, output) = Render(pdf!, capped, hex);
            exit.Should().Be(0, $"under a {label} cap this page must now fit — F2 removed the source-sized decode array. Output: {output}");
            File.ReadAllBytes(capped).Should().Equal(File.ReadAllBytes(baseline));
        }
        finally
        {
            try { work.Delete(recursive: true); } catch { }
        }
    }

    private const int TimedOut = -9;

    private static (int Exit, string Out) Render(string pdf, string png, string? heapLimitHex)
    {
        var root = TestRepoLayout.LocalCheckoutRoot ?? throw new InvalidOperationException(
            "no local checkout above " + AppContext.BaseDirectory);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = root,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add("Excise.Cli");
        psi.ArgumentList.Add("--no-build"); psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("render"); psi.ArgumentList.Add(pdf);
        psi.ArgumentList.Add("--page"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--dpi"); psi.ArgumentList.Add("150");
        psi.ArgumentList.Add("--output"); psi.ArgumentList.Add(png);
        if (heapLimitHex != null)
            psi.Environment["DOTNET_GCHeapHardLimit"] = heapLimitHex;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            // Measured 2026-09-19: near a hard cap the runtime can sit at 0% CPU for
            // tens of seconds to minutes before it gives up (F2 removed the single big
            // allocation that used to trip the cap instantly). That is the GC's
            // behaviour, not the renderer's, and the invariant this test guards is
            // "never exit 0 with a different picture" — so a stall is killed and
            // reported as a non-zero exit, and the PNG check below still runs on
            // whatever was written. It is NOT a pass: TimedOut is returned so a rung
            // that stalls is visible in the output, not silently green.
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(10_000);
            return (TimedOut, "[ladder] killed after 120 s without exiting\n" + o.GetAwaiter().GetResult() + e.GetAwaiter().GetResult());
        }
        return (p.ExitCode, o.GetAwaiter().GetResult() + e.GetAwaiter().GetResult());
    }
}
