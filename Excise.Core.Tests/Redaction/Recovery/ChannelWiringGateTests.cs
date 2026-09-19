using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1665 — every channel <see cref="RecoveryScanner.Channels"/> DECLARES must be
/// accounted for in the report: run, or skipped with a reason.
///
/// <para><b>Why this gate exists.</b> `prior-revision` was declared as a constant,
/// listed `covered` in the failure-mode registry, and had a passing test — and
/// <see cref="RecoveryScanner"/> never called it. It appeared in NEITHER
/// <c>ChannelsRun</c> nor <c>ChannelsSkipped</c>, so a caller could not tell the
/// channel had not run. One caller existed, in the CLI; the library, the GUI and
/// the bench's own real-world survey all silently got less than they asked for.</para>
///
/// <para><b>Why the channel list is REFLECTED, not written out.</b> A hand-listed
/// set would have to be updated by the same person who forgot to wire the channel
/// up. Reflection over the constants means a new channel is covered by this gate
/// the moment it is declared — which is exactly when the mistake is made.</para>
/// </summary>
public class ChannelWiringGateTests
{
    private static IReadOnlyList<string> DeclaredChannels() =>
        typeof(RecoveryScanner.Channels)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    /// <summary>
    /// Channels this assembly cannot run because their dependency lives
    /// elsewhere. They are the documented exception in RecoveryScanner's own
    /// summary — the caller adds them to the same builder — so they are not
    /// expected in a scanner-only report.
    /// </summary>
    private static readonly HashSet<string> AddedByTheCaller = new()
    {
        RecoveryScanner.Channels.Residue,          // needs a renderer's glyph positions
        RecoveryScanner.Channels.OcrDifferential,  // needs tesseract
    };

    [Fact]
    public void EveryDeclaredChannel_IsEitherRunOrDeclaredSkipped()
    {
        var pdf = RecoveryFixtureBuilder.TextUnderBox("SECRET");
        var report = RecoveryScanner.Scan(pdf);

        var accounted = report.ChannelsRun
            .Concat(report.ChannelsSkipped.Keys.Select(k => k.Replace(" (partial)", "")))
            .ToHashSet();

        var unaccounted = DeclaredChannels()
            .Where(c => !AddedByTheCaller.Contains(c))
            .Where(c => !accounted.Contains(c))
            .ToList();

        unaccounted.Should().BeEmpty(
            "a channel that is declared but neither run nor declared skipped is invisible " +
            "to every caller — #1665 sat that way behind a green suite and a `covered` " +
            "registry row");
    }

    /// <summary>
    /// The document overload cannot run prior-revision, and must SAY so rather
    /// than leaving it out. This is the half that was missing.
    /// </summary>
    [Fact]
    public void TheDocumentOverload_DeclaresPriorRevisionSkipped_WithAReason()
    {
        using var document = PdfDocument.Open(RecoveryFixtureBuilder.TextUnderBox("SECRET"));
        var report = RecoveryScanner.Scan(document);

        report.ChannelsRun.Should().NotContain(RecoveryScanner.Channels.PriorRevision);
        report.ChannelsSkipped.Should().ContainKey(RecoveryScanner.Channels.PriorRevision);
        report.ChannelsSkipped[RecoveryScanner.Channels.PriorRevision]
            .Should().NotBeNullOrWhiteSpace("a skip without a reason is the #1172 failure");
    }

    /// <summary>And the byte overload actually runs it, on a file that has one.</summary>
    [Fact]
    public void TheByteOverload_RunsPriorRevision_AndRecoversFromIt()
    {
        var pdf = FailureModeFixtures.PriorRevisionDepth1("KILIMNIK");
        var report = RecoveryScanner.Scan(pdf);

        report.ChannelsRun.Should().Contain(RecoveryScanner.Channels.PriorRevision);
        report.AllFindings.Should().Contain(
            f => f.Channel == RecoveryScanner.Channels.PriorRevision && f.Text == "KILIMNIK");
    }
}
