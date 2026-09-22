using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Excise.App.Services;

/// <summary>
/// The only route to Avalonia's file Open and Save pickers in Excise.App (#1477).
/// </summary>
/// <remarks>
/// <para>
/// On macOS, Avalonia's native picker installs a file-type accessory view (a
/// label and a popup) whenever a filter is passed, and never removes it. The
/// finished panel and its <c>NSAccessoryViewWindow</c> stay alive, and AppKit
/// loops on laying out that accessory for the rest of the session: measured
/// live at 4.6-5.3% idle CPU against a 0.23% baseline.
/// </para>
/// <para>
/// Every call therefore schedules <see cref="MacFilePanelAccessoryCleanup"/> in
/// a <c>finally</c>, so the teardown runs after a chosen file, a cancel, and an
/// exception alike. Unfiltered pickers go through here too. The cleanup finds
/// nothing to do for them, and a single route keeps the source-scan gate
/// (<c>scripts/check-viewmodel-seams.sh</c>, #1773) a simple rule: no direct
/// <c>OpenFilePickerAsync</c>/<c>SaveFilePickerAsync</c> call outside this file.
/// </para>
/// </remarks>
internal static class StoragePickers
{
    /// <summary>
    /// Test seam. When set, it replaces the scheduled native cleanup, so a test
    /// can count how often the cleanup ran. Null in production.
    /// </summary>
    internal static Action? AfterPickerHookForTests { get; set; }

    internal static async Task<IReadOnlyList<IStorageFile>> OpenFilesAsync(
        IStorageProvider storageProvider, FilePickerOpenOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);
        try
        {
            return await storageProvider.OpenFilePickerAsync(options);
        }
        finally
        {
            AfterPicker(logger);
        }
    }

    internal static async Task<IStorageFile?> SaveFileAsync(
        IStorageProvider storageProvider, FilePickerSaveOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);
        try
        {
            return await storageProvider.SaveFilePickerAsync(options);
        }
        finally
        {
            AfterPicker(logger);
        }
    }

    private static void AfterPicker(ILogger? logger)
    {
        var hook = AfterPickerHookForTests;
        if (hook != null)
        {
            hook();
            return;
        }

        MacFilePanelAccessoryCleanup.Schedule(logger);
    }
}
