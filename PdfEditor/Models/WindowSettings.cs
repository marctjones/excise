using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using PdfEditor.Services;

namespace PdfEditor.Models;

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
                var settings = JsonSerializer.Deserialize(json, PdfeJsonContext.Default.WindowSettings);
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
            // from PdfeJsonContext's [JsonSourceGenerationOptions].
            var json = JsonSerializer.Serialize(this, PdfeJsonContext.Default.WindowSettings);
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
