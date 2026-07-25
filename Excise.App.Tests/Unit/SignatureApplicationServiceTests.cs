using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Tests for SignatureApplicationService (#623): apply a PKCS#7/CMS detached
/// signature with a self-signed, locally-held certificate.
///
/// The round-trip oracle is SignatureVerificationService (#466) — an
/// independent implementation of ByteRange extraction and CMS verification.
/// The signer proving itself against the verifier avoids a tool being its own
/// oracle for the property it guarantees: the two sides were built separately
/// and must agree on the exact byte layout.
///
/// All certificates are generated in-process; no network, no machine trust
/// store dependence (trust assertions use explicit custom anchors).
/// </summary>
public class SignatureApplicationServiceTests : IDisposable
{
    private readonly SignatureApplicationService _signer;
    private readonly List<string> _tempFiles = new();

    public SignatureApplicationServiceTests()
    {
        _signer = new SignatureApplicationService(
            NullLogger<SignatureApplicationService>.Instance);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch (IOException) { }
        }
        GC.SuppressFinalize(this);
    }

    // ── round-trip through the #466 verifier ────────────────────────────────

    [Fact]
    public void SignDocument_SelfSigned_VerifiesValidThroughIndependentVerifier()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Excise Signing Test");
        var signedPath = SignSamplePdf(certificate);

        var results = CreateVerifier().VerifySignatures(signedPath);

        results.Should().HaveCount(1);
        var result = results[0];
        result.IsValid.Should().BeTrue("the signature must verify through the independent #466 verifier");
        result.ByteRangeStructureChecked.Should().BeTrue();
        result.ByteRangeStructureValid.Should().BeTrue(
            "the two-pass ByteRange must exclude exactly the /Contents value: {0}",
            result.ByteRangeStructureMessage);
        result.ByteRangeIntegrityChecked.Should().BeTrue();
        result.ByteRangeIntegrityValid.Should().BeTrue("the digest over both ranges must match");
        result.CoversWholeDocument.Should().BeTrue("the signature must cover the whole file except the hole");
        result.SignedBy.Should().Contain("Excise Signing Test");
        result.StatusMessage.Should().Contain("ByteRange digest matches");
    }

    [Fact]
    public void SignDocument_ByteRangeOffsets_AreByteExact()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Offset Check");
        var signedPath = SignSamplePdf(certificate);
        var fileBytes = File.ReadAllBytes(signedPath);
        var text = Encoding.Latin1.GetString(fileBytes);

        var match = Regex.Match(text, @"/ByteRange \[(\d+) (\d+) (\d+) (\d+)\]");
        match.Success.Should().BeTrue("the signed file must carry a numeric /ByteRange");
        var start1 = long.Parse(match.Groups[1].Value);
        var length1 = long.Parse(match.Groups[2].Value);
        var start2 = long.Parse(match.Groups[3].Value);
        var length2 = long.Parse(match.Groups[4].Value);

        start1.Should().Be(0, "the first range must start at the first byte of the file");
        start2.Should().BeGreaterThan(length1);
        (start2 + length2).Should().Be(fileBytes.Length, "the second range must run to end of file");

        // The excluded gap must be exactly the hex string token: '<' at the
        // first excluded byte, '>' at the last.
        fileBytes[length1].Should().Be((byte)'<');
        fileBytes[start2 - 1].Should().Be((byte)'>');

        // Gap = default 8192-byte capacity as hex plus the delimiters.
        (start2 - length1).Should().Be(8192 * 2 + 2);

        // Everything inside the gap is hex.
        for (var i = length1 + 1; i < start2 - 1; i++)
        {
            var c = (char)fileBytes[i];
            Uri.IsHexDigit(c).Should().BeTrue($"byte {i} inside /Contents must be a hex digit, got '{c}'");
        }
    }

    // ── tamper detection ────────────────────────────────────────────────────

    [Fact]
    public void SignDocument_TamperedSignedByte_VerifierReportsInvalid()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Tamper Test");
        var signedPath = SignSamplePdf(certificate, new SignatureApplicationOptions
        {
            Reason = "ORIGINAL-REASON"
        });

        // Flip bytes inside the signed range (the /Reason string sits in the
        // signature dictionary, outside the /Contents hole) without breaking
        // PDF syntax.
        var fileBytes = File.ReadAllBytes(signedPath);
        ReplaceAsciiMarker(fileBytes, "ORIGINAL-REASON", "TAMPERED-REASON");
        File.WriteAllBytes(signedPath, fileBytes);

        var results = CreateVerifier().VerifySignatures(signedPath);

        results.Should().HaveCount(1);
        results[0].IsValid.Should().BeFalse("a modified byte inside the signed range must fail verification");
        results[0].ByteRangeStructureValid.Should().BeTrue("the ByteRange itself is still well-formed");
        results[0].ByteRangeIntegrityChecked.Should().BeTrue();
        results[0].ByteRangeIntegrityValid.Should().BeFalse();
        results[0].State.Should().Be(SignatureVerificationState.Invalid,
            "a digest mismatch is proven tampering");
    }

    // ── trust: self-signed is valid-but-untrusted; injected anchor flips it ──

    [Fact]
    public void SignDocument_SelfSigned_EmptyTrustAnchors_IsValidUntrusted()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Untrusted Signer");
        var signedPath = SignSamplePdf(certificate);

        var results = CreateVerifier().VerifySignatures(signedPath); // no anchors

        results.Should().HaveCount(1);
        results[0].IsValid.Should().BeTrue("cryptographic validity is independent of trust");
        results[0].TrustStatus.Should().Be(SignatureTrustStatus.Untrusted,
            "a self-signed signer has no trust anchor — the correct, expected result (#623)");
        results[0].State.Should().Be(SignatureVerificationState.ValidUntrusted);
    }

    [Fact]
    public void SignDocument_SelfSignedInjectedAsTrustAnchor_IsValidTrusted()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Pinned Signer");
        var signedPath = SignSamplePdf(certificate);

        var results = CreateVerifier(certificate).VerifySignatures(signedPath);

        results.Should().HaveCount(1);
        results[0].IsValid.Should().BeTrue();
        results[0].TrustStatus.Should().Be(SignatureTrustStatus.Trusted,
            "explicitly pinning the self-signed certificate as an anchor makes it trusted");
        results[0].State.Should().Be(SignatureVerificationState.ValidTrusted);
    }

    // ── signing an existing (authored) empty signature field ────────────────

    [Fact]
    public void SignDocument_ExistingEmptySignatureField_SignsThatField()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Field Signer");
        var basePath = CreateBasePdf();

        using var document = PdfDocument.Open(basePath);
        document.AddSignatureField(
            pageNumber: 1,
            rect: new PdfRectangle(72, 700, 244, 740),
            fieldName: "ApproverSignature");

        var signedBytes = _signer.SignDocument(document, certificate,
            new SignatureApplicationOptions { FieldName = "ApproverSignature" });
        var signedPath = WriteTempFile(signedBytes);

        var results = CreateVerifier().VerifySignatures(signedPath);

        results.Should().HaveCount(1, "the existing field must be signed, not a second field added");
        results[0].SignatureName.Should().Be("ApproverSignature");
        results[0].IsValid.Should().BeTrue();
        results[0].CoversWholeDocument.Should().BeTrue();
    }

    [Fact]
    public void SignDocument_FieldNameCollidesWithNonSignatureField_Throws()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Collision");
        var basePath = CreateBasePdf();

        using var document = PdfDocument.Open(basePath);
        document.AddTextField(
            pageNumber: 1,
            rect: new PdfRectangle(72, 600, 244, 620),
            fieldName: "NotASignature");

        var act = () => _signer.SignDocument(document, certificate,
            new SignatureApplicationOptions { FieldName = "NotASignature" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a signature field*");
    }

    // ── full-rewrite save invalidates prior signatures: refuse re-signing ───

    [Fact]
    public void SignDocument_AlreadySignedDocument_Throws()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("First Signer");
        var signedPath = SignSamplePdf(certificate);

        using var reopened = PdfDocument.Open(signedPath);
        using var secondCertificate = SigningCertificateFactory.CreateSelfSigned("Second Signer");

        var act = () => _signer.SignDocument(reopened, secondCertificate,
            new SignatureApplicationOptions { FieldName = "Signature2" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already contains a signed signature*",
                "a full-rewrite save would silently invalidate the existing signature");
    }

    // ── signing time ────────────────────────────────────────────────────────

    [Fact]
    public void SignDocument_VerifierExtractsSigningTimeFromSignedAttributes()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Timed Signer");
        var signedPath = SignSamplePdf(certificate);

        var results = CreateVerifier().VerifySignatures(signedPath);

        results.Should().HaveCount(1);
        results[0].SigningTime.Should().NotBe(default(DateTime));
        results[0].SigningTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(10));
    }

    // ── PKCS#12 load path ───────────────────────────────────────────────────

    [Fact]
    public void SignFile_IdentityLoadedFromPkcs12_VerifiesValid()
    {
        var pfxPath = Path.GetTempFileName() + ".pfx";
        _tempFiles.Add(pfxPath);
        using (var original = SigningCertificateFactory.CreateSelfSigned("P12 Signer"))
        {
            File.WriteAllBytes(pfxPath, original.Export(X509ContentType.Pkcs12, "test-password"));
        }

        using var loaded = SigningCertificateFactory.LoadFromPkcs12(pfxPath, "test-password");
        loaded.HasPrivateKey.Should().BeTrue();

        var basePath = CreateBasePdf();
        var signedPath = basePath + ".signed.pdf";
        _tempFiles.Add(signedPath);
        _signer.SignFile(basePath, signedPath, loaded);

        var results = CreateVerifier().VerifySignatures(signedPath);
        results.Should().HaveCount(1);
        results[0].IsValid.Should().BeTrue();
        results[0].SignedBy.Should().Contain("P12 Signer");
        results[0].CoversWholeDocument.Should().BeTrue();
    }

    // ── argument validation ─────────────────────────────────────────────────

    [Fact]
    public void SignDocument_CertificateWithoutPrivateKey_Throws()
    {
        using var withKey = SigningCertificateFactory.CreateSelfSigned("Public Only");
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
        var basePath = CreateBasePdf();
        using var document = PdfDocument.Open(basePath);

        var act = () => _signer.SignDocument(document, publicOnly);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*private key*");
    }

    [Fact]
    public void SignDocument_TooSmallSignatureCapacity_Throws()
    {
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Tiny Capacity");
        var basePath = CreateBasePdf();
        using var document = PdfDocument.Open(basePath);

        var act = () => _signer.SignDocument(document, certificate,
            new SignatureApplicationOptions { SignatureCapacityBytes = 100 });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*at least 2048*");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static SignatureVerificationService CreateVerifier(params X509Certificate2[] anchors)
    {
        // Custom trust anchors (possibly none) keep trust assertions
        // deterministic and machine-independent (#466).
        var anchorList = new List<X509Certificate2>(anchors);
        return new SignatureVerificationService(
            NullLogger<SignatureVerificationService>.Instance,
            new SignatureTrustEvaluator(anchorList));
    }

    private string CreateBasePdf()
    {
        var path = Path.GetTempFileName() + ".pdf";
        _tempFiles.Add(path);
        TestPdfGenerator.CreateSimpleTextPdf(path, "Signature application test document");
        return path;
    }

    private string SignSamplePdf(X509Certificate2 certificate, SignatureApplicationOptions? options = null)
    {
        var basePath = CreateBasePdf();
        using var document = PdfDocument.Open(basePath);
        var signedBytes = _signer.SignDocument(document, certificate, options);
        return WriteTempFile(signedBytes);
    }

    private string WriteTempFile(byte[] bytes)
    {
        var path = Path.GetTempFileName() + ".pdf";
        _tempFiles.Add(path);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void ReplaceAsciiMarker(byte[] bytes, string marker, string replacement)
    {
        marker.Length.Should().Be(replacement.Length);
        var index = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker));
        index.Should().BeGreaterThanOrEqualTo(0, $"marker '{marker}' must appear in the signed file");
        Encoding.ASCII.GetBytes(replacement).CopyTo(bytes.AsSpan(index));
    }
}
