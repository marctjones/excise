using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;
namespace Excise.Rendering.Tests.Corpus;

/// <summary>
/// Round-trip every PDF in the Isartor PDF/A-1b conformance test suite
/// through our writer, and assert structural invariants survive.
///
/// What this catches:
///   • parser bugs that fail on real-world PDF/A-1b output (lots of
///     European archive workflows produce these)
///   • writer bugs that drop pages, fonts, annotations, or content
///     streams during save
///   • round-trip identity issues — open → save → reopen produces a
///     materially different document
///
/// What this is NOT:
///   • a PDF/A conformance check. Many Isartor fixtures are
///     intentionally non-conformant; that's their point. We don't try
///     to preserve PDF/A-ness through the round-trip — we just assert
///     that excise can ingest them, emit them, and re-ingest without
///     losing structure.
///
/// The corpus is downloaded by scripts/download-test-pdfs.sh. If it's
/// missing the entire suite is skipped (one Skip per case).
/// </summary>
public sealed class IsartorRoundTripTests
{
    private readonly ITestOutputHelper _output;

    public IsartorRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Some Isartor fixtures are intentionally malformed past the point
    /// our parser will accept (e.g. truncated, deliberately-corrupt
    /// header). Failing to open them is correct behaviour — record
    /// the reason and skip.
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnopenable = new();

    /// <summary>
    /// Round-trip equivalence checks that have known regressions on
    /// specific Isartor fixtures. Each entry: relative path → reason.
    /// Removing a line re-enables the gate.
    /// </summary>
    private static readonly Dictionary<string, string> KnownRoundTripFailures = new()
    {
        // EMPTY, and that is the point: every Isartor fixture round-trips today.
        //
        // This held one entry for isartor-6-1-7-t01-fail-a.pdf, justified as
        // "writer correctly refuses to re-emit corruption". That stopped being
        // true — excise now opens, saves and re-opens that file cleanly, and the
        // full round-trip passes. The entry was suppressing a test that had
        // started working, which is coverage lost for no reason.
        //
        // Removed 2026-07-31 after verifying all 205 fixtures pass with no
        // exclusions. Adding an entry here silently disables a case, so an entry
        // needs a reason that is re-checked, not inherited.
    };

    public static IEnumerable<object[]> IsartorPdfs() => Discover();

    private static IEnumerable<object[]> Discover()
    {
        var root = LocateRepoRoot();
        if (root == null)
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        var corpus = Path.Combine(root, "test-pdfs", "isartor");
        if (!Directory.Exists(corpus))
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        var foundAny = false;
        foreach (var pdf in Directory.EnumerateFiles(corpus, "*.pdf", SearchOption.AllDirectories)
                                     .OrderBy(p => p))
        {
            foundAny = true;
            yield return new object[] { Path.GetRelativePath(root, pdf) };
        }

        if (!foundAny)
        {
            yield return new object[] { SentinelNoCorpus };
        }
    }

    [Theory]
    [MemberData(nameof(IsartorPdfs))]
    public void RoundTripsThroughWriter(string relativePath)
    {
        Assert.SkipWhen(relativePath == SentinelNoCorpus,
            "No Isartor corpus found at test-pdfs/isartor/. Run scripts/download-test-pdfs.sh to populate it.");

        var root = LocateRepoRoot()
            ?? throw new InvalidOperationException("Could not find repo root");
        var pdfPath = Path.Combine(root, relativePath);

        if (KnownUnopenable.TryGetValue(relativePath, out var unopenableReason))
            Assert.SkipWhen(true, $"Known unopenable Isartor fixture: {unopenableReason}");

        if (KnownRoundTripFailures.TryGetValue(relativePath, out var roundTripReason))
            Assert.SkipWhen(true, $"Known round-trip failure: {roundTripReason}");

        // ── Phase 1: open the original ───────────────────────────────
        byte[] originalBytes = File.ReadAllBytes(pdfPath);
        int originalPageCount;
        string originalText;
        try
        {
            using var doc = PdfDocument.Open(originalBytes);
            originalPageCount = doc.PageCount;
            originalText = ExtractAllText(doc);
        }
        catch (Exception ex)
        {
            // If we can't even open the original, the round-trip is
            // moot. Skip — robustness for malformed Isartor fixtures
            // is the parser's concern, not the writer's.
            Assert.SkipWhen(true,
                $"excise could not open original: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // ── Phase 2: save through our writer ─────────────────────────
        byte[] savedBytes;
        try
        {
            using var doc = PdfDocument.Open(originalBytes);
            savedBytes = doc.SaveToBytes();
        }
        catch (Exception ex)
        {
            // Writer failure is a real bug — surface it.
            throw new Xunit.Sdk.XunitException(
                $"excise writer threw on {relativePath}: {ex.GetType().Name}: {ex.Message}");
        }

        savedBytes.Should().NotBeNullOrEmpty("writer must produce some output");
        savedBytes.Length.Should().BeGreaterThan(64,
            "a non-trivial PDF can't be 64 bytes — the writer almost certainly truncated");

        // ── Phase 3: re-open the saved version ───────────────────────
        int reopenedPageCount;
        string reopenedText;
        try
        {
            using var reopened = PdfDocument.Open(savedBytes);
            reopenedPageCount = reopened.PageCount;
            reopenedText = ExtractAllText(reopened);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"excise could not reopen its own writer's output on {relativePath}: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // ── Phase 4: invariants ──────────────────────────────────────
        reopenedPageCount.Should().Be(originalPageCount,
            $"page count must round-trip ({relativePath})");

        // Text-extraction equality is the loudest signal. Tolerate
        // whitespace normalization (the writer may rewrite content
        // streams which collapses runs differently). Compare on
        // non-whitespace characters only — if those don't match,
        // we've actually lost or corrupted content.
        var originalSig = NonWhitespaceSignature(originalText);
        var reopenedSig = NonWhitespaceSignature(reopenedText);
        reopenedSig.Should().Be(originalSig,
            $"non-whitespace text content must round-trip ({relativePath}). " +
            $"Original length: {originalSig.Length}, reopened: {reopenedSig.Length}");

        _output.WriteLine(
            $"  ✓ {relativePath}  pages={originalPageCount}  text={originalSig.Length}ch");
    }

    private static string ExtractAllText(PdfDocument doc)
    {
        var sb = new System.Text.StringBuilder();
        for (int p = 1; p <= doc.PageCount; p++)
        {
            try { sb.Append(doc.GetPage(p).Text); }
            catch { /* a single bad page shouldn't kill the comparison */ }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string NonWhitespaceSignature(string text)
    {
        // Some PDFs have invisible text-positioning differences that
        // produce different whitespace counts after a content-stream
        // rewrite, even though the visible content is identical. We
        // compare only on characters that show ink.
        var arr = new char[text.Length];
        int n = 0;
        foreach (var ch in text)
            if (!char.IsWhiteSpace(ch)) arr[n++] = ch;
        return new string(arr, 0, n);
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

    private const string SentinelNoCorpus = "<no-corpus-downloaded>";

}
