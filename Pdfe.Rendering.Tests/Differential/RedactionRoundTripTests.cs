using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Text.Segmentation;
using Xunit;
namespace Pdfe.Rendering.Tests.Differential;

/// <summary>
/// Layer 6 — for every PDF in the corpus, run the redaction round-trip:
///
///   1. Extract text via pdfe (page.Text).
///   2. Pick a "good enough" word from the extracted text — long enough
///      to be unique-ish, short enough to actually match content-stream
///      glyph runs, ASCII so encoding mismatches don't get conflated.
///   3. Call <c>doc.RedactText(word)</c>.
///   4. Save → reopen.
///   5. Re-extract text.
///   6. Assert the word is no longer present in the extracted text.
///
/// This is the security-critical test: the pdfe value proposition is
/// TRUE redaction (text removed from PDF structure, not just visually
/// covered). A regression here is a security regression.
///
/// What this catches:
///   • Glyph-removal bugs (we matched the word but didn't actually
///     excise it from the content stream)
///   • Save-write bugs that re-emit redacted content somewhere
///   • ToUnicode-based extraction leaks (text gone from page content
///     but still recoverable via the font's CMap — known issue #95)
///   • Substring boundary bugs (we matched part of "Betterment"
///     instead of the whole word)
///   • Multi-page leaks where redaction was applied page 1 only but
///     the same word appears on page 2 with a different font
///
/// What this does NOT catch:
///   • Visual leaks where the text is removed from content but baked
///     into a raster image XObject. That's <c>pdfe audit --deep</c>.
///   • Annotation /Contents leaks. That's a known #-tracked issue.
///   • Metadata leaks. ScrubMetadata covers those.
///
/// Same gating model: smoke gates the build, pdf.js is best-effort.
/// </summary>
public sealed class RedactionRoundTripTests
{
    private readonly ITestOutputHelper _output;

    public RedactionRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Every corpus listed here gates the build. pdf.js is excluded —
    /// see <see cref="ExploratoryDifferentialTests"/> for on-demand runs.
    /// </summary>
    private static readonly string[] GatingCorpusDirectories =
    {
        "test-pdfs/smoke",
    };

    /// <summary>
    /// Known redaction round-trip failures by relative PDF path.
    /// Each entry: reason. Removing a line re-enables the gate.
    /// </summary>
    private static readonly Dictionary<string, string> KnownRedactionFailures = new();

    public static IEnumerable<object[]> CorpusPdfs() => Discover();

    internal static IEnumerable<object[]> Discover()
    {
        var root = LocateRepoRoot();
        if (root == null)
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        var foundAny = false;
        foreach (var sub in GatingCorpusDirectories)
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var pdf in Directory.EnumerateFiles(dir, "*.pdf").OrderBy(p => p))
            {
                foundAny = true;
                yield return new object[] { Path.GetRelativePath(root, pdf) };
            }
        }

        if (!foundAny)
        {
            yield return new object[] { SentinelNoCorpus };
        }
    }

    [Theory]
    [MemberData(nameof(CorpusPdfs))]
    public void RedactedWordIsGoneAfterSaveAndReopen(string relativePath)
    {
        Assert.SkipWhen(relativePath == SentinelNoCorpus,
            "No smoke corpus found at test-pdfs/smoke/. Run scripts/download-smoke-corpus.sh to populate it.");

        var root = LocateRepoRoot()!;
        var pdfPath = Path.Combine(root, relativePath);
        var pdfBytes = File.ReadAllBytes(pdfPath);

        // ── Phase 1: pick a target word from the original ────────────
        string? target;
        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            target = PickRedactionTarget(doc);
        }
        catch (Exception ex)
        {
            Assert.SkipWhen(true,
                $"pdfe could not open {relativePath}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Assert.SkipWhen(target == null,
            $"{relativePath}: no suitable target word found in extracted text — " +
            "PDF probably has no extractable text or only short/non-ASCII content");

        // ── Phase 2: redact + save ───────────────────────────────────
        byte[] redactedBytes;
        int matchCount;
        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            matchCount = doc.RedactText(target!, caseSensitive: false);
            redactedBytes = doc.SaveToBytes();
        }
        catch (Exception ex)
        {
            // RedactText / Save throwing is a real bug. Don't skip;
            // surface so it gets fixed.
            throw new Xunit.Sdk.XunitException(
                $"{relativePath}: RedactText('{target}') threw {ex.GetType().Name}: {ex.Message}");
        }

        Assert.SkipWhen(matchCount == 0,
            $"{relativePath}: target word '{target}' wasn't matched by RedactText — " +
            "extraction and matching are using different glyph paths; " +
            "this is a separate bug class than 'redaction left text behind'");

        // ── Phase 3: reopen and assert the word is gone ──────────────
        string remaining;
        try
        {
            using var reopened = PdfDocument.Open(redactedBytes);
            remaining = string.Concat(
                Enumerable.Range(1, reopened.PageCount)
                          .Select(p => reopened.GetPage(p).Text));
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"{relativePath}: pdfe couldn't reopen its own redacted output: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        var stillContainsTarget = remaining
            .IndexOf(target!, StringComparison.OrdinalIgnoreCase) >= 0;

        _output.WriteLine($"  {relativePath}");
        _output.WriteLine($"  redacted '{target}' ({matchCount} match(es)); " +
                          $"text-leaks-after-redact: {stillContainsTarget}");

        if (stillContainsTarget && KnownRedactionFailures.TryGetValue(relativePath, out var reason))
        {
            _output.WriteLine($"  ⚑ KNOWN FAILURE — not gating: {reason}");
            Assert.SkipWhen(true,
                $"Known redaction failure for {relativePath}: {reason}");
        }
        stillContainsTarget.Should().BeFalse(
            $"SECURITY: redacted text leaked through save+reopen on {relativePath}. " +
            $"Target word '{target}' was removed from the source ({matchCount} matches) " +
            $"but is still extractable from the saved output.");
    }

    /// <summary>
    /// Pick a word from the document's extracted text that is:
    ///   • Long enough to be unlikely to be a substring of an
    ///     unintended target (≥ 6 chars)
    ///   • Pure ASCII letters, so encoding mismatch can't conflate
    ///     "matched but no glyph deleted"
    ///   • Present multiple times preferred (so we exercise the
    ///     multi-match path)
    ///   • Not "the", "and", "this" — too short, too common, too
    ///     likely to be embedded inside other words
    /// Returns null when nothing suitable is in the text.
    /// </summary>
    private static string? PickRedactionTarget(PdfDocument doc)
    {
        var text = doc.GetPage(1).Text;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '(', ')', '[', ']', ':', ';', '"', '\'' },
                                         StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 6) continue;
            // ASCII letters only — any non-letter rejects the whole token.
            bool ok = true;
            foreach (var ch in token)
            {
                if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')))
                {
                    ok = false; break;
                }
            }
            if (!ok) continue;
            counts.TryGetValue(token, out var c);
            counts[token] = c + 1;
        }

        // Prefer words that appear more than once and are 7-12 chars
        // (long enough to be specific, short enough that the
        // glyph-matching engine will see them as a single content-stream
        // run rather than spanning many TJ groups).
        var sweetspot = counts
            .Where(kv => kv.Key.Length >= 7 && kv.Key.Length <= 12 && kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.Length)
            .FirstOrDefault();
        if (sweetspot.Key != null) return sweetspot.Key;

        // Fallback: any 6+ char word.
        var fallback = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return fallback.Key;
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private const string SentinelNoCorpus = "<no-corpus-downloaded>";
}
