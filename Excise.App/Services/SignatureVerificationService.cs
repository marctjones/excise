using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.Core.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.X509;
using System.Linq;

namespace Excise.App.Services;

/// <summary>
/// Consolidated per-signature verdict combining cryptographic validity and
/// signer trust. Derived from the underlying check fields so the UI and tests
/// consume the same structured state and cannot disagree (#466).
/// </summary>
public enum SignatureVerificationState
{
    /// <summary>The signature could not be parsed or verified (malformed object, missing data, engine error).</summary>
    Indeterminate = 0,

    /// <summary>Verification ran and FAILED: the signed bytes do not match the signature, or the ByteRange does not cover the document correctly. Treat as modified-after-signing or a broken signature.</summary>
    Invalid,

    /// <summary>Cryptographically valid signature, but the signer certificate does NOT chain to a trusted root.</summary>
    ValidUntrusted,

    /// <summary>Cryptographically valid signature; signer trust was not evaluated or could not be determined.</summary>
    ValidTrustUnknown,

    /// <summary>Cryptographically valid signature AND the signer chains to a trusted root.</summary>
    ValidTrusted
}

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

    /// <summary>Signer certificate chain trust. Independent of, and additional to, cryptographic validity (#466).</summary>
    public SignatureTrustStatus TrustStatus { get; set; } = SignatureTrustStatus.NotEvaluated;
    public string TrustDetails { get; set; } = string.Empty;

    /// <summary>
    /// Consolidated verdict. Computed from the check fields (never stored) so a
    /// "trusted" state is impossible unless the signature is also
    /// cryptographically valid over the correct byte range.
    /// </summary>
    public SignatureVerificationState State
    {
        get
        {
            if (IsValid)
            {
                return TrustStatus switch
                {
                    SignatureTrustStatus.Trusted => SignatureVerificationState.ValidTrusted,
                    SignatureTrustStatus.Untrusted => SignatureVerificationState.ValidUntrusted,
                    _ => SignatureVerificationState.ValidTrustUnknown
                };
            }

            // Not valid: distinguish "verification ran and failed" (tampering /
            // broken signature) from "could not verify at all".
            if (ByteRangeIntegrityChecked && !ByteRangeIntegrityValid)
            {
                return SignatureVerificationState.Invalid;
            }

            if (ByteRangeStructureChecked && !ByteRangeStructureValid)
            {
                return SignatureVerificationState.Invalid;
            }

            return SignatureVerificationState.Indeterminate;
        }
    }
}

/// <summary>
/// Service for verifying digital signatures in PDF documents
/// Uses Excise.Core for parsing and BouncyCastle for cryptographic validation
/// </summary>
public class SignatureVerificationService
{
    private readonly ILogger<SignatureVerificationService> _logger;
    private readonly SignatureTrustEvaluator _trustEvaluator;

    public SignatureVerificationService(ILogger<SignatureVerificationService> logger)
        : this(logger, trustEvaluator: null)
    {
    }

    public SignatureVerificationService(
        ILogger<SignatureVerificationService> logger,
        SignatureTrustEvaluator? trustEvaluator)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _trustEvaluator = trustEvaluator ?? new SignatureTrustEvaluator();
    }

    public List<SignatureVerificationResult> VerifySignatures(string pdfPath)
    {
        var results = new List<SignatureVerificationResult>();
        _logger.LogInformation("Verifying signatures for {File}", Path.GetFileName(pdfPath));

        try
        {
            // We use Excise.Core to open the document and find signature dictionaries
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
                        ExtractSigningTime(signer, result);

                        // Trust is ADDITIONAL to validity, never a replacement:
                        // only a cryptographically valid signature earns a trust
                        // evaluation, and the consolidated State can only reach
                        // ValidTrusted through both checks (#466).
                        EvaluateTrust(cert, store, result);
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

    private void EvaluateTrust(
        X509Certificate signerCertificate,
        Org.BouncyCastle.Utilities.Collections.IStore<X509Certificate> certificateStore,
        SignatureVerificationResult result)
    {
        try
        {
            // Hand the evaluator every certificate embedded in the CMS bundle
            // so intermediates resolve without any network access.
            var candidates = certificateStore.EnumerateMatches(null)
                .Select(c => c.GetEncoded())
                .ToList();
            var trust = _trustEvaluator.Evaluate(signerCertificate.GetEncoded(), candidates);
            result.TrustStatus = trust.Status;
            result.TrustDetails = trust.Details;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trust-chain evaluation failed for signer {Signer}", result.SignedBy);
            result.TrustStatus = SignatureTrustStatus.Indeterminate;
            result.TrustDetails = $"trust evaluation failed: {ex.Message}";
        }
    }

    private void ExtractSigningTime(SignerInformation signer, SignatureVerificationResult result)
    {
        try
        {
            var attribute = signer.SignedAttributes?[Org.BouncyCastle.Asn1.Cms.CmsAttributes.SigningTime];
            if (attribute is { AttrValues.Count: > 0 })
            {
                result.SigningTime = Org.BouncyCastle.Asn1.Cms.Time
                    .GetInstance(attribute.AttrValues[0])
                    .ToDateTime();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract signing time for {Signer}", result.SignedBy);
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
