using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
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

    // ── visible signature appearance (#623, last bullet) ────────────────────
    //
    // No-self-oracle: excise reading its own /AP stream and declaring it
    // present proves nothing about whether a real viewer draws it. These
    // assertions render the signed page with mutool — an implementation we
    // do not own — and measure ink in the widget rectangle.

    [Fact]
    public void SignDocument_VisibleRect_IndependentRendererShowsInkInSignatureRect_AndStillVerifies()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // CreateSimpleTextPdf's own text sits near the top of the page
        // (pdfY ~= 692), so the widget goes in an otherwise-empty region
        // near the bottom — any ink measured there can only be the baked
        // appearance, not the base document's own content.
        var rect = new PdfRectangle(72, 100, 300, 160);
        var basePath = CreateBasePdf();

        // Baseline: confirm the region is genuinely blank *before* signing,
        // so a positive result after signing can't be attributed to
        // pre-existing content in that rectangle.
        using (var unsigned = MutoolReferenceRenderer.RenderPage(basePath, 1, dpi: 150))
        {
            unsigned.Should().NotBeNull();
            InkFractionIn(unsigned!, rect, pageHeight: 792, dpi: 150).Should().BeLessThan(0.001,
                "fixture sanity — the measurement rectangle must be blank before signing, or a " +
                "positive ink reading after signing would prove nothing about the appearance");
        }

        using var certificate = SigningCertificateFactory.CreateSelfSigned("Visible Signer");

        // Author the field and sign it on the SAME open document instance —
        // signing reopens nothing, so the field must exist on the instance
        // SignDocument receives (mirrors SignDocument_ExistingEmptySignatureField_SignsThatField).
        using var document = PdfDocument.Open(basePath);
        document.AddSignatureField(pageNumber: 1, rect: rect, fieldName: "VisibleSignature");

        var signedBytes = _signer.SignDocument(document, certificate, new SignatureApplicationOptions
        {
            FieldName = "VisibleSignature",
            Reason = "Approval",
            Location = "Remote"
        });
        var signedPath = WriteTempFile(signedBytes);

        // The appearance must not corrupt the CMS/ByteRange it sits beside.
        var results = CreateVerifier().VerifySignatures(signedPath);
        results.Should().HaveCount(1);
        results[0].IsValid.Should().BeTrue(
            "baking a visible appearance stream must not disturb the signed ByteRange or CMS");
        results[0].CoversWholeDocument.Should().BeTrue();

        using var rendered = MutoolReferenceRenderer.RenderPage(signedPath, 1, dpi: 150);
        rendered.Should().NotBeNull("mutool must be able to render the signed page at all");

        var ink = InkFractionIn(rendered!, rect, pageHeight: 792, dpi: 150);
        ink.Should().BeGreaterThan(0.005,
            "an independent renderer must draw *something* inside the signature widget's /Rect — " +
            "the whole point of a baked /AP /N is that a third-party viewer honors it without " +
            "excise's own rendering code in the loop. The region was confirmed blank before signing.");
    }

    [Fact]
    public void SignDocument_DefaultZeroRectField_StaysAppearanceLess()
    {
        // FindOrCreateSignatureField authors a fresh field at Rect(0,0,0,0)
        // when none exists (the invisible-signature default). That must
        // remain untouched — invisible signatures are still fully valid
        // per #623's acceptance criteria — not retrofitted with an /AP.
        using var certificate = SigningCertificateFactory.CreateSelfSigned("Invisible Signer");
        var signedPath = SignSamplePdf(certificate);

        using var reopened = PdfDocument.Open(signedPath);
        var field = reopened.GetAcroForm()?.FindField("Signature1");
        field.Should().NotBeNull();
        field!.RawDictionary.GetOptional("AP").Should().BeNull(
            "a zero-size /Rect signature field must stay appearance-less, not gain a baked /AP");
    }

    /// <summary>
    /// Fraction of non-white pixels inside <paramref name="box"/> (PDF content
    /// coordinates, bottom-left origin) of a page rendered at <paramref name="dpi"/>.
    /// Mirrors RedactionReferenceVerificationTests's InkFractionIn — the same
    /// "is there ink here" measurement, applied to a signature widget instead
    /// of a redaction target.
    /// </summary>
    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box, double pageHeight, int dpi)
    {
        double scale = dpi / 72.0;

        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));

        if (x1 <= x0 || y1 <= y0) return 0;

        int ink = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
        }

        return total == 0 ? 0 : (double)ink / total;
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

    [Fact]
    public void SignDocument_EncryptedSource_FailsClosed()
    {
        // Signing serializes through the plaintext save path; silently writing
        // an encrypted source back without its protection is the loss
        // #638/#641 guard against, so signing must refuse instead.
        var basePath = CreateBasePdf();
        var encryptedPath = basePath + ".enc.pdf";
        _tempFiles.Add(encryptedPath);
        using (var plain = PdfDocument.Open(basePath))
        {
            plain.Save(encryptedPath, new Excise.Core.Security.PdfEncryptionOptions
            {
                UserPassword = "user-pw",
                OwnerPassword = "owner-pw"
            });
        }

        using var certificate = SigningCertificateFactory.CreateSelfSigned("Encrypted Source");
        using var encrypted = PdfDocument.Open(encryptedPath, "user-pw");

        var act = () => _signer.SignDocument(encrypted, certificate);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*encrypted*", "signing must not silently strip document protection");
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

    // ── external oracle: poppler pdfsig ─────────────────────────────────────

    /// <summary>
    /// The #466 verifier shares this repo; poppler's pdfsig shares nothing.
    /// "A tool must not be its own oracle for the property it exists to
    /// guarantee" — this asserts a third-party reader sees exactly what the
    /// issue #623 acceptance criteria demand: signature valid, total document
    /// signed, issuer unknown (self-signed ⇒ valid-but-untrusted, not corrupt).
    /// </summary>
    [Fact]
    public async Task SignDocument_ExternalOracle_PdfsigReportsValidUntrustedSignature()
    {
        var pdfsig = FindPdfsig();
        Assert.SkipWhen(pdfsig == null, "poppler pdfsig not installed on this machine");

        using var certificate = SigningCertificateFactory.CreateSelfSigned("External Oracle Signer");
        var signedPath = SignSamplePdf(certificate);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = pdfsig,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(signedPath);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        // #925: drain both redirected pipes concurrently, or a chatty stderr
        // wedges the child and this read never returns.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit(30_000).Should().BeTrue("pdfsig must terminate");
        var output = await stdoutTask;
        _ = await stderrTask;

        output.Should().Contain("Signature is Valid.",
            "an independent reader must accept the CMS signature and digest");
        output.Should().Contain("Total document signed",
            "the two-pass ByteRange must cover the whole file except the /Contents hole");
        output.Should().Contain("Certificate issuer is unknown",
            "self-signed must be valid-but-untrusted, never trusted, never corrupt");
        output.Should().Contain("adbe.pkcs7.detached");
        output.Should().Contain("External Oracle Signer");
    }

    private static string? FindPdfsig()
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var candidates = new List<string>();
        foreach (var dir in pathDirs)
        {
            candidates.Add(Path.Combine(dir, "pdfsig"));
        }
        // Common install locations not always on the test host's PATH.
        candidates.Add("/opt/homebrew/bin/pdfsig");
        candidates.Add("/usr/local/bin/pdfsig");
        candidates.Add("/usr/bin/pdfsig");

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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
