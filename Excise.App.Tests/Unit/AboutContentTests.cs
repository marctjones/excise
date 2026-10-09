using System;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1640 step 2 (About): the version the window reports is the version the repository declares,
/// and every third-party entry has text a person can read. AboutVersionTests pins the resolver's
/// rules with synthetic input; this ties the real build to <c>Directory.Build.props</c>.
/// </summary>
public class AboutContentTests
{
    [Fact]
    public void TheVersionAboutShows_IsTheVersionDirectoryBuildPropsDeclares()
    {
        var props = TestRepoLayout.FindFileInLocalCheckout("Directory.Build.props");
        Assert.SkipWhen(props == null, TestRepoLayout.AbsenceReason("Directory.Build.props", "Directory.Build.props"));

        var declared = Regex.Match(System.IO.File.ReadAllText(props!), @"<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>");
        declared.Success.Should().BeTrue("Directory.Build.props declares VersionPrefix");

        // A dotnet build without -p:Version reports the props value; a release build that passes
        // -p:Version stamps the tag's version, which scripts/check-version-consistency.sh holds to the
        // same number before tagging. The bundle path is exercised by AboutVersionTests.
        new AboutWindowViewModel().AppVersion.Should().StartWith(declared.Groups[1].Value);
    }

    [Fact]
    public void EveryLicenceEntry_HasReadableText_OrALinkToIt()
    {
        var vm = new AboutWindowViewModel();
        vm.Packages.Count.Should().BeGreaterThan(20, "the shipped dependency list is not a stub");

        var problems = vm.Packages
            .Select(p => (p, text: p.EffectiveLicenseText))
            .Where(x => string.IsNullOrWhiteSpace(x.text) && string.IsNullOrWhiteSpace(x.p.LicenseSpdxUrl ?? x.p.LicenseUrl))
            .Select(x => $"{x.p.Id} {x.p.Version}: no licence text and no licence link")
            .ToList();

        foreach (var (package, text) in vm.Packages.Select(p => (p, p.EffectiveLicenseText)))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Any(c => c == '\0' || (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
                problems.Add($"{package.Id}: licence text holds control characters");
            if (text.Contains('�'))
                problems.Add($"{package.Id}: licence text holds replacement characters (an encoding fault)");
            if (text.Trim().Length < 40)
                problems.Add($"{package.Id}: licence text is only {text.Trim().Length} characters");
        }

        problems.Should().BeEmpty();
    }
}
