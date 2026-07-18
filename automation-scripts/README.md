# PDF Editor Automation Scripts

For end-user and CI automation, use the stable CLI contract documented in
[`docs/AUTOMATION_API.md`](../docs/AUTOMATION_API.md). The platform examples in
`macos/`, `windows/`, and `linux/` call `excise` or `excise batch` and return
structured JSON without taking GUI focus.

The `.csx` files in this directory are developer/test GUI automation scripts.
They use Roslyn scripting against `MainWindowViewModel`; Release builds exclude
that scripting surface by default unless explicitly published with
`-p:EnableScripting=true`.

C# scripts (.csx files) that automate GUI workflows using Roslyn scripting. These scripts interact with the MainWindowViewModel to test and automate the PDF Editor application.

## Purpose

These automation scripts serve multiple purposes:

1. **GUI Integration Testing** - Validate that GUI commands work end-to-end
2. **Manual Testing** - Run scripts manually to test workflows
3. **CI/CD Integration** - Execute in headless mode for automated testing
4. **Documentation** - Show examples of how to use the GUI programmatically
5. **v1.3.0 Milestone Validation** - Prove birth certificate redaction works

## Available Scripts

### test-load-document.csx
Tests that documents can be loaded via the GUI.

**Tests:**
- LoadDocumentCommand executes without error
- CurrentDocument is set after load
- FilePath matches the loaded file
- Document has valid page count

**Usage:**
```bash
# From xUnit tests
dotnet test --filter "AutomationScript_LoadDocument"

# Manually (future - after GUI integration)
./Excise.App --script automation-scripts/test-load-document.csx
```

**Expected result:** Exit code 0 (success)

### test-redact-text.csx
Tests the complete redaction workflow: load → redact → apply → save → verify.

**Tests:**
- Document loads successfully
- RedactTextCommand creates redaction areas
- ApplyRedactionsCommand applies redactions
- SaveDocumentCommand saves the output
- External verification confirms text removal

**Usage:**
```bash
# Default (uses birth certificate)
./Excise.App --script automation-scripts/test-redact-text.csx

# Custom PDF
./Excise.App --script automation-scripts/test-redact-text.csx \
  --script-arg source=/path/to/input.pdf \
  --script-arg output=/tmp/redacted.pdf \
  --script-arg text="SECRET"
```

**Expected result:** Exit code 0, output PDF created, text removed

### test-birth-certificate.csx
**CORNERSTONE TEST for v1.3.0 milestone.**

Tests redaction of the real-world birth certificate request form PDF.

