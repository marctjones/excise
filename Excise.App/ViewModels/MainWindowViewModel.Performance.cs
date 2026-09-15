using System;
using Excise.App.Models;
using Microsoft.Extensions.Logging;

namespace Excise.App.ViewModels;

/// <summary>
/// Preferences → Performance: the memory/CPU trade-offs the user chose, applied
/// to the running app at once. The thumbnail sidebar is owned here; the viewer
/// and the cache-trim coordinator belong to the window and App, which listen to
/// <see cref="PerformanceSettingsApplied"/>.
/// </summary>
public partial class MainWindowViewModel
{
    private PerformanceSettings _performanceSettings = PerformanceSettings.Balanced;

    /// <summary>The performance settings in force.</summary>
    internal PerformanceSettings PerformanceSettings => _performanceSettings;

    /// <summary>
    /// Raised on the UI thread after <see cref="ApplyPerformanceSettings"/>, so
    /// the window can push the values into the viewer and the trim coordinator.
    /// </summary>
    internal event EventHandler<PerformanceSettings>? PerformanceSettingsApplied;

    /// <summary>
    /// Supplies the viewer's current tile-cache bytes to the Preferences
    /// readout. Set by the main window, which owns the viewer; null elsewhere.
    /// </summary>
    internal Func<long?>? ViewerTileCacheResidentBytesProvider { get; set; }

    /// <summary>
    /// Apply <paramref name="settings"/> (clamped) now. UI thread.
    /// <paramref name="fromPersistedStartup"/> is the window restoring
    /// window.json: then thumbnail pre-render is only ever turned OFF, because
    /// on is already the session default and a restore must not re-enable
    /// work that the host (or a test) disabled before the window existed.
    /// </summary>
    internal void ApplyPerformanceSettings(PerformanceSettings settings, bool fromPersistedStartup = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Clamped();
        _performanceSettings = settings;

        _thumbnailSession.KeepMarginPages = settings.ThumbnailKeepMargin;
        if (!fromPersistedStartup || !settings.ThumbnailPrewarm)
            _thumbnailSession.PrewarmEnabled = settings.ThumbnailPrewarm;

        PerformanceSettingsApplied?.Invoke(this, settings);
    }

    /// <summary>
    /// The Preferences dialog's Save: copy its values here, apply them, and
    /// write window.json immediately rather than when the main window closes.
    /// Runs on the UI thread (called from the dialog's Save command).
    /// </summary>
    internal void ApplySavedPreferences(PreferencesViewModel preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.SaveToMainViewModel(this);
        WindowSettings.Update(WritePreferencesTo);
        _logger.LogInformation("Preferences saved: performance preset {Preset}", _performanceSettings.DetectPreset());
    }

    /// <summary>
    /// Copy every Preferences-owned value into <paramref name="settings"/>.
    /// Window geometry and document state are not touched; their writers own them.
    /// </summary>
    internal void WritePreferencesTo(WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ReadingOrderStrategy = ReadingOrderStrategy.ToString();
        settings.WhitespaceMode = WhitespaceMode.ToString();
        // #1052/#1169/#1189: redaction policy is a preference like any
        // other. A security choice that silently resets to the less-safe
        // default on every launch is worse than no choice at all.
        settings.RedactionWholeWord = RedactionWholeWord;
        settings.RedactionWidthPolicy = RedactionWidthPolicy.ToString();
        settings.LinkUriCarrierPolicy = LinkUriCarrierPolicy.ToString();
        settings.MetadataCarrierPolicy = MetadataCarrierPolicy.ToString();
        _performanceSettings.WriteTo(settings);
    }
}
