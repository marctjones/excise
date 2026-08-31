using Excise.Core.Document;
using Excise.Core.Text.Segmentation;

namespace Excise.Cli.Commands;

/// <summary>
/// Selects the established CLI term-redaction safety policy and translates the
/// shared report into note data. Console text, JSON, and exit behavior remain
/// with their existing presenters.
/// </summary>
internal static class CliRedactedCopySafetyAdapter
{
    private static readonly RedactedCopySafetyOptions TermRedactionOptions = new()
    {
        ScrubMetadata = false,
        ScrubAttachments = false,
        ScrubRequestedTerms = false,
        RunCarrierAudit = true,
        VerifyRequestedTerms = false,
        RunHiddenTextAudit = false,
        RunRasterRedactionAudit = false,
    };

    internal static IReadOnlyList<string> AuditTermRedaction(
        PdfDocument document,
        string term)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        return RedactedCopySafetyPolicy.Evaluate(
                document,
                RedactedCopySafetyRequest.ForTerms(
                    new[] { term },
                    TermRedactionOptions))
            .Warnings;
    }
}
