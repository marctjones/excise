using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfEditor.Services;

/// <summary>
/// Service for managing recent files with pin/unpin support.
/// Persists to JSON with pinned entries never evicted (bounded to 20 total, all pinned + recent).
/// </summary>
public class RecentFilesService
{
    private readonly ILogger<RecentFilesService> _logger;
    private const int MaxRecentFiles = 20;
    private readonly string _recentFilesPath;

    /// <summary>
    /// Represents a single recent file entry with pin state
    /// </summary>
    public class RecentFileEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("isPinned")]
        public bool IsPinned { get; set; } = false;

        [JsonPropertyName("lastAccessedUtc")]
        public DateTime LastAccessedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// JSON wrapper for serialization. Internal (not private) so the
    /// source-generated <see cref="PdfeJsonContext"/> can reference it.
    /// </summary>
    internal sealed class RecentFilesData
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("entries")]
        public List<RecentFileEntry> Entries { get; set; } = new();
    }

    public RecentFilesService(ILogger<RecentFilesService> logger)
    {
        _logger = logger;
        _recentFilesPath = AppPaths.RecentFilesPath;
    }

    /// <summary>
    /// Load recent files from disk, ordered by: pinned first (by last accessed), then unpinned (by last accessed)
    /// </summary>
    public ObservableCollection<RecentFileEntry> Load()
    {
        _logger.LogDebug("Loading recent files from {Path}", _recentFilesPath);

        var result = new ObservableCollection<RecentFileEntry>();

        try
        {
            if (!File.Exists(_recentFilesPath))
            {
                _logger.LogDebug("Recent files file does not exist");
                return result;
            }

            var json = File.ReadAllText(_recentFilesPath);
            var data = JsonSerializer.Deserialize(json, PdfeJsonContext.Default.RecentFilesData);

            if (data?.Entries == null || data.Entries.Count == 0)
            {
                _logger.LogDebug("Recent files file is empty");
                return result;
            }

            // Filter to only existing files, order by pinned first, then by last accessed
            var validEntries = data.Entries
                .Where(e => System.IO.File.Exists(e.Path))
                .OrderByDescending(e => e.IsPinned)
                .ThenByDescending(e => e.LastAccessedUtc)
                .Take(MaxRecentFiles)
                .ToList();

            foreach (var entry in validEntries)
            {
                result.Add(entry);
            }

            _logger.LogInformation("Loaded {Count} recent files ({Pinned} pinned)",
                result.Count, result.Count(e => e.IsPinned));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading recent files");
        }

        return result;
    }

    /// <summary>
    /// Save recent files to disk
    /// </summary>
    public void Save(IEnumerable<RecentFileEntry> entries)
    {
        _logger.LogDebug("Saving {Count} recent files to {Path}", entries.Count(), _recentFilesPath);

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_recentFilesPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var data = new RecentFilesData
            {
                Entries = entries.ToList()
            };

            var json = JsonSerializer.Serialize(data, PdfeJsonContext.Default.RecentFilesData);

            File.WriteAllText(_recentFilesPath, json);
            _logger.LogInformation("Saved {Count} recent files", entries.Count());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving recent files");
        }
    }

    /// <summary>
    /// Add or update a file in recent files. Moves to top (most recent) if already exists.
    /// </summary>
    public void AddOrUpdate(string filePath, ObservableCollection<RecentFileEntry> entries)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            _logger.LogWarning("Cannot add empty file path to recent files");
            return;
        }

        if (!System.IO.File.Exists(filePath))
        {
            _logger.LogWarning("File does not exist: {FilePath}", filePath);
            return;
        }

        // Remove if already exists
        var existing = entries.FirstOrDefault(e => e.Path == filePath);
        if (existing != null)
        {
            entries.Remove(existing);
        }

        // Add to beginning with current timestamp
        var entry = new RecentFileEntry
        {
            Path = filePath,
            IsPinned = false,
            LastAccessedUtc = DateTime.UtcNow
        };

        entries.Insert(0, entry);

        // Trim to max: pinned entries are kept, unpinned are evicted first
        TrimToMax(entries);

        Save(entries);
        _logger.LogDebug("Added/updated recent file: {FilePath}", filePath);
    }

    /// <summary>
    /// Toggle pin state for a file. Pinned files are never evicted.
    /// </summary>
    public void TogglePin(string filePath, ObservableCollection<RecentFileEntry> entries)
    {
        var entry = entries.FirstOrDefault(e => e.Path == filePath);
        if (entry == null)
        {
            _logger.LogWarning("File not in recent files: {FilePath}", filePath);
            return;
        }

        entry.IsPinned = !entry.IsPinned;
        entry.LastAccessedUtc = DateTime.UtcNow;

        Save(entries);
        _logger.LogInformation("Toggled pin for: {FilePath}, IsPinned={IsPinned}",
            filePath, entry.IsPinned);
    }

    /// <summary>
    /// Remove a file from recent files
    /// </summary>
    public void Remove(string filePath, ObservableCollection<RecentFileEntry> entries)
    {
        var entry = entries.FirstOrDefault(e => e.Path == filePath);
        if (entry != null)
        {
            entries.Remove(entry);
            Save(entries);
            _logger.LogDebug("Removed from recent files: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Trim the recent files collection to MaxRecentFiles, respecting pinned entries
    /// </summary>
    private void TrimToMax(ObservableCollection<RecentFileEntry> entries)
    {
        while (entries.Count > MaxRecentFiles)
        {
            // Remove oldest unpinned entry first, then oldest pinned
            var unpinned = entries.Where(e => !e.IsPinned).OrderBy(e => e.LastAccessedUtc).FirstOrDefault();
            if (unpinned != null)
            {
                entries.Remove(unpinned);
            }
            else
            {
                // All remaining are pinned; remove oldest
                var oldest = entries.OrderBy(e => e.LastAccessedUtc).FirstOrDefault();
                if (oldest != null)
                    entries.Remove(oldest);
                else
                    break;
            }
        }
    }
}
