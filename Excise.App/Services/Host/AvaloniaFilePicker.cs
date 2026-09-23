using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;

namespace Excise.App.Services.Host;

/// <summary>
/// The production <see cref="IFilePicker"/>: Avalonia's storage pickers,
/// resolved through <see cref="IWindowHost"/> and mapped to local paths.
/// </summary>
/// <remarks>
/// File Open and Save go through <see cref="StoragePickers"/>, which is
/// mandatory (#1477) — a picker that skips it leaves Avalonia's macOS
/// file-type accessory looping in AppKit layout, and
/// <c>scripts/check-viewmodel-seams.sh</c> scans the source to keep it that way.
/// The FOLDER picker deliberately does not, and #1518 confirmed this is
/// correct rather than an oversight: Avalonia's <see cref="FolderPickerOpenOptions"/>
/// has no file-type-filter property (only <c>AllowMultiple</c> plus the
/// <c>Title</c>/<c>SuggestedStartLocation</c>/<c>SuggestedFileName</c> it
/// inherits from <c>PickerOptions</c>), and <see cref="StoragePickers"/>'s own
/// remarks say the accessory view is only installed "whenever a filter is
/// passed". With no filter data to reach the native directory-mode
/// <c>NSOpenPanel</c>, it never gets that accessory view, so there is nothing
/// for <see cref="MacFilePanelAccessoryCleanup"/> to tear down. Routing this
/// picker through <see cref="StoragePickers"/> would only add a scheduled
/// no-op cleanup after every folder pick.
/// </remarks>
internal sealed class AvaloniaFilePicker : IFilePicker
{
    private readonly IWindowHost _windowHost;
    private readonly ILogger<AvaloniaFilePicker> _logger;

    internal AvaloniaFilePicker(IWindowHost windowHost, ILogger<AvaloniaFilePicker> logger)
    {
        _windowHost = windowHost ?? throw new ArgumentNullException(nameof(windowHost));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> OpenFilesAsync(OpenFilesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var storageProvider = ResolveStorageProvider(request.Title);
        if (storageProvider == null)
            return Array.Empty<string>();

        var options = new FilePickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = request.AllowMultiple,
        };
        if (request.Filters.Count > 0)
            options.FileTypeFilter = ToFileTypes(request.Filters);

        var files = await StoragePickers.OpenFilesAsync(storageProvider, options, _logger);

        return files
            .Select(f => f.Path.LocalPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<string?> SaveFileAsync(SaveFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var storageProvider = ResolveStorageProvider(request.Title);
        if (storageProvider == null)
            return null;

        var options = new FilePickerSaveOptions
        {
            Title = request.Title,
            SuggestedFileName = request.SuggestedFileName,
        };
        if (request.DefaultExtension != null)
            options.DefaultExtension = request.DefaultExtension;
        if (request.Filters.Count > 0)
            options.FileTypeChoices = ToFileTypes(request.Filters);

        await TrySetStartDirectoryAsync(storageProvider, options, request.SuggestedStartDirectory);

        var file = await StoragePickers.SaveFileAsync(storageProvider, options, _logger);

        var path = file?.Path.LocalPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Intentionally not routed through <see cref="StoragePickers"/> — see the
    /// class remarks and #1518. <see cref="FolderPickerOpenOptions"/> carries
    /// no file-type filter, so the macOS accessory view #1477 tears down is
    /// never installed for a directory-mode picker; routing through the
    /// helper would only add a scheduled no-op cleanup on every call.
    /// </remarks>
    public async Task<string?> PickFolderAsync(string title)
    {
        var storageProvider = ResolveStorageProvider(title);
        if (storageProvider == null)
            return null;

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        if (folders.Count == 0)
            return null;

        var path = folders[0].Path.LocalPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private IStorageProvider? ResolveStorageProvider(string title)
    {
        var storageProvider = _windowHost.StorageProvider;
        if (storageProvider == null)
            _logger.LogWarning("Storage provider unavailable, cannot show picker: {Title}", title);

        return storageProvider;
    }

    private static IReadOnlyList<FilePickerFileType> ToFileTypes(
        IReadOnlyList<FilePickerFilter> filters)
    {
        var types = new List<FilePickerFileType>(filters.Count);
        foreach (var filter in filters)
        {
            var type = new FilePickerFileType(filter.Name)
            {
                Patterns = filter.Patterns,
            };
            if (filter.MimeTypes != null)
                type.MimeTypes = filter.MimeTypes;
            types.Add(type);
        }

        return types;
    }

    /// <summary>
    /// Best effort, exactly as the pre-#1500 redacted-save dialog was: any
    /// failure here leaves the panel at its default location rather than
    /// failing the save.
    /// </summary>
    private static async Task TrySetStartDirectoryAsync(
        IStorageProvider storageProvider,
        FilePickerSaveOptions options,
        string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return;

        try
        {
            if (Directory.Exists(directory))
                options.SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(directory);
        }
        catch
        {
            // Ignore errors, will use default location
        }
    }
}
