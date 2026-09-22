using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Cli;
using Excise.Cli.Commands;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// Tests for the <c>excise redact</c> subcommand. Exercises both the
/// typed <see cref="RedactCommandHandler"/> core and the CLI surface
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

        var (_, notes) = RedactCommandTestDriver.RunRedactWithNotes(input, output, "Ng", caseSensitive: false);

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

        var (_, notes) = RedactCommandTestDriver.RunRedactWithNotes(input, output, "Confidential", caseSensitive: false);

        notes.Should().BeEmpty(
            "no bookmarks, no annotation text, and the term is above the floor — there is " +
            "nothing excise failed to examine");
    }

    /// <summary>
    /// ⚠️ <b>This test asserted the OPPOSITE until 2026-09-17.</b> It pinned
    /// "CLI term redaction is surgical; GUI-style wholesale safe-share metadata
    /// removal would be a compatibility-breaking policy change" — and #1586 is
    /// that policy change, decided deliberately: the CLI, batch, scripting and
    /// library paths now match the GUI safe copy, because a redaction whose
    /// thoroughness depends on which front end you used is the #896 failure
    /// with a different carrier.
    ///
    /// <para>What it buys: a producer may put text under ANY <c>/Info</c> key
    /// (§14.3.3), so a targeted scrub must know names it cannot know — #1583's
    /// <c>/CaseName</c> trap survived the targeted scrub and does not survive
    /// the strip.</para>
    ///
    /// <para>The opt-out is <c>RedactionOptions.StripDocumentMetadata =
    /// false</c>, which is what the second half of this test pins.</para>
    /// </summary>
    [Fact]
    public void RunRedactWithNotes_StripsDocumentMetadataUnderTheStandardProfile()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        byte[] sourceBytes;
        using (var source = PdfDocument.Open(
                   TestPdfBuilder.SinglePage("Confidential content here")))
        {
            source.SetTitle("Public quarterly report");
            sourceBytes = source.SaveToBytes();
        }
        File.WriteAllBytes(input, sourceBytes);

        var (count, notes) = RedactCommandTestDriver.RunRedactWithNotes(
            input,
            output,
            "Confidential",
            caseSensitive: false);

        count.Should().Be(1);
        notes.Should().BeEmpty();
        using (var redacted = PdfDocument.Open(output))
        {
            redacted.Title.Should().BeNullOrEmpty(
                "#1586: the Standard profile strips /Info and XMP wholesale on every path, " +
                "so the CLI output matches the GUI safe copy");
        }

        // The opt-out, and the planted failure: with the strip off the title
        // survives, which proves the fixture really had one and that the first
        // assertion is not passing because SetTitle did nothing.
        var keptOutput = TempPath(".pdf");
        using (var doc = PdfDocument.Open(input))
        {
            doc.RedactText("Confidential", Excise.Core.Text.Segmentation.RedactionOptions.Default
                with { StripDocumentMetadata = false });
            doc.Save(keptOutput);
        }
        using (var kept = PdfDocument.Open(keptOutput))
        {
            kept.Title.Should().Be("Public quarterly report",
                "StripDocumentMetadata: false restores the surgical term-scrub behaviour");
        }
    }

    [Fact]
    public void RunRedact_RemovesExactMatch_FromContentStream()
    {
        // HELLO WORLD → redact WORLD
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false);

        count.Should().Be(1);

        // NECESSARY, BUT BLIND (CLAUDE.md): reads only the content stream
        // through excise's own parser -- excise vouching for excise.
        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().NotContain("WORLD");
        raw.Should().Contain("HELLO", "the non-redacted word must survive");

        // The actual "pdftotext can't recover it" property: a carrier-agnostic
        // scan of the SAVED, compressed bytes (#1049/t0-gates review 2026-09-21)
        // — this comment used to claim that property while only the excise-read
        // assertion above ran, and pdftotext was never invoked.
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "WORLD").Should().BeEmpty();
    }

    [Fact]
    public void RunRedact_SameInputAndOutputPath_RemovesExactMatch()
    {
        var path = TempPath(".pdf");
        File.WriteAllBytes(path, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = RedactCommandTestDriver.RunRedact(path, path, "WORLD", caseSensitive: false);

        count.Should().Be(1);
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().NotContain("WORLD",
            "same-path redaction relies on #918's byte-backed open path and must not regress to a held FileStream");
        raw.Should().Contain("HELLO");

        // Carrier-agnostic corroboration on the saved bytes, not just excise's
        // own reader (t0-gates review 2026-09-21).
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(path), "WORLD").Should().BeEmpty();
    }

    [Fact]
    public void RunRedact_OcrImageText_RemovesASecretBakedOnlyIntoPixels()
    {
        Assert.SkipUnless(new PdfOcrService().IsAvailable(), "tesseract not installed");
        // #1706 — the shared locator, not a hand-rolled walk to .git.
        var input = TestRepoLayout.FindFile(
            "test-pdfs", "redaction-adversarial", "image-baked-text--IMAGEBAKEDSECRET.pdf");
        Assert.SkipWhen(input == null, TestRepoLayout.AbsenceReason(
            "image-baked-text redaction fixture",
            "test-pdfs/redaction-adversarial/image-baked-text--IMAGEBAKEDSECRET.pdf"));
        var output = TempPath(".pdf");

        RedactCommandTestDriver.RunRedact(input!, output, "IMAGEBAKEDSECRET", caseSensitive: false,
            ocrImageText: true).Should().Be(1,
                "OCR must locate the image-only term before the normal structural and image-redaction path runs (#1186)");

        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), "IMAGEBAKEDSECRET").Should().BeEmpty(
            "the temporary invisible OCR layer must not leave the target in saved bytes");
        using var redacted = PdfDocument.Open(output);
        new PdfOcrService().RecognizePage(redacted.GetPage(1)).Text.Should().NotContain("IMAGEBAKEDSECRET",
            "the baked pixels must be altered too, not merely the temporary OCR layer");
    }

    [Fact]
    public async Task RunAsync_RedactFlattenOcr_ReplacesTheSourceWithCleanImageOnlyOutput()
    {
        Assert.SkipUnless(new PdfOcrService().IsAvailable(), "tesseract not installed");
        // #1706 — the shared locator, not a hand-rolled walk to .git.
        var input = TestRepoLayout.FindFile(
            "test-pdfs", "redaction-adversarial", "image-baked-text--IMAGEBAKEDSECRET.pdf");
        Assert.SkipWhen(input == null, TestRepoLayout.AbsenceReason(
            "image-baked-text redaction fixture",
            "test-pdfs/redaction-adversarial/image-baked-text--IMAGEBAKEDSECRET.pdf"));
        var output = TempPath(".pdf");

        (await Program.RunAsync(new[] { "redact", input!, output, "IMAGEBAKEDSECRET", "--flatten-ocr" })).Should().Be(0);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), "IMAGEBAKEDSECRET").Should().BeEmpty();
        using var doc = PdfDocument.Open(output);
        doc.GetPage(1).Text.Should().BeEmpty("the flattened output intentionally has no text layer");
        new PdfOcrService().RecognizePage(doc.GetPage(1)).Text.Should().NotContain("IMAGEBAKEDSECRET",
            "the source pixels must be altered, not merely omitted from extraction");
    }

    [Fact]
    public async Task RunAsync_RedactFlattenOcr_NoMatch_FailsWithoutWritingOutput()
    {
        Assert.SkipUnless(new PdfOcrService().IsAvailable(), "tesseract not installed");
        // #1706 — the shared locator, not a hand-rolled walk to .git.
        var input = TestRepoLayout.FindFile(
            "test-pdfs", "redaction-adversarial", "image-baked-text--IMAGEBAKEDSECRET.pdf");
        Assert.SkipWhen(input == null, TestRepoLayout.AbsenceReason(
            "image-baked-text redaction fixture",
            "test-pdfs/redaction-adversarial/image-baked-text--IMAGEBAKEDSECRET.pdf"));
        var output = TempPath(".pdf");

        (await Program.RunAsync(new[] { "redact", input!, output, "NOTPRESENT", "--flatten-ocr" })).Should().NotBe(0);
        File.Exists(output).Should().BeFalse("a failed image-only redaction must not create an unredacted output");
    }

    [Fact]
    public async Task RunAsync_RedactFlattenOcr_EncryptedSource_PreservesPasswordProtection()
    {
        Assert.SkipUnless(new PdfOcrService().IsAvailable(), "tesseract not installed");
        var input = WriteEncryptedFixture("IMAGEBAKEDSECRET", password: "flatten-pw");
        var output = TempPath(".pdf");

        (await Program.RunAsync(new[]
        {
            "redact", input, output, "IMAGEBAKEDSECRET", "--flatten-ocr", "--password", "flatten-pw"
        })).Should().Be(0);

        var withoutPassword = () => PdfDocument.Open(File.ReadAllBytes(output));
        withoutPassword.Should().Throw<Excise.Core.Parsing.PdfEncryptionNotSupportedException>();
        using var redacted = PdfDocument.Open(File.ReadAllBytes(output), "flatten-pw");
        redacted.IsEncrypted.Should().BeTrue("image-only redaction must preserve source protection by default");
        redacted.GetPage(1).Text.Should().BeEmpty();
    }

    [Fact]
    public void RunRedact_NoMatch_ReturnsZero_AndOutputExists()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "BANANA", caseSensitive: false);

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

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "world", caseSensitive: false);

        count.Should().Be(1);
        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().NotContain("WORLD");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "WORLD").Should().BeEmpty();
    }

    [Fact]
    public void RunRedact_CaseSensitive_DoesNotMatchDifferentCase()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO WORLD"));

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "world", caseSensitive: true);

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
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "SECRET").Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_RedactProgress_WritesPageBasedOverallPercentToStderr()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("SECRET DATA"));

        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var capturedErr = new StringWriter();
        Console.SetOut(new StringWriter());
        Console.SetError(capturedErr);
        try
        {
            (await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "SECRET", "--progress"
            })).Should().Be(0);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }

        capturedErr.ToString().Should().Contain("Progress: 0% (0/1 pages)")
            .And.Contain("Progress: 100% (1/1 pages)");
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

        exitCode.Should().Be(1);
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

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false, allowDecrypt: false);

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
            count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false);
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

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false, password: "pw123");

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
            RedactCommandTestDriver.RunRedact(inputPath, outputPath, "WORLD", caseSensitive: false, allowDecrypt: true);
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

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "TARGET", caseSensitive: false);

        count.Should().Be(3);
        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        var raw = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        raw.Should().NotContain("TARGET",
            "all three occurrences must be removed from the content stream");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "TARGET").Should().BeEmpty();
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

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "SECRET", caseSensitive: false, drawBox: false);

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

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "SECRET", caseSensitive: false);

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

        int count = RedactCommandTestDriver.RunRedact(inputPath, outputPath, "SECRET", caseSensitive: false,
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
        RedactCommand.TryParseBoxColor(spec, out var color, out var error).Should().BeTrue();
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
        RedactCommand.TryParseBoxColor(spec, out var color, out var error).Should().BeFalse();
        color.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("uri=remove-whole", Excise.Core.Operations.RedactionCarriers.ActionUris,
        Excise.Core.Operations.CarrierScrubMode.RemoveWhole)]
    [InlineData("INFO=Report-Only", Excise.Core.Operations.RedactionCarriers.Info,
        Excise.Core.Operations.CarrierScrubMode.ReportOnly)]
    [InlineData("outlines=strip", Excise.Core.Operations.RedactionCarriers.Outlines,
        Excise.Core.Operations.CarrierScrubMode.Strip)]
    public void TryParseCarrierPolicy_AcceptsCarrierEqualsMode(
        string spec,
        Excise.Core.Operations.RedactionCarriers carrier,
        Excise.Core.Operations.CarrierScrubMode mode)
    {
        RedactCommand.TryParseCarrierPolicy(new[] { spec }, out var policy, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        policy.ModeFor(carrier).Should().Be(mode);
    }

    [Theory]
    [InlineData("--close-width")]
    [InlineData("--no-box")]
    public async Task RunAsync_Redact_OvershootBox_RejectsContradictoryFlags(string other)
    {
        // #1189: --close-width draws no box (#1140) and --no-box draws no box,
        // so there is nothing to widen. A redaction tool must never silently
        // ignore a flag the user passed.
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO SECRET"));

        var prevErr = Console.Error;
        Console.SetError(new StringWriter());
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "SECRET", "--overshoot-box", other
            });
        }
        finally
        {
            Console.SetError(prevErr);
        }

        exitCode.Should().Be(1);
        File.Exists(outputPath).Should().BeFalse("the run was rejected before writing");
    }

    [Fact]
    public async Task RunAsync_Redact_OvershootBox_EndToEnd_StillRemovesTheText()
    {
        // The full CLI path for --overshoot-box. The box policy is cosmetic;
        // glyph removal is unconditional and must stay so.
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO SECRET WORLD"));

        var prevOut = Console.Out;
        Console.SetOut(new StringWriter());
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "SECRET", "--overshoot-box"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        exitCode.Should().Be(0);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "SECRET").Should().BeEmpty();
        AppendedFillBoxColors(outputPath).Should().NotBeEmpty("overshoot still draws a box");
    }

    [Fact]
    public async Task RunAsync_Redact_WholeWordFlag_EndToEnd_SparesTheLongerWord()
    {
        // #1052 through the real CLI path: option registration -> request ->
        // RedactionOptions.WholeWord -> FindTextMatches. A transposed argument
        // would pass every unit test while --whole-word silently gutted
        // "Sleeman" -- exactly the "plumbing exists but is mis-wired" gap worth
        // pinning on a redaction tool.
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("Sleeman met Lee"));

        var prevOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "Lee", "--whole-word"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        exitCode.Should().Be(0);
        using var redacted = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(outputPath));
        var text = redacted.GetPage(1).Text;
        text.Should().Contain("Sleeman", "whole-word matching spares the longer word");
        captured.ToString().Should().Contain("whole-word matching",
            "#1052: the rule that ran must be visible in the result");
    }

    [Fact]
    public void TryParseCarrierPolicy_LeavesUnnamedCarriersAtStrip()
    {
        RedactCommand.TryParseCarrierPolicy(
            new[] { "uri=remove-whole" }, out var policy, out _).Should().BeTrue();
        policy.ModeFor(Excise.Core.Operations.RedactionCarriers.Info)
            .Should().Be(Excise.Core.Operations.CarrierScrubMode.Strip,
                "only the carrier the user named changes");
    }

    [Theory]
    [InlineData("uri")]                 // no '='
    [InlineData("bookmarks=strip")]     // unknown carrier
    [InlineData("uri=delete")]          // unknown mode
    [InlineData("uri=remove-whole=x")]  // malformed
    public void TryParseCarrierPolicy_RejectsBadSpec_RatherThanIgnoringIt(string spec)
    {
        // A silently ignored spec is a leak the user cannot see: they believe
        // they asked for remove-whole and got the revealing strip residue.
        RedactCommand.TryParseCarrierPolicy(new[] { spec }, out _, out var error)
            .Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunAsync_Redact_NoBoxFlag_EndToEnd_RemovesTextAndDrawsNoBox()
    {
        // Exercises the full CLI path: option registration -> GetValue(noBox)
        // -> drawBox: !noBox -> RunRedactWithNotes -> RedactText. A transposed
        // argument here would pass the RunRedact-level tests while --no-box
        // silently drew a box, which on a redaction tool is exactly the kind of
        // "the plumbing exists but is mis-wired" gap worth pinning.
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO SECRET"));

        var prevOut = Console.Out;
        Console.SetOut(new StringWriter());
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "SECRET", "--no-box"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        exitCode.Should().Be(0);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "SECRET").Should().BeEmpty();
        AppendedFillBoxColors(outputPath).Should().BeEmpty("--no-box draws no covering rectangle");
    }

    [Fact]
    public async Task RunAsync_Redact_BoxColorRgb_EndToEnd_DrawsThatColor()
    {
        // The full CLI path for --box-color, including TryParseBoxColor being
        // fed the flag's value and the parsed color reaching the box.
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("HELLO SECRET"));

        var prevOut = Console.Out;
        Console.SetOut(new StringWriter());
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "redact", inputPath, outputPath, "SECRET", "--box-color", "255,0,0"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        exitCode.Should().Be(0);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "SECRET").Should().BeEmpty();
        var boxes = AppendedFillBoxColors(outputPath);
        boxes.Should().NotBeEmpty();
        boxes.Should().OnlyContain(c => c.R == 1 && c.G == 0 && c.B == 0, "255,0,0 -> rg 1 0 0");
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
