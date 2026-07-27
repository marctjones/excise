using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Open-source license COMPLIANCE GATE.
///
/// Every third-party NuGet package that Excise.App actually ships must have a
/// resolved license entry in the embedded <c>third-party-licenses.json</c>
/// manifest that the About dialog surfaces. This test is the guarantee that a
/// future dependency cannot be added — directly or transitively — without its
/// attribution appearing in the About dialog.
///
/// It enumerates the ACTUAL restored package closure via
/// <c>dotnet list package --include-transitive</c> — an independent source, so
/// the manifest is not permitted to vouch for its own completeness — and fails
/// if any shipped package is missing from the manifest or carries an
/// unresolved license.
///
/// The exclude set below MUST mirror the one in
/// <c>scripts/generate-license-manifest.sh</c>. Both list only build-time
/// tooling and reference-only packages that carry no runtime DLL and are
/// therefore not redistributed with the app.
/// </summary>
public class ThirdPartyLicenseCompletenessTests
{
    private static readonly HashSet<string> ExcludedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Avalonia.Diagnostics",
        "Microsoft.CodeAnalysis.Analyzers",
        "Avalonia.BuildServices",
        "Fody",
        "Microsoft.NETCore.Platforms",
        "NETStandard.Library",
    };

    [Fact]
    public void EveryShippedPackage_HasResolvedLicenseEntry()
    {
        var closure = ResolvePackageClosure();
        closure.Should().NotBeEmpty(
            "dotnet list package must report the Excise.App dependency closure");

        var vm = new AboutWindowViewModel();
        vm.Packages.Should().NotBeEmpty("the embedded license manifest must load");

        var resolvedIds = vm.Packages
            .Where(p => !string.IsNullOrEmpty(p.Id) && HasResolvedLicense(p))
            .Select(p => p.Id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = closure
            .Where(id => !ExcludedPackages.Contains(id))
            .Where(id => !resolvedIds.Contains(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        missing.Should().BeEmpty(
            "every shipped package must appear in Excise.App/Assets/third-party-licenses.json with a " +
            "resolved license — regenerate via scripts/generate-license-manifest.sh (add a LICENSE_OVERRIDES " +
            "entry if the license cannot be auto-detected). Packages missing an attribution: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void NoManifestEntry_HasUnresolvedLicense()
    {
        var vm = new AboutWindowViewModel();
        vm.Packages.Should().NotBeEmpty();

        var unresolved = vm.Packages
            .Where(p => !HasResolvedLicense(p))
            .Select(p => $"{p.Id} {p.Version}")
            .ToList();

        unresolved.Should().BeEmpty(
            "every manifest entry must carry a resolved license identity (an SPDX id or a real license " +
            "name — not the '(see licenseUrl)' placeholder). Unresolved: {0}",
            string.Join(", ", unresolved));
    }

    /// <summary>
    /// A license is "resolved" when the entry names a license the user can
    /// act on: an SPDX id, or a human-readable license name that is not the
    /// generator's "(see licenseUrl)" placeholder. A bare URL is not an
    /// attribution and does not count.
    /// </summary>
    private static bool HasResolvedLicense(ThirdPartyPackage p)
    {
        if (!string.IsNullOrWhiteSpace(p.Spdx)) return true;
        if (string.IsNullOrWhiteSpace(p.LicenseName)) return false;
        return !p.LicenseName!.Trim().Equals("(see licenseUrl)", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ResolvePackageClosure()
    {
        var csproj = Path.Combine(FindRepoRoot(), "Excise.App", "Excise.App.csproj");
        File.Exists(csproj).Should().BeTrue($"expected the GUI project at {csproj}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add(csproj);
        psi.ArgumentList.Add("package");
        psi.ArgumentList.Add("--include-transitive");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("json");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'dotnet list package'");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000).Should().BeTrue("dotnet list package should finish promptly");
        proc.ExitCode.Should().Be(0, $"dotnet list package failed: {stderr}");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(stdout);
        foreach (var project in json.RootElement.GetProperty("projects").EnumerateArray())
        {
            if (!project.TryGetProperty("frameworks", out var fws) || fws.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var fw in fws.EnumerateArray())
            {
                foreach (var kind in new[] { "topLevelPackages", "transitivePackages" })
                {
                    if (!fw.TryGetProperty(kind, out var arr) || arr.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var pkg in arr.EnumerateArray())
                    {
                        if (pkg.TryGetProperty("id", out var idEl) && idEl.GetString() is { } id)
                            ids.Add(id);
                    }
                }
            }
        }
        return ids.ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root from test base directory.");
    }
}
