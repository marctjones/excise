using System.CommandLine;
using Excise.Core.Document;
using Excise.Core.Security;

namespace Excise.Cli;

/// <summary>
/// Thrown when a document's /P permissions deny the requested action and no
/// override was supplied.
/// </summary>
internal sealed class PdfPermissionDeniedException(string message) : InvalidOperationException(message);

/// <summary>
/// Document-permission (/P) enforcement for the CLI — issue #642.
///
/// excise enforces ISO 32000-2 Table 22 permissions at the ACTION layer: each
/// verb that extracts/exports content or edits the document checks the
/// document's <see cref="PdfDocument.EffectivePermissions"/> before doing
/// the work. The engine (<c>page.Text</c>, search, rendering, redaction)
/// stays permission-blind. Redaction verbs are deliberately NOT gated:
/// redaction is excise's core security purpose, and a document author's
/// no-modify bit must not prevent a user from redacting their own copy.
///
/// Every gate fails closed with an explanation on stderr and offers the
/// explicit <c>--ignore-permissions</c> override (mirroring the
/// <c>--allow-decrypt</c> precedent, #638): the legitimate owner may only
/// hold the user password, because owner-password opening is #324 and not
/// yet supported. Explicit assistive extraction via <c>--for-accessibility</c>
/// ignores bit 10 and treats bit 5 as set, per ISO 32000-2 (#1952).
/// </summary>
internal static class CliPermissionOptions
{
    internal static Option<bool> CreateIgnorePermissionsOption() => new("--ignore-permissions")
    {
        Description = "Proceed even when the document's /P permission flags deny this action. " +
            "Explicit override for legitimate use — e.g. you are the document owner (excise cannot " +
            "yet verify owner passwords, #324). What was overridden is reported on stderr.",
        DefaultValueFactory = _ => false,
    };

    internal static Option<bool> CreateForAccessibilityOption() => new("--for-accessibility")
    {
        Description = "Extract text in support of accessibility (screen readers, assistive " +
            "technology). Per ISO 32000-2, ignores /P bit 10 and treats the general " +
            "copy/extraction permission (/P bit 5) as set for this purpose.",
        DefaultValueFactory = _ => false,
    };
}

internal static class DocumentPermissionGuard
{
    /// <summary>
    /// Enforce the document's effective /P permissions for
    /// <paramref name="action"/>. No-op when the action is allowed (or the
    /// document is unencrypted / opened with the owner password, both of
    /// which make <see cref="PdfDocument.EffectivePermissions"/>
    /// all-allowed). When denied: with <paramref name="ignorePermissions"/>
    /// the override is logged to stderr and execution continues; otherwise
    /// a <see cref="PdfPermissionDeniedException"/> explains which bit
    /// denied the action and how to override.
    /// </summary>
    /// <param name="doc">The open document.</param>
    /// <param name="action">What the caller is about to do.</param>
    /// <param name="actionDescription">Human phrase for messages, e.g. "text extraction".</param>
    /// <param name="ignorePermissions">The explicit override flag value.</param>
    /// <param name="forAccessibility">For <see cref="DocumentAction.Extract"/>: the caller declared an assistive extraction purpose under ISO 32000-2.</param>
    /// <param name="accessibilityHint">How this surface spells the accessibility carve-out (e.g. "--for-accessibility"), or null when the surface has none.</param>
    /// <param name="overrideHint">How this surface spells the override (CLI flag vs batch-step property).</param>
    internal static void Require(
        PdfDocument doc,
        DocumentAction action,
        string actionDescription,
        bool ignorePermissions,
        bool forAccessibility = false,
        string? accessibilityHint = null,
        string overrideHint = "--ignore-permissions")
    {
        var perms = doc.EffectivePermissions;
        // ISO 32000-2 Table 22: assistive technology ignores bit 10 and behaves
        // as if bit 5 were set. This purpose declaration applies only to extraction
        // at the action layer; it does not bypass password authentication (#1952).
        var allowed = perms.Allows(action)
            || (action == DocumentAction.Extract && forAccessibility);

        if (allowed)
        {
            if (action == DocumentAction.Extract && forAccessibility && !perms.Allows(DocumentAction.Extract))
            {
                Console.Error.WriteLine(
                    "Note: this document denies general copy/extraction (/P bit 5); proceeding " +
                    "for assistive technology under ISO 32000-2 (ignoring /P bit 10 and treating bit 5 as set).");
            }
            return;
        }

        if (ignorePermissions)
        {
            Console.Error.WriteLine(
                $"Warning: overriding document permissions — {actionDescription} is denied by " +
                $"this document's /P flags ({perms}).");
            return;
        }

        var message =
            $"Blocked by document permissions: {actionDescription} requires {action.Requirement()}, " +
            $"which this document denies ({perms}).";
        if (action == DocumentAction.Extract && !forAccessibility && accessibilityHint != null)
        {
            message += $" Extraction for assistive technology is permitted under ISO 32000-2 regardless of /P bit 10: pass {accessibilityHint}.";
        }
        message +=
            $" If you are the document owner, pass {overrideHint} to override — permissions bind " +
            "user-password opens only, and excise cannot yet verify owner passwords (#324).";

        throw new PdfPermissionDeniedException(message);
    }
}
