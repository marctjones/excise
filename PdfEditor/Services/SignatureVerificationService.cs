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
            if (!TryReadByteRange(byteRangeArray, out var byteRange, out var byteRangeError) ||
                !TryExtractSignedContent(fileBytes, byteRange, out var signedContent, out byteRangeError) ||
                !TryValidateContentsGap(fileBytes, byteRange, out byteRangeError))
            {
                result.IsValid = false;
                result.StatusMessage = $"Invalid ByteRange: {byteRangeError}";
                results.Add(result);
                return;
            }

            result.CoversWholeDocument =
                byteRange[0] == 0 &&
                byteRange[2] + byteRange[3] == fileBytes.Length;

            // 3. Verify the detached CMS signature over the exact document
            // bytes specified by /ByteRange. This checks both the signer
            // signature and the message digest for those byte ranges.
            VerifySignatureBytes(signatureBytes, signedContent, result);

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

    private static bool TryReadByteRange(PdfArray byteRangeArray, out long[] byteRange, out string error)
    {
        byteRange = Array.Empty<long>();
        error = string.Empty;

        if (byteRangeArray.Count != 4)
        {
            error = "expected exactly four numbers";
            return false;
        }

        var values = new long[4];
        for (var i = 0; i < byteRangeArray.Count; i++)
        {
            if (!TryReadInteger(byteRangeArray[i], out values[i]))
            {
                error = $"entry {i} is not an integer";
                return false;
            }
        }

        byteRange = values;
        return true;
    }

    private static bool TryReadInteger(PdfObject value, out long integer)
    {
        switch (value)
        {
            case PdfInteger pdfInteger:
                integer = pdfInteger.Value;
                return true;
            case PdfReal pdfReal when Math.Abs(pdfReal.Value - Math.Round(pdfReal.Value)) < 0.0000001:
                integer = (long)Math.Round(pdfReal.Value);
                return true;
            default:
                integer = 0;
                return false;
        }
    }

    private static bool TryExtractSignedContent(byte[] fileBytes, long[] byteRange, out byte[] signedContent, out string error)
    {
        signedContent = Array.Empty<byte>();
        error = string.Empty;

        var start1 = byteRange[0];
        var length1 = byteRange[1];
        var start2 = byteRange[2];
        var length2 = byteRange[3];

        if (start1 < 0 || length1 < 0 || start2 < 0 || length2 < 0)
        {
            error = "offsets and lengths must be non-negative";
            return false;
        }

        if (start1 != 0)
        {
            error = "first range must start at byte 0";
            return false;
        }

        if (!RangeWithinFile(start1, length1, fileBytes.Length) ||
            !RangeWithinFile(start2, length2, fileBytes.Length))
        {
            error = "range extends beyond end of file";
            return false;
        }

        if (start1 + length1 > start2)
        {
            error = "ranges overlap";
            return false;
        }

        var totalLength = length1 + length2;
        if (totalLength > int.MaxValue)
        {
            error = "signed byte ranges are too large to verify in memory";
            return false;
        }

        signedContent = new byte[checked((int)totalLength)];
        Buffer.BlockCopy(fileBytes, checked((int)start1), signedContent, 0, checked((int)length1));
        Buffer.BlockCopy(fileBytes, checked((int)start2), signedContent, checked((int)length1), checked((int)length2));
        return true;

        static bool RangeWithinFile(long start, long length, int fileLength) =>
            start <= fileLength &&
            length <= fileLength - start;
    }

    private static bool TryValidateContentsGap(byte[] fileBytes, long[] byteRange, out string error)
    {
        error = string.Empty;

        var gapStart = byteRange[0] + byteRange[1];
        var gapEnd = byteRange[2];
        if (gapStart > gapEnd)
        {
            error = "excluded signature gap is invalid";
            return false;
        }

        var searchStart = 0;
        var parsedContentsToken = false;
        while (TryFindAscii(fileBytes, "/Contents"u8, searchStart, out var contentsNameStart))
        {
            var valueStart = SkipWhiteSpace(fileBytes, contentsNameStart + "/Contents".Length);
            if (valueStart >= fileBytes.Length)
            {
                error = "could not locate /Contents value";
                return false;
            }

            if (TryReadStringTokenEnd(fileBytes, valueStart, out var valueEnd))
            {
                parsedContentsToken = true;
                if (valueStart == gapStart && valueEnd == gapEnd)
                {
                    return true;
                }
            }

            searchStart = contentsNameStart + "/Contents".Length;
        }

        error = parsedContentsToken
            ? "/Contents value does not exactly match the unsigned ByteRange gap"
            : "could not locate /Contents string in file bytes";
        return false;
    }

    private static bool TryFindAscii(byte[] fileBytes, ReadOnlySpan<byte> value, int startIndex, out int index)
    {
        for (var i = Math.Max(0, startIndex); i <= fileBytes.Length - value.Length; i++)
        {
            if (fileBytes.AsSpan(i, value.Length).SequenceEqual(value))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static int SkipWhiteSpace(byte[] fileBytes, int startIndex)
    {
        var index = startIndex;
        while (index < fileBytes.Length && IsPdfWhiteSpace(fileBytes[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsPdfWhiteSpace(byte value) =>
        value is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static bool TryReadStringTokenEnd(byte[] fileBytes, int valueStart, out long valueEnd)
    {
        valueEnd = 0;
        if (fileBytes[valueStart] == (byte)'<' &&
            valueStart + 1 < fileBytes.Length &&
            fileBytes[valueStart + 1] != (byte)'<')
        {
            for (var i = valueStart + 1; i < fileBytes.Length; i++)
            {
                if (fileBytes[i] == (byte)'>')
                {
                    valueEnd = i + 1L;
                    return true;
                }
            }

            return false;
        }

        if (fileBytes[valueStart] != (byte)'(')
        {
            return false;
        }

        var depth = 1;
        var escaped = false;
        for (var i = valueStart + 1; i < fileBytes.Length; i++)
        {
            var value = fileBytes[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (value == (byte)'\\')
            {
                escaped = true;
                continue;
            }

            if (value == (byte)'(')
            {
                depth++;
            }
            else if (value == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    valueEnd = i + 1L;
                    return true;
                }
            }
        }

        return false;
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
