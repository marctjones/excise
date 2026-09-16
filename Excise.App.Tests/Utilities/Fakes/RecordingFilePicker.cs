using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Excise.App.Services.Host;

namespace Excise.App.Tests.Utilities.Fakes;

/// <summary>
/// In-memory <see cref="IFilePicker"/> (#1500 step 1): answers with canned
/// paths and records every request.
/// </summary>
/// <remarks>
/// <para>
/// This is what the nine per-feature <c>*Override</c> seams were working around.
/// Avalonia's <c>IStorageProvider</c> and <c>IStorageFile</c> are sealed against
/// user implementations, so a test could only intercept at the picked-PATH
/// boundary and could never see what the command ASKED FOR. With the picker
/// behind an interface, a test can assert the title, the file-type filters, the
/// suggested name and the start directory a command sends — none of which was
/// observable before.
/// </para>
/// <para>
/// Defaults to cancelling every picker (empty list / null), which is the
/// fail-closed choice: a test that forgets to arm a response gets "the user
/// cancelled", never an accidental write to an unexpected path.
/// </para>
/// </remarks>
internal sealed class RecordingFilePicker : IFilePicker
{
    private readonly Queue<IReadOnlyList<string>> _openResponses = new();
    private readonly Queue<string?> _saveResponses = new();
    private readonly Queue<string?> _folderResponses = new();

    /// <summary>Every open request, in call order.</summary>
    internal List<OpenFilesRequest> OpenRequests { get; } = new();

    /// <summary>Every save request, in call order.</summary>
    internal List<SaveFileRequest> SaveRequests { get; } = new();

    /// <summary>Every folder-picker title, in call order.</summary>
    internal List<string> FolderTitles { get; } = new();

    /// <summary>The most recent open request, or null if none.</summary>
    internal OpenFilesRequest? LastOpenRequest =>
        OpenRequests.Count == 0 ? null : OpenRequests[^1];

    /// <summary>The most recent save request, or null if none.</summary>
    internal SaveFileRequest? LastSaveRequest =>
        SaveRequests.Count == 0 ? null : SaveRequests[^1];

    /// <summary>Arms the next open call (and further calls, in order).</summary>
    internal RecordingFilePicker WillOpen(params string[] paths)
    {
        _openResponses.Enqueue(paths);
        return this;
    }

    /// <summary>Arms the next save call. Pass null to model a cancelled picker.</summary>
    internal RecordingFilePicker WillSave(string? path)
    {
        _saveResponses.Enqueue(path);
        return this;
    }

    /// <summary>Arms the next folder call. Pass null to model a cancelled picker.</summary>
    internal RecordingFilePicker WillPickFolder(string? path)
    {
        _folderResponses.Enqueue(path);
        return this;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> OpenFilesAsync(OpenFilesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OpenRequests.Add(request);
        return Task.FromResult(_openResponses.Count > 0
            ? _openResponses.Dequeue()
            : Array.Empty<string>());
    }

    /// <inheritdoc />
    public Task<string?> SaveFileAsync(SaveFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SaveRequests.Add(request);
        return Task.FromResult(_saveResponses.Count > 0 ? _saveResponses.Dequeue() : null);
    }

    /// <inheritdoc />
    public Task<string?> PickFolderAsync(string title)
    {
        FolderTitles.Add(title);
        return Task.FromResult(_folderResponses.Count > 0 ? _folderResponses.Dequeue() : null);
    }
}
