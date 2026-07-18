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
        }

        if (results.Count > 0)
        {
            summary.AppendLine();
        }

        summary.AppendLine("Certificate trust chain: not evaluated by the OS trust store.");
        return summary.ToString().TrimEnd();
    }

    private static string ValueOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

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
