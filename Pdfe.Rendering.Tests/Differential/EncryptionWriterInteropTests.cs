using System;
using System.IO;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using Pdfe.Core.Security;
using Pdfe.Core.Writing;
using Pdfe.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Pdfe.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle verification for the PDF Standard Security Handler
/// writer (AES-256, V=5 R=6 — issue #639). Per CLAUDE.md's "a tool must not
/// be its own oracle for the property it exists to guarantee": pdfe
/// confirming pdfe's own output decrypts with pdfe's own decrypt handler
/// (see <c>Pdfe.Core.Tests/Writing/PdfDocumentWriterEncryptionTests.cs</c>)
/// would only prove the encrypt and decrypt halves share the same
/// misunderstanding of the spec, not that either is actually correct.
///
/// These tests instead ask three tools pdfe does not control — qpdf,
/// mutool, and Ghostscript — to independently parse, decrypt, and/or
/// render what <see cref="PdfDocumentWriter"/> produced.
/// </summary>
public class EncryptionWriterInteropTests : IDisposable
{
    private const string MarkerText = "Hello Encrypted World Verification Marker";
    private readonly string _tempDir;

    public EncryptionWriterInteropTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pdfe-enc-interop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    #region qpdf: structural + key-derivation verification

    [Fact]
    public void Qpdf_EmptyPasswords_ReportsR6AesV3AndBothPasswordsValidate()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");

        var path = SaveEncrypted(new PdfEncryptionOptions());

        var show = QpdfReferenceTool.ShowEncryption(path);
        show.Should().NotBeNull();
        show.Should().Contain("R = 6", "qpdf's independent parser must agree this is R=6");
        show.Should().Contain("AESv3", "qpdf's independent parser must agree streams/strings use AES-256");
        // Algorithm 9's owner-chains-to-user construction: an empty owner
        // password paired with an empty user password must let qpdf
        // recognize the supplied (empty) password as BOTH.
        show.Should().Contain("Supplied password is owner password");
        show.Should().Contain("Supplied password is user password");

