using System;
using System.IO;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Canary for the app's REAL theme load path. #593 excluded
/// Avalonia.Controls.DataGrid as "unused" and every headless test stayed
/// green while the shipped app died at startup with
/// <c>FileNotFoundException: Avalonia.Controls.DataGrid</c> —
/// <c>FluentAvaloniaTheme</c>'s compiled XAML hard-references DataGrid
/// control themes in its constructor. Two lessons are encoded here:
///
///  1. The headless host boots a bare TestApp that never loads
///     FluentAvaloniaTheme, so constructing the REAL theme is asserted
///     explicitly.
///  2. A test-host assertion cannot see APP-output packaging: the test
///     project's own dependency closure always copies DataGrid.dll into the
///     TEST bin, so the theme constructs here even when the APP's output is
///     broken (a first version of this canary "passed" against the exact
///     csproj that crashed the app). The packaging half therefore asserts on
///     Excise.App's build output directory itself.
/// </summary>
[Collection("AvaloniaTests")]
public class AppThemeCanaryTests
{
    [FixedAvaloniaFact]
    public void FluentAvaloniaTheme_TheAppsRealTheme_Constructs()
    {
        var construct = () => new FluentAvalonia.Styling.FluentAvaloniaTheme();
        construct.Should().NotThrow(
            "App.axaml instantiates FluentAvaloniaTheme at startup; if this throws, " +
            "the shipped app cannot launch at all");
    }

    [Fact]
    public void AppOutput_ShipsFluentAvaloniaThemeRuntimeDependencies()
    {
        var appBin = FindAppOutputDirectory();
        File.Exists(Path.Combine(appBin, "Excise.App.dll")).Should().BeTrue(
            $"the app must have been built into {appBin} (it is a project reference of this test project)");

        File.Exists(Path.Combine(appBin, "Avalonia.Controls.DataGrid.dll")).Should().BeTrue(
            "FluentAvaloniaTheme's compiled XAML hard-references DataGrid control themes in its " +
            "CONSTRUCTOR — without this assembly in the APP's output the app dies at first " +
            "launch with FileNotFoundException (#593 regression: an ExcludeAssets=\"all\" on " +
            "the transitive reference passed every headless test and broke the real app)");
    }

    /// <summary>
    /// Excise.App's bin directory for the same configuration this test runs
    /// under (test bin: Excise.App.Tests/bin/{config}/net10.0 → app bin:
    /// Excise.App/bin/{config}/net10.0).
    /// </summary>
    private static string FindAppOutputDirectory()
    {
        var testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfmDir = Path.GetFileName(testBin);                       // net10.0
        var config = Path.GetFileName(Path.GetDirectoryName(testBin)!); // Debug/Release
        var repoRoot = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "Excise.App", "bin", config, tfmDir);
    }
}
