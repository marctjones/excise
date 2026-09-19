namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1625 — the geometric thresholds that decide whether a drawn rectangle is a
/// REDACTION MARK, in one place.
///
/// <para><b>Why this exists.</b> <see cref="MinSidePt"/> was a private const in
/// <see cref="RedactionMarkDetector"/> and a second private const in
/// <see cref="CoveredContentRecovery"/>, and
/// <see cref="RecoveryReportBuilder"/> — which SYNTHESISES marks from a
/// channel's obstruction hint — had no copy and therefore no opinion. So a
/// 468 × 0.48 pt horizontal rule, rejected by the detector's own gate at both
/// of its call sites, re-entered as a <c>FilledBox</c> mark through a third
/// door.</para>
///
/// <para>This is the #1624 shape exactly: a rule held in three hand-rolled
/// copies, one of which was missing. Thresholds get the same treatment as
/// state — one implementation, referenced.</para>
/// </summary>
internal static class RecoveryGeometry
{
    /// <summary>
    /// Smaller than this in either dimension and the rectangle is a rule, an
    /// underline or a glyph-scale artefact — not something anyone redacted
    /// behind. Measured on real filings: text underlines and table rules run
    /// 0.48–1.2 pt, redaction bars 13.8 pt and up.
    ///
    /// <para>⚠️ This is a FALSE-POSITIVE gate, so raising it hides real marks.
    /// The value is set just above the observed rule population and well below
    /// the observed bar population; it is not a tuning knob.</para>
    /// </summary>
    internal const double MinSidePt = 2.0;

    /// <summary>
    /// Is this rectangle big enough in both dimensions to be a mark?
    /// </summary>
    internal static bool IsMarkSized(double width, double height)
        => width >= MinSidePt && height >= MinSidePt;
}
