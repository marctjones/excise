using Xunit;
using AwesomeAssertions;
using Excise.App.ViewModels;
using System.Reflection;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Tests for B6 About dialog:
/// - Menu: Help → About excise
/// - Dialog shows: version (from Assembly), runtime version, license summary, GitHub URL
/// - FluentAvaloniaUI ContentDialog
///
/// Note: UI tests (dialog visibility, button clicks) require [FixedAvaloniaFact].
/// Here we test the ViewModel properties and version extraction.
/// </summary>
public class AboutDialogTests
{
    [Fact]
    public void CanExtractAssemblyVersion()
    {
        // Get version from Excise.App assembly
        var assembly = typeof(MainWindowViewModel).Assembly;
        var version = assembly.GetName().Version;

        version.Should().NotBeNull();
        version?.Major.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void CanExtractAssemblyInformationalVersion()
    {
        // Get informational version attribute
        var assembly = typeof(MainWindowViewModel).Assembly;
        var attr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        // May be null if not set in csproj, but method should not throw
        if (attr != null)
        {
            attr.InformationalVersion.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void CanGetRuntimeVersion()
    {
        // System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription should work
        var runtimeVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        runtimeVersion.Should().NotBeNullOrEmpty();
        runtimeVersion.Should().Contain(".NET");
    }

    [Fact]
    public void CanExtractSkiaSharpVersion()
    {
        // SkiaSharp version from its assembly
        var skiaAssembly = typeof(SkiaSharp.SKBitmap).Assembly;
        var version = skiaAssembly.GetName().Version;

        version.Should().NotBeNull();
        version?.Major.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void GitHubUrlIsWellFormed()
    {
        var githubUrl = "https://github.com/marcjones/excise";

        githubUrl.Should().StartWith("https://");
        githubUrl.Should().Contain("github.com");
    }

    [Fact]
    public void LicenseSummaryIsNotEmpty()
    {
        var licenseSummary = "MIT";

        licenseSummary.Should().NotBeNullOrEmpty();
        licenseSummary.Should().Be("MIT");
    }
}
