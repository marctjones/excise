using System.CommandLine;
using Excise.Core.Document;

namespace Excise.Cli;

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
/// yet supported. Text extraction additionally honours the /P bit 10
/// accessibility carve-out via <c>--for-accessibility</c>.
/// </summary>
partial class Program
{
    /// <summary>What a gated verb is about to do, mapped to the /P bit that governs it.</summary>
    internal enum DocumentAction
    {
        /// <summary>Copy/extract content out of the document — /P bit 5 (with the bit 10 accessibility carve-out for text).</summary>
        Extract,

        /// <summary>Modify document contents (e.g. add form fields) — /P bit 4.</summary>
        ModifyContents,

        /// <summary>Fill in existing interactive form fields — /P bit 6 or bit 9.</summary>
        FillForms,

        /// <summary>Assemble the document — insert/rotate/delete pages, split, merge — /P bit 11.</summary>
        AssembleDocument,
    }

    /// <summary>
    /// Thrown when a document's /P permissions deny the requested action and
    /// no override was supplied. Same shape as
    /// <see cref="LowConfidenceExtractionException"/>: verbs' generic catch
    /// prints the message on stderr and fails with a non-zero exit code.
    /// </summary>
    internal sealed class PdfPermissionDeniedException(string message) : InvalidOperationException(message);

    static Option<bool> CreateIgnorePermissionsOption() => new("--ignore-permissions")
    {
        Description = "Proceed even when the document's /P permission flags deny this action. " +
            "Explicit override for legitimate use — e.g. you are the document owner (excise cannot " +
            "yet verify owner passwords, #324). What was overridden is reported on stderr.",
        DefaultValueFactory = _ => false,
    };

    static Option<bool> CreateForAccessibilityOption() => new("--for-accessibility")
    {
        Description = "Extract text in support of accessibility (screen readers, assistive " +
            "technology). Honoured when the document denies general copy/extraction (/P bit 5) " +
            "but grants extraction for accessibility (/P bit 10).",
        DefaultValueFactory = _ => false,
    };

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
    /// <param name="forAccessibility">For <see cref="DocumentAction.Extract"/>: the caller invoked the /P bit 10 accessibility carve-out.</param>
    /// <param name="accessibilityHint">How this surface spells the accessibility carve-out (e.g. "--for-accessibility"), or null when the surface has none.</param>
    /// <param name="overrideHint">How this surface spells the override (CLI flag vs batch-step property).</param>
    internal static void RequireDocumentPermission(
        PdfDocument doc,
        DocumentAction action,
        string actionDescription,
        bool ignorePermissions,
        bool forAccessibility = false,
        string? accessibilityHint = null,
        string overrideHint = "--ignore-permissions")
    {
        var perms = doc.EffectivePermissions;
        var (allowed, requirement) = action switch
        {
            DocumentAction.Extract => (
                perms.CanCopy || (forAccessibility && perms.CanExtractForAccessibility),
                "copy/extract permission (/P bit 5)"),
            DocumentAction.ModifyContents => (perms.CanModify, "modify permission (/P bit 4)"),
            DocumentAction.FillForms => (perms.CanFillForms, "form fill-in permission (/P bit 6 or 9)"),
            DocumentAction.AssembleDocument => (perms.CanAssemble, "page-assembly permission (/P bit 11)"),
            _ => (true, ""),
        };

        if (allowed)
        {
            if (action == DocumentAction.Extract && forAccessibility && !perms.CanCopy)
            {
                Console.Error.WriteLine(
                    "Note: this document denies general copy/extraction (/P bit 5); proceeding " +
                    "under the extract-for-accessibility permission (/P bit 10).");
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
            $"Blocked by document permissions: {actionDescription} requires {requirement}, " +
            $"which this document denies ({perms}).";
        if (action == DocumentAction.Extract && !forAccessibility
            && perms.CanExtractForAccessibility && accessibilityHint != null)
        {
            message += $" Extraction in support of accessibility is permitted (/P bit 10): pass {accessibilityHint}.";
        }
        message +=
            $" If you are the document owner, pass {overrideHint} to override — permissions bind " +
            "user-password opens only, and excise cannot yet verify owner passwords (#324).";

        throw new PdfPermissionDeniedException(message);
    }
}
