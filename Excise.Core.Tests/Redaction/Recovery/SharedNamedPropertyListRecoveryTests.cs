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
        Assert.Skip(
            "#1672 OPEN — MEASURED as 2, and whether 1 is correct is an open design " +
            "question, not a bug I can fix under merge pressure. Per carrier (one " +
            "dictionary, one scrub, one leak) argues 1; per location (an audit says " +
            "WHERE, and the dictionary paints two page regions) argues 2. Choosing the " +
            "number that makes this green would be fitting the fixture to the " +
            "implementation, which is exactly the habit that produced three weak gates " +
            "on this branch. Decide in #1672, then this assertion becomes real.");

        var matching = Scan().Count(f => f.Text == Carrier);

        matching.Should().Be(1,
            "one dictionary holds the value once, however many spans point at it");
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
