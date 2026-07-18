using System.IO;
using AwesomeAssertions;
using Pdfe.Core.Security;
using Xunit;

namespace Pdfe.Cli.Tests;

/// <summary>
/// #642: CLI enforcement of the document's /P permission flags. Fixtures
/// are generated with pdfe's own encryption writer (#641) using masks whose
/// bit meanings were confirmed against qpdf 12's <c>--show-encryption</c>:
///   -20  → "extract for any purpose: not allowed", accessibility allowed
///   -532 → extraction denied for any purpose INCLUDING accessibility
///   -8   → only printing denied (everything gated here still allowed)
/// All fixtures use an empty USER password (open without prompting) plus an
/// owner password — the classic "restricted but openable" document, and the
/// only authority level pdfe can open with today (#324).
/// </summary>
public class PermissionEnforcementTests : IDisposable
{
    private const long DenyCopyMask = -20;              // -4 & ~16 (clear bit 5)
    private const long DenyCopyAndAccessibilityMask = -532; // -20 & ~512 (also clear bit 10)
    private const long AllAllowedMask = -4;

    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private string TempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfe-perm-test-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Encrypted single-page PDF with the given /P mask, empty user password.</summary>
    private string RestrictedFixture(long permissions, string text = "PERM FIXTURE TEXT")
    {
        var plain = TempPath(".pdf");
        var encrypted = TempPath(".pdf");
        File.WriteAllBytes(plain, TestPdfBuilder.SinglePage(text));
        Program.RunEncrypt(plain, encrypted, userPassword: null, ownerPassword: "owner-pw",
            permissions, PdfEncryptionAlgorithm.Aes256, encryptMetadata: true);
        return encrypted;
    }

    // ---- text ------------------------------------------------------------

    [Fact]
    public async Task Text_CopyForbidden_FailsClosed()
    {
        var pdf = RestrictedFixture(DenyCopyMask);

        var result = await RunCliCaptureAsync(["text", pdf]);

        result.ExitCode.Should().NotBe(0, "a copy-forbidden document must not be extracted from");
        result.StdOut.Should().NotContain("PERM FIXTURE TEXT");
        result.StdErr.Should().Contain("Blocked by document permissions");
        result.StdErr.Should().Contain("bit 5");
        result.StdErr.Should().Contain("--for-accessibility",
            "bit 10 is granted, so the carve-out must be advertised");
        result.StdErr.Should().Contain("--ignore-permissions");
    }

    [Fact]
    public async Task Text_CopyForbidden_ForAccessibility_HonoursBit10CarveOut()
    {
        var pdf = RestrictedFixture(DenyCopyMask);

        var result = await RunCliCaptureAsync(["text", pdf, "--for-accessibility"]);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("PERM FIXTURE TEXT");
        result.StdErr.Should().Contain("accessibility",
            "proceeding under bit 10 while bit 5 is denied should be noted");
    }

    [Fact]
    public async Task Text_CopyAndAccessibilityForbidden_ForAccessibilityDoesNotBypass()
    {
        var pdf = RestrictedFixture(DenyCopyAndAccessibilityMask);

        var result = await RunCliCaptureAsync(["text", pdf, "--for-accessibility"]);

        result.ExitCode.Should().NotBe(0,
            "--for-accessibility is the bit 10 carve-out, not an override; with bit 10 " +
            "denied too, only --ignore-permissions proceeds");
        result.StdOut.Should().NotContain("PERM FIXTURE TEXT");
        result.StdErr.Should().Contain("Blocked by document permissions");
    }

    [Fact]
    public async Task Text_CopyForbidden_IgnorePermissions_OverridesLoudly()
    {
        var pdf = RestrictedFixture(DenyCopyMask);

        var result = await RunCliCaptureAsync(["text", pdf, "--ignore-permissions"]);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("PERM FIXTURE TEXT");
        result.StdErr.Should().Contain("overriding document permissions",
            "the override must be loud, mirroring --allow-decrypt (#638)");
    }

    [Fact]
    public async Task Text_AllAllowedEncrypted_IsNotGated()
    {
        var pdf = RestrictedFixture(AllAllowedMask);

        var result = await RunCliCaptureAsync(["text", pdf]);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("PERM FIXTURE TEXT");
        result.StdErr.Should().NotContain("permission");
    }

