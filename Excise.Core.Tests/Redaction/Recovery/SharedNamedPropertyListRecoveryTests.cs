using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// A named marked-content property list referenced by TWO spans (§14.6.2) —
/// the branch every other named-list fixture in this assembly misses.
///
/// <para><b>How this gap was found.</b> Not by me. The session working on
/// #1599 (which teaches <c>MarkedContentCarrierScrubber</c> to scrub a named
/// list only when <i>every</i> referencing span is affected) asked whether my
/// fixtures covered the shared case. They do not: every one emits a single
/// <c>/Span /P1 BDC</c> against a single <c>/Properties</c> entry, so the
/// dictionary is always EXCLUSIVE to its span and the conservative branch of
/// that guard is never exercised from the recovery side.</para>
///
/// <para><b>⚠️ What this file deliberately does NOT test.</b> The obvious
/// assertion — "two spans share <c>/P1</c>, one is redacted, the carrier
/// survives" — is satisfied by a scrubber that does nothing at all, and on
/// THIS branch the scrubber does exactly that for named lists. That is #1599,
/// still open: <see cref="MarkedContentTextRecovery"/>'s own summary records
/// that the scrub side handles only the inline form. So a survival assertion
/// here would pass for the wrong reason and would keep passing after #1599
/// lands, which makes it a check that cannot fail.</para>
///
/// <para>The scrub-side pair — scrubbed when exclusive, KEPT when shared, with
/// the redaction proven to have run — belongs with #1599, where the behaviour
/// exists and both directions can be pinned against each other. Only the pair
/// separates a working guard from one that never scrubs or always scrubs;
/// either half alone is green under one of those two broken implementations.
/// </para>
///
/// <para><b>What IS testable here, and is a real question:</b> whether the
/// recovery channel resolves a shared dictionary correctly — reporting the
/// carrier it actually holds, exactly ONCE, rather than once per referencing
/// span. Double-reporting would inflate finding counts on any real tagged
/// document, which is the #1625 failure mode in a different carrier.</para>
/// </summary>
public class SharedNamedPropertyListRecoveryTests
{
    private const string Carrier = "KILIMNIK";

    /// <summary>
    /// The channel finds a carrier reached through <c>/Resources /Properties</c>
    /// by more than one span. This is the leak #1599 exists to close, so the
    /// detection side must see it.
    /// </summary>
    [Fact]
    public void ACarrierSharedByTwoSpans_IsFound()
    {
        var findings = Scan();

        findings.Should().NotBeEmpty(
            "a named property list is a real carrier (§14.6.2) and the scrub side " +
            "does not yet reach it — #1599 — so the recovery side must");
        findings.Select(f => f.Text).Should().Contain(Carrier);
    }

    /// <summary>
    /// ⚠️ The discriminating half. Two spans reference one dictionary, so a
    /// channel that reports per-SPAN rather than per-DICTIONARY returns the
    /// same value twice — every count downstream doubles, and the report reads
    /// as two leaks where the file holds one.
    /// </summary>
    [Fact]
    public void ACarrierSharedByTwoSpans_IsReportedOnceNotPerSpan()
    {
        var matching = Scan().Count(f => f.Text == Carrier);

        matching.Should().Be(1,
            "one dictionary holds the value once, however many spans point at it");
    }

    /// <summary>
    /// ⚠️ THE OTHER SIDE, and the one that catches over-collapsing. An INLINE
    /// dictionary is written out per span, so two inline spans carrying the
    /// same text are two carriers IN THE FILE and must stay two findings.
    /// Without this, a dedupe keyed on the VALUE rather than on the named
    /// resource would pass the test above while quietly losing a real second
    /// carrier — and losing a carrier is the failure mode this whole tool
    /// exists to prevent.
    /// </summary>
    [Fact]
    public void TwoINLINECarriersWithTheSameText_AreStillReportedTwice()
    {
        using var document = PdfDocument.Open(RecoveryFixtureBuilder.Build(
            $"/Span <</ActualText ({Carrier})>> BDC\nBT /F1 14 Tf 72 700 Td (one) Tj ET\nEMC\n" +
            $"/Span <</ActualText ({Carrier})>> BDC\nBT /F1 14 Tf 72 670 Td (two) Tj ET\nEMC\n"));

        var findings = RecoveryScanner.Scan(document).AllFindings
            .Where(f => f.Channel == RecoveryScanner.Channels.MarkedContent && f.Text == Carrier)
            .ToList();

        findings.Should().HaveCount(2,
            "two inline dictionaries are two separate carriers; only the NAMED form " +
            "is one object referenced twice");
    }

    private static System.Collections.Generic.IReadOnlyList<RecoveredFinding> Scan()
    {
        using var document = PdfDocument.Open(
            RecoveryFixtureBuilder.SharedNamedPropertyList(Carrier, "FIRST", "SECOND"));

        return RecoveryScanner.Scan(document).AllFindings
            .Where(f => f.Channel == RecoveryScanner.Channels.MarkedContent)
            .ToList();
    }
}
