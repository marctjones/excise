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

            summary.AppendLine($"Signature: {ValueOrUnknown(result.SignatureName)}");
            summary.AppendLine($"Overall: {FormatOverallState(result.State)}");
            summary.AppendLine($"CMS signature check: {(result.IsValid ? "passed" : "failed")} (CMS bytes and ByteRange digest only)");
            summary.AppendLine($"Signer: {ValueOrUnknown(result.SignedBy)}");
            summary.AppendLine(result.SigningTime == default
                ? "Signing time: not extracted"
                : $"Signing time: {result.SigningTime:g}");

            if (!string.IsNullOrWhiteSpace(result.StatusMessage))
            {
                summary.AppendLine($"Details: {result.StatusMessage}");
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

    private static string FormatTrustStatus(SignatureVerificationResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.TrustDetails)
            ? string.Empty
            : $" — {result.TrustDetails}";

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
