using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Redaction.Recovery;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1592 — gates <c>tests/unredaction-failure-modes.json</c>, the matrix of
/// redaction failure modes against the channel that recovers each.
///
/// <para><b>The channel list is DERIVED, not trusted.</b> The registry's channel
/// names are checked against the constants <see cref="RecoveryScanner.Channels"/>
/// actually declares, in BOTH directions: a registry row may not name a channel
/// that does not exist, and a channel may not exist without a row. That second
/// direction is the load-bearing one — it is what stops a channel being added
/// while the matrix quietly keeps describing the old coverage, which is the
/// #1527 lesson (a registry the gate trusts is a registry that drifts).</para>
/// </summary>
public class FailureModeRegistryTests
{
    private sealed record Mode(
        string Id, string Description, string? Channel, string Status,
        string? Evidence, int? Issue, string? Note);

    private static (string Path, IReadOnlyList<Mode> Modes) Load()
    {
        // Through the one locator (#1527): no bounded upward walk, no hand-rolled
        // "..". The registry is TRACKED, so absence is a failure, never a skip.
        var path = TestRepoLayout.FindFile("tests", "unredaction-failure-modes.json");
        path.Should().NotBeNull(
            TestRepoLayout.AbsenceReason(
                "the #1592 failure-mode registry", "tests/unredaction-failure-modes.json"));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var modes = document.RootElement.GetProperty("modes").EnumerateArray()
            .Select(m => new Mode(
                m.GetProperty("id").GetString()!,
                m.GetProperty("description").GetString()!,
                m.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null,
                m.GetProperty("status").GetString()!,
                m.TryGetProperty("evidence", out var e) ? e.GetString() : null,
                m.TryGetProperty("issue", out var i) ? i.GetInt32() : null,
                m.TryGetProperty("note", out var n) ? n.GetString() : null))
            .ToList();
        return (path, modes);
    }

    /// <summary>Every channel constant the scanner declares.</summary>
    private static IReadOnlySet<string> DeclaredChannels() =>
        typeof(RecoveryScanner.Channels)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryRowNamesARealChannelOrNone()
    {
        var (_, modes) = Load();
        var channels = DeclaredChannels();

        foreach (var mode in modes.Where(m => m.Channel != null))
        {
            channels.Should().Contain(mode.Channel!,
                $"row '{mode.Id}' names a channel RecoveryScanner.Channels does not declare — " +
                "a renamed channel must not leave the matrix describing a channel that is gone");
        }
    }

    [Fact]
    public void EveryChannelHasAtLeastOneRow()
    {
        // The direction that catches drift: adding a channel without saying
        // which failure mode it addresses leaves the matrix understating
        // coverage, and nothing else would notice.
        var (path, modes) = Load();
        var covered = modes.Where(m => m.Channel != null)
            .Select(m => m.Channel!).ToHashSet(StringComparer.Ordinal);

        var orphans = DeclaredChannels().Where(c => !covered.Contains(c)).ToList();
        orphans.Should().BeEmpty(
            $"every channel needs a row in {path} saying what it is for; missing: " +
            string.Join(", ", orphans));
    }

    [Fact]
    public void StatusIsOneOfTheThreeDefinedValues()
    {
        var (_, modes) = Load();
        foreach (var mode in modes)
            mode.Status.Should().BeOneOf(new[] { "covered", "partial", "gap" },
                $"row '{mode.Id}' has an undefined status");
    }

    [Fact]
    public void CoveredRowsCiteEvidence()
    {
        // "covered" is a claim about behaviour. Without a named test it is an
        // assertion nobody checks.
        var (_, modes) = Load();
        foreach (var mode in modes.Where(m => m.Status == "covered"))
        {
            mode.Evidence.Should().NotBeNullOrWhiteSpace(
                $"row '{mode.Id}' claims covered but names no evidence test");
            mode.Channel.Should().NotBeNull(
                $"row '{mode.Id}' claims covered but names no channel");
        }
    }

    [Fact]
    public void GapRowsCiteAnIssue()
    {
        // A gap with no issue is a gap nobody is going to close.
        var (_, modes) = Load();
        foreach (var mode in modes.Where(m => m.Status == "gap"))
            mode.Issue.Should().NotBeNull($"row '{mode.Id}' is a gap with no issue tracking it");
    }

    [Fact]
    public void PartialRowsSayWhatIsNotCovered()
    {
        // "partial" without saying which part reads as "mostly fine", which is
        // exactly the overstatement this matrix exists to prevent.
        var (_, modes) = Load();
        foreach (var mode in modes.Where(m => m.Status == "partial"))
            mode.Note.Should().NotBeNullOrWhiteSpace(
                $"row '{mode.Id}' is partial but does not say which part is missing");
    }

    [Fact]
    public void IdsAreUnique()
    {
        var (_, modes) = Load();
        modes.Select(m => m.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheMatrixStillReportsGaps()
    {
        // A deliberately awkward gate. If this ever fails because every row is
        // "covered", that is either a real milestone or — far more likely —
        // somebody flipped rows without evidence. Either way a human should
        // look, and the failure message says so.
        var (_, modes) = Load();
        modes.Should().Contain(m => m.Status == "gap" || m.Status == "partial",
            "every failure mode now claims full coverage. Verify each 'covered' row " +
            "against its evidence test before deleting this gate — a matrix that " +
            "claims completeness is the single most misleading thing this file could say.");
    }
}
