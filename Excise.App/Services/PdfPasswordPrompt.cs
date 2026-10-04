using System;

namespace Excise.App.Services;

/// <summary>Identifies supported password-entry failures for open and Combine (#1947).</summary>
internal static class PdfPasswordPrompt
{
    internal static bool IsPasswordVerificationFailure(Exception exception)
        => exception.Message.Contains("password verification failed", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("requires a non-empty user password", StringComparison.OrdinalIgnoreCase);
}
