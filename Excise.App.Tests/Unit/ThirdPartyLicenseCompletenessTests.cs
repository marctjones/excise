using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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

    [Fact]
    public void EveryShippedPackage_HasVerbatimLicenseText()
    {
        var closure = ResolvePackageClosure();
        closure.Should().NotBeEmpty();

        var vm = new AboutWindowViewModel();
        var byId = vm.Packages
            .Where(p => !string.IsNullOrEmpty(p.Id))
            .GroupBy(p => p.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // For every shipped package that IS in the manifest, the About dialog
        // must be able to show verbatim license text — a bundled license file
        // or the canonical SPDX body (MIT/BSD permission notice). A bare SPDX id
        // + URL is not enough: permissive licenses require the notice text to
        // travel with the redistribution. (Absence from the manifest entirely is
        // the other gate's job, so only packages present-but-textless fail here.)
        var textless = closure
            .Where(id => !ExcludedPackages.Contains(id))
            .Where(id => byId.TryGetValue(id, out var p)
                         && string.IsNullOrWhiteSpace(p.EffectiveLicenseText))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        textless.Should().BeEmpty(
            "every shipped package must display verbatim license text in the About dialog — embed the " +
            "license file (LICENSE_OVERRIDES in scripts/generate-license-manifest.sh) or add its SPDX id " +
            "to SpdxLicenseTexts. Packages showing only a link, not the text: {0}",
            string.Join(", ", textless));
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

    /// <summary>
    /// The restored package closure, read from NuGet's own <c>project.assets.json</c>.
    ///
    /// <para><b>This used to shell out to <c>dotnet list package
    /// --include-transitive</c> and it wedged the entire suite.</b> Running a
    /// nested <c>dotnet</c> from inside <c>dotnet test</c> leaves MSBuild
    /// node-reuse workers alive holding the inherited stdout handle, so the pipe
    /// never reached EOF; <c>WaitForExit(ms)</c> returned true because the child
    /// HAD exited, and the read that followed blocked forever. Three consecutive
    /// full-suite runs aborted with "host process exited unexpectedly", taking
    /// all 1,310 tests with them — a compliance check that can hide every
    /// correctness test behind it costs far more than it protects.</para>
    ///
    /// <para>The assets file keeps the property that mattered: it is written by
    /// NuGet during restore, so the manifest is still not permitted to vouch for
    /// its own completeness. It is also instant, needs no network, and cannot
    /// hang.</para>
    /// </summary>
    private static List<string> ResolvePackageClosure()
    {
        var assets = Path.Combine(FindRepoRoot(), "Excise.App", "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue(
            $"expected NuGet's restore output at {assets} — run 'dotnet restore Excise.App'");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(File.ReadAllText(assets));

        // "targets" is keyed by target framework; each entry is "<id>/<version>"
        // and carries a "type" of "package" or "project".
        foreach (var framework in json.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (var entry in framework.Value.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("type", out var type)) continue;
                if (type.GetString() != "package") continue;

                var slash = entry.Name.IndexOf('/');
                ids.Add(slash > 0 ? entry.Name[..slash] : entry.Name);
            }
        }

        ids.Should().NotBeEmpty("the restored closure cannot be empty; an empty one would " +
            "make this gate pass by finding nothing to check");
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
