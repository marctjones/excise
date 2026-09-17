using System;

namespace Excise.App.Workspace;

/// <summary>
/// The document window's title (#1552/#1553). With several windows the title
/// is how a window is told apart: macOS shows it as the native tab label and in
/// the Window menu, Windows and Linux on the taskbar.
/// </summary>
internal static class DocumentWindowTitle
{
    internal const string AppName = "Excise";

    /// <summary>
    /// macOS: the document name, with "Edited" for unsaved changes (the
    /// Preview and TextEdit convention). Elsewhere: "name - Excise", with a
    /// leading asterisk for unsaved changes. No document: the app name.
    /// </summary>
    internal static string For(string? documentName, bool hasUnsavedChanges, bool macOS)
    {
        if (string.IsNullOrWhiteSpace(documentName))
            return AppName;

        if (macOS)
            return hasUnsavedChanges ? $"{documentName} — Edited" : documentName;

        return hasUnsavedChanges
            ? $"*{documentName} - {AppName}"
            : $"{documentName} - {AppName}";
    }

    internal static string For(string? documentName, bool hasUnsavedChanges) =>
        For(documentName, hasUnsavedChanges, OperatingSystem.IsMacOS());
}
