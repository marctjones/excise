namespace Excise.Core.Text.Segmentation;

/// <summary>
/// How a redaction residue's per-character width is handled (#1187 surface for
/// #1045/#1145). Only implemented values are present; <c>Overshoot</c> (obscure
/// the run's width against the de-redaction side channel) arrives with #1189.
/// </summary>
public enum WidthPolicy
{
    /// <summary>
    /// Keep each glyph's advance so surrounding layout does not reflow, but
    /// collapse the removed glyph to zero ink (#1045). The default.
    /// </summary>
    CollapsePreserveLayout,

    /// <summary>
    /// Close the gap the removed glyphs left, destroying the width residue
    /// channel at the cost of reflowing the line (#1145).
    /// </summary>
    CloseGap,
}

/// <summary>
/// One place to see and set how redaction behaves (#1187). Every field here is
/// enforced by the <b>engine</b> (Excise.Core) — the defaults reproduce the
/// pre-#1187 behaviour exactly, so constructing <see cref="Default"/> changes
/// nothing.
///
/// <para><b>Knobs NOT in this record, and why.</b> This type deliberately holds
/// only what Core can honour; a field Core would silently ignore is the
/// silent-fallback sin this project forbids. The remaining redaction knobs live
/// where they can actually execute:</para>
/// <list type="table">
///   <item><term>Confidence gate</term><description>refuse/warn/proceed on low
///   extraction coverage — enforced by the CLI (<c>--strict</c> /
///   <c>--allow-low-confidence</c>) and the App via
///   <c>Excise.Ocr.RedactionConfidenceChecker</c>; Core has no OCR
///   dependency.</description></item>
///   <item><term>Flatten-OCR mode</term><description>rasterise + OCR + paint —
///   needs Excise.Rendering + Excise.Ocr; defined by #1186 at the orchestration
///   layer.</description></item>
///   <item><term>Scorched-earth carrier scrub</term><description><c>RemoveAllMetadata</c>
///   — App-level today (RedactionService); the engine-level per-carrier surface
///   is #1188.</description></item>
///   <item><term>Whole-word / sub-3-char match</term><description>#1052 / carrier
///   policy — not implemented as a boundary rule (word boundaries were decided
///   NOT to be the fix, 2026-08-10), so no field yet.</description></item>
/// </list>
/// </summary>
public sealed record RedactionOptions
{
    /// <summary>Match the term case-sensitively. Default false. Enforced by: Core.</summary>
    public bool CaseSensitive { get; init; } = false;

    /// <summary>Which glyph/image overlap rule selects content for removal.
    /// Default <see cref="GlyphRemovalStrategy.AnyOverlap"/>. Enforced by: Core.</summary>
    public GlyphRemovalStrategy Strategy { get; init; } = GlyphRemovalStrategy.AnyOverlap;

    /// <summary>How the removed glyphs' width residue is handled.
    /// Default <see cref="WidthPolicy.CollapsePreserveLayout"/>. Enforced by: Core.</summary>
    public WidthPolicy Width { get; init; } = WidthPolicy.CollapsePreserveLayout;

    /// <summary>Draw the covering box over each redacted run (visual
    /// confirmation only — removal is what secures). Default true. Enforced by: Core.</summary>
    public bool DrawBox { get; init; } = true;

    /// <summary>Covering-box fill colour, RGB 0..1; null = black (#1158).
    /// Ignored when <see cref="DrawBox"/> is false. Enforced by: Core.</summary>
    public (double R, double G, double B)? BoxColor { get; init; } = null;

    /// <summary>Also reach glyphs inside hidden optional-content layers.
    /// Default true. Enforced by: Core.</summary>
    public bool IncludeHiddenLayers { get; init; } = true;

    /// <summary>Scrub the document-level text carriers (/Info, XMP, outlines,
    /// annotation /Contents, link /URI). Default true. <b>Per-entry-point
    /// semantics</b>: <c>RedactText</c> scrubs BY TERM (#896); <c>RedactArea</c>
    /// strips positionless carriers WHOLESALE (#897) because it has no term.
    /// Enforced by: Core.</summary>
    public bool ScrubDocumentCarriers { get; init; } = true;

    /// <summary>The all-defaults options — reproduces pre-#1187 behaviour.</summary>
    public static RedactionOptions Default { get; } = new();

    internal bool CloseWidth => Width == WidthPolicy.CloseGap;
}
