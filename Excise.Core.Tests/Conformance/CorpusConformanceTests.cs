using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.TestSupport;
using Xunit;
namespace Excise.Core.Tests.Conformance;

/// <summary>
/// Parses every PDF in the local corpus (veraPDF, poppler, pdf.js, Isartor —
/// whatever <c>scripts/download-test-pdfs.sh</c> and friends have fetched
/// under <c>test-pdfs/</c>) and gates on the result. Skipped automatically
/// when the corpus is not present — this runs in CI today
/// (.github/workflows/ci.yml has no Corpus-category filter for
/// Excise.Core.Tests), but silently no-ops there since test-pdfs/ is
/// gitignored and absent on GitHub-hosted runners; it does real work
/// locally once the corpus is downloaded.
///
/// This is #648's corpus-based resilience gate: excise parses untrusted input
/// by definition, and poppler/pdf.js/Isartor's corpora already contain
/// files that are deliberately malformed, non-conformant, or were kept
/// because they broke another reader — "malformed-but-recoverable inputs,
/// and more" per pdf.js's own corpus description. A refusal (a typed
/// PdfParseException/PdfEncryptionNotSupportedException) is an acceptable
/// outcome; a raw unhandled exception, a hang, or unbounded memory growth
/// is not.
///
/// Run: dotnet test --filter "FullyQualifiedName~Corpus"
/// </summary>
public class CorpusConformanceTests
{
    // t0-gates review (2026-09-21): this used to be a hard-coded relative
    // walk ("../../../../test-pdfs"), one of the hand-rolled locator shapes
    // #1527/check-fixture-locators.sh exists to catch but this exact literal
    // slipped past (it names no anchor the gate looks for). Routed through
    // the one shared locator instead, same as every other corpus lookup.
    private static string? CorpusRoot => TestRepoLayout.FindDirectory("test-pdfs");

    // A single file's parse+touch must not hang the gate. Every real file in
    // this corpus parses in well under a second; 10s is generous headroom,
    // not a target — a file that needs 10s to fail is itself the finding.
    private static readonly TimeSpan PerFileBudget = TimeSpan.FromSeconds(10);

    // Coarse tripwire, not a leak detector: a single small PDF retaining
    // this much memory after GC is a red flag worth a human look (relates
    // to #615's unbounded-cache concern, though that issue is about the
    // GUI's tile cache specifically, not this parsing path).
    private const long PerFileMemoryBudgetBytes = 500L * 1024 * 1024;

    private readonly ITestOutputHelper _out;

