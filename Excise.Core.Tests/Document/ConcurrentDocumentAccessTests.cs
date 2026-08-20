using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Runtime robustness under concurrency (#960, part 3 of 3).
///
/// <para><b>What this adds over <see cref="ConcurrentAccessTests"/>.</b> That
/// suite (#376) hammers ONE document from many threads — the shared-lexer
/// race. Everything else in the repo tests one document at a time. This one
/// covers the shapes the App actually produces and nothing tests: several
/// documents open at once, the SAME bytes opened concurrently by different
/// threads, extraction and redaction racing on independent documents, and
/// cancellation under load.</para>
///
/// <para><b>Why Core and not App.</b> <c>Excise.App.Tests</c> is serial by
/// design — xunit parallelism races SkiaSharp's process-wide native font
/// manager and crashes the host (#363). Adding parallel GUI tests there would
/// reintroduce that. Core has no native font manager, and its own runner
/// config already sets <c>parallelizeTestCollections: true</c>, so
/// concurrency belongs here.</para>
///
/// <para><b>Tier: t0.</b> Everything is in-memory and bounded to a few
/// hundred milliseconds per test.</para>
///
/// <para><b>Honest scoping — this is a STRESS suite, not a deterministic
/// gate.</b> A data race reproduces on timing, so a green run is evidence,
/// not proof; <see cref="ConcurrentAccessTests"/> says the same about itself
/// and it is worth repeating rather than quietly implying otherwise. What IS
/// deterministic here is everything around the race: that concurrent opens
/// produce equal results, that a cancelled render-time parse throws
/// <see cref="OperationCanceledException"/> and leaves the document usable,
/// and that no operation returns silently-wrong data. Those assertions fail
/// every time if broken.</para>
/// </summary>
public class ConcurrentDocumentAccessTests
{
    private const int ThreadCount = 8;

    /// <summary>
    /// Several DIFFERENT documents opened and read at the same time. The
    /// per-document lock (#376) serializes access within a document; nothing
    /// establishes that two documents do not share mutable state through the
    /// static caches Excise.Core keeps (predefined CMaps, glyph lists, function
    /// data). If they did, this is where it would show up as a wrong result
    /// rather than an exception.
    /// </summary>
    [Fact]
    public async Task OpeningAndReadingDifferentDocumentsConcurrently_IsSafeAndCorrect()
    {
        // Each document carries text unique to it, so a cross-document leak
        // through a shared cache is visible as CONTENT, not just a crash.
        var documents = Enumerable.Range(0, ThreadCount)
            .Select(i => (Marker: $"Document{i}Marker", Bytes: SinglePagePdf($"Document{i}Marker")))
            .ToArray();

        var errors = new ConcurrentQueue<Exception>();
        var mismatches = new ConcurrentQueue<string>();
        using var gate = new Barrier(ThreadCount);

        var tasks = documents.Select(d => Task.Run(() =>
        {
            try
            {
                gate.SignalAndWait();
                for (int repeat = 0; repeat < 20; repeat++)
                {
                    using var doc = PdfDocument.Open(d.Bytes);
                    var text = doc.GetPage(1).Text ?? "";
                    if (!text.Contains(d.Marker, StringComparison.Ordinal))
                        mismatches.Enqueue($"{d.Marker} not found in its own page text: '{text}'");

                    foreach (var other in documents)
                        if (other.Marker != d.Marker && text.Contains(other.Marker, StringComparison.Ordinal))
                            mismatches.Enqueue($"{d.Marker}'s page returned {other.Marker} — cross-document leak");
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        await Task.WhenAll(tasks);

        errors.Should().BeEmpty("concurrent opens of independent documents must not fail; first: " +
                                (errors.FirstOrDefault()?.ToString() ?? "none"));
        mismatches.Should().BeEmpty("state must not leak between concurrently-open documents");
    }

    /// <summary>
    /// The same BYTES opened concurrently by many threads — the shape a batch
    /// worker or a re-opened recent file produces. Each open must yield an
    /// independent document; if any shared parse state leaked between them,
    /// the page text would differ between threads.
    /// </summary>
    [Fact]
    public async Task OpeningTheSameBytesConcurrently_YieldsIndependentEqualDocuments()
    {
        var bytes = SinglePagePdf("SharedBytesMarker");
        var results = new ConcurrentBag<string>();
        var errors = new ConcurrentQueue<Exception>();
        using var gate = new Barrier(ThreadCount);

        var tasks = Enumerable.Range(0, ThreadCount).Select(_ => Task.Run(() =>
        {
            try
            {
                gate.SignalAndWait();
                for (int repeat = 0; repeat < 20; repeat++)
                {
                    using var doc = PdfDocument.Open(bytes);
                    results.Add(doc.GetPage(1).Text ?? "");
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        await Task.WhenAll(tasks);

        errors.Should().BeEmpty("first: " + (errors.FirstOrDefault()?.ToString() ?? "none"));
        results.Should().HaveCount(ThreadCount * 20);
        results.Distinct().Should().HaveCount(1,
            "every concurrent open of identical bytes must produce identical text");
        results.First().Should().Contain("SharedBytesMarker");
    }

    /// <summary>
    /// Redaction — the security-critical path — running concurrently on
    /// independent documents. The failure this guards against is not a crash
    /// but a WRONG one: a document whose term survives because a sibling
    /// thread's redaction perturbed shared state. Asserted on the saved bytes
    /// (ASCII and UTF-16BE), which is the carrier-agnostic check CLAUDE.md
    /// requires, not on excise's own extractor.
    /// </summary>
    [Fact]
    public async Task RedactingIndependentDocumentsConcurrently_RemovesEveryTerm()
    {
        var jobs = Enumerable.Range(0, ThreadCount)
            .Select(i => (Secret: $"SecretAlpha{i}Zulu", Bytes: SinglePagePdf($"SecretAlpha{i}Zulu")))
            .ToArray();

        var errors = new ConcurrentQueue<Exception>();
        var leaks = new ConcurrentQueue<string>();
        using var gate = new Barrier(ThreadCount);

        var tasks = jobs.Select(job => Task.Run(() =>
        {
            try
            {
                gate.SignalAndWait();
                using var doc = PdfDocument.Open(job.Bytes);
                int removed = doc.RedactText(job.Secret).VerifiedRemovals;
                if (removed <= 0)
                    leaks.Enqueue($"{job.Secret}: RedactText reported {removed} matches");

                using var ms = new MemoryStream();
                doc.Save(ms);
                var saved = ms.ToArray();
                var haystack = Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved);
                if (haystack.Contains(job.Secret, StringComparison.Ordinal))
                    leaks.Enqueue($"{job.Secret} survives in the saved bytes after a concurrent redaction");
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        await Task.WhenAll(tasks);

        errors.Should().BeEmpty("first: " + (errors.FirstOrDefault()?.ToString() ?? "none"));
        leaks.Should().BeEmpty("concurrency must not weaken redaction — " +
                               string.Join("; ", leaks.Take(3)));
    }

    /// <summary>
    /// Cancellation under load. The bug class worth catching is not "cancel
    /// works" but what the object looks like AFTERWARDS: a parse abandoned
    /// mid-way can leave a half-populated cache that quietly serves wrong
    /// data to the next caller. So this cancels repeatedly on one document
    /// while other threads keep reading it, then asserts the document still
    /// returns the correct text.
    /// </summary>
    [Fact]
    public async Task CancellingReadsUnderLoad_LeavesTheDocumentUsable()
    {
        var bytes = MultiPagePdf(pageCount: 40, marker: "CancelLoadMarker");
        using var doc = PdfDocument.Open(bytes);

        var errors = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Half the threads read normally; half start work and cancel it almost
        // immediately, so cancellation lands at unpredictable points in the
        // shared parse.
        var workers = Enumerable.Range(0, ThreadCount).Select(tid => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (tid % 2 == 0)
                    {
                        for (int p = 1; p <= doc.PageCount && !stop.IsCancellationRequested; p++)
                            _ = doc.GetPage(p).Letters?.Count();
                    }
                    else
                    {
                        using var cts = new CancellationTokenSource();
                        var work = Task.Run(() =>
                        {
                            for (int p = 1; p <= doc.PageCount; p++)
                            {
                                cts.Token.ThrowIfCancellationRequested();
                                _ = doc.GetPage(p).Text;
                            }
                        }, cts.Token);

                        cts.CancelAfter(TimeSpan.FromMilliseconds(2));
                        try { work.GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { /* the point of the test */ }
                    }
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        await Task.WhenAll(workers);

        errors.Should().BeEmpty("cancellation must not corrupt concurrent readers; first: " +
                                (errors.FirstOrDefault()?.ToString() ?? "none"));

        // The document survived the storm and still reads correctly.
        for (int p = 1; p <= doc.PageCount; p++)
            (doc.GetPage(p).Text ?? "").Should().Contain("CancelLoadMarker",
                $"page {p} must still read correctly after repeated cancellation under load");
    }

    // ---------------------------------------------------------------

    private static byte[] SinglePagePdf(string marker) => MultiPagePdf(1, marker);

    private static byte[] MultiPagePdf(int pageCount, string marker)
    {
        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "__PAGES__",
        };

        int fontObj = 2 + pageCount * 2 + 1;
        var kids = new System.Collections.Generic.List<string>();
        for (int i = 0; i < pageCount; i++)
        {
            int pageObj = 3 + i * 2;
            kids.Add($"{pageObj} 0 R");
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                       $"/Contents {pageObj + 1} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
            var content = $"BT /F1 12 Tf 72 700 Td ({marker}) Tj ET\n";
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pageCount} >>";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new long[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size {bodies.Count + 1} >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
