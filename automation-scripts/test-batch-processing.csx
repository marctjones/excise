/// <summary>
/// GUI Test: Batch Processing Workflow
/// Simulates a real-world batch processing scenario:
/// 1. Find all PDFs in a directory
/// 2. Redact common sensitive terms from each
/// 3. Save redacted versions with naming convention
/// 4. Generate processing report
/// </summary>
/// <returns>0 on success, 1 on failure</returns>

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;

Console.WriteLine("=== GUI Test: Batch Processing Workflow ===");

// Configuration
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

var inputDir = Path.Combine(repoRoot, "test-pdfs", "verapdf-corpus", "veraPDF-corpus-master", "PDF-A");
var outputDir = "/tmp/excise-batch-output";
var maxFiles = 10;

var termsToRedact = new[] { "test", "sample", "example", "data" };

// Create output directory
Directory.CreateDirectory(outputDir);

Console.WriteLine($"Input directory: {inputDir}");
Console.WriteLine($"Output directory: {outputDir}");
Console.WriteLine($"Max files: {maxFiles}");
Console.WriteLine($"Terms to redact: {string.Join(", ", termsToRedact)}");

if (!Directory.Exists(inputDir))
{
    Console.WriteLine($"\n❌ SKIP: Input directory not found");
    return 0;
}

try
{
    // Find PDFs
    Console.WriteLine($"\n[1/4] Scanning for PDF files");
    var pdfFiles = Directory.GetFiles(inputDir, "*.pdf", SearchOption.AllDirectories)
        .Take(maxFiles)
        .ToList();

    if (pdfFiles.Count == 0)
    {
        Console.WriteLine($"❌ SKIP: No PDF files found");
        return 0;
    }

    Console.WriteLine($"✅ Found {pdfFiles.Count} PDF file(s)");

    // Process each file
    Console.WriteLine($"\n[2/4] Processing files");

    var stopwatch = Stopwatch.StartNew();
    var results = new List<(string filename, int pages, int redactions, bool success, string message)>();
    var currentIndex = 0;

    foreach (var inputPath in pdfFiles)
    {
        currentIndex++;
        var filename = Path.GetFileName(inputPath);
        var progress = (currentIndex * 100) / pdfFiles.Count;
        Console.WriteLine($"\n  [{currentIndex}/{pdfFiles.Count}] ({progress}%) Processing: {filename}");

        try
        {
            // Load document
            Console.WriteLine($"    Loading...");
            await LoadDocumentCommand(inputPath);

            if (CurrentDocument == null)
            {
                results.Add((filename, 0, 0, false, "Failed to load"));
                Console.WriteLine($"    ❌ Failed to load");
                continue;
            }

            var pageCount = CurrentDocument.PageCount;
            Console.WriteLine($"    Pages: {pageCount}");

            // Skip files with too many pages
            if (pageCount > 50)
            {
                Console.WriteLine($"    ⚠️  Skipping (too large)");
                results.Add((filename, pageCount, 0, true, "Skipped - too large"));
                continue;
            }

            // Redact all terms
            Console.WriteLine($"    Redacting {termsToRedact.Length} term(s)...");
            var totalRedactions = 0;

            foreach (var term in termsToRedact)
            {
                var beforeCount = PendingRedactions.Count;
                await RedactTextCommand(term);
                var afterCount = PendingRedactions.Count;
                var added = afterCount - beforeCount;

                if (added > 0)
                {
                    Console.WriteLine($"      '{term}': {added} area(s)");
                    totalRedactions += added;
                }
            }

            if (totalRedactions > 0)
            {
                Console.WriteLine($"    Total redactions: {totalRedactions}");

                // Apply redactions
                Console.WriteLine($"    Applying redactions...");
                await ApplyRedactionsCommand();

                // Save with naming convention: original_name_REDACTED.pdf
                var outputFilename = Path.GetFileNameWithoutExtension(filename) + "_REDACTED.pdf";
                var outputPath = Path.Combine(outputDir, outputFilename);

                Console.WriteLine($"    Saving: {outputFilename}");
                await SaveDocumentCommand(outputPath);

                if (File.Exists(outputPath))
                {
                    var outputSize = new FileInfo(outputPath).Length;
                    Console.WriteLine($"    ✅ Saved: {outputFilename} ({outputSize / 1024} KB)");
                    results.Add((filename, pageCount, totalRedactions, true, "Success"));
                }
                else
                {
                    results.Add((filename, pageCount, totalRedactions, false, "Output not created"));
                    Console.WriteLine($"    ❌ Failed to save output");
                }
            }
            else
            {
                // No redactions needed - copy original or skip
                Console.WriteLine($"    ⚠️  No redactions needed");
                results.Add((filename, pageCount, 0, true, "No redactions"));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ❌ Exception: {ex.Message}");
            results.Add((filename, 0, 0, false, ex.Message));
        }
    }

    stopwatch.Stop();

    // Generate report
    Console.WriteLine($"\n[3/4] Processing Report");
    Console.WriteLine($"{'='.ToString().PadRight(70, '=')}");

    var successful = results.Count(r => r.success);
    var failed = results.Count(r => !r.success);
    var totalPages = results.Sum(r => r.pages);
    var totalRedactionsAcrossAllFiles = results.Sum(r => r.redactions);

    Console.WriteLine($"Files processed: {pdfFiles.Count}");
    Console.WriteLine($"  ✅ Successful: {successful}");
    Console.WriteLine($"  ❌ Failed: {failed}");
    Console.WriteLine($"\nStatistics:");
    Console.WriteLine($"  Total pages: {totalPages}");
    Console.WriteLine($"  Total redactions: {totalRedactionsAcrossAllFiles}");
    Console.WriteLine($"  Processing time: {stopwatch.Elapsed:mm\\:ss}");
    Console.WriteLine($"  Average: {stopwatch.Elapsed.TotalSeconds / pdfFiles.Count:F1} sec/file");

    // Output files
    var outputFiles = Directory.GetFiles(outputDir, "*_REDACTED.pdf");
    Console.WriteLine($"\nOutput files: {outputFiles.Length}");
    Console.WriteLine($"Output directory: {outputDir}");

    // Detailed results table
    Console.WriteLine($"\n[4/4] Detailed Results");
    Console.WriteLine($"{'='.ToString().PadRight(70, '=')}");
    Console.WriteLine($"{"Filename",-40} {"Pages",6} {"Redact",7} {"Status",-10}");
    Console.WriteLine($"{"-".ToString().PadRight(70, '-')}");

    foreach (var (filename, pages, redactions, success, message) in results)
    {
        var shortName = filename.Length > 40 ? filename.Substring(0, 37) + "..." : filename;
        var status = success ? "✅ OK" : $"❌ {message.Substring(0, Math.Min(5, message.Length))}";
        Console.WriteLine($"{shortName,-40} {pages,6} {redactions,7} {status,-10}");
    }

    Console.WriteLine($"{'='.ToString().PadRight(70, '=')}");

    // Success criteria
    var successRate = (double)successful / pdfFiles.Count;

    if (successRate >= 0.80)
    {
        Console.WriteLine($"\n✅ PASS: Batch processing successful ({successRate:P0} success rate)");
        Console.WriteLine($"  Processed {pdfFiles.Count} files in {stopwatch.Elapsed:mm\\:ss}");
        Console.WriteLine($"  Created {outputFiles.Length} redacted PDF(s)");
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
    Console.WriteLine($"  Stack trace: {ex.StackTrace}");
    return 1;
}
