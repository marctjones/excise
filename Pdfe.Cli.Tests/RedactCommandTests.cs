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
    /// #638: --allow-decrypt defaults to false but must never block a
    /// redaction of an unencrypted source — the flag only matters when
    /// pdfe would otherwise silently drop the source's encryption.
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
    }

    /// <summary>
    /// #638's actual security property: an encrypted source must not be
    /// silently redacted into an unprotected copy. Uses a hand-built
    /// empty-password-encrypted fixture (see
    /// <see cref="TestPdfBuilder.EncryptedSinglePageEmptyPassword"/>) since
    /// no such fixture exists in the checked-in corpus and pdfe cannot
    /// write one itself (#624).
    /// </summary>
    [Fact]
    public void RunRedact_EncryptedSource_WithoutAllowDecrypt_ThrowsAndWritesNoOutput()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.EncryptedSinglePageEmptyPassword());

        var act = () => Program.RunRedact(inputPath, outputPath, "SECRET", caseSensitive: false, allowDecrypt: false);

        act.Should().Throw<Program.PdfWouldLoseEncryptionException>()
            .WithMessage("*--allow-decrypt*");
        File.Exists(outputPath).Should().BeFalse("no output must be written when the encryption-loss guard fires");
    }

    /// <summary>
    /// With --allow-decrypt, the guard must not fire — RunRedact proceeds
    /// to open/redact/save rather than throwing before it gets there. The
    /// fixture's content stream is plaintext (see
    /// <see cref="TestPdfBuilder.EncryptedSinglePageEmptyPassword"/>'s
    /// remarks — building genuinely per-object-encrypted content is #624's
    /// concern, not this guard's), so pdfe's decrypt-on-read pass garbles it
    /// and the match count is not asserted here; what this proves is that
    /// allowDecrypt:true reaches RedactText/Save at all instead of throwing.
    /// </summary>
    [Fact]
    public void RunRedact_EncryptedSource_WithAllowDecrypt_ProceedsAndWarns()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.EncryptedSinglePageEmptyPassword());

        var prevErr = Console.Error;
        var capturedErr = new StringWriter();
        Console.SetError(capturedErr);
        try
        {
            var act = () => Program.RunRedact(inputPath, outputPath, "SECRET", caseSensitive: false, allowDecrypt: true);
            act.Should().NotThrow<Program.PdfWouldLoseEncryptionException>(
                "allowDecrypt:true must bypass the encryption-loss guard");
        }
        finally
        {
            Console.SetError(prevErr);
        }

        File.Exists(outputPath).Should().BeTrue();
        capturedErr.ToString().Should().Contain("output will NOT be encrypted");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.IsEncrypted.Should().BeFalse("--allow-decrypt proceeds, and pdfe cannot write encrypted output (#624)");
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
