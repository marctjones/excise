using System;
using System.Globalization;
using System.IO;
using Excise.App.Models;

namespace Excise.App.Services.Host;

/// <summary>
/// Persisted application settings as a dependency (#1500 step 2): the one
/// route to <c>window.json</c> and <c>zoom.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is mechanism, not policy. The store reads and writes; every validity
/// rule stays with its owner — the caller still range-checks a loaded zoom
/// against <c>DocumentViewportSession.MinimumZoom/MaximumZoom</c>, and
/// <see cref="Update"/> keeps the read-modify-write discipline so one writer
/// cannot revert another's fields.
/// </para>
/// <para>
/// The point of the interface is the fake. <c>window.json</c> is the mechanism
/// behind the view-mode leak that only reproduced in a full serial run
/// (CLAUDE.md, "Chunking caveat"): tests share one redirected config directory,
/// so state one test persists reaches the next.
/// <c>ResetPersistedSettingsBeforeEachTest</c> mitigates that by deleting the
/// files, which treats the symptom; an in-memory store removes the mechanism.
/// </para>
/// </remarks>
internal interface ISettingsStore
{
    /// <summary>The persisted settings, or defaults when nothing is stored.</summary>
    WindowSettings Load();

    /// <summary>
    /// Load, apply <paramref name="mutate"/>, save — atomically with respect to
    /// other writers. Each writer changes only the fields it owns.
    /// </summary>
    void Update(Action<WindowSettings> mutate);

    /// <summary>
    /// The persisted zoom level as stored, or null when absent or unparseable.
    /// Deliberately unvalidated: the range check belongs to the viewport.
    /// </summary>
    double? LoadZoom();

    /// <summary>Persists <paramref name="zoom"/>.</summary>
    void SaveZoom(double zoom);
}

/// <summary>
/// The production <see cref="ISettingsStore"/>.
/// </summary>
/// <remarks>
/// Delegates to <see cref="WindowSettings"/>'s existing statics rather than
/// re-implementing them, so the lock discipline in
/// <see cref="WindowSettings.Update"/> and the "drop document states whose file
/// is gone" filter in <see cref="WindowSettings.Load"/> keep working unchanged,
/// and the tests that call those statics directly still observe what the app
/// writes.
/// </remarks>
internal sealed class FileSettingsStore : ISettingsStore
{
    /// <inheritdoc />
    public WindowSettings Load() => WindowSettings.Load();

    /// <inheritdoc />
    public void Update(Action<WindowSettings> mutate) => WindowSettings.Update(mutate);

    /// <inheritdoc />
    public double? LoadZoom()
    {
        var path = AppPaths.ZoomSettingsPath;
        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path).Trim();
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var zoom)
            ? zoom
            : null;
    }

    /// <inheritdoc />
    public void SaveZoom(double zoom)
    {
        // AppPaths.ConfigDir ensures the directory exists.
        File.WriteAllText(AppPaths.ZoomSettingsPath, zoom.ToString(CultureInfo.InvariantCulture));
    }
}