    [Fact]
    public async Task Text_PrintOnlyForbidden_IsNotGated()
    {
        // -8 denies exactly printing (qpdf-confirmed); extraction is allowed.
        var pdf = RestrictedFixture(-8);

        var result = await RunCliCaptureAsync(["text", pdf]);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("PERM FIXTURE TEXT");
    }

    // ---- letters / render ------------------------------------------------

    [Fact]
    public async Task Letters_CopyForbidden_FailsClosed_AndOverrides()
    {
        var pdf = RestrictedFixture(DenyCopyMask);

        var blocked = await RunCliCaptureAsync(["letters", pdf]);
        blocked.ExitCode.Should().NotBe(0);
        blocked.StdErr.Should().Contain("Blocked by document permissions");

        var overridden = await RunCliCaptureAsync(["letters", pdf, "--ignore-permissions"]);
        overridden.ExitCode.Should().Be(0);
        overridden.StdOut.Should().Contain("letters");
    }

    [Fact]
    public async Task Render_CopyForbidden_FailsClosed_AndOverrides()
    {
        var pdf = RestrictedFixture(DenyCopyMask);
        var png = TempPath(".png");

        var blocked = await RunCliCaptureAsync(["render", pdf, "-o", png]);
        blocked.ExitCode.Should().NotBe(0);
        File.Exists(png).Should().BeFalse("a blocked export must not produce a file");
        blocked.StdErr.Should().Contain("Blocked by document permissions");

        var overridden = await RunCliCaptureAsync(["render", pdf, "-o", png, "--ignore-permissions"]);
        overridden.ExitCode.Should().Be(0);
        File.Exists(png).Should().BeTrue();
    }

    // ---- real-world fixture ---------------------------------------------

    [Fact]
    public async Task Text_GdayOwnerFixture_CopyForbidden_FailsClosed_ButAccessibilityWorks()
    {
        // P = -3392 (qpdf: extract not allowed, accessibility allowed),
        // empty user password — opens without prompting.
        var path = Path.Combine(FindRepoRoot(), "test-pdfs/poppler/unittestcases/Gday garçon - owner.pdf");
        Assert.SkipWhen(!File.Exists(path), "Encrypted PDF fixture not available");

        var blocked = await RunCliCaptureAsync(["text", path]);
        blocked.ExitCode.Should().NotBe(0);
        blocked.StdOut.Should().NotContain("garçon");
        blocked.StdErr.Should().Contain("Blocked by document permissions");

        var accessible = await RunCliCaptureAsync(["text", path, "--for-accessibility"]);
        accessible.ExitCode.Should().Be(0);
        accessible.StdOut.Should().Contain("garçon");
    }

    // ---- forms -----------------------------------------------------------