        var check = QpdfReferenceTool.Check(path);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue($"qpdf --check reported problems:\n{check.Value.Output}");
    }

    [Fact]
    public void Qpdf_Decrypt_WithEmptyPassword_ProducesAReadablePlaintextFile()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");

        var path = SaveEncrypted(new PdfEncryptionOptions());
        var outPath = Path.Combine(_tempDir, "decrypted.pdf");

        var succeeded = QpdfReferenceTool.Decrypt(path, outPath);

        succeeded.Should().BeTrue("qpdf must be able to independently derive the file key and strip /Encrypt");
        File.Exists(outPath).Should().BeTrue();
        QpdfReferenceTool.IsEncrypted(outPath).Should().BeFalse();
    }

    [Fact]
    public void Qpdf_DifferentUserAndOwnerPasswords_BothIndependentlyOpenTheFile()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");

        var path = SaveEncrypted(new PdfEncryptionOptions
        {
            UserPassword = "user-pw-1",
            OwnerPassword = "owner-pw-2",
        });

        var checkUser = QpdfReferenceTool.Check(path, "user-pw-1");
        checkUser.Should().NotBeNull();
        checkUser!.Value.Success.Should().BeTrue($"user password should open the file:\n{checkUser.Value.Output}");

        var checkOwner = QpdfReferenceTool.Check(path, "owner-pw-2");
        checkOwner.Should().NotBeNull();
        checkOwner!.Value.Success.Should().BeTrue($"owner password should also open the file:\n{checkOwner.Value.Output}");

        var showOwner = QpdfReferenceTool.ShowEncryption(path, "owner-pw-2");
        showOwner.Should().Contain("Supplied password is owner password",
            "the owner password must unlock full (owner-level) access, not just user-level");
    }

    [Fact]
    public void Qpdf_NonAsciiPassword_ValidatesAsUtf8()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        const string password = "pâsswörd-日本語-🔒";

        var path = SaveEncrypted(new PdfEncryptionOptions { UserPassword = password });

        var show = QpdfReferenceTool.ShowEncryption(path, password);
        show.Should().Contain("R = 6");
        show.Should().Contain($"User password = {password}");
    }

    [Fact]
    public void Qpdf_ShowEncryption_ReportsPermissionsFromOptions()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");

        // Default Permissions (-4) grants everything while zeroing the two
        // reserved bits — structural plumbing only (#642 owns enforcement),
        // but qpdf's independent parser should still report it correctly.
        var path = SaveEncrypted(new PdfEncryptionOptions());

        var show = QpdfReferenceTool.ShowEncryption(path);
        show.Should().Contain("P = -4");
        show.Should().Contain("modify anything: allowed");
    }

    #endregion

    #region mutool: independent decryption + text extraction

    [Fact]
    public void Mutool_EmptyPassword_ExtractsCorrectText()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");

        var path = SaveEncrypted(new PdfEncryptionOptions());

        var extracted = MutoolTextExtractor.ExtractPage(path, 1, password: null);

        extracted.Should().NotBeNull();
        extracted.Should().Contain(MarkerText);
    }

    [Fact]
    public void Mutool_DifferentUserAndOwnerPasswords_BothExtractTheSameCorrectText()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");

        var path = SaveEncrypted(new PdfEncryptionOptions
        {
            UserPassword = "user-pw-1",
            OwnerPassword = "owner-pw-2",
        });

        var viaUser = MutoolTextExtractor.ExtractPage(path, 1, "user-pw-1");
        var viaOwner = MutoolTextExtractor.ExtractPage(path, 1, "owner-pw-2");

        viaUser.Should().NotBeNull();
        viaUser.Should().Contain(MarkerText);
        viaOwner.Should().NotBeNull();
        viaOwner.Should().Contain(MarkerText);
    }

    [Fact]
    public void Mutool_NonAsciiPassword_ExtractsCorrectText()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");
        const string password = "pâsswörd-日本語-🔒";

        var path = SaveEncrypted(new PdfEncryptionOptions { UserPassword = password });

        var extracted = MutoolTextExtractor.ExtractPage(path, 1, password);

        extracted.Should().NotBeNull();
        extracted.Should().Contain(MarkerText);
    }

    [Fact]
    public void Mutool_WrongPassword_FailsToExtract()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");

        var path = SaveEncrypted(new PdfEncryptionOptions { UserPassword = "correct-password" });

        var extracted = MutoolTextExtractor.ExtractPage(path, 1, "wrong-password");

        // mutool refuses (null) or, if it emits anything at all, must not
        // contain the marker text — either way the wrong password must not
        // recover the real content.
        if (extracted != null)
            extracted.Should().NotContain(MarkerText);
    }

    #endregion

    #region Ghostscript: independent rendering

    [Fact]
    public void Ghostscript_EmptyPassword_RendersIdenticallyToUnencryptedBaseline()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var plainPath = SavePlain();
        var encPath = SaveEncrypted(new PdfEncryptionOptions());

        var baseline = GhostscriptReferenceRenderer.RenderPage(plainPath, 1, dpi: 150, timeoutMs: 30_000, userPassword: null);
        var encrypted = GhostscriptReferenceRenderer.RenderPage(encPath, 1, dpi: 150, timeoutMs: 30_000, userPassword: null);

        baseline.Should().NotBeNull();
        encrypted.Should().NotBeNull();
        MaxChannelDiff(baseline!, encrypted!).Should().BeLessThanOrEqualTo(2,
            "Ghostscript's independent decryption + rendering of the encrypted file must match the unencrypted baseline pixel-for-pixel");
    }

    [Fact]
    public void Ghostscript_OwnerPassword_RendersIdenticallyToUnencryptedBaseline()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var plainPath = SavePlain();
        var encPath = SaveEncrypted(new PdfEncryptionOptions
        {
            UserPassword = "user-pw-1",
            OwnerPassword = "owner-pw-2",
        });

        var baseline = GhostscriptReferenceRenderer.RenderPage(plainPath, 1, dpi: 150, timeoutMs: 30_000, userPassword: null);
        var viaOwner = GhostscriptReferenceRenderer.RenderPage(encPath, 1, dpi: 150, timeoutMs: 30_000, userPassword: "owner-pw-2");

        baseline.Should().NotBeNull();
        viaOwner.Should().NotBeNull();
        MaxChannelDiff(baseline!, viaOwner!).Should().BeLessThanOrEqualTo(2,
            "the owner password must unlock full rendering, matching the unencrypted baseline");
    }

    #endregion

    #region Helpers

    private byte[] BuildDocumentBytes()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, 200);
        var font = PdfFont.Helvetica(12);
        using (var g = page.GetGraphics())
        {
            g.DrawText(MarkerText, font, PdfBrush.Black, new PdfRectangle(20, 20, 280, 180));
        }
        return doc.SaveToBytes();
    }

    private string SavePlain()
    {
        var path = Path.Combine(_tempDir, $"plain-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildDocumentBytes());
        return path;
    }

    private string SaveEncrypted(PdfEncryptionOptions options)
    {
        using var doc = PdfDocument.Open(BuildDocumentBytes());
        var path = Path.Combine(_tempDir, $"enc-{Guid.NewGuid():N}.pdf");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            new PdfDocumentWriter(doc, options).Write(fs);
        }
        return path;
    }

    /// <summary>Maximum per-channel (R/G/B) absolute pixel difference between two same-size bitmaps.</summary>
    private static int MaxChannelDiff(SKBitmap a, SKBitmap b)
    {
        a.Width.Should().Be(b.Width, "renders must be the same size to compare");
        a.Height.Should().Be(b.Height);

        int maxDiff = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                int d = Math.Max(Math.Abs(pa.Red - pb.Red),
                    Math.Max(Math.Abs(pa.Green - pb.Green), Math.Abs(pa.Blue - pb.Blue)));
                if (d > maxDiff) maxDiff = d;
            }
        }
        return maxDiff;
    }

    #endregion
}
