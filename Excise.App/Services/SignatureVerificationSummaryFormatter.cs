using Excise.Core.Signatures;
using System.Collections.Generic;
using System.Text;

namespace Excise.App.Services;

public sealed class SignatureVerificationSummaryFormatter
{
    public string Format(IReadOnlyList<SignatureVerificationResult> results)
    {
        var summary = new StringBuilder();

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (i > 0)
            {
                summary.AppendLine();
            }

            // #1205: the signature field name and the signer DN are both
            // document-supplied identity claims, and this summary is where a
            // user decides whether to trust the signature. A bidi override in a
            // Subject DN can make an untrusted signer read as a trusted one.
            summary.AppendLine($"Signature: {Safe(ValueOrUnknown(result.SignatureName))}");
            summary.AppendLine($"Overall: {FormatOverallState(result.State)}");
            summary.AppendLine($"CMS signature check: {(result.IsValid ? "passed" : "failed")} (CMS bytes and ByteRange digest only)");
            summary.AppendLine($"Signer: {Safe(ValueOrUnknown(result.SignedBy))}");
            if (Excise.Core.Text.UnicodeTextSafety.ContainsBidiControl(result.SignedBy))
            {
                summary.AppendLine(
                    "  \u26a0 The signer name contains text-direction control characters, " +
                    "which can make an identity display differently from what it is.");
            }
            summary.AppendLine(result.SigningTime == default
                ? "Signing time: not extracted"
                : $"Signing time: {result.SigningTime:g}");

            if (!string.IsNullOrWhiteSpace(result.StatusMessage))
            {
                summary.AppendLine($"Details: {Safe(result.StatusMessage)}");
            }

            if (result.UnsignedTrailingContentBytes > 0)
            {
                // #1494: ISO 32000-2 12.8.3.3.1 requires /Contents to be zero-padded.
                // Anything else sits inside the signature field, authenticated by neither
                // the ByteRange digest nor the CMS object — a place to carry data that a
                // "valid signature" verdict says nothing about.
                summary.AppendLine(
                    $"  ⚠ {result.UnsignedTrailingContentBytes} bytes of non-zero data follow the " +
                    "signature object inside /Contents. They are not covered by the signature.");
            }

            summary.AppendLine($"ByteRange structure: {FormatByteRangeStructureStatus(result)}");
            summary.AppendLine($"Signed byte-range digest: {FormatByteRangeDigestStatus(result)}");
            summary.AppendLine($"Covers whole document: {(result.CoversWholeDocument ? "yes" : "no")}");
            summary.AppendLine($"Certificate trust chain: {FormatTrustStatus(result)}");
        }

        if (results.Count > 0)
        {
            summary.AppendLine();
        }

        summary.AppendLine("Signer trust is checked against the configured certificate trust store; certificate revocation (CRL/OCSP) is not checked.");
        return summary.ToString().TrimEnd();
    }

    private static string ValueOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    // The strong phrase "trusted signer" is reserved for ValidTrusted: no other
    // state may emit wording that overclaims trust (#466 acceptance criterion).
    private static string FormatOverallState(SignatureVerificationState state) => state switch
    {
        SignatureVerificationState.ValidTrusted =>
            "VALID signature from a trusted signer (cryptographically valid; signer chains to a trusted root)",
        SignatureVerificationState.ValidUntrusted =>
            "valid signature, but the signer is UNTRUSTED (cryptographically valid; signer does not chain to a trusted root)",
        SignatureVerificationState.ValidTrustUnknown =>
            "valid signature; signer trust could not be determined",
        SignatureVerificationState.Invalid =>
            "INVALID — the document does not match the signature (modified after signing, or the signature is broken)",
        _ =>
            "could not be verified (malformed or unsupported signature)"
    };

    /// <summary>#1205 — make invisible/bidi controls explicit on a trust display.</summary>
    private static string Safe(string? value) =>
        Excise.Core.Text.UnicodeTextSafety.EscapeForDisplay(value);

    private static string FormatTrustStatus(SignatureVerificationResult result)
    {
        // #1205: TrustDetails is built from the certificate chain's Subject
        // DNs — document-embedded identity text on a trust display.
        var details = string.IsNullOrWhiteSpace(result.TrustDetails)
            ? string.Empty
            : $" — {Safe(result.TrustDetails)}";

        return result.TrustStatus switch
        {
            SignatureTrustStatus.Trusted => $"trusted{details}",
            SignatureTrustStatus.Untrusted => $"UNTRUSTED{details}",
            SignatureTrustStatus.Indeterminate => $"could not be evaluated{details}",
            _ => "not evaluated (requires a cryptographically valid signature)"
        };
    }

    private static string FormatByteRangeStructureStatus(SignatureVerificationResult result)
    {
        if (!result.ByteRangeStructureChecked)
        {
            return "not checked";
        }

        return result.ByteRangeStructureValid ? "passed" : "failed";
    }

    private static string FormatByteRangeDigestStatus(SignatureVerificationResult result)
    {
        if (!result.ByteRangeIntegrityChecked)
        {
            return "not checked";
        }

        return result.ByteRangeIntegrityValid ? "passed" : "failed";
    }
}
