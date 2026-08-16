using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// Bounded long-session soak (#960, part 3 of 3): repeat the full
/// open -> render -> extract -> redact -> save -> close cycle N times in one
/// process and assert nothing grows per cycle.
///
/// <para><b>Why this is not covered by
/// <c>MemoryBenchmarkTests.WorkingSet_ReturnsToBaseline_AfterDocumentClose</c>.</b>
/// That test runs ONE cycle and allows a 50 MB band. A leak of a few hundred
/// KB per document sits inside that band forever and is invisible to it —
/// while over a real editing session it is exactly what turns into #861's
/// 8.5 GB. This measures the SLOPE across cycles instead of a single
/// before/after, which is the shape a leak actually has.</para>
///
/// <para><b>Gate vs. report — deliberately split.</b> The managed heap after
/// a forced full collection is stable enough to gate on. Resident set size
/// is NOT: it is affected by other processes, by the OS's willingness to
/// reclaim, and by Skia's native allocator holding freed pages. So RSS is
/// reported through <see cref="ITestOutputHelper"/> and never asserted. A
/// flaky gate is worse than no gate — this repo has been burned by exactly
/// that (#854, #855) — and half a gate that always means something beats a
/// whole one that gets muted.</para>
///
/// <para><b>Blind spot, stated plainly:</b> gating the managed heap cannot
/// see a native leak. SKBitmap pixels are unmanaged, so a Skia-side leak
/// would show only in the reported RSS trajectory, which nothing fails on.
/// Closing that needs a native-allocation oracle this repo does not have;
/// #861 is the tracking issue.</para>
///
/// <para><b>Tier: t1.</b> ~24 render cycles at 72 DPI is a few seconds —
/// too slow for t0's ~30s budget alongside everything else, and it lives in
/// Excise.Rendering.Tests, which is a t1 project. #960 asks for a nightly
/// tier; nightly-corpus is still status: planned with primaryCommand: null,
/// so t1 is where this actually runs.</para>
/// </summary>
public class LongSessionSoakTests
{
    private readonly ITestOutputHelper _out;

    public LongSessionSoakTests(ITestOutputHelper output) => _out = output;

    /// <summary>Total cycles. Enough for a per-cycle slope to separate from noise.</summary>
    private const int Cycles = 24;

    /// <summary>
    /// Cycles discarded before measuring. The first passes pay for JIT, for
    /// Skia's font-manager initialisation, and for Excise.Core's static caches
    /// (predefined CMaps, glyph lists, ICC data) — all one-time and all
    /// size-bounded, so counting them as "growth" would make the gate
    /// meaningless in exactly the direction that hides real leaks.
    /// </summary>
    private const int WarmupCycles = 8;

    /// <summary>
    /// Managed-heap growth allowed across the measured cycles, as an absolute
    /// figure rather than a percentage band.
    ///
    /// <para>Justification: after warmup a cycle should retain NOTHING —
    /// every document is disposed, every bitmap is disposed, and each cycle's
    /// inputs are identical, so the steady-state heap is flat by construction
    /// and the honest threshold is "about zero, plus measurement noise".
    /// Measured on this workload over three consecutive runs (macOS, .NET 10,
    /// Debug): 80.5 KB, 80.5 KB, 155.1 KB of growth across the 16 measured
    /// cycles — i.e. 5-10 KB per cycle, with the spread being GC timing, not
    /// retention. 1 MB is ~6.5x the worst observed run, so noise cannot reach
    /// it, while a real leak cannot hide under it: retaining a single page's
    /// letter model (tens of KB) per cycle crosses it inside the window.</para>
    ///
    /// <para>Sensitivity was measured, not assumed: injecting a 64 KB-per-cycle
    /// leak (a static list appended once per cycle) fails this gate at
    /// 2678.5 KB of growth against the 1024 KB threshold.</para>
    ///
    /// <para>If this ever fails intermittently, do NOT raise it — a soak gate
    /// that gets relaxed each time it fires is the gate #854 warns about.
    /// Print the trajectory and find out which cycle grew.</para>
    /// </summary>
    private const long MaxManagedGrowthBytes = 1L * 1024 * 1024;

