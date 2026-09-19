using System.Collections.Generic;
using AwesomeAssertions;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// The collateral ratchet's ceiling rule (#1530), including the case the corpus
/// cannot reach.
///
/// <para>A key ABSENT from the baseline used to get no ceiling at all, so
/// redaction could destroy an arbitrary amount of untargeted text on that
/// fixture/term and the gate said nothing. That is the #942/#899 defect class —
/// redaction destroying 5–36% of a document per term — and it is the property
/// this harness exists to measure.</para>
///
/// <para>⚠️ Measured 2026-09-19: on a fully provisioned machine all 101 rows the
/// harness runs are in the baseline, so the absent branch is NEVER taken by the
/// corpus. It guards a fixture or term that does not exist yet. A guard nothing
/// exercises is indistinguishable from one that does not work, so it is
/// exercised here directly rather than left to the corpus to reach some day.</para>
/// </summary>
public class RedactionCollateralCeilingTests
{
    private static readonly IReadOnlyDictionary<string, int> Baseline = new Dictionary<string, int>
    {
        ["known.pdf|term"] = 0,
        ["busy.pdf|term"] = 200,
    };

    [Theory]
    // An UNBASELINED key is held to the ceiling a zero-baseline row gets. 220
    // of the 235 baselined keys are <= 0, so "expect none" is what the corpus
    // actually looks like rather than a guess.
    [InlineData("brand-new.pdf|term", 0, false)]
    [InlineData("brand-new.pdf|term", 50, false)]
    [InlineData("brand-new.pdf|term", 51, true)]
    [InlineData("brand-new.pdf|term", 5000, true)]
    // A baselined ZERO behaves identically — the two paths must not diverge.
    [InlineData("known.pdf|term", 50, false)]
    [InlineData("known.pdf|term", 51, true)]
    // A large baseline keeps its proportional headroom: 200 + max(50, 20).
    [InlineData("busy.pdf|term", 250, false)]
    [InlineData("busy.pdf|term", 251, true)]
    public void TheCeiling_BoundsUnbaselinedRowsToo(string key, int collateral, bool expectedFailure)
    {
        RedactionCollateralHarness
            .CollateralExceedsCeiling(Baseline, key, collateral, out _)
            .Should().Be(expectedFailure);
    }

    [Fact]
    public void TheFailureMessage_SaysWhereTheCeilingCameFrom()
    {
        // A ratchet failure is read by someone deciding whether to re-baseline.
        // "exceeds baseline 0" and "there was no baseline" call for different
        // actions, so the message has to distinguish them.
        RedactionCollateralHarness.CollateralExceedsCeiling(Baseline, "brand-new.pdf|term", 999, out var absent)
            .Should().BeTrue();
        absent.Should().Contain("no baseline entry");

        RedactionCollateralHarness.CollateralExceedsCeiling(Baseline, "busy.pdf|term", 999, out var known)
            .Should().BeTrue();
        known.Should().Contain("baseline 200");
    }
}
