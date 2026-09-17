using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Excise.App.Services;

namespace Excise.App.Models;

/// <summary>
/// Window settings for persistence across sessions.
/// Saves window position, size, and state.
/// Also persists per-document zoom level and last page index.
///
/// See Issue #23: Save and restore window position, size, zoom level, and last page
/// Uses AppPaths for cross-platform storage locations (Issues #265, #266, #267).
/// </summary>
public class WindowSettings
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 800;
    public bool IsMaximized { get; set; }
    public bool ContinuousScrollEnabled { get; set; } = true;

    /// <summary>
    /// Whether the sidebar's Attachments pane is shown (#1563). Visible by
    /// default, so a window.json written before this field existed shows it.
    /// </summary>
    public bool AttachmentsSidebarVisible { get; set; } = true;

    /// <summary>
    /// Text-selection reading-order strategy (#774). Persisted as a string so
    /// the source-generated JSON stays simple; parsed back to
    /// <see cref="Excise.Core.Text.ReadingOrderStrategy"/> on load.
    /// Defaults to the highest-quality multi-column behaviour.
    /// </summary>
    public string ReadingOrderStrategy { get; set; } = "ColumnAware";

    /// <summary>
    /// Copied-text whitespace mode. Persisted as a string; parsed back to
    /// <see cref="Excise.Core.Text.WhitespaceMode"/> on load. Defaults to
    /// paragraph/list-aware <c>Smart</c>.
    /// </summary>
    public string WhitespaceMode { get; set; } = "Smart";

    /// <summary>
    /// Whole-word matching for text redaction (#1052). Default false — the
    /// substring behaviour #1000 decided on.
    /// </summary>
    public bool RedactionWholeWord { get; set; }

    /// <summary>
    /// Redaction width / covering-box policy (#1189). Persisted as a string;
    /// parsed back to <see cref="Excise.Core.Text.Segmentation.WidthPolicy"/>.
    /// </summary>
    public string RedactionWidthPolicy { get; set; } = "CollapsePreserveLayout";

    /// <summary>
    /// How a link's <c>/A /URI</c> holding the redacted term is handled (#1169).
    /// Parsed back to <see cref="Excise.Core.Operations.CarrierScrubMode"/>.
    /// </summary>
    public string LinkUriCarrierPolicy { get; set; } = "Strip";

    /// <summary>The same for /Info and the XMP packet (#1169).</summary>
    public string MetadataCarrierPolicy { get; set; } = "Strip";

    /// <summary>
    /// Print page scaling (#1545). Persisted as a string; parsed back to
    /// <see cref="Excise.App.Services.Printing.PrintScalingMode"/>.
    /// </summary>
    public string PrintScaling { get; set; } = "ShrinkOversized";

    /// <summary>
    /// Trim the viewer's caches when the OS reports memory pressure (#1478).
    /// Only fires when the OS asks, so it is on by default. No UI: an
    /// internal switch for A/B measurement in a live session.
    /// </summary>
    public bool CacheTrimOnMemoryPressure { get; set; } = true;

    /// <summary>
    /// Trim the viewer's scroll-back caches when the window is deactivated or
    /// minimized, and after <see cref="CacheTrimIdleSeconds"/> without viewer
    /// activity (#1478). On by default since the 2026-09-14 live session
    /// (#1483 L5, IRS 1040 instructions): scroll-back after a trim rendered
    /// bands at p50 15 ms / max 32 ms with no blank tiles, and idle CPU stayed
    /// at 0.08%. What a trim saves shows in the footprint only once the
    /// allocator returns its empty regions (0 MB after one trim, 195 MB after
    /// the next), so treat it as a cheap best effort. A window.json that already
    /// holds false keeps it. No UI.
    /// </summary>
    public bool CacheTrimSoftTriggers { get; set; } = true;

    /// <summary>Idle delay for <see cref="CacheTrimSoftTriggers"/>, in seconds.</summary>
    public int CacheTrimIdleSeconds { get; set; } = 30;

    // ── Preferences → Performance ────────────────────────────────────────────
    // Read through PerformanceSettings.FromWindowSettings, written through
    // PerformanceSettings.WriteTo. Every default below is the Balanced value,
    // and PerformancePreset defaults to null, so a window.json written before
    // these fields existed resolves from its values: all defaults read as
    // Balanced, and a saved CacheTrimSoftTriggers=false is kept (as Custom).

    /// <summary>
    /// The preset last saved (a <see cref="Models.PerformancePreset"/> name), or
    /// null. A named preset is re-derived for this machine on load; Custom or
    /// null uses the individual values below.
    /// </summary>
    public string? PerformancePreset { get; set; }

    /// <summary>Continuous-view tile cache budget, MiB.</summary>
    public int TileCacheBudgetMb { get; set; } = 200;

    /// <summary>Single-page view LRU capacity, in rendered pages.</summary>
    public int SinglePageCachedPages { get; set; } = 6;

    /// <summary>Background thumbnail pre-render after a document opens.</summary>
    public bool ThumbnailPrewarm { get; set; } = true;

    /// <summary>Thumbnails kept in memory either side of the visible ones.</summary>
    public int ThumbnailKeepMargin { get; set; } = 48;

    /// <summary>Concurrent continuous-view band renders.</summary>
    public int RenderThreads { get; set; } = Math.Clamp(Environment.ProcessorCount - 1, 2, 6);

    private static readonly object StoreLock = new();

    /// <summary>
    /// The one way to write window.json: load the file as it is NOW, apply
    /// <paramref name="mutate"/>, save, all under one lock. Every writer
    /// changes only the fields it owns, so the window-close writer cannot
    /// revert a Preferences save and the document-state writer cannot revert
    /// either (before this, each saved a whole snapshot and the last one won).
    /// </summary>
    public static void Update(Action<WindowSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (StoreLock)
        {
            var settings = Load();
            mutate(settings);
            settings.Save();
        }
    }

    /// <summary>
    /// Per-document state: file path -> (zoom level, last page index, timestamp).
    /// Limited to 50 most recent documents to avoid unbounded growth.
    /// </summary>
    public List<DocumentState> DocumentStates { get; set; } = new();

    /// <summary>
    /// Per-document state model.
    /// </summary>
    public class DocumentState
    {
        public string FilePath { get; set; } = string.Empty;
        public double ZoomLevel { get; set; } = 1.0;
        public int LastPageIndex { get; set; } = 0;
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
    }

    // Use AppPaths for cross-platform correct paths
    private static string SettingsPath => AppPaths.WindowSettingsPath;

    /// <summary>
    /// Load settings from disk, or return default settings if not found.
    /// </summary>
    public static WindowSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize(json, ExciseJsonContext.Default.WindowSettings);
                if (settings != null)
                {
                    // Drop document states whose file is gone. A stale entry
                    // pointing at a deleted /tmp/... fixture from an earlier
                    // test run could otherwise drive the GUI's
                    // restore/recent-file logic into a hot loop the next time
                    // the user launches.
                    if (settings.DocumentStates.Count > 0)
                    {
                        settings.DocumentStates.RemoveAll(d =>
                            string.IsNullOrEmpty(d.FilePath) ||
                            !File.Exists(d.FilePath));
                    }
                    return settings;
                }
            }
        }
        catch
        {
            // Ignore errors, use defaults
        }

        return new WindowSettings();
    }

    /// <summary>
    /// Save settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            // AppPaths.ConfigDir ensures directory exists. WriteIndented comes
            // from ExciseJsonContext's [JsonSourceGenerationOptions].
            var json = JsonSerializer.Serialize(this, ExciseJsonContext.Default.WindowSettings);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    /// <summary>
    /// Apply settings to a window.
    /// </summary>
    public void ApplyTo(Window window)
    {
        // Set size first
        if (Width > 0 && Height > 0)
        {
            window.Width = Width;
            window.Height = Height;
        }

        // Set position if valid (not off-screen)
        if (IsPositionValid())
        {
            window.Position = new PixelPoint((int)X, (int)Y);
        }

        // Set maximized state after position/size
        if (IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Capture current window state.
    /// </summary>
    public void CaptureFrom(Window window)
    {
        IsMaximized = window.WindowState == WindowState.Maximized;

        // Only save position/size if not maximized
        if (!IsMaximized)
        {
            X = window.Position.X;
            Y = window.Position.Y;
            Width = window.Width;
            Height = window.Height;
        }
    }

    /// <summary>
    /// Check if the saved position would place the window on a visible screen.
    /// </summary>
    private bool IsPositionValid()
    {
        // Basic sanity check - position should be reasonable
        // A more complete implementation would check against actual screen bounds
        return X >= -100 && Y >= -100 && X < 10000 && Y < 10000;
    }

    /// <summary>
    /// Get or create document state for a file path.
    /// </summary>
    public DocumentState GetOrCreateDocumentState(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        var existing = DocumentStates.FirstOrDefault(d =>
            Path.GetFullPath(d.FilePath) == normalizedPath);

        if (existing != null)
        {
            existing.LastAccessed = DateTime.UtcNow;
            return existing;
        }

        var newState = new DocumentState
        {
            FilePath = filePath,
            ZoomLevel = 1.0,
            LastPageIndex = 0,
            LastAccessed = DateTime.UtcNow
        };
        DocumentStates.Add(newState);
        TrimToMaxDocuments();
        return newState;
    }

    /// <summary>
    /// Update document state for a file path.
    /// </summary>
    public void UpdateDocumentState(string filePath, double zoomLevel, int pageIndex)
    {
        var state = GetOrCreateDocumentState(filePath);
        state.ZoomLevel = zoomLevel;
        state.LastPageIndex = pageIndex;
        state.LastAccessed = DateTime.UtcNow;
    }

    /// <summary>
    /// Trim document states to keep only the 50 most recently accessed.
    /// </summary>
    private void TrimToMaxDocuments()
    {
        const int MaxDocuments = 50;
        if (DocumentStates.Count > MaxDocuments)
        {
            var excess = DocumentStates.Count - MaxDocuments;
            var toRemove = DocumentStates
                .OrderBy(d => d.LastAccessed)
                .Take(excess)
                .ToList();
            foreach (var item in toRemove)
            {
                DocumentStates.Remove(item);
            }
        }
    }
}
