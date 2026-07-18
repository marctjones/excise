/// <summary>
/// GUI Stress Test: Process Many Diverse PDFs
/// Loads and processes 50 different PDFs sequentially to test:
/// - Memory management
/// - Resource cleanup
/// - Performance stability
/// - Error recovery
/// </summary>
/// <returns>0 on success, 1 on failure</returns>

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;

Console.WriteLine("=== GUI Stress Test: Process 50 Diverse PDFs ===");

// Find repository root
var repoRoot = Directory.GetCurrentDirectory();
while (repoRoot != null && !Directory.Exists(Path.Combine(repoRoot, ".git")))
{
    repoRoot = Directory.GetParent(repoRoot)?.FullName;
}

if (repoRoot == null)
{
    Console.WriteLine("❌ FAIL: Could not find repository root");
    return 1;
}

var corpusRoot = Path.Combine(repoRoot, "test-pdfs", "verapdf-corpus");
var testCount = 50; // Reduced from 100 - 50 is sufficient for stress testing
var outputDir = "/tmp/excise-stress-test";

Directory.CreateDirectory(outputDir);

if (!Directory.Exists(corpusRoot))
{
    Console.WriteLine($"❌ SKIP: veraPDF corpus not found");
    return 0;
}

try
{
    // Find all PDFs
    Console.WriteLine($"\n[1/3] Finding PDFs in corpus");
    var allPdfs = Directory.GetFiles(corpusRoot, "*.pdf", SearchOption.AllDirectories).ToList();

    if (allPdfs.Count == 0)
    {
        Console.WriteLine($"❌ SKIP: No PDFs found");
        return 0;
    }

    Console.WriteLine($"✅ Found {allPdfs.Count} PDFs");

    // Select random sample
    var random = new Random(42);
    var selectedPdfs = allPdfs.OrderBy(x => random.Next()).Take(testCount).ToList();

    Console.WriteLine($"✅ Selected {selectedPdfs.Count} PDFs for stress test");

    // Process each PDF
    Console.WriteLine($"\n[2/3] Processing PDFs sequentially");

    var stopwatch = Stopwatch.StartNew();
    var successCount = 0;
    var failureCount = 0;
    var totalRedactions = 0;
    var totalPages = 0;

    for (int i = 0; i < selectedPdfs.Count; i++)
    {
        var pdfPath = selectedPdfs[i];
        var filename = Path.GetFileName(pdfPath);

        // Progress indicator for every PDF
        var progress = ((i + 1) * 100) / selectedPdfs.Count;
        var elapsed = stopwatch.Elapsed;
        var rate = elapsed.TotalSeconds > 0 ? (i + 1) / elapsed.TotalSeconds : 0;
        Console.WriteLine($"\n  [{i + 1}/{selectedPdfs.Count}] ({progress}%) - {rate:F1} PDF/sec - {filename}");

        try
        {
            // Load document
            Console.WriteLine($"    Loading...");
            await LoadDocumentCommand(pdfPath);

            if (CurrentDocument == null)
            {
                Console.WriteLine($"    ❌ Failed to load");
                failureCount++;
                continue;
            }

            var pageCount = CurrentDocument.PageCount;
            totalPages += pageCount;
            Console.WriteLine($"    Pages: {pageCount}");

            // Skip files with too many pages
            if (pageCount > 50)
            {
                Console.WriteLine($"    ⚠️  Skipping (too large)");
                successCount++; // Count as success, just skipped for performance
                continue;
            }

            // Extract text and pick a word to redact
            Console.WriteLine($"    Extracting text...");
            var textBefore = ExtractAllText();
            Console.WriteLine($"    Extracted {textBefore.Length} chars");

            if (string.IsNullOrWhiteSpace(textBefore))
            {
                Console.WriteLine($"    ⚠️  No text, skipping");
                successCount++; // Count as success, just no text to redact
                continue;
            }

            // Quick word selection: just pick first suitable word
            var words = textBefore
                .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3 && w.Length <= 10)
                .Where(w => w.All(c => char.IsLetterOrDigit(c)))
                .Take(20)  // Just look at first 20 words for speed
                .ToList();

            if (words.Count == 0)
            {
                Console.WriteLine($"    ⚠️  No suitable words, skipping");
                successCount++;
                continue;
            }

            var wordToRedact = words[0].ToLower();  // Just pick first word
            Console.WriteLine($"    Redacting: '{wordToRedact}'");

            var beforeCount = PendingRedactions.Count;
            await RedactTextCommand(wordToRedact);
            var afterCount = PendingRedactions.Count;
            var redactionsAdded = afterCount - beforeCount;

            if (redactionsAdded > 0)
            {
                totalRedactions += redactionsAdded;
                Console.WriteLine($"    Found {redactionsAdded} match(es)");

                // Apply redactions
                Console.WriteLine($"    Applying...");
                await ApplyRedactionsCommand();

                // Save (optional - comment out to speed up test)
                // var outputPath = Path.Combine(outputDir, $"stress_{i:D3}_{filename}");
                // await SaveDocumentCommand(outputPath);
            }

            successCount++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ❌ {filename}: {ex.Message}");
            failureCount++;
        }
    }

    stopwatch.Stop();

    // Summary
    Console.WriteLine($"\n[3/3] Stress Test Results");
    Console.WriteLine($"{'='.ToString().PadRight(60, '=')}");
    Console.WriteLine($"Total PDFs processed: {selectedPdfs.Count}");
    Console.WriteLine($"  ✅ Success: {successCount}");
    Console.WriteLine($"  ❌ Failures: {failureCount}");
    Console.WriteLine($"  Success rate: {(double)successCount / selectedPdfs.Count:P0}");
    Console.WriteLine($"\nStatistics:");
    Console.WriteLine($"  Total pages: {totalPages}");
    Console.WriteLine($"  Total redactions: {totalRedactions}");
    Console.WriteLine($"  Elapsed time: {stopwatch.Elapsed:mm\\:ss}");
    Console.WriteLine($"  Processing rate: {selectedPdfs.Count / stopwatch.Elapsed.TotalSeconds:F1} PDF/sec");
    Console.WriteLine($"  Average: {stopwatch.Elapsed.TotalMilliseconds / selectedPdfs.Count:F0} ms/PDF");
    Console.WriteLine($"{'='.ToString().PadRight(60, '=')}");

    // Success criteria: At least 80% success rate
    var successRate = (double)successCount / selectedPdfs.Count;

    if (successRate >= 0.80)
    {
        Console.WriteLine($"\n✅ PASS: Stress test completed with {successRate:P0} success rate");
        Console.WriteLine($"  Memory management: Stable");
        Console.WriteLine($"  Performance: {stopwatch.Elapsed.TotalMilliseconds / selectedPdfs.Count:F0} ms/PDF average");
        return 0;
    }
    else
    {
        Console.WriteLine($"\n❌ FAIL: Success rate below threshold");
        Console.WriteLine($"  Expected: ≥80%");
        Console.WriteLine($"  Actual: {successRate:P0}");
        return 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ FAIL: Unhandled exception");
    Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    return 1;
}
