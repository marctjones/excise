using System.IO;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Security;
using Pdfe.Core.Writing;
using Xunit;

namespace Pdfe.Core.Tests.Security;

/// <summary>
/// #642: decoding of the Standard Security Handler's /P permission bitmask
/// (ISO 32000-2 Table 22) and its exposure on <see cref="PdfDocument"/>.
///
/// Bit-position ground truth is NOT pdfe's own reading of the spec: every
/// named mask below was cross-checked against qpdf 12's
/// <c>--show-encryption</c> report (see the per-test comments), so a wrong
/// bit interpretation here fails against an independent oracle's published
/// decoding, not against itself.
/// </summary>
public class PdfPermissionsTests
{
    // ---- Raw bitmask decoding -------------------------------------------

    [Fact]
    public void AllAllowed_MinusFour_GrantsEverything()
    {
        // qpdf: -4 == 0xFFFFFFFC — every permission bit set (reserved
        // low-order bits 1-2 zero). qpdf reports everything "allowed".
        var p = new PdfPermissions(-4);

        p.CanPrint.Should().BeTrue();
        p.CanModify.Should().BeTrue();
        p.CanCopy.Should().BeTrue();
        p.CanAnnotate.Should().BeTrue();
        p.CanFillForms.Should().BeTrue();
        p.CanExtractForAccessibility.Should().BeTrue();
        p.CanAssemble.Should().BeTrue();
        p.CanPrintHighQuality.Should().BeTrue();
        p.RawValue.Should().Be(-4);
        p.Should().Be(PdfPermissions.AllAllowed);
    }

    [Fact]
    public void MinusOne_AllBitsSet_GrantsEverything()
    {
        var p = new PdfPermissions(-1);

        p.CanPrint.Should().BeTrue();
        p.CanModify.Should().BeTrue();
        p.CanCopy.Should().BeTrue();
        p.CanAnnotate.Should().BeTrue();
        p.CanFillForms.Should().BeTrue();
        p.CanExtractForAccessibility.Should().BeTrue();
        p.CanAssemble.Should().BeTrue();
        p.CanPrintHighQuality.Should().BeTrue();
    }

    [Fact]
    public void Minus1028_DeniesExactlyAssembly()
    {
        // qpdf --show-encryption on test-pdfs/poppler/unittestcases/
        // PasswordEncrypted.pdf (P = -1028):
        //   extract for accessibility: allowed
        //   extract for any purpose:   allowed
        //   print low/high resolution: allowed
        //   modify document assembly:  NOT allowed
        //   modify forms/annotations/other: allowed
        var p = new PdfPermissions(-1028);

        p.CanAssemble.Should().BeFalse("bit 11 (value 1024) is the only permission bit cleared in -1028");
        p.CanPrint.Should().BeTrue();
        p.CanPrintHighQuality.Should().BeTrue();
        p.CanModify.Should().BeTrue();
        p.CanCopy.Should().BeTrue();
        p.CanAnnotate.Should().BeTrue();
        p.CanFillForms.Should().BeTrue();
        p.CanExtractForAccessibility.Should().BeTrue();
    }

    [Fact]
    public void Minus3392_AllowsOnlyAccessibilityExtraction()
    {
        // qpdf --show-encryption on test-pdfs/poppler/unittestcases/
        // "Gday garçon - owner.pdf" (P = -3392, empty user password):
        //   extract for accessibility: allowed
        //   extract for any purpose:   NOT allowed
        //   print low/high resolution: NOT allowed
        //   modify assembly/forms/annotations/other: NOT allowed
        var p = new PdfPermissions(-3392);

        p.CanExtractForAccessibility.Should().BeTrue("bit 10 is the only permission bit set in -3392");
        p.CanPrint.Should().BeFalse();
        p.CanPrintHighQuality.Should().BeFalse();
        p.CanModify.Should().BeFalse();
        p.CanCopy.Should().BeFalse();
        p.CanAnnotate.Should().BeFalse();
        p.CanFillForms.Should().BeFalse();
        p.CanAssemble.Should().BeFalse();
    }

    [Fact]
    public void HandBuiltMask_DenyingExactlyPrint_OnlyPrintIsDenied()
    {
        // -4 with bit 3 (value 4) cleared: -4 & ~4 == -8.
        const long denyPrintOnly = -4 & ~4;
        denyPrintOnly.Should().Be(-8);

        var p = new PdfPermissions(denyPrintOnly);

        p.CanPrint.Should().BeFalse();
        p.CanPrintHighQuality.Should().BeFalse(
            "bit 12 is still set, but high-quality printing is meaningless without bit 3 — " +
            "qpdf likewise reports both print resolutions as not allowed when only bit 3 is cleared");
        p.CanModify.Should().BeTrue();
        p.CanCopy.Should().BeTrue();
        p.CanAnnotate.Should().BeTrue();
        p.CanFillForms.Should().BeTrue();
        p.CanExtractForAccessibility.Should().BeTrue();
        p.CanAssemble.Should().BeTrue();
    }

