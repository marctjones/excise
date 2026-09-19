using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1645 — every failure mode carries at least 3 fixtures, or a registered
/// exemption naming why not.
///
/// <para><b>Why the count is DERIVED and not read.</b> Each registry row has an
/// `evidence` string naming its tests. That is prose a human maintains, and it
/// drifts — <c>leftover-document-metadata</c> named none at all while reading
/// `partial`. This gate counts what <see cref="FailureModeFixtures"/> actually
/// produces, so the registry cannot claim coverage the builder does not have.</para>
///
/// <para><b>Why 3 rather than 1.</b> One fixture proves a channel CAN fire and
/// says nothing about the range it fires across. #1617 is the cost:
/// <c>box-over-intact-text</c> read `covered`, with a passing test, while excise
/// found ZERO marks on the real Manafort filing — every fixture wrote
/// <c>0 0 0 rg</c> and the document omits it.</para>
/// </summary>
public class FailureModeCoverageGateTests
{
    private sealed record Mode(string Id, string Status, bool Exempt, string ExemptionReason);

    private static IReadOnlyList<Mode> Registry()
    {
        var path = TestRepoLayout.FindFile("tests", "unredaction-failure-modes.json");
        if (path == null) return Array.Empty<Mode>();

        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        return doc.RootElement.GetProperty("modes").EnumerateArray().Select(m =>
        {
            var exempt = m.TryGetProperty("fixtureExemption", out var e);
            var reason = exempt
                ? string.Join(" ", e.EnumerateArray().Select(x => x.GetString()))
                : "";
            return new Mode(m.GetProperty("id").GetString()!,
                m.GetProperty("status").GetString()!, exempt, reason);
        }).ToList();
    }

    [Fact]
    public void EveryModeHasThreeFixtures_OrARegisteredExemption()
    {
        var registry = Registry();
        Assert.SkipUnless(registry.Count > 0, TestRepoLayout.AbsenceReason(
            "the failure-mode registry",
            System.IO.Path.Combine("tests", "unredaction-failure-modes.json")));

        var minimum = 3;
        var shortfall = new List<string>();

        foreach (var mode in registry)
        {
            var count = FailureModeFixtures.CountFor(mode.Id);
            if (mode.Exempt)
            {
                mode.ExemptionReason.Should().NotBeNullOrWhiteSpace(
                    $"'{mode.Id}' claims an exemption — an exemption without a reason is " +
                    "just being under the floor quietly");
                continue;
            }
            if (count < minimum) shortfall.Add($"{mode.Id} has {count}");
        }

        shortfall.Should().BeEmpty(
            "every non-exempt mode needs at least 3 fixtures that vary a parameter the " +
            "channel responds to — see fixtureCoverageRule in the registry");
    }

    /// <summary>
    /// An exemption must name a mode that EXISTS. A typo would otherwise
    /// silently exempt nothing while looking like it exempted something.
    /// </summary>
    [Fact]
    public void EveryFixtureGroup_NamesARegisteredMode()
    {
        var registry = Registry();
        Assert.SkipUnless(registry.Count > 0, TestRepoLayout.AbsenceReason(
            "the failure-mode registry",
            System.IO.Path.Combine("tests", "unredaction-failure-modes.json")));

        var known = registry.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        FailureModeFixtures.Modes.Where(m => !known.Contains(m)).Should().BeEmpty(
            "a fixture group for a mode the registry does not list is coverage nobody counts");
    }
}
