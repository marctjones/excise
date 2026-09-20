using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1690 — which recovery channels the tool is FOCUSED on, and which are
/// DEFERRED. One authority, keyed by channel name, so the engine, the CLI and
/// the bench cannot disagree about what was graded.
///
/// <para><b>The decision (Marc, 2026-09-20).</b> <c>excise unredact</c> focuses
/// on TEXT recovery. The attack that breaks excise's own guarantee is a text
/// attack (the PoPETs 2023 glyph-shift work, #1689), and text is the only
/// channel with crisp ground truth: a recovered string either matches or it
/// does not, and mutool and pdftotext can confirm it independently. Nothing
/// else here can be graded that honestly.</para>
///
/// <para><b>Deferred is not deleted.</b> Every deferred channel is still
/// implemented, still tested, and still reachable — it simply does not run
/// unless asked for, is not counted in the headline, and is not what the bench
/// is tuned on. See <see cref="BlindSpot"/> for what that costs, which any
/// report that skips them has to say out loud.</para>
/// </summary>
public enum RecoveryTier
{
    /// <summary>Focus: deterministic text recovery, graded, independently confirmable.</summary>
    Text = 1,

    /// <summary>Deferred (#1690): present, tested, opt-in, not graded.</summary>
    Deferred = 2,
}

/// <summary>#1690 — the channel → tier map, and the wording that goes with it.</summary>
public static class RecoveryChannelTiers
{
    /// <summary>
    /// The CLI flag that turns the deferred channels back on. Named here rather
    /// than in the CLI so every "how do I get this back?" string in the engine
    /// and the bench quotes the same one.
    /// </summary>
    public const string OptInFlag = "--include-deferred";

    /// <summary>
    /// ⚠️ #1690 — what the deferral COSTS, in one sentence, to be printed next
    /// to the score rather than buried in a footnote.
    ///
    /// <para>Worded narrowly on purpose. It is <b>not</b> "blind to scanned
    /// documents": a scanned page whose invisible OCR text layer survives under
    /// the box is still recovered, by the Tier 1 hidden-text channel
    /// (<c>text-render-mode-3</c> in the failure-mode registry). The hole is the
    /// page where the box covers PIXELS and no text layer survives beneath
    /// it — which is the common shape in court records.</para>
    /// </summary>
    public const string BlindSpot =
        "DEFERRED (#1690): the image and OCR channels did not run, so a scanned page " +
        "whose redaction box covers PIXELS — with no surviving text layer under it — is " +
        "reported as holding nothing. That document class is not covered by this score. " +
        "Run again with " + OptInFlag + " (and --ocr for the OCR differential) to include them.";

    private static readonly Dictionary<string, string> Deferred = new(StringComparer.Ordinal)
    {
        // Inferential by construction: OCR carries its own error rate, so a
        // finding cannot be confirmed by an independent extractor the way a
        // string read out of the bytes can.
        [RecoveryScanner.Channels.OcrDifferential] =
            "deferred (#1690): an OCR reading carries its own error rate, so no independent " +
            "extractor can confirm it the way one confirms a string read from the file. " +
            "Still implemented and tested; opt in with --ocr.",

        // No crisp ground truth for "recovered": an image under a box is
        // PresentOnly, never a value, so there is nothing to grade a match
        // against.
        [RecoveryScanner.Channels.CoveredImage] =
            "deferred (#1690): raster content under a mark is reported present-only, never as a " +
            "value, so there is no exact answer to grade a recovery against. Still implemented " +
            "and tested; opt in with " + OptInFlag + ".",

        [RecoveryScanner.Channels.ImageLayer] =
            "deferred (#1690): an orphaned or fully masked image original is reported " +
            "present-only, never as a value, so there is no exact answer to grade a recovery " +
            "against. Still implemented and tested; opt in with " + OptInFlag + ".",
    };

    /// <summary>Every deferred channel, in declaration order.</summary>
    public static IReadOnlyList<string> DeferredChannels => Deferred.Keys.ToList();

    /// <summary>
    /// The tier of a channel. An unknown name is <see cref="RecoveryTier.Text"/>:
    /// a channel added without a tier decision is graded, which is the failure
    /// that gets noticed. The opposite default would silently drop a new text
    /// channel out of the headline.
    /// </summary>
    public static RecoveryTier TierOf(string channel) =>
        channel != null && Deferred.ContainsKey(channel) ? RecoveryTier.Deferred : RecoveryTier.Text;

    public static bool IsDeferred(string channel) => TierOf(channel) == RecoveryTier.Deferred;

    /// <summary>
    /// Why this channel did not run, phrased for <c>ChannelsSkipped</c>: what
    /// was deferred, why, and the exact flag that brings it back.
    /// </summary>
    public static string DeferralReason(string channel) =>
        channel != null && Deferred.TryGetValue(channel, out var reason)
            ? reason
            : throw new ArgumentException($"'{channel}' is not a deferred channel (#1690)", nameof(channel));
}

/// <summary>
/// #1690 — what a scan is asked to run. Defaults to the Tier 1 text focus.
///
/// <para>⚠️ The default is the same in the LIBRARY as in the CLI, deliberately.
/// #1665 is in this very file's neighbourhood: the CLI held the only correct
/// call and every library caller silently got a different channel set. A
/// default that differed by caller would rebuild that.</para>
/// </summary>
/// <param name="IncludeDeferredChannels">
/// Run the Tier 2 channels too (<see cref="RecoveryChannelTiers.DeferredChannels"/>).
/// When false they are declared SKIPPED with their reason, never silently
/// omitted.
/// </param>
public sealed record RecoveryScanOptions(bool IncludeDeferredChannels = false)
{
    /// <summary>The Tier 1 text focus (#1690).</summary>
    public static readonly RecoveryScanOptions Default = new();

    /// <summary>Every channel this assembly can run, deferred ones included.</summary>
    public static readonly RecoveryScanOptions IncludingDeferred = new(IncludeDeferredChannels: true);
}