    public CorpusConformanceTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// Exception types that mean "excise correctly refused this file" — the
    /// acceptable outcome per #648. Same set <c>ParserFuzzTests.IsGraceful</c>
    /// uses for the synthetic fuzz corpus; kept identical on purpose so a
    /// file that fails the corpus gate here would also have failed there.
    /// </summary>
    /// <summary>
    /// Re-measure ONE file's retained memory in isolation (#953): settle the
    /// heap, parse exactly what the sweep parses, settle again. Returns the
    /// delta in bytes, or 0 if the parse fails — a file that cannot be parsed
    /// on the second pass cannot be the thing retaining memory, and the sweep
    /// has already classified its parse outcome.
    ///
    /// This does NOT make the measurement airtight: another collection can
    /// still allocate concurrently during this window. It makes a false
    /// positive require the SAME neighbour to allocate the SAME half-gigabyte
    /// twice, which is the difference between a gate that reds once a day and
    /// one that means something.
    /// </summary>
    private static long MeasureRetention(string path)
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long before = GC.GetTotalMemory(forceFullCollection: true);
            using (var doc = PdfDocument.Open(path))
            {
                for (int p = 1; p <= Math.Min(doc.PageCount, 5); p++)
                {
                    var page = doc.GetPage(p);
                    _ = page.Width;
                    _ = page.Height;
                    _ = page.Rotation;
                    _ = page.Resources;
                }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return GC.GetTotalMemory(forceFullCollection: true) - before;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsGraceful(Exception ex) =>
        ex is PdfParseException
        or PdfEncryptionNotSupportedException
        or NotSupportedException
        or EndOfStreamException;

    [Fact]
    public async Task Corpus_ParsesWithoutCrash_AllPdfs()
    {
        // t0-gates review (2026-09-21): a silent `return` here reported
        // PASSED on an absent corpus -- #1172's skip-reason gate had nothing
        // to read and #894's test-count gate counted the no-op as "run". A
        // declared skip with a checkable absence claim replaces it.
        var corpusRoot = CorpusRoot;
        Assert.SkipWhen(corpusRoot == null,
            TestRepoLayout.AbsenceReason("the PDF corpora", "test-pdfs"));

        var files = Directory.GetFiles(corpusRoot!, "*.pdf", SearchOption.AllDirectories);
        Assert.SkipWhen(files.Length == 0,
            $"no *.pdf files found under {corpusRoot} [{TestRepoLayout.SearchedMarker} {corpusRoot}]");

        int total = 0, ok = 0, gracefulFailure = 0, crash = 0, hang = 0, memoryExceeded = 0, memoryUnattributed = 0;
        var crashes = new List<string>();
        var hangs = new List<string>();
        var memoryFindings = new List<string>();

        foreach (var f in files)
        {
            total++;

            // t0-gates review (2026-09-21): this used to force a full GC here
            // AND after every file (~7,800 forced collections over the full
            // corpus, measured as 250s of the 256s wall this test costs in
            // t0). A forced collection is for CONFIRMING a suspected leak,
            // not for routinely screening ~3,900 clean files. The cheap,
            // non-forcing reading below is noisier (it can see garbage the
            // collector has not reclaimed yet) but only ever OVER-reports a
            // delta, never under — nothing that would actually leak
            // PerFileMemoryBudgetBytes can fail to cross this cheap
            // threshold too, and MeasureRetention below still re-confirms
            // with a real forced collection before anything is flagged.
            long memBefore = GC.GetTotalMemory(forceFullCollection: false);
            var task = Task.Run(() =>
            {
                using var doc = PdfDocument.Open(f);
                for (int p = 1; p <= Math.Min(doc.PageCount, 5); p++)
                {
                    var page = doc.GetPage(p);
                    _ = page.Width;
                    _ = page.Height;
                    // Width/Height come from /MediaBox, which is very often on
                    // the page dictionary itself and so never walks /Parent.
                    // The INHERITED lookups are the ones that can loop on a
                    // cyclic page tree: bug_517126568.pdf sailed through this
                    // gate for exactly that reason while costing 120s of CPU on
                    // /Rotate (#881). Touch the inherited paths too, or this
                    // certifies "parses without hanging" while an infinite loop
                    // sits one property away.
                    _ = page.Rotation;
                    _ = page.Resources;
                }
            });

            var winner = await Task.WhenAny(task, Task.Delay(PerFileBudget));
            if (winner != task)
            {
                hang++;
                hangs.Add($"HANG   {Path.GetFileName(f)}: exceeded {PerFileBudget.TotalSeconds}s");
                continue; // the task is still running on a pool thread; leave it, don't wait further.
            }

            if (task.IsFaulted)
            {
                var inner = task.Exception!.InnerException ?? task.Exception!;
                if (IsGraceful(inner))
                    gracefulFailure++;
                else
                {
                    crash++;
                    crashes.Add($"CRASH  {Path.GetFileName(f)}: {inner.GetType().Name}: {inner.Message}");
                }
                continue; // no memory measurement for a faulted parse — nothing meaningful to attribute it to.
            }

            long memAfter = GC.GetTotalMemory(forceFullCollection: false);
            long delta = memAfter - memBefore;
            if (delta > PerFileMemoryBudgetBytes)
            {
                // DO NOT fail on the first reading (#953). GC.GetTotalMemory is
                // PROCESS-WIDE and xunit.runner.json sets
                // parallelizeTestCollections: true here, so a concurrent test's
                // allocation lands in whatever file this loop happens to be
                // measuring. That produced a t0 false red blaming a 380-byte
                // fixture (test-pdfs/pdfium/version_in_catalog.pdf) for 694MB,
                // with Hang: 0 and a clean rerun seconds later.
                //
                // So re-measure THIS file alone and require the retention to
                // reproduce — the same transient-vs-genuine discipline
                // check-test-count.sh uses for a lost test result. A real leak
                // reproduces; a neighbour's allocation does not.
                long confirmDelta = MeasureRetention(f);
                if (confirmDelta > PerFileMemoryBudgetBytes)
                {
                    memoryExceeded++;
                    memoryFindings.Add(
                        $"MEMORY {Path.GetFileName(f)}: retained {delta / (1024 * 1024)}MB after parsing 5 page(s), " +
                        $"reproduced at {confirmDelta / (1024 * 1024)}MB on an isolated re-measure");
                }
                else
                {
                    memoryUnattributed++;
                    memoryFindings.Add(
                        $"note   {Path.GetFileName(f)}: first reading {delta / (1024 * 1024)}MB did NOT reproduce " +
                        $"({confirmDelta / (1024 * 1024)}MB isolated) — concurrent/prior process state, not this file (#953)");
                }
            }

            if (task.IsCompletedSuccessfully) ok++;
        }

        _out.WriteLine($"Total: {total}  OK: {ok}  GracefulFailure: {gracefulFailure}  Crash: {crash}  Hang: {hang}  MemoryExceeded: {memoryExceeded}  MemoryUnattributed: {memoryUnattributed}");
        foreach (var c in crashes.Take(50)) _out.WriteLine(c);
        foreach (var h in hangs.Take(50)) _out.WriteLine(h);
        foreach (var m in memoryFindings.Take(50)) _out.WriteLine(m);

        // The actual gate (#648): a malformed file may refuse (graceful
        // typed exception) but must never crash with an unhandled
        // exception, hang past the per-file budget, or blow through the
        // memory tripwire.
        crash.Should().Be(0, "an unhandled exception on untrusted input is exactly the DoS #648 exists to close — see the CRASH lines above");
        hang.Should().Be(0, "a file that never finishes parsing hangs the tool a redaction depends on — see the HANG lines above");
        memoryExceeded.Should().Be(0, "unbounded memory growth on a single file is a resource-exhaustion primitive — see the MEMORY lines above");
    }

    [Fact]
    public void Corpus_SmokeFiles_ParsesCleanly()
    {
        // t0-gates review (2026-09-21): same silent-`return`-reports-PASSED
        // shape as Corpus_ParsesWithoutCrash_AllPdfs, replaced the same way.
        var smokeDir = TestRepoLayout.FindDirectory("test-pdfs", "smoke");
        Assert.SkipWhen(smokeDir == null,
            TestRepoLayout.AbsenceReason("the smoke corpus", "test-pdfs/smoke"));

        var files = Directory.GetFiles(smokeDir!, "*.pdf", SearchOption.AllDirectories);
        var failures = new List<string>();

        foreach (var f in files)
        {
            try
            {
                using var doc = PdfDocument.Open(f);
                for (int p = 1; p <= doc.PageCount; p++)
                {
                    var page = doc.GetPage(p);
                    _ = page.GetContentStreamBytes();
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        failures.Should().BeEmpty("smoke PDFs must always parse without errors");
    }
}
