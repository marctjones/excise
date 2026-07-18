// Example automation script: Redact birth certificate form
// This script can be executed via ScriptingService to automate GUI testing
//
// Usage:
//   From code: await scriptingService.ExecuteFileAsync("scripts/example-redact-birth-certificate.csx");
//   From CLI:  ./Excise.App --script scripts/example-redact-birth-certificate.csx

using System;
using System.IO;
using System.Threading.Tasks;

// The MainWindowViewModel is available as the global 'this' context
Console.WriteLine("Starting birth certificate redaction automation...");

// Configuration
var sourcePdf = "/home/marc/Downloads/Birth Certificate Request (PDF).pdf";
var outputPdf = "/tmp/birth-certificate-redacted.pdf";

// Check if source file exists
if (!File.Exists(sourcePdf))
{
    Console.WriteLine($"ERROR: Source PDF not found: {sourcePdf}");
    return 1;
}

// 1. Load the PDF document
Console.WriteLine($"Loading PDF: {sourcePdf}");
// TODO: Add LoadDocumentCommand.Execute() when GUI integration is complete

// 2. Redact sensitive information
var termsToRedact = new[]
{
    "TORRINGTON",
    "CERTIFICATE",
    "BIRTH",
    "CITY CLERK",
    "$20.00",
    "$15.00"
};

foreach (var term in termsToRedact)
{
    Console.WriteLine($"Redacting: {term}");
    // TODO: Add RedactTextCommand.Execute(term) when GUI integration is complete
}

// 3. Save the redacted document
Console.WriteLine($"Saving redacted PDF: {outputPdf}");
// TODO: Add SaveDocumentCommand.Execute(outputPdf) when GUI integration is complete

Console.WriteLine("✅ Birth certificate redaction complete!");
return 0;
