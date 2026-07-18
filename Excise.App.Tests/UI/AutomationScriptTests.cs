using System;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.ViewModels;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// Integration tests that execute automation scripts to validate GUI functionality.
/// These tests run the .csx files from automation-scripts/ directory.
/// </summary>
public class AutomationScriptTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _scriptsDir;

    public AutomationScriptTests(ITestOutputHelper output)
    {
        _output = output;

        // Find the automation-scripts directory
        var currentDir = Directory.GetCurrentDirectory();
        var repoRoot = FindRepositoryRoot(currentDir);

        if (repoRoot == null)
        {
            throw new InvalidOperationException("Could not find repository root directory");
        }

        _scriptsDir = Path.Combine(repoRoot, "automation-scripts");

        if (!Directory.Exists(_scriptsDir))
        {
            throw new DirectoryNotFoundException($"Automation scripts directory not found: {_scriptsDir}");
        }

        _output.WriteLine($"Automation scripts directory: {_scriptsDir}");
    }

    /// <summary>
    /// Helper to execute an automation script and validate it succeeds.
    /// </summary>
    private async Task<ScriptExecutionResult> RunAutomationScriptAsync(
        string scriptName,
        MainWindowViewModel? viewModel = null)
    {
        viewModel ??= new MainWindowViewModel();

        var scriptPath = Path.Combine(_scriptsDir, scriptName);

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Automation script not found: {scriptPath}");
        }

        _output.WriteLine($"Executing automation script: {scriptName}");
        _output.WriteLine($"Script path: {scriptPath}");

        var scriptingService = new ScriptingService(viewModel);
        var result = await scriptingService.ExecuteFileAsync(scriptPath);

        _output.WriteLine($"Execution completed: {(result.Success ? "SUCCESS" : "FAILED")}");

        if (!result.Success)
        {
            _output.WriteLine($"Error: {result.ErrorMessage}");
        }
        else
        {
            _output.WriteLine($"Return value: {result.ReturnValue}");
        }

        return result;
    }

    /// <summary>
    /// Helper to find the repository root directory.
    /// </summary>
    private string? FindRepositoryRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);

        while (dir != null)
        {
            // Look for .git directory or automation-scripts directory
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                Directory.Exists(Path.Combine(dir.FullName, "automation-scripts")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public async Task AutomationScript_LoadDocument_ExecutesSuccessfully()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();

        // Act
        var result = await RunAutomationScriptAsync("test-load-document.csx", viewModel);

        // Assert
        result.Success.Should().BeTrue("script should compile and execute");

        // Script returns 0 on success, 1 on failure
        var exitCode = Convert.ToInt32(result.ReturnValue);
        exitCode.Should().Be(0, "script should pass all assertions");
    }

    [Fact]
    public async Task AutomationScript_RedactText_ExecutesSuccessfully()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();

        // Act
        var result = await RunAutomationScriptAsync("test-redact-text.csx", viewModel);

        // Assert
        result.Success.Should().BeTrue("script should compile and execute");

        var exitCode = Convert.ToInt32(result.ReturnValue);
        exitCode.Should().Be(0, "complete redaction workflow should succeed");
    }

    [Fact]
    public async Task AutomationScript_BirthCertificate_ExecutesSuccessfully()
    {
        // This is the CORNERSTONE TEST for v1.3.0 milestone
        // If this passes, the milestone is complete

        // Arrange
        var viewModel = new MainWindowViewModel();

        // Act
        var result = await RunAutomationScriptAsync("test-birth-certificate.csx", viewModel);

        // Assert
        result.Success.Should().BeTrue("script should compile and execute");

        var exitCode = Convert.ToInt32(result.ReturnValue);
        exitCode.Should().Be(0, "birth certificate redaction should succeed");

        _output.WriteLine("\n✅ v1.3.0 MILESTONE COMPLETE");
        _output.WriteLine("Birth certificate redaction works end-to-end via GUI");
    }

    [Fact]
    public async Task AutomationScript_BirthCertificateSpecificWords_ExecutesSuccessfully()
    {
        // Targeted test: specific known words in birth certificate PDF
        // Tests precision, case handling, word boundaries

        // Arrange
        var viewModel = new MainWindowViewModel();

        // Act
        var result = await RunAutomationScriptAsync("test-birth-certificate-specific-words.csx", viewModel);

        // Assert
        result.Success.Should().BeTrue("script should compile and execute");

        var exitCode = Convert.ToInt32(result.ReturnValue);
        exitCode.Should().Be(0, "all specific word redaction tests should pass");

        _output.WriteLine("\n✅ Specific word redaction validated");
        _output.WriteLine("Known words correctly redacted from known PDF");
    }

    [Fact]
    public async Task AutomationScript_VeraPdfCorpusSample_ExecutesSuccessfully()
    {
        // Test redaction across diverse PDF standards from veraPDF corpus.
        //
        // The unconditional [Fact(Skip = ...)] this test used to carry made it
        // impossible to enable even when the corpus IS present locally. The
        // companion .csx (automation-scripts/test-verapdf-corpus-sample.csx)
        // already self-skips by returning 0 with a "veraPDF corpus not found"
        // message when test-pdfs/verapdf-corpus is missing — that's the
        // single source of truth for the corpus-presence gate. No need to
        // duplicate it as an attribute Skip.
        //
        // Issue #190 FIX: Now uses file-based TextRedactor API (like CLI)
        // which bypasses coordinate conversion issues.

        // Arrange
        var viewModel = new MainWindowViewModel();

        // Act
        var result = await RunAutomationScriptAsync("test-verapdf-corpus-sample.csx", viewModel);

        // Assert
        result.Success.Should().BeTrue("script should compile and execute");

        var exitCode = Convert.ToInt32(result.ReturnValue);
        exitCode.Should().Be(0, "corpus sample test should pass");

        _output.WriteLine("\n✅ Corpus diversity validated");
        _output.WriteLine("GUI handles diverse PDF structures (PDF/A, PDF/UA, ISO 32000)");
    }

    [Fact]
    public async Task AutomationScript_StressDiversePdfs_ExecutesSuccessfully()
    {
        // Stress test: Process 100 diverse PDFs sequentially

        // Arrange
        var viewModel = new MainWindowViewModel();

        // Act
        var result = await RunAutomationScriptAsync("test-stress-diverse-pdfs.csx", viewModel);

        // Assert
        result.Success.Should().BeTrue("script should compile and execute");

        var exitCode = Convert.ToInt32(result.ReturnValue);
        exitCode.Should().Be(0, "stress test should achieve ≥80% success rate");

        _output.WriteLine("\n✅ Stress test passed");
        _output.WriteLine("GUI maintains stability processing 100 diverse PDFs");
    }

    [Fact]
    public async Task AutomationScript_BatchProcessing_ExecutesSuccessfully()
    {
        // Test realistic batch processing workflow

        // Arrange
        var viewModel = new MainWindowViewModel();

        // Act
        var result = await RunAutomationScriptAsync("test-batch-processing.csx", viewModel);

        // Assert
        result.Success.Should().BeTrue("script should compile and execute");

        var exitCode = Convert.ToInt32(result.ReturnValue);
        exitCode.Should().Be(0, "batch processing should complete successfully");

        _output.WriteLine("\n✅ Batch processing validated");
        _output.WriteLine("GUI supports production batch workflows");
    }

    [Fact]
    public void AutomationScripts_AllScriptsExist()
    {
        // Verify all required automation scripts are present

        var requiredScripts = new[]
        {
            "test-load-document.csx",
            "test-redact-text.csx",
            "test-birth-certificate.csx",
            "test-birth-certificate-specific-words.csx",
            "test-verapdf-corpus-sample.csx",
            "test-stress-diverse-pdfs.csx",
            "test-batch-processing.csx",
        };

        foreach (var scriptName in requiredScripts)
        {
            var scriptPath = Path.Combine(_scriptsDir, scriptName);
            File.Exists(scriptPath).Should().BeTrue($"{scriptName} should exist at {scriptPath}");
        }
    }

    [Fact]
    public async Task AutomationScripts_ValidateAllScriptSyntax()
    {
        // Validate that all automation scripts have valid C# syntax
        // This doesn't execute them, just checks for compilation errors

        var viewModel = new MainWindowViewModel();
        var scriptingService = new ScriptingService(viewModel);

        var scriptFiles = Directory.GetFiles(_scriptsDir, "*.csx");

        _output.WriteLine($"Found {scriptFiles.Length} automation script(s) to validate");

        foreach (var scriptPath in scriptFiles)
        {
            var scriptName = Path.GetFileName(scriptPath);
            _output.WriteLine($"\nValidating: {scriptName}");

            var scriptCode = await File.ReadAllTextAsync(scriptPath);
            var errors = scriptingService.ValidateScript(scriptCode);

            if (errors.Count > 0)
            {
                _output.WriteLine($"  ❌ Compilation errors:");
                foreach (var error in errors)
                {
                    _output.WriteLine($"    - {error}");
                }
            }
            else
            {
                _output.WriteLine($"  ✅ Valid syntax");
            }

            errors.Should().BeEmpty($"{scriptName} should have valid C# syntax");
        }
    }
}
