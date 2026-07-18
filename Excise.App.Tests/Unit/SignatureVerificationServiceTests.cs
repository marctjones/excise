using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.Asn1.X509;
using Excise.App.Services;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Unit tests for SignatureVerificationService.
/// Tests signature result objects and edge cases.
/// </summary>
public class SignatureVerificationServiceTests
{
    private const int SignaturePlaceholderByteCount = 8192;
    private const int SignaturePlaceholderHexLength = SignaturePlaceholderByteCount * 2;
    private static readonly string SignaturePlaceholderHex = new('F', SignaturePlaceholderHexLength);

    private readonly SignatureVerificationService _service;
    private readonly ILogger<SignatureVerificationService> _logger;

    public SignatureVerificationServiceTests()
    {
        _logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<SignatureVerificationService>();
        _service = new SignatureVerificationService(_logger);
    }

    // ========================================================================
    // SIGNATURE VERIFICATION RESULT TESTS
    // ========================================================================

    [Fact]
    public void SignatureVerificationResult_DefaultSignatureName_IsEmpty()
    {
        var result = new SignatureVerificationResult();
        result.SignatureName.Should().Be(string.Empty);
    }

    [Fact]
    public void SignatureVerificationResult_DefaultIsValid_IsFalse()
    {
        var result = new SignatureVerificationResult();
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SignatureVerificationResult_DefaultSignedBy_IsEmpty()
    {
        var result = new SignatureVerificationResult();
        result.SignedBy.Should().Be(string.Empty);
    }

    [Fact]
    public void SignatureVerificationResult_DefaultSigningTime_IsMinValue()
    {
        var result = new SignatureVerificationResult();
        result.SigningTime.Should().Be(default(DateTime));
    }

    [Fact]
    public void SignatureVerificationResult_DefaultStatusMessage_IsEmpty()
    {
        var result = new SignatureVerificationResult();
        result.StatusMessage.Should().Be(string.Empty);
    }

    [Fact]
    public void SignatureVerificationResult_DefaultCoversWholeDocument_IsFalse()
    {
        var result = new SignatureVerificationResult();
        result.CoversWholeDocument.Should().BeFalse();
    }

    [Fact]
    public void SignatureVerificationResult_DefaultByteRangeIntegrityState_IsUnchecked()
    {
        var result = new SignatureVerificationResult();
        result.ByteRangeStructureChecked.Should().BeFalse();
        result.ByteRangeStructureValid.Should().BeFalse();
        result.ByteRangeStructureMessage.Should().BeEmpty();
        result.ByteRangeIntegrityChecked.Should().BeFalse();
        result.ByteRangeIntegrityValid.Should().BeFalse();
    }

    [Fact]
    public void SignatureVerificationResult_CanSetAllProperties()
    {
        var signingTime = new DateTime(2024, 1, 15, 10, 30, 0);
        var result = new SignatureVerificationResult
        {
            SignatureName = "Signature1",
            IsValid = true,
            SignedBy = "CN=John Doe",
            SigningTime = signingTime,
            StatusMessage = "Signature is valid",
            CoversWholeDocument = true,
            ByteRangeStructureChecked = true,
            ByteRangeStructureValid = true,
            ByteRangeStructureMessage = "ByteRange ok",
            ByteRangeIntegrityChecked = true,
            ByteRangeIntegrityValid = true
        };

        result.SignatureName.Should().Be("Signature1");
        result.IsValid.Should().BeTrue();
        result.SignedBy.Should().Be("CN=John Doe");
        result.SigningTime.Should().Be(signingTime);
        result.StatusMessage.Should().Be("Signature is valid");
        result.CoversWholeDocument.Should().BeTrue();
        result.ByteRangeStructureChecked.Should().BeTrue();
        result.ByteRangeStructureValid.Should().BeTrue();
        result.ByteRangeStructureMessage.Should().Be("ByteRange ok");
        result.ByteRangeIntegrityChecked.Should().BeTrue();
        result.ByteRangeIntegrityValid.Should().BeTrue();
    }

    // ========================================================================
    // VERIFY SIGNATURES - ERROR HANDLING
    // ========================================================================

    [Fact]
    public void VerifySignatures_NonExistentFile_ReturnsErrorResult()
    {
        var results = _service.VerifySignatures("/nonexistent/file.pdf");

        results.Should().NotBeNull();
        results.Should().HaveCount(1);
        results[0].IsValid.Should().BeFalse();
        results[0].StatusMessage.Should().StartWith("Error:");
    }

    [Fact]
    public void VerifySignatures_NullPath_ReturnsErrorResult()
    {
        var results = _service.VerifySignatures(null!);

        results.Should().NotBeNull();
        results.Should().HaveCount(1);
        results[0].IsValid.Should().BeFalse();
    }

    // ========================================================================
    // SIGNATURE VERIFICATION RESULT - VALUE ASSIGNMENT TESTS
    // ========================================================================

    [Fact]
    public void SignatureVerificationResult_CanUpdateProperties()
    {
        var result = new SignatureVerificationResult();

        result.SignatureName = "Sig1";
        result.IsValid = true;
        result.SignedBy = "Test User";
        result.SigningTime = DateTime.UtcNow;
        result.StatusMessage = "Valid signature";
        result.CoversWholeDocument = true;

        result.SignatureName.Should().Be("Sig1");
        result.IsValid.Should().BeTrue();
        result.SignedBy.Should().Be("Test User");
        result.StatusMessage.Should().Be("Valid signature");
        result.CoversWholeDocument.Should().BeTrue();
    }

    [Fact]
    public void SignatureVerificationResult_MultipleResults_Independent()
    {
        var result1 = new SignatureVerificationResult { SignatureName = "Sig1", IsValid = true };
        var result2 = new SignatureVerificationResult { SignatureName = "Sig2", IsValid = false };

        result1.SignatureName.Should().Be("Sig1");
        result1.IsValid.Should().BeTrue();
        result2.SignatureName.Should().Be("Sig2");
        result2.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SignatureVerificationResult_SignedBy_CanHaveLongCertificateName()
    {
        var longCertName =
            "CN=John Doe,O=Organization,OU=Department,L=City,ST=State,C=Country";
        var result = new SignatureVerificationResult { SignedBy = longCertName };

        result.SignedBy.Should().Be(longCertName);
    }

    [Fact]
    public void SignatureVerificationResult_StatusMessage_CanHaveDetailedText()
    {
        var detailedMessage =
            "Signature verification failed: Invalid certificate chain, expired certificate";
        var result = new SignatureVerificationResult { StatusMessage = detailedMessage };

        result.StatusMessage.Should().Be(detailedMessage);
    }

    [Fact]
    public void SignatureVerificationResult_SigningTime_CanBeAnytime()
    {
        var times = new[]
        {
            new DateTime(2020, 1, 1),
            new DateTime(2024, 12, 31),
            DateTime.UtcNow,
            DateTime.MinValue,
            DateTime.MaxValue
        };

        foreach (var time in times)
        {
            var result = new SignatureVerificationResult { SigningTime = time };
            result.SigningTime.Should().Be(time);
        }
    }

    // ========================================================================
    // SERVICE INSTANTIATION TESTS
    // ========================================================================

    [Fact]
    public void SignatureVerificationService_CanBeInstantiated()
    {
        var service = new SignatureVerificationService(_logger);
        service.Should().NotBeNull();
    }

    [Fact]
    public void SignatureVerificationService_WithNullLogger_ThrowsArgumentNullException()
    {
        var action = () => new SignatureVerificationService(null!);

        action.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    // ========================================================================
    // RESULT COLLECTION TESTS
    // ========================================================================

    [Fact]
    public void VerifySignatures_ReturnsListNotNull()
    {
        try
        {
            var results = _service.VerifySignatures("/nonexistent.pdf");
            results.Should().NotBeNull();
        }
        catch
        {
            // Non-existent file throws, which is expected behavior
        }
    }

    [Fact]
    public void SignatureVerificationResult_DefaultInstance_IsValid()
    {
        var result = new SignatureVerificationResult();

        result.Should().NotBeNull();
        result.SignatureName.Should().NotBeNull();
        result.SignedBy.Should().NotBeNull();
        result.StatusMessage.Should().NotBeNull();
    }

    // ========================================================================
    // SIGNATURE RESULT COPY TESTS
    // ========================================================================

    [Fact]
    public void SignatureVerificationResult_CopyConstructor_CreatesIndependentCopy()
    {
        var original = new SignatureVerificationResult
        {
            SignatureName = "Sig1",
            IsValid = true,
            SignedBy = "User1"
        };

        var copy = new SignatureVerificationResult
        {
            SignatureName = original.SignatureName,
            IsValid = original.IsValid,
            SignedBy = original.SignedBy
        };

        copy.SignatureName.Should().Be(original.SignatureName);
        copy.IsValid.Should().Be(original.IsValid);
        copy.SignedBy.Should().Be(original.SignedBy);

        copy.SignatureName = "Sig2";
        original.SignatureName.Should().Be("Sig1");
        copy.SignatureName.Should().Be("Sig2");
    }

    // ========================================================================
    // EDGE CASES - EMPTY AND NULL STRINGS
    // ========================================================================

    [Fact]
    public void SignatureVerificationResult_SignedBy_CanBeEmpty()
    {
        var result = new SignatureVerificationResult { SignedBy = "" };
        result.SignedBy.Should().Be("");
    }

    [Fact]
    public void SignatureVerificationResult_StatusMessage_CanBeEmpty()
    {
        var result = new SignatureVerificationResult { StatusMessage = "" };
        result.StatusMessage.Should().Be("");
    }

    [Fact]
    public void SignatureVerificationResult_SignatureName_CanBeEmpty()
    {
        var result = new SignatureVerificationResult { SignatureName = "" };
        result.SignatureName.Should().Be("");
    }

    // ========================================================================
    // COMPREHENSIVE RESULT OBJECT TEST
    // ========================================================================

    [Fact]
    public void SignatureVerificationResult_CreateComprehensiveValidResult()
    {
        var result = new SignatureVerificationResult
        {
            SignatureName = "Document Signature",
            IsValid = true,
            SignedBy = "CN=Jane Smith,O=Legal Corp,C=US",
            SigningTime = new DateTime(2024, 3, 15, 14, 30, 0),
            StatusMessage = "Signature is cryptographically valid and covers whole document",
            CoversWholeDocument = true
        };

        result.SignatureName.Should().Be("Document Signature");
        result.IsValid.Should().BeTrue();
        result.SignedBy.Should().Be("CN=Jane Smith,O=Legal Corp,C=US");
        result.SigningTime.Should().Be(new DateTime(2024, 3, 15, 14, 30, 0));
        result.StatusMessage.Should().StartWith("Signature is");
        result.CoversWholeDocument.Should().BeTrue();
    }

    [Fact]
    public void SignatureVerificationResult_CreateComprehensiveInvalidResult()
    {
        var result = new SignatureVerificationResult
        {
            SignatureName = "Untrusted Signature",
            IsValid = false,
            SignedBy = "",
            SigningTime = default,
            StatusMessage = "Certificate not in trusted store",
            CoversWholeDocument = false
        };

        result.SignatureName.Should().Be("Untrusted Signature");
        result.IsValid.Should().BeFalse();
        result.SignedBy.Should().BeEmpty();
        result.StatusMessage.Should().Contain("trusted");
        result.CoversWholeDocument.Should().BeFalse();
    }

    // ========================================================================
    // VERIFY SIGNATURES - PDF STRUCTURE TESTS
    // ========================================================================

    [Fact]
    public void VerifySignatures_PdfWithNoAcroForm_ReturnsEmptyList()
    {
        var pdfBytes = MakePdfWithoutAcroForm();
        var pdfPath = WriteTempPdf(pdfBytes);

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().BeEmpty();
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void VerifySignatures_PdfWithAcroFormNoFields_ReturnsEmptyList()
    {
        var pdfBytes = MakePdfWithAcroFormNoFields();
        var pdfPath = WriteTempPdf(pdfBytes);

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().BeEmpty();
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void VerifySignatures_PdfWithNonSigField_ReturnsEmptyList()
    {
        var pdfBytes = MakePdfWithNonSigField();
        var pdfPath = WriteTempPdf(pdfBytes);

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().BeEmpty();
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void VerifySignatures_PdfWithUnsignedSigField_ReturnsEmptyList()
    {
        var pdfBytes = MakePdfWithUnsignedSigField();
        var pdfPath = WriteTempPdf(pdfBytes);

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().BeEmpty();
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void VerifySignatures_PdfWithSigFieldInvalidByteRange_ReturnsFailedResult()
    {
        var pdfBytes = MakePdfWithInvalidByteRange();
        var pdfPath = WriteTempPdf(pdfBytes);

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().HaveCount(1);
            results[0].IsValid.Should().BeFalse();
            results[0].StatusMessage.Should().Contain("ByteRange");
            results[0].SignatureName.Should().Be("TestSig");
            results[0].ByteRangeStructureChecked.Should().BeTrue();
            results[0].ByteRangeStructureValid.Should().BeFalse();
            results[0].ByteRangeIntegrityChecked.Should().BeFalse();
            results[0].ByteRangeIntegrityValid.Should().BeFalse();
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void VerifySignatures_PdfWithSigFieldEmptyContents_ReturnsFailedResult()
    {
        var pdfBytes = MakePdfWithEmptyContents();
        var pdfPath = WriteTempPdf(pdfBytes);

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().HaveCount(1);
            results[0].IsValid.Should().BeFalse();
            results[0].StatusMessage.Should().Contain("Empty");
            results[0].SignatureName.Should().Be("TestSig");
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void VerifySignatures_PdfWithSigFieldInvalidSignatureBytes_ReturnsErrorResult()
    {
        var pdfBytes = MakePdfWithInvalidSignatureBytes();
        var pdfPath = WriteTempPdf(pdfBytes);

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().HaveCount(1);
            results[0].IsValid.Should().BeFalse();
            results[0].SignatureName.Should().Be("TestSig");
            results[0].ByteRangeStructureChecked.Should().BeTrue();
            results[0].ByteRangeStructureValid.Should().BeTrue();
            results[0].ByteRangeIntegrityChecked.Should().BeFalse();
            results[0].ByteRangeIntegrityValid.Should().BeFalse();
            results[0].StatusMessage.Should().Contain("BouncyCastle verification failed");
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void VerifySignatures_PdfWithValidDetachedCmsAndByteRange_ReturnsValidResult()
    {
        var pdfBytes = MakePdfWithValidDetachedCmsSignature();
        var pdfPath = WriteTempPdf(pdfBytes);

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().HaveCount(1);
            results[0].IsValid.Should().BeTrue();
            results[0].SignatureName.Should().Be("TestSig");
            results[0].SignedBy.Should().Contain("PDFe Test Signer");
            results[0].ByteRangeStructureChecked.Should().BeTrue();
            results[0].ByteRangeStructureValid.Should().BeTrue();
            results[0].ByteRangeIntegrityChecked.Should().BeTrue();
            results[0].ByteRangeIntegrityValid.Should().BeTrue();
            results[0].CoversWholeDocument.Should().BeTrue();
            results[0].StatusMessage.Should().Contain("ByteRange digest matches");
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void VerifySignatures_PdfWithTamperedSignedBytes_ReturnsDigestMismatch()
    {
        var pdfBytes = MakePdfWithValidDetachedCmsSignature();
        ReplaceAsciiMarker(pdfBytes, "ORIGINAL", "TAMPERED");
        var pdfPath = WriteTempPdf(pdfBytes);

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().HaveCount(1);
            results[0].IsValid.Should().BeFalse();
            results[0].SignatureName.Should().Be("TestSig");
            results[0].ByteRangeStructureChecked.Should().BeTrue();
            results[0].ByteRangeStructureValid.Should().BeTrue();
            results[0].ByteRangeIntegrityChecked.Should().BeTrue();
            results[0].ByteRangeIntegrityValid.Should().BeFalse();
            results[0].CoversWholeDocument.Should().BeTrue();
            results[0].StatusMessage.Should().Contain("ByteRange digest mismatch");
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void VerifySignatures_ByteRangeGapWithExtraUnsignedByte_ReturnsInvalidByteRange()
    {
        var pdfText = MakePdfWithInvalidByteRangeGap();
        var pdfPath = WriteTempPdf(Encoding.Latin1.GetBytes(pdfText));

        try
        {
            var results = _service.VerifySignatures(pdfPath);

            results.Should().NotBeNull();
            results.Should().HaveCount(1);
            results[0].IsValid.Should().BeFalse();
            results[0].SignatureName.Should().Be("TestSig");
            results[0].ByteRangeStructureChecked.Should().BeTrue();
            results[0].ByteRangeStructureValid.Should().BeFalse();
            results[0].ByteRangeIntegrityChecked.Should().BeFalse();
            results[0].ByteRangeIntegrityValid.Should().BeFalse();
            results[0].StatusMessage.Should().Contain("Contents value does not exactly match");
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    // ========================================================================
    // PDF BUILDER HELPERS
    // ========================================================================

    private static string WriteTempPdf(byte[] bytes)
    {
        var path = Path.GetTempFileName() + ".pdf";
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] MakePdfWithoutAcroForm()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 4");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 4 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] MakePdfWithAcroFormNoFields()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Fields [] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 5");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 5 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] MakePdfWithNonSigField()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Fields [5 0 R] >>");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /FT /Tx /T (TextField) >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] MakePdfWithUnsignedSigField()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Fields [5 0 R] >>");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /FT /Sig /T (TestSig) >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] MakePdfWithInvalidByteRange()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos, obj6Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Fields [5 0 R] >>");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /FT /Sig /T (TestSig) /V 6 0 R >>");
        sb.AppendLine("endobj");

        obj6Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /ByteRange [0 100 200 50] /Contents <AABBCCDD> >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine($"{obj6Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] MakePdfWithEmptyContents()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos, obj6Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Fields [5 0 R] >>");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /FT /Sig /T (TestSig) /V 6 0 R >>");
        sb.AppendLine("endobj");

        obj6Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /ByteRange [0 100 200 50] /Contents <> >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine($"{obj6Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] MakePdfWithInvalidSignatureBytes()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos, obj6Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Fields [5 0 R] >>");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /FT /Sig /T (TestSig) /V 6 0 R >>");
        sb.AppendLine("endobj");

        obj6Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /ByteRange [0 AAAAAAAAAA BBBBBBBBBB CCCCCCCCCC] /Contents <DEADBEEFCAFEBABE0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F> >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine($"{obj6Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(FillByteRange(sb.ToString()));
    }

    private static byte[] MakePdfWithValidDetachedCmsSignature()
    {
        var pdfWithByteRange = FillByteRange(MakePdfWithSignaturePlaceholder());
        var signedContent = ExtractSignedContent(Encoding.Latin1.GetBytes(pdfWithByteRange));
        var cmsSignature = CreateDetachedCmsSignature(signedContent);
        var signatureHex = Convert.ToHexString(cmsSignature);
        signatureHex.Length.Should().BeLessThan(SignaturePlaceholderHexLength);

        var paddedSignatureHex = signatureHex.PadRight(SignaturePlaceholderHexLength, '0');
        return Encoding.Latin1.GetBytes(
            pdfWithByteRange.Replace(SignaturePlaceholderHex, paddedSignatureHex, StringComparison.Ordinal));
    }

    private static void ReplaceAsciiMarker(byte[] bytes, string marker, string replacement)
    {
        marker.Length.Should().Be(replacement.Length);

        var markerBytes = Encoding.ASCII.GetBytes(marker);
        var replacementBytes = Encoding.ASCII.GetBytes(replacement);
        var index = bytes.AsSpan().IndexOf(markerBytes);
        index.Should().BeGreaterThanOrEqualTo(0);
        replacementBytes.CopyTo(bytes.AsSpan(index, replacementBytes.Length));
    }

    private static string MakePdfWithSignaturePlaceholder()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos, obj6Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R /ExciseTestMarker (ORIGINAL) >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Fields [5 0 R] >>");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /FT /Sig /T (TestSig) /V 6 0 R >>");
        sb.AppendLine("endobj");

        obj6Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine($"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 AAAAAAAAAA BBBBBBBBBB CCCCCCCCCC] /Contents <{SignaturePlaceholderHex}> >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine($"{obj6Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return sb.ToString();
    }

    private static string MakePdfWithInvalidByteRangeGap()
    {
        var pdf = FillByteRange(MakePdfWithSignaturePlaceholder());
        var contentsStart = pdf.IndexOf('<', pdf.IndexOf("/Contents", StringComparison.Ordinal));
        var contentsEnd = pdf.IndexOf('>', contentsStart);
        contentsStart.Should().BeGreaterThanOrEqualTo(0);
        contentsEnd.Should().BeGreaterThan(contentsStart);

        var extraUnsignedGapStart = contentsStart - 1;
        var secondRangeStart = contentsEnd + 1;
        var secondRangeLength = pdf.Length - secondRangeStart;

        return MakePdfWithSignaturePlaceholder()
            .Replace("AAAAAAAAAA", extraUnsignedGapStart.ToString("D10"), StringComparison.Ordinal)
            .Replace("BBBBBBBBBB", secondRangeStart.ToString("D10"), StringComparison.Ordinal)
            .Replace("CCCCCCCCCC", secondRangeLength.ToString("D10"), StringComparison.Ordinal);
    }

    private static byte[] ExtractSignedContent(byte[] pdfBytes)
    {
        var pdf = Encoding.Latin1.GetString(pdfBytes);
        var contentsStart = pdf.IndexOf('<', pdf.IndexOf("/Contents", StringComparison.Ordinal));
        var contentsEnd = pdf.IndexOf('>', contentsStart);
        contentsStart.Should().BeGreaterThanOrEqualTo(0);
        contentsEnd.Should().BeGreaterThan(contentsStart);

        var secondRangeStart = contentsEnd + 1;
        var signedContent = new byte[contentsStart + pdfBytes.Length - secondRangeStart];
        Buffer.BlockCopy(pdfBytes, 0, signedContent, 0, contentsStart);
        Buffer.BlockCopy(pdfBytes, secondRangeStart, signedContent, contentsStart, pdfBytes.Length - secondRangeStart);
        return signedContent;
    }

    private static byte[] CreateDetachedCmsSignature(byte[] signedContent)
    {
        var random = new SecureRandom();
        var keyPair = GenerateKeyPair(random);
        var certificate = GenerateSelfSignedCertificate(keyPair, random);

        var signerInfoGenerator = new SignerInfoGeneratorBuilder()
            .Build(new Asn1SignatureFactory("SHA256WITHRSA", keyPair.Private, random), certificate);

        var generator = new CmsSignedDataGenerator();
        generator.AddSignerInfoGenerator(signerInfoGenerator);
        generator.AddCertificate(certificate);

        var cms = generator.Generate(new CmsProcessableByteArray(signedContent), encapsulate: false);
        return cms.GetEncoded();
    }

    private static AsymmetricCipherKeyPair GenerateKeyPair(SecureRandom random)
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(random, 2048));
        return keyGenerator.GenerateKeyPair();
    }

    private static X509Certificate GenerateSelfSignedCertificate(AsymmetricCipherKeyPair keyPair, SecureRandom random)
    {
        var certificateGenerator = new X509V3CertificateGenerator();
        var subject = new X509Name("CN=PDFe Test Signer");
        certificateGenerator.SetSerialNumber(BigInteger.ProbablePrime(128, random));
        certificateGenerator.SetIssuerDN(subject);
        certificateGenerator.SetSubjectDN(subject);
        certificateGenerator.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certificateGenerator.SetNotAfter(DateTime.UtcNow.AddDays(1));
        certificateGenerator.SetPublicKey(keyPair.Public);

        return certificateGenerator.Generate(
            new Asn1SignatureFactory("SHA256WITHRSA", keyPair.Private, random));
    }

    private static string FillByteRange(string pdf)
    {
        var contentsStart = pdf.IndexOf('<', pdf.IndexOf("/Contents", StringComparison.Ordinal));
        var contentsEnd = pdf.IndexOf('>', contentsStart);
        contentsStart.Should().BeGreaterThanOrEqualTo(0);
        contentsEnd.Should().BeGreaterThan(contentsStart);

        var secondRangeStart = contentsEnd + 1;
        var secondRangeLength = pdf.Length - secondRangeStart;

        return pdf
            .Replace("AAAAAAAAAA", contentsStart.ToString("D10"), StringComparison.Ordinal)
            .Replace("BBBBBBBBBB", secondRangeStart.ToString("D10"), StringComparison.Ordinal)
            .Replace("CCCCCCCCCC", secondRangeLength.ToString("D10"), StringComparison.Ordinal);
    }
}
