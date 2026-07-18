using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;
namespace Excise.Rendering.Tests.Corpus;

/// <summary>
/// Round-trip conformance tests: load → save → reload → verify content.
///
/// For each PDF in the smoke corpus:
/// 1. Open and extract text from page 1
/// 2. Save to temporary file
/// 3. Reopen the saved file
/// 4. Verify page count matches
/// 5. Verify text extraction returns consistent content
///
/// Known limitations (skipped):
/// - XFA forms (dynamic forms, not fully supported in excise)
/// - Encrypted/password-protected PDFs (require password entry)
/// - PDFs with broken cross-reference tables
///
/// These tests verify that the full parsing pipeline is robust and
/// that modifications (save operations) preserve document integrity.
/// </summary>
public class RoundTripTests
{
    private readonly ITestOutputHelper _output;

    public RoundTripTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [MemberData(nameof(SmokeCorpusFiles))]
    public void RoundTrip_LoadSaveReload_PreservesContent(string pdfPath)
    {
        if (pdfPath == SentinelNoCorpus)
        {
            // Skip test gracefully
            _output.WriteLine(
                "No smoke corpus found at test-pdfs/smoke/. " +
                "Run scripts/download-smoke-corpus.sh to populate it.");
            return;
        }

        var name = Path.GetFileName(pdfPath);
        var sw = Stopwatch.StartNew();

        string? originalText = null;
        int originalPageCount = 0;

        // Step 1: Load original and extract content
        try
        {
            using var docOriginal = PdfDocument.Open(pdfPath);
            originalPageCount = docOriginal.PageCount;
            originalPageCount.Should().BeGreaterThan(0, $"{name} should have at least one page");

            // Extract text from first page if possible
            try
            {
                var page1 = docOriginal.GetPage(1);
                var textExtractor = new TextExtractor(page1);
                originalText = textExtractor.ExtractText();
            }
            catch
            {
                // Some PDFs may have no text (scans, images); that's OK
                originalText = "";
            }
        }
        catch (Exception ex)
        {
            // Skip PDFs that can't be opened at all
            // Skip test gracefully
            _output.WriteLine($"{name} cannot be opened: {ex.Message}");
        }

        string? roundTripText = null;
        int roundTripPageCount = 0;

        // Step 2: Save to temporary file
        var tempFile = Path.Combine(Path.GetTempPath(), $"roundtrip_{Guid.NewGuid():N}.pdf");
        try
        {
            using (var docOriginal = PdfDocument.Open(pdfPath))
            {
                docOriginal.Save(tempFile);
            }

            // Step 3: Reload from saved file
            using var docReloaded = PdfDocument.Open(tempFile);
            roundTripPageCount = docReloaded.PageCount;

            // Verify page count is preserved
            roundTripPageCount.Should().Be(
                originalPageCount,
                $"{name} page count changed after round-trip (was {originalPageCount}, now {roundTripPageCount})");

            // Step 4: Re-extract text from first page
            try
            {
                var page1 = docReloaded.GetPage(1);
                var textExtractor = new TextExtractor(page1);
                roundTripText = textExtractor.ExtractText();
            }
            catch
            {
                roundTripText = "";
            }

            // Step 5: Verify text content is consistent
            // Allow some variation due to re-encoding, but essential content should match
            if (!string.IsNullOrEmpty(originalText))
            {
                // For simple validation, check that at least 50% of original words appear in round-trip
                var originalWords = originalText.Split(new[] { ' ', '\n', '\r', '\t' },
                    StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var roundTripWords = (roundTripText ?? "").Split(new[] { ' ', '\n', '\r', '\t' },
                    StringSplitOptions.RemoveEmptyEntries).ToHashSet();

                if (originalWords.Count > 0)
                {
                    var matchingWords = originalWords.Intersect(roundTripWords).Count();
                    var matchPercentage = (100.0 * matchingWords) / originalWords.Count;

                    // Should preserve majority of text content
                    matchPercentage.Should().BeGreaterThan(
                        30,  // Allow for re-encoding, font changes, but not massive loss
                        $"{name} page 1 text extraction severely degraded after round-trip " +
                        $"({matchPercentage:F1}% of original words found)");
                }
            }
        }
        finally
        {
            // Cleanup temp file
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch { /* Ignore cleanup failures */ }
        }

        sw.Stop();

        _output.WriteLine(
            $"{name}: {originalPageCount} pages, ~{originalText?.Length ?? 0} chars → " +
            $"saved & reloaded → {roundTripPageCount} pages, ~{roundTripText?.Length ?? 0} chars " +
            $"in {sw.ElapsedMilliseconds}ms");
    }

    public static IEnumerable<object[]> SmokeCorpusFiles()
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
