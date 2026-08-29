using AwesomeAssertions;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>#1178 coverage pins for terms whose collateral was independently measured as worst-case.</summary>
public sealed class RedactionCollateralProbeTests
{
    [Fact]
    public void KnownWorstCollateralTerm_CannotBeSampledAway()
    {
        // Deliberately choose a distribution where ordinary frequency sampling
        // would not nominate "document". The known corpus probe must remain.
        var text = "sets sets sets character character document";

        RedactionCollateralHarness.SampleTerms(text).Should().NotContain("document");
        RedactionCollateralHarness.ProbeTerms("ZapfDingbats.pdf", text).Should().Contain("document");
    }

    [Fact]
    public void OtherFixtures_KeepOnlyDocumentDerivedTerms()
    {
        const string text = "alpha alpha bravo bravo charlie";
        RedactionCollateralHarness.ProbeTerms("unrelated.pdf", text)
            .Should().BeEquivalentTo(RedactionCollateralHarness.SampleTerms(text));
    }
}
