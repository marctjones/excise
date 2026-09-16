using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Services.Host;

namespace Excise.App.Tests.Utilities.Fakes;

/// <summary>
/// In-memory <see cref="ISettingsStore"/> (#1500 step 2): the same
/// read-modify-write contract as the file store, with no <c>window.json</c>.
/// </summary>
/// <remarks>
/// This removes the view-mode leak class at its mechanism rather than at its
/// symptom. Tests share one redirected config directory, so a preference one
/// test persists reaches the next — which only reproduced in a full serial run
/// (CLAUDE.md, "Chunking caveat"). <c>ResetPersistedSettingsBeforeEachTest</c>
/// deletes the files before each test and stays as belt and braces; a view
/// model given this store never writes one.
/// </remarks>
internal sealed class InMemorySettingsStore : ISettingsStore
{
    private WindowSettings _settings;
    private double? _zoom;

    internal InMemorySettingsStore(WindowSettings? seed = null)
    {
        _settings = seed ?? new WindowSettings();
    }

    /// <summary>How many times <see cref="Update"/> ran.</summary>
    internal int UpdateCount { get; private set; }

    /// <summary>How many times <see cref="SaveZoom"/> ran.</summary>
    internal int SaveZoomCount { get; private set; }

    /// <summary>Seeds the persisted zoom, as a prior session would have left it.</summary>
    internal InMemorySettingsStore WithZoom(double zoom)
    {
        _zoom = zoom;
        return this;
    }

    /// <summary>
    /// Seeds per-document state, as a prior session would have left it.
    /// </summary>
    internal InMemorySettingsStore WithDocumentState(string filePath, double zoomLevel, int lastPageIndex)
    {
        _settings.UpdateDocumentState(filePath, zoomLevel, lastPageIndex);
        return this;
    }

    /// <summary>
    /// The stored settings without going through <see cref="Load"/>'s copy, for
    /// asserting what a write actually persisted.
    /// </summary>
    internal WindowSettings Current => _settings;

    /// <inheritdoc />
    /// <remarks>
    /// Returns a COPY, because the file store's <c>Load()</c> deserializes a
    /// fresh object every call. Handing out the live instance would let a
    /// caller mutate persisted state without an <see cref="Update"/> and teach
    /// a new caller a habit that does not work in production.
    /// </remarks>
    public WindowSettings Load() => Copy(_settings);

    /// <inheritdoc />
    public void Update(Action<WindowSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        UpdateCount++;
        var working = Copy(_settings);
        mutate(working);
        _settings = working;
    }

    /// <inheritdoc />
    public double? LoadZoom() => _zoom;

    /// <inheritdoc />
    public void SaveZoom(double zoom)
    {
        SaveZoomCount++;
        _zoom = zoom;
    }

    private static WindowSettings Copy(WindowSettings source)
    {
        // Through the source-generated context, so the copy stays AOT-safe and
        // round-trips exactly the fields the real file store persists.
        var json = JsonSerializer.Serialize(source, ExciseJsonContext.Default.WindowSettings);
        return JsonSerializer.Deserialize(json, ExciseJsonContext.Default.WindowSettings)
            ?? new WindowSettings();
    }
}

/// <summary>
/// In-memory <see cref="IRecentFilesStore"/> (#1500 step 2): no
/// <c>recent.txt</c>, so the MRU rules can be tested without disk.
/// </summary>
internal sealed class InMemoryRecentFilesStore : IRecentFilesStore
{
    private List<string> _paths;

    internal InMemoryRecentFilesStore(params string[] seed)
    {
        _paths = seed.ToList();
    }

    /// <summary>How many times <see cref="Save"/> ran.</summary>
    internal int SaveCount { get; private set; }

    /// <summary>What is currently stored.</summary>
    internal IReadOnlyList<string> Stored => _paths;

    /// <inheritdoc />
    public IReadOnlyList<string> Load() => _paths.ToList();

    /// <inheritdoc />
    public void Save(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        SaveCount++;
        _paths = paths.ToList();
    }
}
