using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Excise.App.Services;

/// <summary>
/// Provides cross-platform paths for application data storage.
///
/// Implements platform-specific best practices:
/// - Windows: Uses %APPDATA% (Environment.SpecialFolder.ApplicationData)
/// - macOS: Uses ~/Library/Application Support/ per Apple Human Interface Guidelines
/// - Linux: Uses XDG Base Directory Specification ($XDG_CONFIG_HOME or ~/.config/)
///
/// See Issues #265, #266, #267 for platform-specific requirements.
/// </summary>
public static class AppPaths
{
    private const string AppName = "Excise.App";

    private static string? _configDir;
    private static string? _dataDir;
    private static string? _cacheDir;

    /// <summary>
    /// Gets the directory for configuration files (preferences, settings).
    ///
    /// Platform paths:
    /// - Windows: %APPDATA%\Excise.App
    /// - macOS: ~/Library/Application Support/Excise.App
    /// - Linux: $XDG_CONFIG_HOME/Excise.App or ~/.config/Excise.App
    /// </summary>
    public static string ConfigDir
    {
        get
        {
            if (_configDir == null)
            {
                _configDir = GetConfigDirectory();
                EnsureDirectoryExists(_configDir);
            }
            return _configDir;
        }
    }

    /// <summary>
    /// Gets the directory for application data files (recent files, session state).
    ///
    /// Platform paths:
    /// - Windows: %APPDATA%\Excise.App
    /// - macOS: ~/Library/Application Support/Excise.App
    /// - Linux: $XDG_DATA_HOME/Excise.App or ~/.local/share/Excise.App
    /// </summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir == null)
            {
                _dataDir = GetDataDirectory();
                EnsureDirectoryExists(_dataDir);
            }
            return _dataDir;
        }
    }

    /// <summary>
    /// Gets the directory for cache files (rendered pages, thumbnails).
    ///
    /// Platform paths:
    /// - Windows: %LOCALAPPDATA%\Excise.App\Cache
    /// - macOS: ~/Library/Caches/Excise.App
    /// - Linux: $XDG_CACHE_HOME/Excise.App or ~/.cache/Excise.App
    /// </summary>
    public static string CacheDir
    {
        get
        {
            if (_cacheDir == null)
            {
                _cacheDir = GetCacheDirectory();
                EnsureDirectoryExists(_cacheDir);
            }
            return _cacheDir;
        }
    }

    /// <summary>
    /// Gets the path for window settings (position, size, state).
    /// </summary>
    public static string WindowSettingsPath => Path.Combine(ConfigDir, "window.json");

    /// <summary>
    /// Gets the path for recent files list.
    /// </summary>
    public static string RecentFilesPath => Path.Combine(DataDir, "recent.txt");

    /// <summary>
    /// Gets the path for zoom level persistence.
    /// </summary>
    public static string ZoomSettingsPath => Path.Combine(ConfigDir, "zoom.txt");

    /// <summary>
    /// Gets the path for application preferences.
    /// </summary>
    public static string PreferencesPath => Path.Combine(ConfigDir, "preferences.json");

    /// <summary>
    /// One-shot request file used by packaged GUI smoke to ask the next app
    /// launch to write a responsiveness report when Launch Services does not
    /// propagate environment variables or command-line args consistently.
    /// </summary>
    public static string ResponsivenessReportRequestPath =>
        Path.Combine(DataDir, "responsiveness-report-request.txt");

    /// <summary>
    /// Test-only: resolve each storage directory ignoring both the cache and
    /// the OverrideForTests redirection. Lets AppPathsTests verify the real
    /// XDG / platform-specific resolution logic without disturbing the
    /// assembly-wide test isolation (which other tests need).
    /// </summary>
    internal static string ResolveConfigDirFresh() => GetConfigDirectory();
    internal static string ResolveDataDirFresh()   => GetDataDirectory();
    internal static string ResolveCacheDirFresh()  => GetCacheDirectory();

    private static string GetConfigDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: Use %APPDATA% (Roaming AppData)
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: Use ~/Library/Application Support/ per Apple guidelines
            // Environment.SpecialFolder.ApplicationData returns ~/.config/ which is wrong for macOS
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppName);
        }
        else
        {
            // Linux: Use XDG_CONFIG_HOME or ~/.config/
            var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdgConfig))
            {
                return Path.Combine(xdgConfig, AppName);
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", AppName);
        }
    }

    private static string GetDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: Same as config (Roaming AppData)
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: Same as config (Application Support)
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppName);
        }
        else
        {
            // Linux: Use XDG_DATA_HOME or ~/.local/share/
            var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdgData))
            {
                return Path.Combine(xdgData, AppName);
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".local", "share", AppName);
        }
    }

    private static string GetCacheDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: Use %LOCALAPPDATA%\Cache
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName, "Cache");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: Use ~/Library/Caches/
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Caches", AppName);
        }
        else
        {
            // Linux: Use XDG_CACHE_HOME or ~/.cache/
            var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (!string.IsNullOrEmpty(xdgCache))
            {
                return Path.Combine(xdgCache, AppName);
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".cache", AppName);
        }
    }

    private static void EnsureDirectoryExists(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
        catch
        {
            // Ignore directory creation errors - will fail on file access later
        }
    }

    /// <summary>
    /// Resets cached paths. Only for testing purposes.
    /// </summary>
    internal static void Reset()
    {
        _configDir = null;
        _dataDir = null;
        _cacheDir = null;
        _overrideRoot = null;
    }

    private static string? _overrideRoot;

    /// <summary>
    /// Test-only: redirect every directory under a single root.
    /// Subsequent ConfigDir/DataDir/CacheDir reads return
    /// {root}/Config/Excise.App, {root}/Data/Excise.App, {root}/Cache/Excise.App
    /// respectively. The trailing "Excise.App" segment matches the production
    /// path shape so existing AppPaths tests that assert EndWith("Excise.App")
    /// continue to pass under isolation.
    /// </summary>
    internal static void OverrideForTests(string? root)
    {
        _overrideRoot = root;
        _configDir = root != null ? EnsureExists(Path.Combine(root, "Config", AppName)) : null;
        _dataDir   = root != null ? EnsureExists(Path.Combine(root, "Data",   AppName)) : null;
        _cacheDir  = root != null ? EnsureExists(Path.Combine(root, "Cache",  AppName)) : null;
    }

    private static string EnsureExists(string dir)
    {
        EnsureDirectoryExists(dir);
        return dir;
    }
}
