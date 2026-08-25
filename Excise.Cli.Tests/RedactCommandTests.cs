using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Cli;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// Tests for the <c>excise redact</c> subcommand. Exercises both the
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
        var path = Path.Combine(Path.GetTempPath(), $"excise-cli-test-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// #905 — a term below the scrub floor is redacted from page content but
    /// NOT from document metadata. That asymmetry was silent; the CLI must now
    /// say so, because an unattended run has no other channel.
    /// </summary>
    [Fact]
    public void RunRedactWithNotes_TermBelowTheScrubFloor_ReportsThatMetadataWasNotScrubbed()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, TestPdfBuilder.SinglePage("Ng and other content"));

        var (_, notes) = Program.RunRedactWithNotes(input, output, "Ng", caseSensitive: false);

        notes.Should().Contain(n => n.Contains("'Ng'") && n.Contains("metadata"),
            "page content is redacted but the sanitizer's 3-character floor skips carriers — " +
            "an under-redaction the user has no way to discover otherwise (#905)");
    }

    /// <summary>
    /// The control: a normal term on a document with nothing unexaminable must
    /// produce NO notes. A warning that always fires is one people stop reading.
    /// </summary>
    [Fact]
    public void RunRedactWithNotes_OrdinaryTerm_PlainDocument_ReportsNothing()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, TestPdfBuilder.SinglePage("Confidential content here"));

        var (_, notes) = Program.RunRedactWithNotes(input, output, "Confidential", caseSensitive: false);

        notes.Should().BeEmpty(
            "no bookmarks, no annotation text, and the term is above the floor — there is " +
            "nothing excise failed to examine");
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
    public void RunRedact_SameInputAndOutputPath_RemovesExactMatch()
    {
        var path = TempPath(".pdf");
        File.WriteAllBytes(path, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = Program.RunRedact(path, path, "WORLD", caseSensitive: false);

        count.Should().Be(1);
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().NotContain("WORLD",
            "same-path redaction relies on #918's byte-backed open path and must not regress to a held FileStream");
        raw.Should().Contain("HELLO");
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
                "redact", "/tmp/excise-does-not-exist-xyz.pdf", outputPath, "SECRET"
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
        withoutPassword.Should().Throw<Excise.Core.Parsing.PdfEncryptionNotSupportedException>(
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
    /// Writes a REAL excise-writer-encrypted copy of a simple one-page fixture
    /// (unlike <see cref="TestPdfBuilder.EncryptedSinglePageEmptyPassword"/>,
    /// whose content stream is not actually per-object encrypted), so
    /// redaction, re-encryption, and reopening all behave like production.
    /// </summary>
    private string WriteEncryptedFixture(string text, string? password, long permissions = -4)
    {
        var path = TempPath(".pdf");
        using var doc = PdfDocument.Open(TestPdfBuilder.SinglePage(text));
        File.WriteAllBytes(path, doc.SaveToBytes(new Excise.Core.Security.PdfEncryptionOptions
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
    /// #643: `excise redact --password` end-to-end — opens a
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

    // ---------------------------------------------------------------------
    // #1158 — --no-box / --box-color.
    //
    // The covering rectangle is COSMETIC. --no-box must never weaken content
    // removal — it only skips the visual box. The fixture's base content stream
    // is pure text (BT/Tf/Td/Tj/ET, see TestPdfBuilder), so any fill rectangle
    // in the output is one AppendBlackRectangle added and nothing else.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Count the RGB-fill covering rectangles (an rg color op followed by a
    /// rectangle and a fill) that redaction appended to a saved page, and the
    /// color each was drawn with. On the text-only fixture the base stream has
    /// none, so a non-empty result is exactly the boxes redaction added.
    /// </summary>
    private static IReadOnlyList<(double R, double G, double B)> AppendedFillBoxColors(string pdfPath)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(pdfPath));
        var ops = doc.GetPage(1).GetContentStream().Operators;
        var boxes = new List<(double, double, double)>();
        (double R, double G, double B)? pendingRgb = null;
        var sawRect = false;
        foreach (var op in ops)
        {
            switch (op.Name)
            {
                case "rg":
                    pendingRgb = (op.Operands[0].GetNumber(), op.Operands[1].GetNumber(), op.Operands[2].GetNumber());
                    sawRect = false;
                    break;
                case "re":
                    sawRect = true;
                    break;
                case "f":
                case "F":
                case "f*":
                    if (pendingRgb != null && sawRect)
                        boxes.Add(pendingRgb.Value);
                    break;
            }
        }
        return boxes;
    }

    [Fact]
    public void RunRedact_NoBox_RemovesTextFromSavedBytes_AndDrawsNoRectangle()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO SECRET"));

        int count = Program.RunRedact(inputPath, outputPath, "SECRET", caseSensitive: false, drawBox: false);

        count.Should().Be(1);

        // (a) The SECURITY invariant: --no-box must NOT weaken removal. Search
        //     the SAVED bytes, including inside compressed streams, in every
        //     carrier. A tool must not be its own oracle for the property it
        //     exists to guarantee, but this scanner reads the file directly
        //     (ZLibStream, not excise's own filters) — CLAUDE.md carrier #1.
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "SECRET")
            .Should().BeEmpty("--no-box removes the box, never the content");

        // (b) No covering rectangle was drawn.
        AppendedFillBoxColors(outputPath).Should().BeEmpty(
            "--no-box means no fill rectangle over the redacted region");
    }

    [Fact]
    public void RunRedact_DefaultBox_DrawsBlackRectangle_AndRemovesText()
    {
        // The control for the test above: default behaviour is UNCHANGED — a
        // black box is still drawn. This pins that --no-box did not silently
        // become the default.
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO SECRET"));

        int count = Program.RunRedact(inputPath, outputPath, "SECRET", caseSensitive: false);

        count.Should().Be(1);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "SECRET").Should().BeEmpty();

        var boxes = AppendedFillBoxColors(outputPath);
        boxes.Should().NotBeEmpty("the default still draws a covering box");
        boxes.Should().OnlyContain(c => c.R == 0 && c.G == 0 && c.B == 0, "default box is black");
    }

    [Fact]
    public void RunRedact_BoxColorWhite_DrawsWhiteRectangle_AndRemovesText()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO SECRET"));

        int count = Program.RunRedact(inputPath, outputPath, "SECRET", caseSensitive: false,
            drawBox: true, boxColor: (1.0, 1.0, 1.0));

        count.Should().Be(1);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "SECRET").Should().BeEmpty(
            "a white box is still a redaction — the glyphs are gone, not merely hidden");

        var boxes = AppendedFillBoxColors(outputPath);
        boxes.Should().NotBeEmpty();
        boxes.Should().OnlyContain(c => c.R == 1 && c.G == 1 && c.B == 1, "box color white -> rg 1 1 1");
    }

    [Theory]
    [InlineData("black", 0.0, 0.0, 0.0)]
    [InlineData("white", 1.0, 1.0, 1.0)]
    [InlineData("BLACK", 0.0, 0.0, 0.0)]     // case-insensitive
    [InlineData("255,0,0", 1.0, 0.0, 0.0)]
    [InlineData("0, 255, 0", 0.0, 1.0, 0.0)] // whitespace tolerated
    [InlineData("128,128,128", 128 / 255.0, 128 / 255.0, 128 / 255.0)]
    public void TryParseBoxColor_AcceptsNamedAndRgb(string spec, double r, double g, double b)
    {
        Program.TryParseBoxColor(spec, out var color, out var error).Should().BeTrue();
        error.Should().BeNull();
        color.Should().NotBeNull();
        color!.Value.R.Should().BeApproximately(r, 1e-9);
        color.Value.G.Should().BeApproximately(g, 1e-9);
        color.Value.B.Should().BeApproximately(b, 1e-9);
    }

    [Theory]
    [InlineData("red")]          // unknown name
    [InlineData("1,2")]          // too few components
    [InlineData("1,2,3,4")]      // too many components
    [InlineData("300,0,0")]      // out of 0-255 range
    [InlineData("-1,0,0")]       // negative
    [InlineData("1.5,0,0")]      // non-integer
    [InlineData("a,b,c")]        // non-numeric
    public void TryParseBoxColor_RejectsBadSpec(string spec)
    {
        Program.TryParseBoxColor(spec, out var color, out var error).Should().BeFalse();
        color.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunAsync_Redact_NoBoxAndBoxColorTogether_IsRejected()
    {
        // A redaction tool must never silently ignore a flag the user passed.
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO SECRET"));

        var prevErr = Console.Error;
        var capturedErr = new StringWriter();
        Console.SetError(capturedErr);
        try
        {
            await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "SECRET", "--no-box", "--box-color", "white"
            });
        }
        finally
        {
            Console.SetError(prevErr);
        }

        capturedErr.ToString().Should().Contain("mutually exclusive");
        File.Exists(outputPath).Should().BeFalse("a rejected invocation writes no output");
        Environment.ExitCode = 0;
    }
}
