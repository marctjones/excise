using System;
using System.Collections.Generic;
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
/// THE encryption interop gate (#644, closing the #624 epic): the encryption
/// writer is not "done" until this suite is green. Where
/// <see cref="EncryptionWriterInteropTests"/> grew organically per writer
/// issue (#639/#640) and <see cref="EncryptionPreservationInteropTests"/>
/// covers the #643 round-trip, this file is the deliberate, systematic
/// matrix: for BOTH algorithms pdfe can write (AES-256/R6 and AES-128/R4),
/// against every scriptable independent reader on the reference-tool bench:
///
/// <code>
///                       mutool   qpdf   Ghostscript   pdftoppm
///  correct user pw        ✓       ✓         ✓            ✓
///  owner pw (full auth)   ✓       ✓         —            ✓
///  wrong pw REJECTED      ✓       ✓         ✓            ✓
///  NO pw REJECTED         ✓       ✓         ✓            ✓
///  /P reported as set     —       ✓         —            —
/// </code>
///
/// The catastrophic failure mode this gate exists to catch is the last two
/// rows: a mis-emitted <c>/Encrypt</c> dictionary that some reader silently
/// IGNORES, opening the "protected" file without any password. pdfe
/// verifying pdfe (writer round-trips through its own decrypt handler)
/// would never surface that — the no-self-oracle rule, learned three times
/// over in redaction (#636/#608/#637).
///
/// Empirical tool behavior these assertions are calibrated against
/// (verified on mutool 1.26, qpdf 12.3.2, Ghostscript 10.07.1, Poppler
/// 26.06.0 while building this gate):
/// <list type="bullet">
/// <item>Ghostscript EXITS 0 on a wrong or missing password — it prints
/// "Error: Password did not work." / "This file requires a password" and
/// simply writes no output file. Asserting on exit codes would green a
/// failure; these tests assert on whether a decoded, non-blank rendering
/// was actually produced (the wrapper returns null when no PNG appears).</item>
/// <item>qpdf's <c>--check</c> reports a wrong password as
/// <c>"invalid password"</c> WITHOUT the <c>"error:"</c> substring its
/// warning-tolerant success heuristic looks for, so rejection assertions go
/// through <c>--requires-password</c>'s documented exit codes
/// (<see cref="QpdfReferenceTool.RequiresPassword"/>) instead.</item>
/// <item>mutool and pdftoppm exit non-zero and produce no output on a
/// wrong/missing password; their wrappers surface that as null.</item>
/// </list>
///
/// Adobe Acrobat is deliberately NOT automated here: it is not scriptable
/// on this machine (no fake pass, no silent skip pretending otherwise).
/// It is covered as an explicit MANUAL step in
/// <c>docs/RELEASE_CHECKLIST.md</c>'s "Encryption Evidence" section: open
/// R6 and R4 samples in Acrobat with the correct, wrong, and no password
/// before tagging a release.
///
/// Skips are loud by convention (<c>Assert.SkipUnless</c> naming the
/// missing tool), and <see cref="AtLeastOneIndependentToolIsAvailable_GateIsNotVacuous"/>
/// keeps a bare machine from greening the whole gate vacuously.
///
/// Falsifiability drill (2026-07-17, part of #644's acceptance): blanking
/// the trailer's <c>/Encrypt N 0 R</c> reference on the fixtures — the
/// exact "reader silently ignores the encryption" shape — flipped 28 of
/// these 33 tests red, including EVERY no-password assertion for qpdf,
/// Ghostscript, and pdftoppm (each opened/rendered the file passwordless).
/// The gate does not merely pass; it has been shown to fail when the
/// property it guards is broken.
/// </summary>
public class EncryptionInteropGateTests : IClassFixture<EncryptionGateFixtures>
{
    private readonly EncryptionGateFixtures _fx;

    public EncryptionInteropGateTests(EncryptionGateFixtures fx) => _fx = fx;

    private const string User = EncryptionGateFixtures.UserPassword;
    private const string Owner = EncryptionGateFixtures.OwnerPassword;
    private const string Wrong = EncryptionGateFixtures.WrongPassword;
    private const string Marker = EncryptionGateFixtures.MarkerText;

    public static IEnumerable<object[]> BothAlgorithms => new[]
    {
        new object[] { PdfEncryptionAlgorithm.Aes256 },
        new object[] { PdfEncryptionAlgorithm.Aes128 },
    };

    #region meta: the gate must not pass vacuously

