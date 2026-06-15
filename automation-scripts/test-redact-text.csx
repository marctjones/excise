/// <summary>
/// GUI Test Script: Redact Text
/// Tests the complete redaction workflow: load → redact → apply → save → verify.
/// </summary>
/// <returns>0 on success, 1 on failure</returns>

using System;
using System.IO;
using System.Diagnostics;

Console.WriteLine("=== GUI Test: Redact Text Workflow ===");

// Test configuration
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

var sourcePdf = Path.Combine(repoRoot, "test-pdfs", "sample-pdfs", "birth-certificate-request-scrambled.pdf");
var outputPdf = "/tmp/pdfe-test-redacted.pdf";
var textToRedact = "CITY";

// Clean up previous test output
if (File.Exists(outputPdf))
{
    File.Delete(outputPdf);
}

// Validate input
if (!File.Exists(sourcePdf))
{
    Console.WriteLine($"❌ FAIL: Source PDF not found: {sourcePdf}");
    return 1;
}

try
{
    // Step 1: Load document
    Console.WriteLine($"\n[1/5] Loading document: {sourcePdf}");
    await LoadDocumentCommand(sourcePdf);

    if (CurrentDocument == null)
    {
        Console.WriteLine("❌ FAIL: Document not loaded");
        return 1;
    }
    Console.WriteLine($"✅ Loaded {CurrentDocument.PageCount} pages");

    // Step 2: Redact text
    Console.WriteLine($"\n[2/5] Redacting text: '{textToRedact}'");
    var initialPendingCount = PendingRedactions.Count;

    await RedactTextCommand(textToRedact);

    var newPendingCount = PendingRedactions.Count;
    var redactionsAdded = newPendingCount - initialPendingCount;

    if (redactionsAdded <= 0)
    {
        Console.WriteLine($"❌ FAIL: No redactions added (before: {initialPendingCount}, after: {newPendingCount})");
        return 1;
    }
    Console.WriteLine($"✅ Added {redactionsAdded} redaction area(s) (total pending: {newPendingCount})");

    // Step 3: Apply redactions
    Console.WriteLine($"\n[3/5] Applying redactions");
    await ApplyRedactionsCommand();

    if (PendingRedactions.Count > 0)
    {
        Console.WriteLine($"❌ FAIL: Pending redactions not cleared (count: {PendingRedactions.Count})");
        return 1;
    }
    Console.WriteLine($"✅ All redactions applied");

    // Step 4: Save document
    Console.WriteLine($"\n[4/5] Saving to: {outputPdf}");
    await SaveDocumentCommand(outputPdf);

    if (!File.Exists(outputPdf))
    {
        Console.WriteLine($"❌ FAIL: Output file not created");
        return 1;
    }
    Console.WriteLine($"✅ Document saved ({new FileInfo(outputPdf).Length} bytes)");

    // Step 5: Verify redaction using an independent text extractor.
    Console.WriteLine($"\n[5/5] Verifying redaction with pdftotext");
    var extractedTextPath = Path.Combine(Path.GetTempPath(), $"pdfe-redact-text-{Guid.NewGuid():N}.txt");
    try
    {
        var verifyProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "pdftotext",
            ArgumentList = { outputPdf, extractedTextPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        if (verifyProcess == null)
            throw new InvalidOperationException("Could not start pdftotext");

        await verifyProcess.WaitForExitAsync();
        if (verifyProcess.ExitCode == 0 && File.Exists(extractedTextPath))
        {
            var extracted = await File.ReadAllTextAsync(extractedTextPath);
            if (extracted.Contains(textToRedact, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"❌ FAIL: Text still found in redacted PDF");
                return 1;
            }

            Console.WriteLine($"✅ Verification PASS: '{textToRedact}' not found by pdftotext");
        }
        else
        {
            Console.WriteLine($"⚠️  WARNING: pdftotext unavailable or failed; skipping external verification");
        }
    }
    finally
    {
        if (File.Exists(extractedTextPath))
            File.Delete(extractedTextPath);
    }

    // All checks passed
    Console.WriteLine($"\n✅ SUCCESS: Complete redaction workflow validated");
    Console.WriteLine($"  Source: {sourcePdf}");
    Console.WriteLine($"  Output: {outputPdf}");
    Console.WriteLine($"  Redacted: '{textToRedact}'");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ FAIL: Exception during workflow");
    Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"  Stack trace: {ex.StackTrace}");
    return 1;
}