    [Fact]
    public void RepeatedOpenRenderRedactCloseCycles_DoNotGrowTheManagedHeap()
    {
        var pdf = LoadFixture();

        var managed = new long[Cycles];
        var rss = new long[Cycles];
        var process = Process.GetCurrentProcess();

        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            int redacted = RunOneCycle(pdf);

            // A soak whose workload has quietly become a no-op still passes
            // its memory gate — and passes it more easily. Assert the cycle
            // did real work, so a fixture or API change cannot hollow this
            // test out without failing it.
            if (cycle == 0)
                redacted.Should().BeGreaterThan(0,
                    "each cycle must actually redact something; a soak over a no-op measures nothing");

            managed[cycle] = GC.GetTotalMemory(forceFullCollection: true);
            process.Refresh();
            rss[cycle] = process.WorkingSet64;
        }

        for (int cycle = 0; cycle < Cycles; cycle++)
            _out.WriteLine(
                $"cycle {cycle,2}{(cycle < WarmupCycles ? " (warmup)" : "         ")}  " +
                $"managed {managed[cycle] / 1024.0 / 1024.0,8:F2} MB   " +
                $"rss {rss[cycle] / 1024.0 / 1024.0,8:F2} MB");

        long baseline = managed[WarmupCycles - 1];
        long final = managed[Cycles - 1];
        long growth = final - baseline;
        long peakAfterWarmup = managed.Skip(WarmupCycles).Max();

        _out.WriteLine("");
        _out.WriteLine($"managed baseline (end of warmup): {baseline / 1024.0 / 1024.0:F2} MB");
        _out.WriteLine($"managed final:                    {final / 1024.0 / 1024.0:F2} MB");
        _out.WriteLine($"managed growth over {Cycles - WarmupCycles} measured cycles: " +
                       $"{growth / 1024.0:F1} KB ({growth / 1024.0 / (Cycles - WarmupCycles):F1} KB/cycle)");
        _out.WriteLine($"RSS trajectory is REPORTED ONLY, never gated: " +
                       $"{rss[WarmupCycles - 1] / 1024.0 / 1024.0:F1} MB -> {rss[Cycles - 1] / 1024.0 / 1024.0:F1} MB");

        // Measured against the peak, not just the last reading: a leak that
        // grows then happens to dip on the final cycle would otherwise pass.
        (peakAfterWarmup - baseline).Should().BeLessThan(MaxManagedGrowthBytes,
            $"a steady-state cycle must retain nothing — peak was " +
            $"{(peakAfterWarmup - baseline) / 1024.0:F1} KB above the post-warmup baseline over " +
            $"{Cycles - WarmupCycles} identical cycles. See the per-cycle trajectory above for which " +
            "cycle grew; do not raise the threshold to make this pass.");
    }

    /// <summary>
    /// One "session": everything a user does to a document between opening
    /// and closing it. Every disposable is disposed, so anything still held
    /// afterwards is held by something that should not be holding it.
    /// </summary>
    /// <returns>The number of redaction matches removed, so the caller can
    /// verify the cycle did real work.</returns>
    private static int RunOneCycle(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var renderer = new SkiaRenderer();

        int pages = Math.Min(doc.PageCount, 2);
        for (int p = 1; p <= pages; p++)
        {
            var page = doc.GetPage(p);
            // 72 DPI keeps the bitmap small: this test is about what SURVIVES
            // a cycle, not about how much a render costs at full size, which
            // MemoryBenchmarkTests already budgets.
            using var bitmap = renderer.RenderPage(page, new RenderOptions { Dpi = 72 });
            _ = bitmap.Width;
            _ = page.Text?.Length;
            _ = page.Letters?.Count();
        }

        // Case-insensitive by default, so this matches the fixture's
        // "BIRTH CERTIFICATE" heading.
        int redacted = doc.RedactText("Certificate");

        using var ms = new MemoryStream();
        doc.Save(ms);
        return redacted;
    }

    /// <summary>
    /// A git-tracked real-world fixture with text, fonts and a redactable
    /// term — no gitignored corpus, so this needs no [requires:] allowlist
    /// entry and behaves identically on a bare CI runner.
    /// </summary>
    private static byte[] LoadFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "excise.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test binary must sit under the repository");

        var path = Path.Combine(dir!.FullName, "test-pdfs", "sample-pdfs",
            "birth-certificate-request-scrambled.pdf");
        File.Exists(path).Should().BeTrue(
            "this fixture is checked into git; a missing one means a broken checkout, not an " +
            "environment this suite may quietly skip on");
        return File.ReadAllBytes(path);
    }
}
