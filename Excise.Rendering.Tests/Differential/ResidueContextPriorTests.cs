using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;
using RRE = Excise.Rendering.Differential.ResidueRecoveryEngine;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1137 Use B — the OCR-context prior re-weights the width-admissible
/// candidates but NEVER changes which are admissible. Width owns membership and
/// the security number (ResidualEntropyBits); the prior only re-orders and fills
/// a SEPARATE ContextAdjustedBits a reviewer can discount. These pin the
/// architecture, no OCR needed (the context tokens are the input).
/// </summary>
public class ResidueContextPriorTests
{
    private static RRE.Recovery MakeRecovery(params string[] candidates)
    {
        var gap = new RRE.Gap(1, 100, 140, 40, "Helvetica", 12,
            RRE.WidthMetricSource.Standard14Exact, 0.5);
        var bits = System.Math.Log2(System.Math.Max(1, candidates.Length));
        return new RRE.Recovery(gap, candidates, candidates.Length, bits, bits, "ok");
    }

    [Fact]
    public void ContextPrior_ReordersButNeverChangesTheAdmissibleSet()
    {
        var rec = MakeRecovery("James", "Louise", "Farrar");

        // "Louise" is visible in the clear nearby — a redacted secret is unlikely
        // to also sit unredacted next to itself, so it sinks.
        var after = RRE.ApplyContextPrior(rec, new[] { "Louise", "Name", "Address" });

        after.CandidatesFit.Should().BeEquivalentTo(rec.CandidatesFit,
            "the prior re-orders; width owns membership and must be unchanged");
        after.CandidatesFit.Last().Should().Be("Louise", "a context-visible candidate is down-weighted");
        after.CandidatesFit[0].Should().NotBe("Louise");
    }

    [Fact]
    public void ContextPrior_LowersContextBits_ButLeavesWidthBitsAlone()
    {
        var rec = MakeRecovery("James", "Louise", "Farrar");
        var after = RRE.ApplyContextPrior(rec, new[] { "Louise" });

        after.ResidualEntropyBits.Should().Be(rec.ResidualEntropyBits,
            "the width bits are the security number and must not move");
        after.ContextAdjustedBits.Should().BeLessThan(rec.ResidualEntropyBits,
            "a prior that concentrates mass lowers the EFFECTIVE uncertainty");
        after.ContextAdjustedBits.Should().BeLessThanOrEqualTo(after.ResidualEntropyBits);
    }

    [Fact]
    public void ContextPrior_CannotRescueAWidthExcludedCandidate()
    {
        var rec = MakeRecovery("James", "Farrar");

        // "Deborah" is in the context but was NOT width-admissible — it must not
        // appear. OCR informs the prior over the admissible set, nothing more.
        var after = RRE.ApplyContextPrior(rec, new[] { "Deborah", "James" });

        after.CandidatesFit.Should().NotContain("Deborah");
        after.CandidatesFit.Should().BeEquivalentTo(new[] { "James", "Farrar" });
    }

    [Fact]
    public void ContextPrior_NoOpOnASingleOrEmptyCandidateSet()
    {
        var one = MakeRecovery("James");
        RRE.ApplyContextPrior(one, new[] { "James" }).Should().BeSameAs(one);
    }
}
