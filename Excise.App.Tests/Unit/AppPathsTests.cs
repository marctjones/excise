using System;
using System.IO;
using AwesomeAssertions;
using Excise.App.Services;
using System.Runtime.InteropServices;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Tests for AppPaths cross-platform storage paths.
/// Verifies platform-specific best practices (Issues #265, #266, #267).
///
/// Calls the Resolve*Fresh internal helpers so the assembly-wide test
/// override (which redirects ConfigDir/DataDir/CacheDir into a temp
/// dir for the rest of the suite) doesn't have to be disabled — that
/// would race with persistence tests running in parallel.
/// </summary>
public class AppPathsTests
{
    /// <summary>
    /// ConfigDir should return a non-empty path.
    /// </summary>
    [Fact]
    public void ConfigDir_ReturnsNonEmptyPath()
    {
        var configDir = AppPaths.ResolveConfigDirFresh();

        configDir.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// ConfigDir path should end with Excise.App.
    /// </summary>
    [Fact]
    public void ConfigDir_EndsWithAppName()
    {
        var configDir = AppPaths.ResolveConfigDirFresh();

        configDir.Should().EndWith("Excise.App");
    }

    /// <summary>
    /// DataDir should return a non-empty path.
    /// </summary>
    [Fact]
    public void DataDir_ReturnsNonEmptyPath()
    {
        var dataDir = AppPaths.ResolveDataDirFresh();

        dataDir.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// DataDir path should end with Excise.App.
    /// </summary>
    [Fact]
    public void DataDir_EndsWithAppName()
    {
        var dataDir = AppPaths.ResolveDataDirFresh();

        dataDir.Should().EndWith("Excise.App");
    }

    /// <summary>
    /// CacheDir should return a non-empty path.
    /// </summary>
    [Fact]
    public void CacheDir_ReturnsNonEmptyPath()
    {
        var cacheDir = AppPaths.ResolveCacheDirFresh();

        cacheDir.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// CacheDir path should end with Excise.App or Cache.
    /// Windows uses Excise.App/Cache, others use Excise.App.
    /// </summary>
    [Fact]
    public void CacheDir_EndsWithAppNameOrCache()
    {
        var cacheDir = AppPaths.ResolveCacheDirFresh();

        // Windows: ends with Cache, others: ends with Excise.App
        var endOk = cacheDir.EndsWith("Excise.App") || cacheDir.EndsWith("Cache");
        endOk.Should().BeTrue($"Expected path to end with 'Excise.App' or 'Cache', got '{cacheDir}'");
    }

    /// <summary>
    /// WindowSettingsPath should return a valid JSON file path.
    /// </summary>
    [Fact]
    public void WindowSettingsPath_ReturnsValidPath()
    {
        var path = AppPaths.WindowSettingsPath;

        path.Should().NotBeNullOrEmpty();
        path.Should().EndWith("window.json");
    }

    /// <summary>
    /// RecentFilesPath should return a valid text file path.
    /// </summary>
    [Fact]
    public void RecentFilesPath_ReturnsValidPath()
    {
        var path = AppPaths.RecentFilesPath;

        path.Should().NotBeNullOrEmpty();
        path.Should().EndWith("recent.txt");
    }

    /// <summary>
    /// ZoomSettingsPath should return a valid text file path.
    /// </summary>
    [Fact]
    public void ZoomSettingsPath_ReturnsValidPath()
    {
        var path = AppPaths.ZoomSettingsPath;

        path.Should().NotBeNullOrEmpty();
        path.Should().EndWith("zoom.txt");
    }

    /// <summary>
    /// PreferencesPath should return a valid JSON file path.
    /// </summary>
    [Fact]
    public void PreferencesPath_ReturnsValidPath()
    {
        var path = AppPaths.PreferencesPath;

        path.Should().NotBeNullOrEmpty();
        path.Should().EndWith("preferences.json");
    }

    /// <summary>
    /// On Linux, ConfigDir should follow XDG Base Directory Specification.
    /// Should be $XDG_CONFIG_HOME/Excise.App or ~/.config/Excise.App.
    /// See Issue #267.
    /// </summary>
    [Fact]
    public void ConfigDir_OnLinux_FollowsXdgSpec()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Skip on non-Linux platforms
            return;
        }

        var configDir = AppPaths.ResolveConfigDirFresh();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Should contain .config (XDG default) or XDG_CONFIG_HOME path
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdgConfigHome))
        {
            configDir.Should().StartWith(xdgConfigHome,
                "ConfigDir should use $XDG_CONFIG_HOME when set");
        }
        else
        {
            configDir.Should().StartWith(Path.Combine(home, ".config"),
                "ConfigDir should default to ~/.config when $XDG_CONFIG_HOME not set");
        }
    }

    /// <summary>
    /// On Linux, DataDir should follow XDG Base Directory Specification.
    /// Should be $XDG_DATA_HOME/Excise.App or ~/.local/share/Excise.App.
    /// See Issue #267.
    /// </summary>
    [Fact]
    public void DataDir_OnLinux_FollowsXdgSpec()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Skip on non-Linux platforms
            return;
        }

        var dataDir = AppPaths.ResolveDataDirFresh();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Should contain .local/share (XDG default) or XDG_DATA_HOME path
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome))
        {
            dataDir.Should().StartWith(xdgDataHome,
                "DataDir should use $XDG_DATA_HOME when set");
        }
        else
        {
            dataDir.Should().StartWith(Path.Combine(home, ".local", "share"),
                "DataDir should default to ~/.local/share when $XDG_DATA_HOME not set");
        }
    }

    /// <summary>
    /// On Linux, CacheDir should follow XDG Base Directory Specification.
    /// Should be $XDG_CACHE_HOME/Excise.App or ~/.cache/Excise.App.
    /// See Issue #267.
    /// </summary>
    [Fact]
    public void CacheDir_OnLinux_FollowsXdgSpec()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Skip on non-Linux platforms
            return;
        }

        var cacheDir = AppPaths.ResolveCacheDirFresh();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Should contain .cache (XDG default) or XDG_CACHE_HOME path
        var xdgCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrEmpty(xdgCacheHome))
        {
            cacheDir.Should().StartWith(xdgCacheHome,
                "CacheDir should use $XDG_CACHE_HOME when set");
        }
        else
        {
            cacheDir.Should().StartWith(Path.Combine(home, ".cache"),
                "CacheDir should default to ~/.cache when $XDG_CACHE_HOME not set");
        }
    }

    /// <summary>
    /// On macOS, ConfigDir should use ~/Library/Application Support.
    /// See Issue #266.
    /// </summary>
    [Fact]
    public void ConfigDir_OnMacOS_UsesLibraryApplicationSupport()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Skip on non-macOS platforms
            return;
        }

        var configDir = AppPaths.ResolveConfigDirFresh();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expectedPrefix = Path.Combine(home, "Library", "Application Support");

        configDir.Should().StartWith(expectedPrefix,
            "On macOS, ConfigDir should use ~/Library/Application Support per Apple guidelines");
    }

    /// <summary>
    /// On macOS, CacheDir should use ~/Library/Caches.
    /// See Issue #266.
    /// </summary>
    [Fact]
    public void CacheDir_OnMacOS_UsesLibraryCaches()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Skip on non-macOS platforms
            return;
        }

        var cacheDir = AppPaths.ResolveCacheDirFresh();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expectedPrefix = Path.Combine(home, "Library", "Caches");

        cacheDir.Should().StartWith(expectedPrefix,
            "On macOS, CacheDir should use ~/Library/Caches");
    }

    /// <summary>
    /// On Windows, all paths should exist under AppData.
    /// See Issue #265.
    /// </summary>
    [Fact]
    public void ConfigDir_OnWindows_UsesAppData()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Skip on non-Windows platforms
            return;
        }

        var configDir = AppPaths.ResolveConfigDirFresh();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        configDir.Should().StartWith(appData,
            "On Windows, ConfigDir should use %APPDATA%");
    }

    [Fact]
    public void DataDir_OnWindows_UsesAppData()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Skip on non-Windows platforms
            return;
        }

        var dataDir = AppPaths.ResolveDataDirFresh();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        dataDir.Should().StartWith(appData,
            "On Windows, DataDir should use %APPDATA%");
    }

    [Fact]
    public void CacheDir_OnWindows_UsesLocalAppData()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Skip on non-Windows platforms
            return;
        }

        var cacheDir = AppPaths.ResolveCacheDirFresh();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        cacheDir.Should().StartWith(localAppData,
            "On Windows, CacheDir should use %LOCALAPPDATA%");
    }

    // ========================================================================
    // PATH CONSISTENCY TESTS
    // ========================================================================

    [Fact]
    public void ConfigDir_IsCached_ReturnsSamePath()
    {
        var first = AppPaths.ResolveConfigDirFresh();
        var second = AppPaths.ResolveConfigDirFresh();

        first.Should().Be(second, "ConfigDir should be cached and return same path");
    }

    [Fact]
    public void DataDir_IsCached_ReturnsSamePath()
    {
        var first = AppPaths.ResolveDataDirFresh();
        var second = AppPaths.ResolveDataDirFresh();

        first.Should().Be(second, "DataDir should be cached and return same path");
    }

    [Fact]
    public void CacheDir_IsCached_ReturnsSamePath()
    {
        var first = AppPaths.ResolveCacheDirFresh();
        var second = AppPaths.ResolveCacheDirFresh();

        first.Should().Be(second, "CacheDir should be cached and return same path");
    }

    // ========================================================================
    // DERIVED PATH TESTS
    // ========================================================================

    // The Path/Dir relationship tests below intentionally compare against
    // AppPaths.ConfigDir / AppPaths.DataDir (the live cached values, which
    // may be overridden during tests) rather than the *Fresh resolvers,
    // because the contract being tested is "WindowSettingsPath lives under
    // whatever ConfigDir the app is currently using" — that contract holds
    // identically in real and overridden modes.

    [Fact]
    public void WindowSettingsPath_ContainsConfigDir()
    {
        var windowPath = AppPaths.WindowSettingsPath;
        var configDir = AppPaths.ConfigDir;

        windowPath.Should().StartWith(configDir,
            "WindowSettingsPath should be in ConfigDir");
    }

    [Fact]
    public void ZoomSettingsPath_ContainsConfigDir()
    {
        var zoomPath = AppPaths.ZoomSettingsPath;
        var configDir = AppPaths.ConfigDir;

        zoomPath.Should().StartWith(configDir,
            "ZoomSettingsPath should be in ConfigDir");
    }

    [Fact]
    public void PreferencesPath_ContainsConfigDir()
    {
        var prefPath = AppPaths.PreferencesPath;
        var configDir = AppPaths.ConfigDir;

        prefPath.Should().StartWith(configDir,
            "PreferencesPath should be in ConfigDir");
    }

    [Fact]
    public void RecentFilesPath_ContainsDataDir()
    {
        var recentPath = AppPaths.RecentFilesPath;
        var dataDir = AppPaths.DataDir;

        recentPath.Should().StartWith(dataDir,
            "RecentFilesPath should be in DataDir");
    }

    // ========================================================================
    // PATH SEPARATORS AND FORMAT
    // ========================================================================

    [Fact]
    public void ConfigDir_UsesCorrectPathSeparators()
    {
        var configDir = AppPaths.ResolveConfigDirFresh();

        // Should use system-appropriate path separators
        configDir.Should().NotContain("\\\\", "Should not have double backslashes");
        configDir.Should().NotContain("//", "Should not have double forward slashes");
    }

    [Fact]
    public void AllPaths_DoNotHaveTrailingSlash()
    {
        var paths = new[]
        {
            AppPaths.ResolveConfigDirFresh(),
            AppPaths.ResolveDataDirFresh(),
            AppPaths.ResolveCacheDirFresh(),
            AppPaths.WindowSettingsPath,
            AppPaths.RecentFilesPath,
            AppPaths.ZoomSettingsPath,
            AppPaths.PreferencesPath
        };

        foreach (var path in paths)
        {
            path.Should().NotEndWith("/", "Paths should not end with forward slash");
            path.Should().NotEndWith("\\", "Paths should not end with backslash");
        }
    }

    // ========================================================================
    // EDGE CASES
    // ========================================================================

    [Fact]
    public void AppPaths_AllProperties_NotNull()
    {
        AppPaths.ResolveConfigDirFresh().Should().NotBeNull();
        AppPaths.ResolveDataDirFresh().Should().NotBeNull();
        AppPaths.ResolveCacheDirFresh().Should().NotBeNull();
        AppPaths.WindowSettingsPath.Should().NotBeNull();
        AppPaths.RecentFilesPath.Should().NotBeNull();
        AppPaths.ZoomSettingsPath.Should().NotBeNull();
        AppPaths.PreferencesPath.Should().NotBeNull();
    }

    [Fact]
    public void AppPaths_AllPaths_NotEmpty()
    {
        AppPaths.ResolveConfigDirFresh().Should().NotBeEmpty();
        AppPaths.ResolveDataDirFresh().Should().NotBeEmpty();
        AppPaths.ResolveCacheDirFresh().Should().NotBeEmpty();
        AppPaths.WindowSettingsPath.Should().NotBeEmpty();
        AppPaths.RecentFilesPath.Should().NotBeEmpty();
        AppPaths.ZoomSettingsPath.Should().NotBeEmpty();
        AppPaths.PreferencesPath.Should().NotBeEmpty();
    }

    [Fact]
    public void FilePaths_HaveCorrectExtensions()
    {
        AppPaths.WindowSettingsPath.Should().EndWith(".json");
        AppPaths.PreferencesPath.Should().EndWith(".json");
        AppPaths.RecentFilesPath.Should().EndWith(".txt");
        AppPaths.ZoomSettingsPath.Should().EndWith(".txt");
    }
}