    [Fact]
    public void AtLeastOneIndependentToolIsAvailable_GateIsNotVacuous()
    {
        var available = new List<string>();
        if (MutoolReferenceRenderer.IsAvailable) available.Add("mutool");
        if (QpdfReferenceTool.IsAvailable) available.Add("qpdf");
        if (GhostscriptReferenceRenderer.IsAvailable) available.Add("ghostscript");
        if (PdftoppmReferenceRenderer.IsAvailable) available.Add("pdftoppm");

        // On a machine with zero independent readers, every other test in
        // this class skips and "the gate is green" would mean NOTHING was
        // verified. Default: skip — but loudly, as a skip the skip-budget
        // gate (#619/#655) counts, never as a pass. Release evidence must
        // run with PDFE_REQUIRE_ENCRYPTION_INTEROP_TOOLS=1 (see
        // docs/RELEASE_CHECKLIST.md), which turns the vacuous state into a
        // hard failure.
        var strict = Environment.GetEnvironmentVariable("PDFE_REQUIRE_ENCRYPTION_INTEROP_TOOLS") == "1";
        if (!strict)
            Assert.SkipUnless(available.Count > 0,
                "NO independent PDF reader (mutool, qpdf, ghostscript, pdftoppm) is available — every " +
                "EncryptionInteropGateTests test skipped, so the encryption interop gate verified NOTHING " +
                "on this machine. Install at least one tool, or treat this run as no evidence at all.");

        available.Should().NotBeEmpty(
            "the encryption interop gate is meaningless without at least one independent reader; " +
            "PDFE_REQUIRE_ENCRYPTION_INTEROP_TOOLS=1 demands real evidence");
    }

    #endregion

