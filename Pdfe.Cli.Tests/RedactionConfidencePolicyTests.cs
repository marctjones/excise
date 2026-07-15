using AwesomeAssertions;
using Pdfe.Cli;
using Pdfe.Ocr;
using Xunit;

namespace Pdfe.Cli.Tests;

/// <summary>
/// #650: <c>pdfe redact</c>'s policy for what to do with a
/// <see cref="RedactionConfidenceReport"/> — refuse, warn, or proceed
/// silently, and how <c>--strict</c>/<c>--allow-low-confidence</c> change
/// that. Tests <see cref="Program.EnforceConfidencePolicy"/> directly with
/// synthetic reports rather than needing a real SEVERE/Unverified PDF
/// fixture — the classification math itself is covered separately in
/// <c>Pdfe.Ocr.Tests.RedactionConfidenceCheckerTests</c>.
/// </summary>
public class RedactionConfidencePolicyTests
{
    private static RedactionConfidenceReport Report(RedactionConfidenceTier tier, string? oracle = "mutool") =>
        new(tier, oracle, System.Array.Empty<RedactionConfidencePageResult>());

    [Fact]
    public void Ok_NoWarnings_DoesNotThrow()
    {
        var lines = Program.EnforceConfidencePolicy(Report(RedactionConfidenceTier.Ok), strict: false, allowLowConfidence: false);
        lines.Should().BeEmpty();
    }

    [Fact]
    public void Ok_WithStrict_StillDoesNotThrow_OracleWasAvailable()
    {
        // --strict only cares whether an oracle ran at all, not the result.
        var lines = Program.EnforceConfidencePolicy(Report(RedactionConfidenceTier.Ok), strict: true, allowLowConfidence: false);
        lines.Should().BeEmpty();
    }

    [Fact]
    public void Degraded_ReturnsOneWarning_DoesNotThrow()
    {
        var lines = Program.EnforceConfidencePolicy(Report(RedactionConfidenceTier.Degraded), strict: false, allowLowConfidence: false);
        lines.Should().ContainSingle();
        lines[0].Should().Contain("differs somewhat");
    }

    [Fact]
    public void Severe_WithoutOverride_Throws()
    {
        var act = () => Program.EnforceConfidencePolicy(Report(RedactionConfidenceTier.Severe), strict: false, allowLowConfidence: false);
        act.Should().Throw<Program.LowConfidenceExtractionException>()
            .WithMessage("*allow-low-confidence*");
    }

    [Fact]
    public void Severe_WithAllowLowConfidence_ReturnsWarning_DoesNotThrow()
    {
        var lines = Program.EnforceConfidencePolicy(Report(RedactionConfidenceTier.Severe), strict: false, allowLowConfidence: true);
        lines.Should().ContainSingle();
        lines[0].Should().Contain("proceeding despite");
    }

    [Fact]
    public void Unverified_NoOracle_WithoutStrict_ReturnsWarning_DoesNotThrow()
    {
        var lines = Program.EnforceConfidencePolicy(Report(RedactionConfidenceTier.Unverified, oracle: null), strict: false, allowLowConfidence: false);
        lines.Should().ContainSingle();
        lines[0].Should().Contain("could not be independently verified");
    }

    [Fact]
    public void Unverified_NoOracle_WithStrict_Throws()
    {
        var act = () => Program.EnforceConfidencePolicy(Report(RedactionConfidenceTier.Unverified, oracle: null), strict: true, allowLowConfidence: false);
        act.Should().Throw<Program.LowConfidenceExtractionException>()
            .WithMessage("*--strict*");
    }

    [Fact]
    public void Unverified_OneBadPage_OracleWasAvailable_TreatedAsWarnNotNoOracle()
    {
        // Oracle != null but the tier is still Unverified: a specific
        // page's oracle call failed, not "nothing installed at all" — the
        // --strict "no oracle available" refusal must not fire here.
        var lines = Program.EnforceConfidencePolicy(Report(RedactionConfidenceTier.Unverified, oracle: "mutool"), strict: true, allowLowConfidence: false);
        lines.Should().ContainSingle();
        lines[0].Should().Contain("differs somewhat");
    }
}
