using System;
using Microsoft.Extensions.Logging;
using Pdfe.Core.Security;

namespace PdfEditor.ViewModels;

/// <summary>
/// Document-permission (/P) enforcement for GUI actions — issue #642.
///
/// pdfe enforces ISO 32000-2 Table 22 permissions at the ACTION layer:
/// user-initiated copy, export, edit, form and annotation commands check
/// the document's <see cref="Pdfe.Core.Document.PdfDocument.EffectivePermissions"/>
/// and refuse with a visible toast when denied. The engine and the GUI's
/// internal extraction stay permission-blind on purpose: search, rendering,
/// thumbnails, the accessibility/automation tree, and redaction all keep
/// working on a copy-restricted document (the /P bit 10 accessibility
/// carve-out, and basic layering sense). Redaction is deliberately NOT
/// gated — it is pdfe's core security purpose, and a document author's
/// no-modify bit must not prevent a user from redacting their own copy
/// (the redact-encrypted-docs flow itself is #643).
///
/// Permissions bind user-password opens only; the owner password confers
/// full permissions. pdfe cannot yet verify owner passwords (#324), so
/// every open today is user-level and restricted documents stay restricted
/// in the GUI unless a script sets <see cref="IgnoreDocumentPermissions"/>
/// (the scripting counterpart of the CLI's <c>--ignore-permissions</c>).
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Scripting/automation override for document /P permission enforcement
    /// — the counterpart of the CLI's <c>--ignore-permissions</c> flag
    /// (#642). Default <c>false</c>. When set (from a Roslyn script), gated
    /// actions proceed despite the document's restrictions; each override
    /// is logged. Intended for legitimate use by the document owner, who
    /// may only hold the user password because owner-password verification
    /// is not yet supported (#324).
    /// </summary>
    public bool IgnoreDocumentPermissions { get; set; }

    /// <summary>
    /// The permissions in force for the currently loaded document
    /// (all-allowed when no document is loaded or it is unencrypted).
    /// </summary>
    private PdfPermissions CurrentDocumentPermissions =>
        (_documentService.GetCurrentDocument() ?? _pdfCoreDocument)?.EffectivePermissions
        ?? PdfPermissions.AllAllowed;

    /// <summary>
    /// Gate a user-initiated action on the document's /P permissions.
    /// Returns true when the action may proceed (allowed, no document, or
    /// <see cref="IgnoreDocumentPermissions"/> set). Returns false after
    /// showing a warning toast — blocked actions must be visible, never a
    /// silent no-op.
    /// </summary>
    /// <param name="isAllowed">Which permission the action needs.</param>
    /// <param name="actionDescription">Toast phrasing, e.g. "Copying text".</param>
    /// <param name="permissionDescription">What the document denies, e.g. "copying or extracting content (/P bit 5)".</param>
    private bool EnsureDocumentPermission(
        Func<PdfPermissions, bool> isAllowed,
        string actionDescription,
        string permissionDescription)
    {
        var permissions = CurrentDocumentPermissions;
        if (isAllowed(permissions))
            return true;

        if (IgnoreDocumentPermissions)
        {
            _logger.LogWarning(
                "Overriding document permissions ({Permissions}): {Action} proceeds because " +
                "IgnoreDocumentPermissions is set", permissions, actionDescription);
            return true;
        }

        _logger.LogWarning(
            "Blocked by document permissions ({Permissions}): {Action}", permissions, actionDescription);
        _toastService.ShowWarning(
            "Blocked by document permissions",
            $"{actionDescription} is not allowed: this document's security settings deny " +
            $"{permissionDescription}. Permissions bind user-password opens; owner-password " +
            "opening is not yet supported (#324).");
        return false;
    }
}
