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
/// The FOLDER picker deliberately does not: it was never routed through the
/// helper, and routing it there now would newly schedule the macOS accessory
/// cleanup after every folder pick — a behaviour change that does not belong
/// in a structural step. Whether to route it or record the exception as
/// correct is issue #1518.
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
    /// Not routed through <see cref="StoragePickers"/> — see the class remarks.
    /// Issue #1518 decides whether that exception is closed or recorded as
    /// correct; do not "fix" it here without reading it first.
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
