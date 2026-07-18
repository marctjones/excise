using System.IO;
using System.Text;
using AwesomeAssertions;
using Pdfe.Cli;
using Pdfe.Core.Document;
using Xunit;

namespace Pdfe.Cli.Tests;

/// <summary>
/// Tests for the <c>pdfe redact</c> subcommand. Exercises both the
/// internal <see cref="Program.RunRedact"/> core and the CLI surface
/// (<see cref="Program.RunAsync"/>) so we catch regressions in either
/// the argument parser or the redaction pipeline itself.
/// </summary>
public class RedactCommandTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private string TempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfe-cli-test-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void RunRedact_RemovesExactMatch_FromContentStream()
    {
        // HELLO WORLD → redact WORLD
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = Program.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false);

        count.Should().Be(1);

        // The security guarantee: raw content-stream bytes of the output
        // must not contain WORLD. This is the "pdftotext can't recover
        // it" property — structural removal, not visual overlay.
        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().NotContain("WORLD");
        raw.Should().Contain("HELLO", "the non-redacted word must survive");
    }

    [Fact]
    public void RunRedact_NoMatch_ReturnsZero_AndOutputExists()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = Program.RunRedact(inputPath, outputPath, "BANANA", caseSensitive: false);

        count.Should().Be(0);
        File.Exists(outputPath).Should().BeTrue("output is always written even when no matches found");

        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().Contain("HELLO");
        raw.Should().Contain("WORLD");
    }

    [Fact]
    public void RunRedact_CaseInsensitive_MatchesDifferentCase()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = Program.RunRedact(inputPath, outputPath, "world", caseSensitive: false);

        count.Should().Be(1);
        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().NotContain("WORLD");
    }

    [Fact]
    public void RunRedact_CaseSensitive_DoesNotMatchDifferentCase()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = Program.RunRedact(inputPath, outputPath, "world", caseSensitive: true);

        count.Should().Be(0, "case-sensitive search must not match an all-caps word");
    }

    [Fact]
    public async Task RunAsync_RedactSubcommand_EndToEnd_ProducesRedactedOutput()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("SECRET DATA"));

        // Redirect stdout so the "Redacted N occurrence(s)" noise doesn't
        // leak into the xunit output.
        var prevOut = Console.Out;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "SECRET"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        exitCode.Should().Be(0);
        capturedOut.ToString().Should().Contain("Redacted 1 occurrence(s) of 'SECRET'");

        File.Exists(outputPath).Should().BeTrue();
        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().NotContain("SECRET");
        raw.Should().Contain("DATA");
    }

    [Fact]
    public async Task RunAsync_RedactSubcommand_InputDoesNotExist_ReportsError()
    {
        var outputPath = TempPath(".pdf");
        var prevErr = Console.Error;
        var capturedErr = new StringWriter();
        Console.SetError(capturedErr);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "redact", "/tmp/pdfe-does-not-exist-xyz.pdf", outputPath, "SECRET"
            });
        }
        finally
        {
            Console.SetError(prevErr);
        }

        // System.CommandLine invokes the handler (exit code 0) after our
        // explicit Environment.ExitCode=1, but what matters to the user
        // is the error message and that no output file was written.
        capturedErr.ToString().Should().Contain("File not found");
        File.Exists(outputPath).Should().BeFalse();
    }

    /// <summary>
    /// --allow-decrypt defaults to false and must never affect a redaction
    /// of an unencrypted source — the flag only matters when the source
    /// carries encryption to preserve or drop (#638/#643).
    /// </summary>
    [Fact]
    public void RunRedact_UnencryptedSource_AllowDecryptFalse_StillSucceeds()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = Program.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false, allowDecrypt: false);

        count.Should().Be(1);
        File.Exists(outputPath).Should().BeTrue();

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.IsEncrypted.Should().BeFalse("an unencrypted source must stay unencrypted");
    }

    /// <summary>
    /// #643's security property, replacing #638's fail-closed gate: an
    /// encrypted source redacts into an ENCRYPTED copy by default — same
    /// permissions, same (here: empty) password — instead of failing until
    /// the caller opts into decryption.
    /// </summary>
    [Fact]
    public void RunRedact_EncryptedSource_Default_ReEncryptsWithSamePermissions()
    {
        var inputPath = WriteEncryptedFixture("HELLO WORLD", password: null, permissions: -3392);
        var outputPath = TempPath(".pdf");

        var prevErr = Console.Error;
        var capturedErr = new StringWriter();
        Console.SetError(capturedErr);
        int count;
        try
        {
            count = Program.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false);
        }
        finally
        {
            Console.SetError(prevErr);
        }

        count.Should().Be(1);
        capturedErr.ToString().Should().Contain("re-encrypted",
            "the default preservation behavior should be stated, not silent");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.IsEncrypted.Should().BeTrue(
            "redacting a password-protected PDF must yield a password-protected PDF (#643)");
        reopened.Permissions.RawValue.Should().Be(-3392, "the source /P mask must survive");
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value)).Should().NotContain("WORLD");
    }

    /// <summary>
    /// #643: a non-empty-password source needs --password to open at all;
    /// the output is then re-encrypted with that same password.
    /// </summary>
    [Fact]
    public void RunRedact_EncryptedSource_WithPassword_ReEncryptsWithThatPassword()
    {
        var inputPath = WriteEncryptedFixture("HELLO WORLD", password: "pw123");
        var outputPath = TempPath(".pdf");

        int count = Program.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false, password: "pw123");

        count.Should().Be(1);

        var withoutPassword = () => PdfDocument.Open(File.ReadAllBytes(outputPath));
        withoutPassword.Should().Throw<Pdfe.Core.Parsing.PdfEncryptionNotSupportedException>(
            "the redacted output must still require the source's password");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath), "pw123");
        reopened.IsEncrypted.Should().BeTrue();
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value)).Should().NotContain("WORLD");
    }

    /// <summary>
    /// #643 flipped --allow-decrypt's meaning: preservation is the default,
    /// so the flag is now the explicit opt-OUT that writes an unprotected
    /// copy (under #638 it was the opt-in required to proceed at all).
    /// </summary>
    [Fact]
    public void RunRedact_EncryptedSource_WithAllowDecrypt_WritesPlaintextAndWarns()
    {
        var inputPath = WriteEncryptedFixture("HELLO WORLD", password: null);
        var outputPath = TempPath(".pdf");

        var prevErr = Console.Error;
        var capturedErr = new StringWriter();
        Console.SetError(capturedErr);
        try
        {
            Program.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false, allowDecrypt: true);
        }
        finally
        {
            Console.SetError(prevErr);
        }

        File.Exists(outputPath).Should().BeTrue();
        capturedErr.ToString().Should().Contain("output will NOT be encrypted");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.IsEncrypted.Should().BeFalse("--allow-decrypt is the explicit opt-out that drops protection");
    }

    /// <summary>
    /// Writes a REAL pdfe-writer-encrypted copy of a simple one-page fixture
    /// (unlike <see cref="TestPdfBuilder.EncryptedSinglePageEmptyPassword"/>,
    /// whose content stream is not actually per-object encrypted), so
    /// redaction, re-encryption, and reopening all behave like production.
    /// </summary>
    private string WriteEncryptedFixture(string text, string? password, long permissions = -4)
    {
        var path = TempPath(".pdf");
        using var doc = PdfDocument.Open(TestPdfBuilder.SinglePage(text));
        File.WriteAllBytes(path, doc.SaveToBytes(new Pdfe.Core.Security.PdfEncryptionOptions
        {
            UserPassword = password,
            OwnerPassword = password,
            Permissions = permissions,
        }));
        return path;
    }

    [Fact]
    public async Task RunAsync_RedactSubcommand_AllowDecryptFlag_IsRecognized()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("SECRET DATA"));

        var prevOut = Console.Out;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "SECRET", "--allow-decrypt"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        // The flag is a no-op on an unencrypted source; this asserts
        // System.CommandLine accepts it (an unknown option would report a
        // parse error and a non-zero/empty result) and the redaction still
        // runs normally.
        exitCode.Should().Be(0);
        capturedOut.ToString().Should().Contain("Redacted 1 occurrence(s) of 'SECRET'");
        File.Exists(outputPath).Should().BeTrue();
    }

    /// <summary>
    /// #643: `pdfe redact --password` end-to-end — opens a
    /// password-protected source and re-encrypts the output with the same
    /// password by default.
    /// </summary>
    [Fact]
    public async Task RunAsync_RedactSubcommand_PasswordOption_OpensAndReEncrypts()
    {
        var inputPath = WriteEncryptedFixture("SECRET DATA", password: "pw123");
        var outputPath = TempPath(".pdf");

        var prevOut = Console.Out;
        var prevErr = Console.Error;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        Console.SetError(new StringWriter());
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "SECRET", "--password", "pw123"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }

        exitCode.Should().Be(0);
        capturedOut.ToString().Should().Contain("Redacted 1 occurrence(s) of 'SECRET'");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath), "pw123");
        reopened.IsEncrypted.Should().BeTrue("the output must stay protected by the same password (#643)");
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value)).Should().NotContain("SECRET");
    }

    [Fact]
    public void RunRedact_MultipleMatches_AllRemoved()
    {
        // Three copies of the target on one line. The surrounding test
        // string uses wide spacing so each TARGET's bounding box doesn't
        // brush the neighbouring glyphs (the default AnyOverlap strategy
        // would otherwise catch adjacent characters).
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("TARGET TARGET TARGET"));

        int count = Program.RunRedact(inputPath, outputPath, "TARGET", caseSensitive: false);

        count.Should().Be(3);
        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().NotContain("TARGET",
            "all three occurrences must be removed from the content stream");
    }
}
