using System;
using System.Collections.Generic;
using Avalonia.Platform.Storage;

namespace Excise.App.Services;

/// <summary>
/// The one rule for "which of these OS-supplied items should we open?".
/// </summary>
/// <remarks>
/// <para>
/// Three entry points hand excise a list of files it did not choose: macOS
/// Launch Services file activation, the command-line/"Open With" path, and —
/// since #1002 — a drag-and-drop onto the window. They must agree on which
/// item wins, or dropping a folder-plus-PDF selection behaves differently from
/// double-clicking the same selection in Finder.
/// </para>
/// <para>
/// Extracted from <c>App.ResolveActivatedPdfPath</c> rather than copied, so
/// there is exactly one definition of the rule. <c>App</c> now delegates here
/// and keeps its name and its existing tests.
/// </para>
/// </remarks>
internal static class DroppedPdfResolver
{
    /// <summary>
    /// First item that is a local, existing <c>.pdf</c>, as a full path;
    /// <c>null</c> when the drop carried nothing openable.
    /// </summary>
    /// <remarks>
    /// Non-PDF and non-local items are SKIPPED rather than rejecting the whole
    /// drop: a user dragging a mixed selection, or a PDF out of an application
    /// that also advertises it as text, still gets the document opened.
    /// </remarks>
    internal static string? ResolveFirstPdf(IReadOnlyList<IStorageItem> files)
    {
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (!string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                continue;

            if (System.IO.File.Exists(path))
                return System.IO.Path.GetFullPath(path);
        }

        return null;
    }
}
