using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1645 — every variant is CHECKED, because a fixture nobody ran is coverage
/// on paper. Each test states what the variant varies and what it should
/// therefore produce, including the ones that should produce nothing.
///
/// <para>⚠️ Three variants here assert a MISS. That is deliberate: the registry
/// calls two of these modes `partial`, and an expected-miss fixture turns that
/// word into a measurement. If one starts passing, this file fails and the
/// registry must be promoted — a gap that quietly closes is as bad for the
/// bench's honesty as one that quietly opens.</para>
/// </summary>
public class FailureModeFixturesHardTests
{
    private const string Secret = "KILIMNIK";

    // #1690: this file holds the DEFERRED image-layer modes, so it asks for
    // them explicitly. Deferred means not graded, not untested -- a fixture
    // that stopped running would be the "code stays but rots" outcome the
    // decision rules out.
    private static RecoveryReport Scan(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return RecoveryScanner.Scan(document, options: RecoveryScanOptions.IncludingDeferred);
    }

    private static bool Recovered(RecoveryReport r, string channel, string text) =>
        r.AllFindings.Any(f => f.Channel == channel && (f.Text ?? "").Contains(text));

    // ── incremental-update-prior-revision ───────────────────────────────────
    //
    // ⚠️ These call PriorRevisionRecovery.Scan(BYTES) directly, not through
    // RecoveryScanner, because RecoveryScanner NEVER RUNS THIS CHANNEL (#1665).
    // The channel needs the file bytes to walk /Prev and the scanner takes a
    // PdfDocument, so it is silently absent from every library caller's report —
    // including the bench's own real-world survey. Written this way deliberately
    // rather than quietly: the three fixtures below failed through the scanner
    // and pass here, which is what surfaced it.

    private static IReadOnlyList<PriorRevisionRecovery.PriorRevisionText> PriorRevisions(byte[] pdf)
        => PriorRevisionRecovery.Scan(pdf).Findings;

    [Fact]
    public void PriorRevision_OneHopBack_IsRecovered()
        => PriorRevisions(FailureModeFixtures.PriorRevisionDepth1(Secret))
            .Should().Contain(f => f.Text == Secret);

    /// <summary>
    /// ⚠️ The variant that separates a real chain walk from a one-hop read. A
    /// channel that stops at the first /Prev sees "INTERIM TWO" and reports the
    /// document clean.
    /// </summary>
    [Fact]
    public void PriorRevision_ThreeHopsBack_IsStillRecovered()
        => PriorRevisions(FailureModeFixtures.PriorRevisionDepth3(Secret))
            .Should().Contain(f => f.Text == Secret,
                "the secret is three revisions back and the chain must be walked to the end");

    /// <summary>
    /// Two secrets removed at DIFFERENT hops. A channel that walks to the
    /// oldest revision and reads only that finds one and calls the file handled.
    /// </summary>
    [Fact]
    public void PriorRevision_SecretsRemovedAtDifferentHops_AreBothRecovered()
    {
        var found = PriorRevisions(
            FailureModeFixtures.PriorRevisionSecretsAtDifferentHops("KILIMNIK", "MADRID"))
            .Select(f => f.Text).ToList();

        found.Should().Contain("KILIMNIK", "removed at the first update");
        found.Should().Contain("MADRID", "removed at the second — collecting only the oldest misses this");
    }

    // ── leftover-xfa ────────────────────────────────────────────────────────

    [Fact]
    public void Xfa_FlatPacket_IsRecovered()
        => Recovered(Scan(FailureModeFixtures.XfaFlat("123-45-6789")),
                RecoveryScanner.Channels.Xfa, "123-45-6789")
            .Should().BeTrue();

    /// <summary>Four subforms deep — a reader that does not descend is blind.</summary>
    [Fact]
    public void Xfa_NestedFourSubformsDeep_IsRecovered()
        => Recovered(Scan(FailureModeFixtures.XfaNestedSubform("123-45-6789")),
                RecoveryScanner.Channels.Xfa, "123-45-6789")
            .Should().BeTrue("the datasets packet is an arbitrary tree, not a flat list");

    /// <summary>Five siblings — a reader that takes the first node reports a phone number.</summary>
    [Fact]
    public void Xfa_AmongFiveSiblings_RecoversTheRightOne()
        => Recovered(Scan(FailureModeFixtures.XfaAmongSiblings("123-45-6789")),
                RecoveryScanner.Channels.Xfa, "123-45-6789")
            .Should().BeTrue();

    // ── image-smask-trick ───────────────────────────────────────────────────

    [Fact]
    public void SMask_AllZero_IsReportedPresent()
        => Scan(FailureModeFixtures.SMaskAllZero()).AllFindings
            .Any(f => f.Channel == RecoveryScanner.Channels.ImageLayer)
            .Should().BeTrue();

    /// <summary>
    /// ⚠️ EXPECTED MISS — #1608's documented gap, measured. Four non-zero samples
    /// in an otherwise empty mask are visually identical to fully transparent and
    /// structurally not all-zero.
    /// </summary>
    [Fact]
    public void SMask_NearlyAllZero_IsNotYetDetected()
        => Scan(FailureModeFixtures.SMaskNearlyAllZero()).AllFindings
            .Any(f => f.Channel == RecoveryScanner.Channels.ImageLayer)
            .Should().BeFalse(
                "#1608: only an EXACTLY all-zero 8-bit grey mask is detected. If this " +
                "starts passing the gap has closed — promote the registry row from " +
                "`partial` rather than deleting this test");

    /// <summary>
    /// ⚠️ EXPECTED MISS — the other half of #1608. Transparency by /ExtGState
    /// /ca 0 rather than by mask samples; it needs alpha tracking in the walker,
    /// which box-light-or-low-contrast wants too.
    /// </summary>
    [Fact]
    public void ImageDrawnAtZeroAlpha_IsNotYetDetected()
        => Scan(FailureModeFixtures.ImageDrawnFullyTransparent()).AllFindings
            .Any(f => f.Channel == RecoveryScanner.Channels.ImageLayer)
            .Should().BeFalse("#1608: /ca is not tracked, so the image is judged by its samples");

    // ── image-original-object-retained ──────────────────────────────────────

    [Fact]
    public void Orphan_WithAMatchingReplacement_IsReported()
        => Scan(FailureModeFixtures.OrphanWithMatchingReplacement()).AllFindings
            .Any(f => f.Channel == RecoveryScanner.Channels.ImageLayer)
            .Should().BeTrue();

    [Fact]
    public void TwoOrphans_WithAMatchingReplacement_AreBothReported()
        => Scan(FailureModeFixtures.TwoOrphansWithMatchingReplacement()).AllFindings
            .Count(f => f.Channel == RecoveryScanner.Channels.ImageLayer)
            .Should().BeGreaterThanOrEqualTo(2);

    /// <summary>
    /// ⚠️ A NEGATIVE CONTROL, not a gap. An unreferenced image of different
    /// dimensions is ordinary incremental-update debris — it accumulates benignly
    /// in a large fraction of real PDFs, and reporting it would drown the channel
    /// in noise. The same failure shape as #1624, from the other direction.
    /// </summary>
    [Fact]
    public void Orphan_WithNoMatchingReplacement_IsCorrectlySilent()
        => Scan(FailureModeFixtures.OrphanWithNoMatchingReplacement()).AllFindings
            .Any(f => f.Channel == RecoveryScanner.Channels.ImageLayer)
            .Should().BeFalse(
                "an orphan with no same-dimension replacement is update debris, not a swap");
}
