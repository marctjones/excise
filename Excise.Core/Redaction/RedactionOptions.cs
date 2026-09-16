namespace Excise.Core.Text.Segmentation;

/// <summary>
/// How a redaction residue's per-character width is handled (#1187 surface for
/// #1045/#1145/#1189). Width is both a layout and a SECURITY decision: the
/// glyph-position / width de-redaction side channel is the research's top
/// unhardened gap, and each value trades differently against it. Read each
/// value's remarks for what it actually closes — none of them closes
/// everything, and two of them leave the content-stream advance intact.
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

    /// <summary>
    /// As <see cref="CollapsePreserveLayout"/>, but draw the covering box WIDER
    /// than the removed run — out to the surviving neighbours on the line —
    /// so the box's width no longer encodes how long the removed string was
    /// (#1189). Layout does not reflow.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This closes the RENDERED width channel, not the whole width side
    /// channel.</b> Preserving layout means the content stream still carries one
    /// <c>TJ</c> adjustment equal to the removed run's total advance
    /// (<c>OperandGlyphSplitter</c>), and that number is a direct measurement of
    /// the removed string's width to anyone who reads the file rather than looks
    /// at the page. Overshoot defeats the box-as-ruler oracle (#1140) and a
    /// pixel-level measurement of the redacted region; it does not defeat a
    /// content-stream one. <see cref="CloseGap"/> is what destroys that, at the
    /// cost of reflowing the line. Saying otherwise would be a gate that claims
    /// a property the code does not have.
    /// </remarks>
    OvershootPreserveLayout,
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
///   <item><term>Scorched-earth carrier scrub</term><description>the engine-level
///   per-carrier surface SHIPPED with #1188 — see <see cref="Carriers"/> (scope)
///   and <see cref="CarrierPolicy"/> (mode). The App's own
///   <c>RemoveAllMetadata</c> remains a separate, blunter wholesale strip that
///   needs no term.</description></item>
///   <item><term>Sub-3-char carrier match</term><description>the carrier policy's
///   3-character scrub floor is REPORTED, not configurable — a term below it is
///   surfaced in <see cref="RedactionReport.Carriers"/>. (Whole-word matching
///   IS here now: see <see cref="WholeWord"/>. The 2026-08-10 decision that
///   "word boundaries are not the fix" was about unmatched CARRIERS — the fix
///   there is to report them — and is compatible with #1052's explicit,
///   user-selected match rule for page content.)</description></item>
/// </list>
/// </summary>
public sealed record RedactionOptions
{
    /// <summary>Match the term case-sensitively. Default false. Enforced by: Core.</summary>
    public bool CaseSensitive { get; init; } = false;

    /// <summary>
    /// Require the match to be bounded by a non-word character (or the start/end
    /// of the run) on both sides. Default false — substring matching, the #1000
    /// decision. Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// <para>#1052. #1000 established that no single rule can be right:
    /// substring is correct for a case number inside a longer citation and
    /// wrong for <c>Lee</c> inside <c>Sleeman</c>. The tool must not guess —
    /// the person redacting knows which they mean — so the default stays
    /// substring and the strict reading is an explicit choice.</para>
    /// <para>⚠️ Whichever way this is set must be VISIBLE IN THE RESULT, not
    /// just at the moment of clicking: <see cref="RedactionReport.WholeWord"/>
    /// carries it. A user who does not know which rule ran cannot reason about
    /// what was left behind.</para>
    /// <para>The rule applies to page content AND to the document-level carrier
    /// scrub, together. #896 is the lesson: a safe option that existed only in
    /// one front end meant every other caller silently got the unsafe one.</para>
    /// </remarks>
    public bool WholeWord { get; init; } = false;

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
    /// strips positionless carriers WHOLESALE (#897) because it has no term —
    /// keeping only a PDF/A file's <c>pdfaid</c> identification, which is a
    /// conformance requirement and not a text carrier (#1507).
    /// Enforced by: Core.</summary>
    public bool ScrubDocumentCarriers { get; init; } = true;

    /// <summary>Which document-level text carriers the term scrub touches
    /// (#1188). Default <see cref="Operations.RedactionCarriers.All"/> — the safe
    /// choice; disabling a carrier can leave the term in the document (turn one
    /// off only for the #1169 reveal-risk case). Enforced by: Core.</summary>
    public Operations.RedactionCarriers Carriers { get; init; }
        = Operations.RedactionCarriers.All;

    /// <summary>
    /// HOW each in-scope carrier is scrubbed (#1188/#1169): cut the term out
    /// (<see cref="Operations.CarrierScrubMode.Strip"/>, the default), drop the
    /// whole value it was found in, or change nothing and report the hit.
    /// Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// ⚠️ Orthogonal to <see cref="Carriers"/>: that says WHETHER a carrier is
    /// looked at, this says what happens when the term is there. A carrier that
    /// is off is not examined at all; a carrier set to
    /// <see cref="Operations.CarrierScrubMode.ReportOnly"/> is examined, still
    /// holds the term, and says so in <see cref="RedactionReport.Carriers"/>.
    /// </remarks>
    public Operations.CarrierScrubPolicy CarrierPolicy { get; init; }
        = Operations.CarrierScrubPolicy.Default;

    /// <summary>The all-defaults options — reproduces pre-#1187 behaviour.</summary>
    public static RedactionOptions Default { get; } = new();

    internal bool CloseWidth => Width == WidthPolicy.CloseGap;
}
