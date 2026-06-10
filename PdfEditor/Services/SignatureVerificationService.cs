using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.X509;
using System.Linq;

namespace PdfEditor.Services;

public class SignatureVerificationResult
{
    public string SignatureName { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string SignedBy { get; set; } = string.Empty;
    public DateTime SigningTime { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public bool CoversWholeDocument { get; set; }
    public bool ByteRangeStructureChecked { get; set; }
    public bool ByteRangeStructureValid { get; set; }
    public string ByteRangeStructureMessage { get; set; } = string.Empty;
    public bool ByteRangeIntegrityChecked { get; set; }
    public bool ByteRangeIntegrityValid { get; set; }
}

/// <summary>
/// Service for verifying digital signatures in PDF documents
/// Uses Pdfe.Core for parsing and BouncyCastle for cryptographic validation
/// </summary>
public class SignatureVerificationService
{
    private readonly ILogger<SignatureVerificationService> _logger;

    public SignatureVerificationService(ILogger<SignatureVerificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public List<SignatureVerificationResult> VerifySignatures(string pdfPath)
    {
        var results = new List<SignatureVerificationResult>();
        _logger.LogInformation("Verifying signatures for {File}", Path.GetFileName(pdfPath));

        try
        {
            // We use Pdfe.Core to open the document and find signature dictionaries
            using var document = PdfDocument.Open(pdfPath);

            // 1. Find the AcroForm dictionary
            var acroFormObj = document.Catalog.GetOptional("AcroForm");
            if (acroFormObj == null)
            {
                _logger.LogInformation("No AcroForm found, document has no signatures.");
                return results;
            }

            var acroForm = document.Resolve(acroFormObj) as PdfDictionary;
            if (acroForm == null)
            {
                _logger.LogInformation("AcroForm is not a dictionary.");
                return results;
            }

            // 2. Get Fields array
            var fieldsObj = acroForm.GetOptional("Fields");
            if (fieldsObj == null)
            {
                _logger.LogInformation("No fields found in AcroForm.");
                return results;
            }

            var fields = document.Resolve(fieldsObj) as PdfArray;
            if (fields == null)
            {
                _logger.LogInformation("Fields is not an array.");
                return results;
            }

            // 3. Iterate fields to find signatures
            foreach (var item in fields)
            {
                var fieldDict = document.Resolve(item) as PdfDictionary;
                if (fieldDict != null)
                {
                    CheckFieldForSignature(document, fieldDict, pdfPath, results);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying signatures");
            results.Add(new SignatureVerificationResult 
            { 
                StatusMessage = $"Error: {ex.Message}", 
                IsValid = false 
            });
        }

        return results;
    }

    private void CheckFieldForSignature(PdfDocument document, PdfDictionary fieldDict, string pdfPath, List<SignatureVerificationResult> results)
    {
        // Check if it's a signature field (FT = Sig)
        var type = fieldDict.GetNameOrNull("FT");
        if (type != "Sig") return;

        var name = fieldDict.GetStringOrNull("T") ?? "Unknown";
        _logger.LogInformation("Found signature field: {Name}", name);

        // Get the signature value dictionary (V)
        var valueObj = fieldDict.GetOptional("V");
        if (valueObj == null)
        {
            _logger.LogWarning("Signature field {Name} has no value dictionary (unsigned)", name);
            return;
        }

        var valueDict = document.Resolve(valueObj) as PdfDictionary;
        if (valueDict == null)
        {
            _logger.LogWarning("Signature field {Name} value is not a dictionary", name);
            return;
        }

        var result = new SignatureVerificationResult { SignatureName = name };

        try
        {
            // 1. Get ByteRange
            var byteRangeObj = valueDict.GetOptional("ByteRange");
            var byteRangeArray = byteRangeObj != null ? document.Resolve(byteRangeObj) as PdfArray : null;
            if (byteRangeArray == null)
            {
                result.IsValid = false;
                result.ByteRangeStructureChecked = true;
                result.ByteRangeStructureValid = false;
                result.ByteRangeStructureMessage = "missing or invalid ByteRange array";
                result.StatusMessage = "Invalid or missing ByteRange";
                results.Add(result);
                return;
            }

            // 2. Get Contents (the PKCS#7 signature). Signature contents are
            // binary data; using the decoded string value corrupts arbitrary
            // CMS bytes before BouncyCastle sees them.
            var contentsObj = valueDict.GetOptional("Contents");
            var contents = contentsObj != null ? document.Resolve(contentsObj) as PdfString : null;
            if (contents == null || contents.Bytes.Length == 0)
            {
                result.IsValid = false;
                result.StatusMessage = "Empty signature content";
                results.Add(result);
                return;
            }

            byte[] signatureBytes = TrimDerPadding(contents.Bytes);

            var fileBytes = File.ReadAllBytes(pdfPath);
            var byteRangeValidation = SignatureByteRangeValidator.Validate(byteRangeArray, fileBytes);
            result.ByteRangeStructureChecked = true;
            result.ByteRangeStructureValid = byteRangeValidation.IsValid;
            result.ByteRangeStructureMessage = byteRangeValidation.IsValid
                ? "ByteRange is well-formed and excludes exactly the signature /Contents value"
                : byteRangeValidation.Error;

            if (!byteRangeValidation.IsValid)
            {
                result.IsValid = false;
                result.StatusMessage = $"Invalid ByteRange: {byteRangeValidation.Error}";
                results.Add(result);
                return;
            }

            result.CoversWholeDocument = byteRangeValidation.CoversWholeDocument;

            // 3. Verify the detached CMS signature over the exact document
            // bytes specified by /ByteRange. This checks both the signer
            // signature and the message digest for those byte ranges.
            VerifySignatureBytes(signatureBytes, byteRangeValidation.SignedContent, result);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify signature {Name}", name);
            result.IsValid = false;
            result.StatusMessage = $"Verification failed: {ex.Message}";
        }

        results.Add(result);
    }

    private void VerifySignatureBytes(byte[] signatureBytes, byte[] signedContent, SignatureVerificationResult result)
    {
        try
        {
            var cms = new CmsSignedData(new CmsProcessableByteArray(signedContent), signatureBytes);
            // BouncyCastle.Cryptography 2.x dropped the legacy
            // `GetCertificates("Collection")` overload and the
            // IStore.GetMatches(selector) API. The new shape is
            // CmsSignedData.GetCertificates() returning IStore<X509Certificate>,
            // queried via EnumerateMatches(selector). selector=null returns
            // every certificate in the bundle.
            var store = cms.GetCertificates();
            var signers = cms.GetSignerInfos();
            var signerFound = false;
            var certificateFound = false;

            foreach (SignerInformation signer in signers.GetSigners())
            {
                signerFound = true;
                var certCollection = store.EnumerateMatches(signer.SignerID);
                foreach (X509Certificate cert in certCollection)
                {
                    certificateFound = true;
                    result.SignedBy = cert.SubjectDN.ToString();
                    bool signatureValid;
                    try
                    {
                        signatureValid = signer.Verify(cert);
                    }
                    catch (Exception ex)
                    {
                        result.IsValid = false;
                        result.ByteRangeIntegrityChecked = true;
                        result.ByteRangeIntegrityValid = false;
                        result.StatusMessage = $"Signature verification failed or ByteRange digest mismatch: {ex.Message}";
                        return;
                    }

                    if (signatureValid)
                    {
                        result.IsValid = true;
                        result.ByteRangeIntegrityChecked = true;
                        result.ByteRangeIntegrityValid = true;
                        result.StatusMessage = "Signature is cryptographically valid and ByteRange digest matches";
                    }
                    else
                    {
                        result.IsValid = false;
                        result.ByteRangeIntegrityChecked = true;
                        result.ByteRangeIntegrityValid = false;
                        result.StatusMessage = "Signature verification failed or ByteRange digest mismatch";
                    }
                }
            }

            if (!signerFound)
            {
                result.IsValid = false;
                result.StatusMessage = "No signer information found in CMS signature";
            }
            else if (!certificateFound)
            {
                result.IsValid = false;
                result.StatusMessage = "No matching signing certificate found in CMS signature";
            }
        }
        catch (Exception ex)
        {
            throw new Exception("BouncyCastle verification failed", ex);
        }
    }

    private static byte[] TrimDerPadding(byte[] signatureBytes)
    {
        if (signatureBytes.Length < 2 || signatureBytes[0] != 0x30)
        {
            return signatureBytes;
        }

        var lengthByte = signatureBytes[1];
        int lengthOffset;
        int contentLength;

        if ((lengthByte & 0x80) == 0)
        {
            lengthOffset = 2;
            contentLength = lengthByte;
        }
        else
        {
            var lengthByteCount = lengthByte & 0x7F;
            if (lengthByteCount == 0 || lengthByteCount > 4 || signatureBytes.Length < 2 + lengthByteCount)
            {
                return signatureBytes;
            }

            lengthOffset = 2 + lengthByteCount;
            contentLength = 0;
            for (var i = 0; i < lengthByteCount; i++)
            {
                contentLength = (contentLength << 8) | signatureBytes[2 + i];
            }
        }

        var totalLength = lengthOffset + contentLength;
        if (totalLength <= 0 || totalLength > signatureBytes.Length || totalLength == signatureBytes.Length)
        {
            return signatureBytes;
        }

        return signatureBytes.Take(totalLength).ToArray();
    }
}
