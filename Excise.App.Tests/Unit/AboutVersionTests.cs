using System;
using System.IO;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// What the About window tells the user they are running (#1627).
///
/// <para>Until 2026-09-18 nothing set an assembly version, so the window read
/// "version 1.0.0" — .NET's default — inside a bundle whose Info.plist said
/// 3.10.0, for a whole release cycle. The licences page it sits on is exactly
/// where someone checks what they have before reporting a bug, so the number
/// being wrong there is worse than it being absent.</para>
///
/// <para>The bundle wins deliberately: <c>scripts/build-macos-app.sh</c> stamps
/// Info.plist and the assemblies from one argument, and if a future build
/// forgets the assembly half, the shipped bundle's number is still the honest
/// answer.</para>
/// </summary>
public class AboutVersionTests
{
    [Fact]
    public void AppVersion_PrefersTheBundleVersion_OverTheAssemblyAttribute()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"excise-about-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var plist = Path.Combine(dir, "Info.plist");
        File.WriteAllText(plist, """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
                <key>CFBundleShortVersionString</key>
                <string>4.2.1</string>
                <key>CFBundleVersion</key>
                <string>4.2.1</string>
            </dict>
            </plist>
            """);
        try
        {
            AboutWindowViewModel.ResolveVersion(plist).Should().Be("4.2.1",
                "the bundle is the version that shipped; the assembly attribute is whatever the build set");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AppVersion_WithoutABundle_IsTheStampedAssemblyVersion_NotTheDotNetDefault()
    {
        // Passing a path that does not exist forces the assembly-attribute
        // path, which is what a `dotnet run` build reports.
        var version = AboutWindowViewModel.ResolveVersion(
            Path.Combine(Path.GetTempPath(), $"excise-missing-{Guid.NewGuid():N}.plist"));

        version.Should().NotBe("1.0.0",
            "1.0.0 is .NET's default when no version is set — Directory.Build.props must set one");
        version.Should().NotBe("0.0.0");
        version.Should().NotContain("+",
            "the build-provenance suffix is trimmed: a commit sha is not a version a user can act on");
        version.Should().MatchRegex(@"^\d+\.\d+\.\d+",
            "the displayed version is a plain semantic version");
    }

    [Fact]
    public void TheAboutWindow_ShowsTheSameVersion_AsTheResolver()
    {
        // The property is what the XAML binds to; the resolver is what the
        // other two tests exercise. This is the seam between them, so a
        // refactor that leaves the resolver correct and the property stale
        // still fails.
        new AboutWindowViewModel().AppVersion.Should().Be(AboutWindowViewModel.ResolveVersion());
    }
}