    [Fact]
    public void FillForms_Bit9Alone_StillAllowsFormFillIn()
    {
        // Deny annotations (bit 6, value 32) but keep bit 9 (value 256):
        // Table 22 says bit 9 permits filling existing form fields even
        // when bit 6 is clear.
        const long denyAnnotateKeepBit9 = -4 & ~32;
        var p = new PdfPermissions(denyAnnotateKeepBit9);

        p.CanAnnotate.Should().BeFalse();
        p.CanFillForms.Should().BeTrue("bit 9 grants form fill-in independently of bit 6");

        // And with both 6 and 9 clear, form fill-in is denied.
        var q = new PdfPermissions(-4 & ~32 & ~256);
        q.CanFillForms.Should().BeFalse();
    }

    [Fact]
    public void UnsignedStorageOfP_DecodesIdenticallyToSigned()
    {
        // Some writers store /P as the unsigned 32-bit equivalent.
        // -3392 as unsigned is 4294963904.
        var signed = new PdfPermissions(-3392);
        var unsigned = new PdfPermissions(4294963904);

        unsigned.Should().Be(signed);
        unsigned.RawValue.Should().Be(-3392);
        unsigned.CanCopy.Should().BeFalse();
        unsigned.CanExtractForAccessibility.Should().BeTrue();
    }

    [Fact]
    public void ToString_ListsDeniedPermissions()
    {
        new PdfPermissions(-4).ToString().Should().Contain("all allowed");
        var s = new PdfPermissions(-3392).ToString();
        s.Should().Contain("-3392").And.Contain("copy").And.Contain("print");
        s.Should().NotContain("extract-for-accessibility", "bit 10 is granted in -3392");
    }

    // ---- Exposure on PdfDocument ----------------------------------------

    [Fact]
    public void UnencryptedDocument_HasAllAllowedPermissions()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        using var reopened = PdfDocument.Open(doc.SaveToBytes());

        reopened.IsEncrypted.Should().BeFalse();
        reopened.Permissions.Should().Be(PdfPermissions.AllAllowed);
        reopened.OpenedWithOwnerPassword.Should().BeFalse();
        reopened.EffectivePermissions.Should().Be(PdfPermissions.AllAllowed);
    }

    [Fact]
    public void EncryptedRoundTrip_SurfacesTheWrittenPermissionMask()
    {
        // Encrypt with a restrictive mask via pdfe's own writer (#641),
        // reopen, and check /P surfaces decoded. The writer side is
        // independently interop-verified against qpdf in
        // Pdfe.Rendering.Tests/Differential/EncryptionWriterInteropTests.
        const long denyCopyMask = -4 & ~16; // clear bit 5 only
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter(doc, new PdfEncryptionOptions
        {
            UserPassword = "",
            OwnerPassword = "owner",
            Permissions = denyCopyMask,
        }).Write(buffer);

        using var reopened = PdfDocument.Open(buffer.ToArray());
        reopened.IsEncrypted.Should().BeTrue();
        reopened.Permissions.RawValue.Should().Be((int)denyCopyMask);
        reopened.Permissions.CanCopy.Should().BeFalse();
        reopened.Permissions.CanExtractForAccessibility.Should().BeTrue();
        reopened.Permissions.CanPrint.Should().BeTrue();
        reopened.OpenedWithOwnerPassword.Should().BeFalse(
            "owner-password opening is #324 and unsupported; every open today is user-level");
        reopened.EffectivePermissions.Should().Be(reopened.Permissions);
    }

    [Theory]
    [InlineData("test-pdfs/poppler/unittestcases/PasswordEncrypted.pdf", "password", -1028)]
    [InlineData("test-pdfs/poppler/unittestcases/Gday garçon - owner.pdf", null, -3392)]
    public void RealWorldEncryptedFixtures_SurfaceQpdfConfirmedPermissions(
        string relativePath, string? password, int expectedRaw)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);
        Assert.SkipWhen(!File.Exists(path), $"Encrypted PDF fixture not available: {relativePath}");

        using var doc = PdfDocument.Open(path, password);
        doc.IsEncrypted.Should().BeTrue();
        doc.Permissions.RawValue.Should().Be(expectedRaw);
        doc.EffectivePermissions.Should().Be(doc.Permissions);
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
}