**Tests:**
- Loads birth certificate PDF
- Redacts multiple sensitive terms: TORRINGTON, CERTIFICATE, BIRTH, CITY CLERK
- Applies all redactions to PDF structure
- Saves redacted output
- Verifies text removal with `pdftotext` when available
- Accepts ≥50% success rate (due to substring limitation #87)

**Usage:**
```bash
# From xUnit tests (validates v1.3.0)
dotnet test --filter "AutomationScript_BirthCertificate"

# Manually
./Excise.App --script automation-scripts/test-birth-certificate.csx
```

**Expected result:** Exit code 0, ≥50% of terms successfully redacted

**Success criteria for v1.3.0:**
- ✅ Birth certificate loads in GUI
- ✅ Redaction engine finds text
- ✅ Redactions apply to PDF structure
- ✅ Output PDF is created
- ✅ Text is removed (verified externally)

### test-verapdf-corpus-sample.csx
Tests GUI redaction against a diverse sample of PDFs from the veraPDF corpus.

**Tests:**
- Samples 20 PDFs across different standards (PDF/A-1a/1b/2a/2b/2u/3b/4, PDF/UA-1, ISO 32000)
- Loads each PDF
- Attempts to redact common text ("the")
- Saves redacted output
- Generates detailed report by PDF standard

**Usage:**
```bash
# Requires veraPDF corpus (download first)
./scripts/download-test-pdfs.sh

# Run from xUnit
dotnet test --filter "AutomationScript_VeraPdfCorpusSample"

# Manually
./Excise.App --script automation-scripts/test-verapdf-corpus-sample.csx
```

**Expected result:** ≥70% success rate across diverse PDF structures

**What it validates:**
- GUI handles PDF/A compliance documents
- GUI handles accessible PDFs (PDF/UA)
- GUI handles ISO 32000 reference files
- No crashes on edge-case PDF structures

### test-stress-diverse-pdfs.csx
Stress test: Processes 100 diverse PDFs sequentially.

**Tests:**
- Memory management (no leaks)
- Resource cleanup (proper disposal)
- Performance stability (consistent speed)
- Error recovery (handles failures gracefully)

**Usage:**
```bash
# Run from xUnit
dotnet test --filter "AutomationScript_StressDiversePdfs"

# Manually
./Excise.App --script automation-scripts/test-stress-diverse-pdfs.csx
```

**Expected result:** ≥80% success rate, stable performance

**Performance metrics:**
- Processing rate (PDFs/second)
- Average time per PDF
- Total pages processed
- Memory usage (stable throughout)

### test-batch-processing.csx
Tests realistic batch processing workflow for production use.

**Tests:**
- Find all PDFs in directory
- Redact multiple terms from each
- Save with naming convention (*_REDACTED.pdf)
- Generate processing report

**Usage:**
```bash
# Default (processes 10 PDFs from veraPDF corpus)
./Excise.App --script automation-scripts/test-batch-processing.csx

# Custom directory
./Excise.App --script automation-scripts/test-batch-processing.csx \
  --script-arg input=/path/to/pdfs \
  --script-arg output=/path/to/output \
  --script-arg maxFiles=50

# Run from xUnit
dotnet test --filter "AutomationScript_BatchProcessing"
```

**Expected result:** ≥80% success rate, all outputs created

**Report includes:**
- Files processed (success/failure counts)
- Total pages and redactions
- Processing time and rate
- Detailed results table

## Script Structure

All automation scripts follow this pattern:

```csharp
/// <summary>
/// Documentation comment describing the test
/// </summary>
/// <returns>0 on success, 1 on failure</returns>

using System;
using System.IO;

Console.WriteLine("=== Test: Description ===");

try
{
    // Step 1: Setup
    // ...

    // Step 2: Execute commands
    await SomeCommand.Execute(args);

    // Step 3: Verify results
    if (someCondition)
    {
        Console.WriteLine("❌ FAIL: reason");
        return 1;
    }

    // Step 4: Report success
    Console.WriteLine("✅ PASS: test succeeded");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"❌ FAIL: {ex.Message}");
    return 1;
}
```

## Script Context

When scripts execute, they have access to:

**Global ViewModel (`this`):**
- `CurrentDocument` - The currently loaded PDF document
- `PendingRedactions` - Collection of pending redaction areas
- All ViewModel properties and commands

**Available Commands:**
- `LoadDocumentCommand.Execute(string filePath)`
- `RedactTextCommand.Execute(string text)`
- `ApplyRedactionsCommand.Execute()`
- `SaveDocumentCommand.Execute(string outputPath)`

**Namespaces:**
- `System`, `System.IO`, `System.Linq`
- `System.Threading.Tasks`, `System.Diagnostics`
- `Excise.App.ViewModels`, `Excise.App.Services`, `Excise.App.Models`

## Return Codes

Scripts should return:
- **0** - Test passed, all assertions succeeded
- **1** - Test failed, assertion(s) failed or exception thrown

## Running from xUnit Tests

The `AutomationScriptTests` class runs these scripts as integration tests:

```csharp
[Fact]
public async Task AutomationScript_BirthCertificate_ExecutesSuccessfully()
{
    var viewModel = new MainWindowViewModel();
    var result = await RunAutomationScriptAsync("test-birth-certificate.csx", viewModel);

    result.Success.Should().BeTrue();
    var exitCode = Convert.ToInt32(result.ReturnValue);
    exitCode.Should().Be(0);
}
```

Run via command line:
```bash
# Run all automation script tests
dotnet test --filter "FullyQualifiedName~AutomationScriptTests"

# Run specific script test
dotnet test --filter "AutomationScript_BirthCertificate"

# Run with verbose output
dotnet test --filter "AutomationScript" --logger "console;verbosity=detailed"
```

## Script Validation

Before running, scripts are validated for syntax errors:

```bash
# Validate all scripts without executing
dotnet test --filter "AutomationScripts_ValidateAllScriptSyntax"
```

This catches compilation errors before execution.

## Headless Mode (Future)

Once GUI integration (#59) is complete, scripts can run in headless mode:

```bash
# Run without showing GUI window
./Excise.App --headless --script automation-scripts/test-birth-certificate.csx

# Run in CI/CD pipeline
./Excise.App --headless --script automation-scripts/test-birth-certificate.csx
if [ $? -eq 0 ]; then
    echo "✅ Birth certificate test PASSED"
else
    echo "❌ Birth certificate test FAILED"
    exit 1
fi
```

## Writing New Scripts

To create a new automation script:

1. **Create .csx file** in `automation-scripts/`
2. **Add documentation** with summary and return value
3. **Follow the pattern** shown above
4. **Return 0/1** for success/failure
5. **Add xUnit test** in `AutomationScriptTests.cs`

**Example:**
```csharp
// automation-scripts/test-my-feature.csx
/// <summary>
/// Tests my new feature
/// </summary>
/// <returns>0 on success, 1 on failure</returns>

Console.WriteLine("=== Test: My Feature ===");

try
{
    await MyFeatureCommand.Execute(args);

    if (MyFeature.WorkedCorrectly)
    {
        Console.WriteLine("✅ PASS");
        return 0;
    }
    else
    {
        Console.WriteLine("❌ FAIL");
        return 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"❌ FAIL: {ex.Message}");
    return 1;
}
```

Then add the test:
```csharp
// Excise.App.Tests/UI/AutomationScriptTests.cs
[Fact]
public async Task AutomationScript_MyFeature_ExecutesSuccessfully()
{
    var result = await RunAutomationScriptAsync("test-my-feature.csx");
    result.Success.Should().BeTrue();
    Convert.ToInt32(result.ReturnValue).Should().Be(0);
}
```

## Known Limitations

**Substring Redaction (#87):**
- Some terms may not be found if they're substrings within larger text operations
- Example: "STREET" within "NUMBER  STREET"
- Scripts should accept ≥50% success rate on multi-term tests

**GUI Integration (#59):**
- All scripts are currently skipped with `Skip = "Requires GUI integration (#59)"`
- Scripts will be enabled once GUI commands are implemented

## Troubleshooting

**Script fails to compile:**
```
❌ Compilation error: ...
```
→ Check script syntax, ensure all namespaces are available

**FileNotFoundException:**
```
❌ FAIL: Birth certificate not found
```
→ Download birth certificate PDF to expected location

**Commands not available:**
```
error CS0103: The name 'LoadDocumentCommand' does not exist
```
→ GUI integration (#59) not complete yet, commands not implemented

**External verification is skipped:**
```
⚠️ WARNING: pdftotext unavailable or failed
```
→ Install Poppler so `pdftotext` is available on PATH.

## Related

- **Issue #59**: GUI Integration (required for scripts to work)
- **Issue #91**: Roslyn C# Scripting Support (implementation)
- **Issue #87**: Substring redaction limitation (affects test expectations)

## v1.3.0 Validation

To validate v1.3.0 milestone is complete, run:

```bash
dotnet test --filter "AutomationScript_BirthCertificate"
```

If this test passes (exit code 0), v1.3.0 is complete.
