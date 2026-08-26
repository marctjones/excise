using System.Collections.Generic;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;
using BR = Excise.Rendering.Differential.XRayBadRedactionDetector.BadRedaction;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1176 — the redaction benchmark must grade the DELTA, not the OUTPUT STATE.
/// A bad redaction inherited from the source, or one that hides only whitespace,
/// is not this tool's failure. These pin the fair-count helper so the benchmark
/// cannot silently drift back to charging a tool for the document's own junk.
/// (The scenario that motivated this: TAMReview.pdf ships 22 image-over-
/// whitespace regions, which mis-graded excise 97% when its redaction was clean.)
/// </summary>
public class RedactionBenchmarkFairnessTests
{
    private static BR Bad(int page, string text) => new(page, 0, 0, 10, 10, text);

    [Fact]
    public void PreExistingBadRedaction_IsNotChargedToTheTool()
    {
        var input = new List<BR> { Bad(1, "SECRET") };
        var output = new List<BR> { Bad(1, "SECRET") };   // same finding, inherited

        RedactionBenchmarkRunner.NewNonWhitespaceBadRedactions(input, output)
            .Should().Be(0, "a bad redaction already in the source is not this tool's fault");
    }

    [Fact]
    public void WhitespaceOnlyCover_IsNotASecret()
    {
        var output = new List<BR> { Bad(1, "        "), Bad(2, "  ") };

        RedactionBenchmarkRunner.NewNonWhitespaceBadRedactions(null, output)
            .Should().Be(0, "a box over blank space hides no recoverable text");
    }

    [Fact]
    public void NewNonWhitespaceBadRedaction_IsCharged()
    {
        var input = new List<BR> { Bad(1, "old pre-existing") };
        var output = new List<BR>
        {
            Bad(1, "old pre-existing"),   // inherited — not charged
            Bad(1, "        "),           // whitespace — not charged
            Bad(3, "LEAKED NAME"),        // NEW real leak — charged
        };

        RedactionBenchmarkRunner.NewNonWhitespaceBadRedactions(input, output)
            .Should().Be(1, "only the tool-created, text-bearing bad redaction counts");
    }

    [Fact]
    public void NoInputOracle_StillDropsWhitespace_ButCountsRealFindings()
    {
        var output = new List<BR> { Bad(1, "REAL"), Bad(1, "   ") };

        RedactionBenchmarkRunner.NewNonWhitespaceBadRedactions(null, output)
            .Should().Be(1, "without an input baseline, still never charge a whitespace-only cover");
    }
}