    [Fact]
    public void RunAddField_ModifyForbidden_FailsClosed_AndOverrides()
    {
        // Clear bit 4 (value 8): modify denied, everything else allowed.
        var pdf = RestrictedFixture(-4 & ~8);
        var output = TempPath(".pdf");

        var act = () => Program.RunAddField(pdf, output, "Text", "field1", 1, "100,100,300,130", null, []);
        act.Should().Throw<Program.PdfPermissionDeniedException>()
            .WithMessage("*bit 4*");
        File.Exists(output).Should().BeFalse();

        Program.RunAddField(pdf, output, "Text", "field1", 1, "100,100,300,130", null, [],
            ignorePermissions: true);
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public void RunFillForm_FillForbidden_FailsClosed()
    {
        // Clear bits 6 (32) and 9 (256): form fill-in denied both ways.
        var withField = TempPath(".pdf");
        Program.RunAddField(RestrictedFixtureUnencryptedSource(), withField,
            "Text", "name", 1, "100,100,300,130", null, []);
        var restricted = TempPath(".pdf");
        Program.RunEncrypt(withField, restricted, userPassword: null, ownerPassword: "owner-pw",
            -4 & ~32 & ~256, PdfEncryptionAlgorithm.Aes256, encryptMetadata: true);
        var output = TempPath(".pdf");

        var act = () => Program.RunFillForm(restricted, output, ["name=Jane"], flatten: false);
        act.Should().Throw<Program.PdfPermissionDeniedException>()
            .WithMessage("*bit 6 or 9*");

        Program.RunFillForm(restricted, output, ["name=Jane"], flatten: false, ignorePermissions: true)
            .Should().Be(1);
    }

    [Fact]
    public void RunFillForm_Bit9Granted_Bit6Denied_StillFills()
    {
        // Table 22: bit 9 permits filling existing fields even when bit 6
        // (annotate) is denied.
        var withField = TempPath(".pdf");
        Program.RunAddField(RestrictedFixtureUnencryptedSource(), withField,
            "Text", "name", 1, "100,100,300,130", null, []);
        var restricted = TempPath(".pdf");
        Program.RunEncrypt(withField, restricted, userPassword: null, ownerPassword: "owner-pw",
            -4 & ~32, PdfEncryptionAlgorithm.Aes256, encryptMetadata: true);
        var output = TempPath(".pdf");

        Program.RunFillForm(restricted, output, ["name=Jane"], flatten: false).Should().Be(1);
    }

    private string RestrictedFixtureUnencryptedSource()
    {
        var plain = TempPath(".pdf");
        File.WriteAllBytes(plain, TestPdfBuilder.SinglePage("FORM SOURCE"));
        return plain;
    }

    // ---- batch automation surface ---------------------------------------

    [Fact]
    public async Task Batch_TextStep_CopyForbidden_FailsWithPermissionDenied_AndStepOverridesWork()
    {
        var pdf = RestrictedFixture(DenyCopyMask);
        var directory = Path.Combine(Path.GetTempPath(), $"pdfe-perm-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempFiles.Add(Path.Combine(directory, "workflow.json")); // best-effort cleanup
        try
        {
            var workflow = Path.Combine(directory, "workflow.json");

            File.WriteAllText(workflow, System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                steps = new object[]
                {
                    new { id = "text", command = Pdfe.Core.Automation.PdfCommandIds.ExtractText, input = pdf },
                },
            }));
            var blocked = await RunCliCaptureAsync(["batch", workflow, "--json"]);
            blocked.ExitCode.Should().NotBe(0);
            blocked.StdOut.Should().Contain("PERMISSION_DENIED");
            blocked.StdOut.Should().NotContain("PERM FIXTURE TEXT");

            File.WriteAllText(workflow, System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                steps = new object[]
                {
                    new
                    {
                        id = "text",
                        command = Pdfe.Core.Automation.PdfCommandIds.ExtractText,
                        input = pdf,
                        forAccessibility = true,
                    },
                },
            }));
            var accessible = await RunCliCaptureAsync(["batch", workflow, "--json"]);
            accessible.ExitCode.Should().Be(0, "the bit 10 carve-out applies to the automation surface too");
            accessible.StdOut.Should().Contain("PERM FIXTURE TEXT");

            File.WriteAllText(workflow, System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                steps = new object[]
                {
                    new
                    {
                        id = "text",
                        command = Pdfe.Core.Automation.PdfCommandIds.ExtractText,
                        input = pdf,
                        ignorePermissions = true,
                    },
                },
            }));
            var overridden = await RunCliCaptureAsync(["batch", workflow, "--json"]);
            overridden.ExitCode.Should().Be(0);
            overridden.StdOut.Should().Contain("PERM FIXTURE TEXT");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // ---- redaction stays ungated ----------------------------------------

    [Fact]
    public void RunRedact_ModifyForbiddenDocument_StillRedacts()
    {
        // Redaction is deliberately NOT gated on /P (#642): it is pdfe's
        // core security purpose, and a document author's no-modify bit must
        // not prevent a user redacting their own copy. (#643 owns the
        // encrypted-source redaction flow; --allow-decrypt is #638's gate.)
        var pdf = RestrictedFixture(DenyCopyAndAccessibilityMask, text: "REDACTME NOW");
        var output = TempPath(".pdf");

        var count = Program.RunRedact(pdf, output, "REDACTME", caseSensitive: false,
            allowDecrypt: true);

        count.Should().BeGreaterThan(0, "redaction must work regardless of /P restrictions");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test base directory.");
    }

    private static async Task<CliCaptureResult> RunCliCaptureAsync(string[] args)
    {
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var capturedOut = new StringWriter();
        var capturedErr = new StringWriter();
        Console.SetOut(capturedOut);
        Console.SetError(capturedErr);
        Environment.ExitCode = 0;
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(args);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }

        var processExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        return new CliCaptureResult(
            exitCode == 0 ? processExitCode : exitCode,
            capturedOut.ToString(),
            capturedErr.ToString());
    }

    private sealed record CliCaptureResult(int ExitCode, string StdOut, string StdErr);
}
