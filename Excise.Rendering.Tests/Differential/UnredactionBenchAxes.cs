using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Excise.Core.Redaction.Recovery;
using Excise.TestSupport;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1590/#1592 — the bench's AXES, derived from
/// <c>tests/unredaction-failure-modes.json</c> rather than hand-listed here.
///
/// <para><b>Why derived.</b> A hand-written axis list and a hand-written
/// failure-mode registry are two descriptions of the same thing, and they
/// drift: a mode gets added to the matrix, nobody adds the axis, and the bench
/// reports a score over a subset while reading like a score over everything.
/// Deriving makes that impossible — a mode with no axis is a hole the bench
/// itself reports.</para>
///
/// <para><b>Modes with no channel get an axis too, deliberately.</b> Neighbour
/// glyph shifts, XFA and the image-layer leaks have no implementation, so their
/// axis scores zero. That zero is the most useful row in the report: it is the
/// difference between "we measured this and recovered nothing" and "we never
/// looked", and only an axis that exists can tell them apart.</para>
/// </summary>
internal static class UnredactionBenchAxes
{
    /// <param name="Id">The failure-mode id from the registry.</param>
    /// <param name="Channel">The channel that should recover it, or null for an unimplemented mode.</param>
    /// <param name="Status">covered | partial | gap — the registry's own claim.</param>
    /// <param name="Issue">For a gap, the issue tracking it.</param>
    internal sealed record Axis(
        string Id, string Description, string? Channel, string Status, int? Issue, string? Note)
    {
        /// <summary>
        /// True when the registry claims this mode is recoverable today. The
        /// bench holds a covered axis to recovering its planted answer and
        /// expects nothing from a gap axis — so a gap that starts recovering
        /// is a PASS that must be promoted, not a silent improvement.
        /// </summary>
        public bool ExpectedRecoverable => Status == "covered";

        /// <summary>
        /// #1690 — is this axis GRADED (Tier 1, text) or merely MEASURED
        /// (Tier 2, deferred)? Read from the channel through the one authority,
        /// so the bench cannot grade a channel the product defers.
        ///
        /// <para>A mode with no channel is Tier 1 on purpose: it is an
        /// unimplemented TEXT gap whose zero is the point of the row, and
        /// hiding it under "deferred" would lose that.</para>
        /// </summary>
        public RecoveryTier Tier => Channel == null
            ? RecoveryTier.Text
            : RecoveryChannelTiers.TierOf(Channel);

        public bool IsDeferred => Tier == RecoveryTier.Deferred;
    }

    /// <summary>
    /// #1690 — the tier of a failure MODE, by its registry channel. Unknown
    /// mode → Tier 1, matching <see cref="RecoveryChannelTiers.TierOf"/>: a row
    /// nobody classified is graded, which is the failure that gets noticed.
    /// </summary>
    public static RecoveryTier TierOfMode(string modeId) =>
        All.FirstOrDefault(a => a.Id == modeId)?.Tier ?? RecoveryTier.Text;

    public static bool IsDeferredMode(string modeId) =>
        TierOfMode(modeId) == RecoveryTier.Deferred;

    private static readonly Lazy<IReadOnlyList<Axis>> Cached = new(Load);

    public static IReadOnlyList<Axis> All => Cached.Value;

    /// <summary>The registry path, or null when it cannot be located.</summary>
    public static string? RegistryPath =>
        TestRepoLayout.FindFile("tests", "unredaction-failure-modes.json");

    private static IReadOnlyList<Axis> Load()
    {
        var path = RegistryPath;
        if (path == null) return Array.Empty<Axis>();

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("modes").EnumerateArray()
            .Select(m => new Axis(
                m.GetProperty("id").GetString()!,
                m.GetProperty("description").GetString()!,
                m.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null,
                m.GetProperty("status").GetString()!,
                m.TryGetProperty("issue", out var i) ? i.GetInt32() : null,
                m.TryGetProperty("note", out var n) ? n.GetString() : null))
            .ToList();
    }
}
