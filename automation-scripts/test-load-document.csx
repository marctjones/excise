/// <summary>
/// GUI Test Script: Load Document
/// Tests that the LoadDocumentCommand successfully loads a PDF and updates the ViewModel.
/// </summary>
/// <returns>0 on success, 1 on failure</returns>

using System;
using System.IO;

Console.WriteLine("=== GUI Test: Load Document ===");

// Test configuration
// Find repository root
var repoRoot = Directory.GetCurrentDirectory();
while (repoRoot != null && !Directory.Exists(Path.Combine(repoRoot, ".git")))
{
    repoRoot = Directory.GetParent(repoRoot)?.FullName;
}

if (repoRoot == null)
{
    Console.WriteLine("❌ FAIL: Could not find repository root (.git directory)");
    return 1;
}

var testPdf = Path.Combine(repoRoot, "test-pdfs", "sample-pdfs", "birth-certificate-request-scrambled.pdf");

// Validate input
if (!File.Exists(testPdf))
{
    Console.WriteLine($"❌ FAIL: Test PDF not found: {testPdf}");
    return 1;
}

try
{
    // Execute: Load document
    Console.WriteLine($"Loading: {testPdf}");
    await LoadDocumentHeadlessAsync(testPdf);

    // Verify: CurrentDocument is set
    if (CurrentDocument == null)
    {
        Console.WriteLine("❌ FAIL: CurrentDocument is null after LoadDocumentCommand");
        return 1;
    }

    // Verify: FilePath matches
    if (CurrentDocument.FilePath != testPdf)
    {
        Console.WriteLine($"❌ FAIL: FilePath mismatch");
        Console.WriteLine($"  Expected: {testPdf}");
        Console.WriteLine($"  Actual: {CurrentDocument.FilePath}");
        return 1;
    }

    // Verify: Document has pages
    if (CurrentDocument.PageCount <= 0)
    {
        Console.WriteLine($"❌ FAIL: Document has no pages (PageCount = {CurrentDocument.PageCount})");
        return 1;
    }

    // Success
    Console.WriteLine($"✅ PASS: Document loaded successfully");
    Console.WriteLine($"  FilePath: {CurrentDocument.FilePath}");
    Console.WriteLine($"  Pages: {CurrentDocument.PageCount}");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"❌ FAIL: Exception during load");
    Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"  {ex.StackTrace}");
    return 1;
}
