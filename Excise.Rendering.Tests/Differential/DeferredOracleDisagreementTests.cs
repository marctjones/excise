using Xunit;

namespace Excise.Rendering.Tests.Differential;

public class DeferredOracleDisagreementTests
{
    [Fact]
    public void MappingClassification_RequiresPinnedFixtureAndAllObservedResults()
    {
        var hash = DeferredOracleDisagreements.MappingFixtureHash;
        Assert.True(DeferredOracleDisagreements.IsUnresolvedMapping(hash, 2, 36, 1));
        Assert.False(DeferredOracleDisagreements.IsUnresolvedMapping("different fixture", 2, 36, 1));
        Assert.False(DeferredOracleDisagreements.IsUnresolvedMapping(hash, 0, 36, 1));
        Assert.False(DeferredOracleDisagreements.IsUnresolvedMapping(hash, 2, 36, null));
        Assert.False(DeferredOracleDisagreements.IsUnresolvedMapping(hash, 2, 36, 36));
        Assert.False(DeferredOracleDisagreements.IsUnresolvedMapping(hash, 2, 37, 1));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(1)]
    public void CountClassification_RejectsPlantedWrongCountMissingOracleAndResidualText(int count)
    {
        var hash = DeferredOracleDisagreements.CountFixtureHash;
        Assert.True(DeferredOracleDisagreements.CountIsCorroborated(hash, count, count, 0, count, 0));
        Assert.False(DeferredOracleDisagreements.CountIsCorroborated("different fixture", count, count, 0, count, 0));
        Assert.False(DeferredOracleDisagreements.CountIsCorroborated(hash, count + 1, count, 0, count, 0));
        Assert.False(DeferredOracleDisagreements.CountIsCorroborated(hash, count, null, 0, count, 0));
        Assert.False(DeferredOracleDisagreements.CountIsCorroborated(hash, count, count, 0, null, 0));
        Assert.False(DeferredOracleDisagreements.CountIsCorroborated(hash, count, count, 1, count, 0));
        Assert.False(DeferredOracleDisagreements.CountIsCorroborated(hash, count, count, 0, count, 1));
        Assert.False(DeferredOracleDisagreements.CountIsCorroborated(hash, 0, 0, 0, 0, 0));
    }
}