    #region mutool

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Mutool_CorrectUserPassword_ExtractsMarkerText(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");

        var extracted = MutoolTextExtractor.ExtractPage(_fx.EncryptedPath(algorithm), 1, User);

        extracted.Should().NotBeNull("mutool must open a pdfe-encrypted file with the correct user password");
        extracted.Should().Contain(Marker,
            "mutool's independent decryption must recover the real page content");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Mutool_OwnerPassword_ExtractsMarkerText(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");

        var extracted = MutoolTextExtractor.ExtractPage(_fx.EncryptedPath(algorithm), 1, Owner);

        extracted.Should().NotBeNull("the distinct owner password must also open the file");
        extracted.Should().Contain(Marker);
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Mutool_WrongPassword_RefusesToExtractOrRender(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");
        var path = _fx.EncryptedPath(algorithm);

        // mutool exits non-zero ("cannot authenticate password") → null.
        // If mutool ever produced ANY output for a wrong password on a
        // pdfe-written file, that is exactly the mis-emitted-/Encrypt
        // failure mode this gate exists to catch — fail loudly, do not
        // soften this to "output may exist but must not contain the marker".
        MutoolTextExtractor.ExtractPage(path, 1, Wrong).Should().BeNull(
            "mutool must reject the wrong password outright");
        MutoolReferenceRenderer.RenderPage(path, 1, 72, 30_000, Wrong).Should().BeNull(
            "mutool must not render any page for the wrong password");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Mutool_NoPassword_RefusesToExtractOrRender(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");
        var path = _fx.EncryptedPath(algorithm);

        MutoolTextExtractor.ExtractPage(path, 1, password: null).Should().BeNull(
            "a file with a non-empty user password must not open with NO password — " +
            "if it does, some part of /Encrypt was emitted so badly the reader ignored it");
        MutoolReferenceRenderer.RenderPage(path, 1, 72).Should().BeNull(
            "mutool must not render any page without a password");
    }

    #endregion

    #region qpdf

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Qpdf_RecognizedAsEncrypted_AndNoPasswordIsRejected(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        var path = _fx.EncryptedPath(algorithm);

        QpdfReferenceTool.IsEncrypted(path).Should().BeTrue(
            "qpdf's independent parser must agree the file is encrypted at all");
        QpdfReferenceTool.RequiresPassword(path).Should().Be(QpdfPasswordStatus.PasswordRequired,
            "with a non-empty user password set, opening with NO password must be rejected — " +
            "NotEncrypted here would mean qpdf is ignoring the /Encrypt dictionary pdfe wrote");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Qpdf_CorrectUserPassword_AcceptedAndFileChecksClean(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        var path = _fx.EncryptedPath(algorithm);

        QpdfReferenceTool.RequiresPassword(path, User).Should().Be(QpdfPasswordStatus.PasswordCorrect);

        var check = QpdfReferenceTool.Check(path, User);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue($"qpdf --check reported problems:\n{check.Value.Output}");

        var show = QpdfReferenceTool.ShowEncryption(path, User);
        show.Should().NotBeNull();
        show.Should().Contain("Supplied password is user password");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Qpdf_OwnerPassword_AcceptedWithOwnerAuthority(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        var path = _fx.EncryptedPath(algorithm);

        QpdfReferenceTool.RequiresPassword(path, Owner).Should().Be(QpdfPasswordStatus.PasswordCorrect,
            "the distinct owner password must open the file");
        var show = QpdfReferenceTool.ShowEncryption(path, Owner);
        show.Should().NotBeNull();
        show.Should().Contain("Supplied password is owner password",
            "the owner password must unlock full owner-level authority, not just user-level access");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Qpdf_WrongPassword_Rejected(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");

        QpdfReferenceTool.RequiresPassword(_fx.EncryptedPath(algorithm), Wrong)
            .Should().Be(QpdfPasswordStatus.PasswordRequired,
                "a wrong password must be rejected; PasswordCorrect here would mean the /U//O " +
                "validation entries pdfe wrote accept arbitrary passwords");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Qpdf_UserPasswordOnly_NoPasswordIsStillRejected(PdfEncryptionAlgorithm algorithm)
    {
        // SECURITY REGRESSION PIN: with a user password and NO owner
        // password, the R6 writer originally emitted /O//OE that validated
        // against the EMPTY password — and since the owner password confers
        // full authority, qpdf ("Supplied password is owner password"),
        // Ghostscript, and pdftoppm all opened the "protected" file with no
        // password at all. Caught by a manual qpdf --requires-password
        // probe AFTER this gate first went green: the original matrix only
        // ever built dual-password fixtures, so the shipped-by-default
        // `pdfe encrypt --user-password X` shape was never tested. R4 was
        // never vulnerable (Algorithm 3 step (a) falls back to the user
        // password); CreateR6 now does the same.
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        var path = _fx.UserOnlyEncryptedPath(algorithm);

        QpdfReferenceTool.RequiresPassword(path).Should().Be(QpdfPasswordStatus.PasswordRequired,
            "a user-password-only file must reject a passwordless open — PasswordCorrect here " +
            "means the empty owner password grants full authority, silently bypassing the user password");
        QpdfReferenceTool.RequiresPassword(path, User).Should().Be(QpdfPasswordStatus.PasswordCorrect,
            "the user password must still open its own file");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Pdftoppm_UserPasswordOnly_NoPasswordProducesNoRendering(PdfEncryptionAlgorithm algorithm)
    {
        // Renderer-side pin of the empty-owner bypass (see
        // Qpdf_UserPasswordOnly_NoPasswordIsStillRejected): pdftoppm
        // rendered the user-password-only fixture passwordless before the
        // CreateR6 fallback landed.
        Assert.SkipUnless(PdftoppmReferenceRenderer.IsAvailable, "pdftoppm not on PATH");
        var path = _fx.UserOnlyEncryptedPath(algorithm);

        var noPassword = PdftoppmReferenceRenderer.TryRenderPage(path, 1, 72);
        noPassword.Bitmap.Should().BeNull(
            "a user-password-only file must not render without a password " +
            $"(status: {noPassword.Status})");

        var withPassword = PdftoppmReferenceRenderer.TryRenderPage(path, 1, 72, 30_000, userPassword: User);
        withPassword.Bitmap.Should().NotBeNull("the user password must still render its own file");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Qpdf_PermissionsMask_ReportedSemanticallyExactlyAsSet(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");

        var show = QpdfReferenceTool.ShowEncryption(_fx.EncryptedPath(algorithm), User);
        show.Should().NotBeNull();

        // Revision/method sanity: the requested algorithm is what landed.
        if (algorithm == PdfEncryptionAlgorithm.Aes256)
        {
            show.Should().Contain("R = 6");
            show.Should().Contain("AESv3");
        }
        else
        {
            show.Should().Contain("R = 4");
            show.Should().Contain("AESv2");
            show.Should().NotContain("AESv3");
        }

        // The raw mask, byte-exact...
        show.Should().Contain($"P = {EncryptionGateFixtures.RestrictivePermissions}");

        // ...AND qpdf's semantic decode of every capability bit. Matching
        // only "P = -3392" would pass even if pdfe and qpdf disagreed about
        // which BITS the number sets; these lines are qpdf's independent
        // reading of the mask's meaning. -3392 = only bits 7/8 (reserved)
        // and 10 (accessibility extraction) set: everything else denied.
        show.Should().Contain("extract for accessibility: allowed");
        show.Should().Contain("extract for any purpose: not allowed");
        show.Should().Contain("print low resolution: not allowed");
        show.Should().Contain("print high resolution: not allowed");
        show.Should().Contain("modify document assembly: not allowed");
        show.Should().Contain("modify forms: not allowed");
        show.Should().Contain("modify annotations: not allowed");
        show.Should().Contain("modify other: not allowed");
        show.Should().Contain("modify anything: not allowed");
    }

    #endregion

    #region Ghostscript

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Ghostscript_CorrectUserPassword_RendersIdenticallyToPlainBaseline(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        using var baseline = GhostscriptReferenceRenderer.RenderPage(_fx.PlainPath, 1, 150, 30_000, userPassword: null);
        using var encrypted = GhostscriptReferenceRenderer.RenderPage(_fx.EncryptedPath(algorithm), 1, 150, 30_000, User);

        baseline.Should().NotBeNull();
        InkFraction(baseline!).Should().BeGreaterThan(0.001,
            "the plain baseline must actually contain the marker's ink — a blank-vs-blank match proves nothing");
        encrypted.Should().NotBeNull("Ghostscript must open a pdfe-encrypted file with the correct user password");
        MaxChannelDiff(baseline!, encrypted!).Should().BeLessThanOrEqualTo(2,
            "Ghostscript's independent decryption + rendering must match the unencrypted baseline");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Ghostscript_WrongPassword_ProducesNoRendering(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        // gs 10.x EXITS 0 here ("Error: Password did not work." on stderr,
        // no output file) — the assertion is on the absence of a decodable
        // rendering, never the exit code. See the class doc.
        GhostscriptReferenceRenderer.RenderPage(_fx.EncryptedPath(algorithm), 1, 150, 30_000, Wrong)
            .Should().BeNull("Ghostscript must not produce any rendering for the wrong password");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Ghostscript_NoPassword_ProducesNoRendering(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        GhostscriptReferenceRenderer.RenderPage(_fx.EncryptedPath(algorithm), 1, 150, 30_000, userPassword: null)
            .Should().BeNull(
                "a file with a non-empty user password must not render with NO password — " +
                "if it does, Ghostscript ignored the /Encrypt dictionary pdfe wrote");
    }

    #endregion

    #region pdftoppm (Poppler)

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Pdftoppm_CorrectUserPassword_RendersIdenticallyToPlainBaseline(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(PdftoppmReferenceRenderer.IsAvailable, "pdftoppm (poppler-utils) not on PATH");

        using var baseline = PdftoppmReferenceRenderer.RenderPage(_fx.PlainPath, 1, 150);
        using var encrypted = PdftoppmReferenceRenderer.RenderPage(_fx.EncryptedPath(algorithm), 1, 150, 30_000, User);

        baseline.Should().NotBeNull();
        InkFraction(baseline!).Should().BeGreaterThan(0.001,
            "the plain baseline must actually contain the marker's ink — a blank-vs-blank match proves nothing");
        encrypted.Should().NotBeNull("pdftoppm must open a pdfe-encrypted file with the correct user password");
        MaxChannelDiff(baseline!, encrypted!).Should().BeLessThanOrEqualTo(2,
            "Poppler's independent decryption + rendering must match the unencrypted baseline");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Pdftoppm_OwnerPassword_RendersIdenticallyToPlainBaseline(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(PdftoppmReferenceRenderer.IsAvailable, "pdftoppm (poppler-utils) not on PATH");

        using var baseline = PdftoppmReferenceRenderer.RenderPage(_fx.PlainPath, 1, 150);
        using var viaOwner = PdftoppmReferenceRenderer.RenderPage(
            _fx.EncryptedPath(algorithm), 1, 150, 30_000, userPassword: null, ownerPassword: Owner);

        baseline.Should().NotBeNull();
        viaOwner.Should().NotBeNull(
            "the distinct owner password (-opw) must open the file with full authority");
        MaxChannelDiff(baseline!, viaOwner!).Should().BeLessThanOrEqualTo(2);
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Pdftoppm_WrongPassword_RefusesToRender(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(PdftoppmReferenceRenderer.IsAvailable, "pdftoppm (poppler-utils) not on PATH");

        PdftoppmReferenceRenderer.RenderPage(_fx.EncryptedPath(algorithm), 1, 150, 30_000, Wrong)
            .Should().BeNull("pdftoppm must reject the wrong password (exit 1, 'Incorrect password')");
    }

    [Theory]
    [MemberData(nameof(BothAlgorithms))]
    public void Pdftoppm_NoPassword_RefusesToRender(PdfEncryptionAlgorithm algorithm)
    {
        Assert.SkipUnless(PdftoppmReferenceRenderer.IsAvailable, "pdftoppm (poppler-utils) not on PATH");

        PdftoppmReferenceRenderer.RenderPage(_fx.EncryptedPath(algorithm), 1, 150)
            .Should().BeNull(
                "a file with a non-empty user password must not render with NO password — " +
                "if it does, Poppler ignored the /Encrypt dictionary pdfe wrote");
    }

    #endregion

    #region pixel helpers

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

    /// <summary>Fraction of pixels dark enough to be text ink (any channel &lt; 128).</summary>
    private static double InkFraction(SKBitmap bitmap)
    {
        long dark = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red < 128 || p.Green < 128 || p.Blue < 128) dark++;
            }
        }
        return (double)dark / ((long)bitmap.Width * bitmap.Height);
    }

    #endregion
}

/// <summary>
/// Shared fixtures for <see cref="EncryptionInteropGateTests"/>: one plain
/// baseline plus one encrypted file per algorithm, written ONCE per test run
/// by <see cref="PdfDocumentWriter"/> with the gate's canonical hardening —
/// non-empty user password, DISTINCT owner password (so owner-authority
/// tests mean something), and the restrictive <c>/P</c> mask -3392 (only
/// the reserved bits 7/8 and accessibility-extraction bit 10 set) so the
/// permission-report assertions exercise a non-trivial mask.
/// </summary>
public sealed class EncryptionGateFixtures : IDisposable
{
    public const string MarkerText = "ENCRYPTION GATE MARKER 7F3A";
    public const string UserPassword = "gate-user-pw-1";
    public const string OwnerPassword = "gate-owner-pw-2";
    public const string WrongPassword = "definitely-not-the-password";
    public const long RestrictivePermissions = -3392;

    private readonly string _tempDir;
    private readonly Dictionary<PdfEncryptionAlgorithm, string> _encrypted = new();
    private readonly Dictionary<PdfEncryptionAlgorithm, string> _userOnly = new();

    public string PlainPath { get; }

    public EncryptionGateFixtures()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pdfe-enc-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var documentBytes = BuildDocumentBytes();
        PlainPath = Path.Combine(_tempDir, "plain.pdf");
        File.WriteAllBytes(PlainPath, documentBytes);

        foreach (var algorithm in new[] { PdfEncryptionAlgorithm.Aes256, PdfEncryptionAlgorithm.Aes128 })
        {
            using (var doc = PdfDocument.Open(documentBytes))
            {
                var path = Path.Combine(_tempDir, $"encrypted-{algorithm}.pdf");
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    new PdfDocumentWriter(doc, new PdfEncryptionOptions
                    {
                        Algorithm = algorithm,
                        UserPassword = UserPassword,
                        OwnerPassword = OwnerPassword,
                        Permissions = RestrictivePermissions,
                    }).Write(fs);
                }
                _encrypted[algorithm] = path;
            }

            // User-password-only fixture: no owner password supplied. The
            // empty-owner bypass (an /O//OE pair validating against the
            // EMPTY password grants passwordless FULL authority in qpdf,
            // Ghostscript, and pdftoppm) shipped exactly this configuration
            // and was invisible to the dual-password matrix above — see
            // Qpdf_UserPasswordOnly_NoPasswordIsStillRejected.
            using (var doc = PdfDocument.Open(documentBytes))
            {
                var path = Path.Combine(_tempDir, $"encrypted-useronly-{algorithm}.pdf");
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    new PdfDocumentWriter(doc, new PdfEncryptionOptions
                    {
                        Algorithm = algorithm,
                        UserPassword = UserPassword,
                        OwnerPassword = null,
                        Permissions = RestrictivePermissions,
                    }).Write(fs);
                }
                _userOnly[algorithm] = path;
            }
        }
    }

    public string EncryptedPath(PdfEncryptionAlgorithm algorithm) => _encrypted[algorithm];

    public string UserOnlyEncryptedPath(PdfEncryptionAlgorithm algorithm) => _userOnly[algorithm];

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] BuildDocumentBytes()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, 200);
        var font = PdfFont.Helvetica(14);
        using (var g = page.GetGraphics())
        {
            g.DrawText(MarkerText, font, PdfBrush.Black, new PdfRectangle(15, 20, 285, 180));
        }
        return doc.SaveToBytes();
    }
}
