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
/// enforced by the <b>engine</b> (Excise.Core). The defaults reproduced the
/// pre-#1187 behaviour exactly until 2026-09-17, when
/// <see cref="KeepAttachments"/> made attachment removal the default (#1572).
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

    /// <summary>
    /// Keep the document's embedded files instead of removing them. Default
    /// false: redacted output carries no attachments (#1572). Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// <para><b>The default was flipped on 2026-09-17</b> (Marc's decision):
    /// before, only the GUI's redacted-copy flow removed attachments and
    /// <c>RedactText</c> removed just the ones whose name, description or
    /// content matched the term — so an attachment excise could not read (a
    /// spreadsheet, an image) always survived.</para>
    /// <para>With this set, kept attachments are still examined:
    /// text files (txt, csv, xml, html, json, md, or a <c>text/*</c> type) have
    /// the term cut out; nested PDFs are redacted with these same options, or
    /// the redaction is refused (<see cref="Excise.Core.Document.AttachmentRedactionRefusedException"/>);
    /// anything else is reported as not checked, and
    /// <see cref="RedactionReport.IsCleanSuccess"/> is false.</para>
    /// <para>A PDF portfolio (<c>/Collection</c>) is refused when this is
    /// false (<see cref="Excise.Core.Document.PdfPortfolioRedactionException"/>): its
    /// attachments are the documents.</para>
    /// </remarks>
    public bool KeepAttachments { get; init; } = false;

    // ── #1586: output profile ───────────────────────────────────────────────

    /// <summary>
    /// Which output profile this run was asked for — a LABEL for the report,
    /// not something the engine branches on. Build options with
    /// <see cref="ForProfile"/>; the individual flags below are what actually
    /// runs, so a flag changed afterwards wins over the label.
    /// Default <see cref="RedactionProfile.Standard"/>. Enforced by: Core.
    /// </summary>
    public RedactionProfile Profile { get; init; } = RedactionProfile.Standard;

    /// <summary>
    /// Remove every JavaScript action — the document name tree,
    /// <c>/OpenAction</c>, and catalog/page/annotation/field <c>/A</c> and
    /// <c>/AA</c>, including <c>/JS</c> held as a STREAM — whether or not the
    /// term matches. Default true (#1586, #1581). Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// Turning this off falls back to the TERM SCRUB, which reaches the same
    /// places but can only cut out the term it was given: a script that
    /// restates the redacted value in a form excise cannot match still ships.
    /// </remarks>
    public bool RemoveScripts { get; init; } = true;

    /// <summary>
    /// Remove every action whose effect reaches outside this document —
    /// <c>/Launch</c>, <c>/SubmitForm</c>, <c>/ImportData</c>, <c>/GoToR</c>,
    /// <c>/GoToE</c>. Internal <c>/GoTo</c> and <c>/Named</c> navigation is
    /// kept. Default true (#1586, #1581). Enforced by: Core.
    /// </summary>
    public bool RemoveExternalActions { get; init; } = true;

    /// <summary>
    /// Remove <c>/PieceInfo</c> private application data from the catalog and
    /// every page. Default true (#1586, #1583). Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// A producer's private dictionary can hold anything, including a draft of
    /// the text on the page, and no consumer other than the producer can read
    /// it — so there is nothing to lose and no way to scrub it selectively.
    /// </remarks>
    public bool RemovePieceInfo { get; init; } = true;

    /// <summary>
    /// Remove each page's <c>/Thumb</c> thumbnail image. Default true (#1586).
    /// Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// A thumbnail is a pre-rendered picture of the page BEFORE the redaction.
    /// Nothing regenerates it, so it survives as a small but complete image of
    /// what was removed.
    /// </remarks>
    public bool RemoveThumbnails { get; init; } = true;

    /// <summary>
    /// Remove content in optional-content groups that are OFF in the
    /// document's default configuration, and the groups themselves.
    /// Default true (#1586). Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The most destructive Standard step on a real document.</b> A
    /// hidden layer is often a watermark, a print-only mark or an alternate
    /// language, and this deletes it. It is on by default because a layer that
    /// is invisible in the default view is fully extractable by any tool, so a
    /// reviewer who checked the page has not seen it — and
    /// <see cref="IncludeHiddenLayers"/> only redacts the TERM there.
    /// Every removal is counted in <see cref="RedactionReport.Removals"/>.
    /// </remarks>
    public bool RemoveHiddenLayerContent { get; init; } = true;

    /// <summary>
    /// Remove the appearance stream of any annotation or widget flagged Hidden
    /// or NoView. Default true (#1586, #1581). Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// The appearance of an annotation nothing paints is text with no reader —
    /// the #1581 <c>widget-appearance</c> trap. Same class as a hidden layer.
    /// </remarks>
    public bool RemoveHiddenAnnotationAppearances { get; init; } = true;

    /// <summary>
    /// Strip the document <c>/Info</c> dictionary and the XMP <c>/Metadata</c>
    /// packet WHOLESALE instead of cutting the term out of them, keeping only
    /// the PDF/A and PDF/UA identifications (#1507/#1586). Default true.
    /// Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// <para>This is what makes the library, CLI and batch paths match the GUI
    /// safe copy, which has always done the wholesale strip. It also closes the
    /// #1583 custom-<c>/Info</c>-key leak by construction: a targeted scrub has
    /// to know the key names, and a producer can invent any.</para>
    /// <para>⚠️ <b>Changes existing behaviour.</b> Before #1586, a
    /// <c>RedactText</c> run kept <c>/Title</c>, <c>/Author</c>, the XMP
    /// packet and every custom schema, minus the term. Set this false to get
    /// that back.</para>
    /// </remarks>
    public bool StripDocumentMetadata { get; init; } = true;

    // ── Maximum-only removals (off by default) ──────────────────────────────

    /// <summary>
    /// Remove the document outline (bookmarks) entirely. Default false;
    /// <see cref="RedactionProfile.Maximum"/> sets it. Enforced by: Core.
    /// </summary>
    public bool RemoveBookmarks { get; init; } = false;

    /// <summary>
    /// Remove every Link annotation. Default false;
    /// <see cref="RedactionProfile.Maximum"/> sets it. Enforced by: Core.
    /// </summary>
    public bool RemoveLinkAnnotations { get; init; } = false;

    /// <summary>
    /// Remove every markup/comment annotation (§12.5.6.2 — Text, Highlight,
    /// StrikeOut, FreeText, Stamp, Ink, Popup, …). Default false;
    /// <see cref="RedactionProfile.Maximum"/> sets it. Enforced by: Core.
    /// </summary>
    public bool RemoveMarkupAnnotations { get; init; } = false;

    /// <summary>
    /// Remove AcroForm field names (<c>/T</c>) and tooltips (<c>/TU</c>).
    /// Default false; <see cref="RedactionProfile.Maximum"/> sets it.
    /// Enforced by: Core.
    /// </summary>
    /// <remarks>
    /// A field name is frequently a sentence ("Your name as printed on your
    /// passport") and is the last carrier a reviewer looks at. Removing it
    /// breaks form submission and screen-reader labelling, which is why this
    /// belongs to Maximum.
    /// </remarks>
    public bool RemoveFieldNames { get; init; } = false;

    /// <summary>
    /// Flatten forms and annotations into page content, so no interactive
    /// object survives to carry text. Default false;
    /// <see cref="RedactionProfile.Maximum"/> sets it. Enforced by: Core.
    /// </summary>
    public bool FlattenInteractiveContent { get; init; } = false;

    /// <summary>The all-defaults options — the <see cref="RedactionProfile.Standard"/> profile.</summary>
    public static RedactionOptions Default { get; } = new();

    /// <summary>
    /// The options for <paramref name="profile"/>. Everything else keeps its
    /// default; use a <c>with</c> expression to adjust.
    /// </summary>
    public static RedactionOptions ForProfile(RedactionProfile profile) => profile switch
    {
        RedactionProfile.Maximum => Maximum,
        _ => Default,
    };

    /// <summary>
    /// <see cref="RedactionProfile.Maximum"/>: every Standard removal, plus
    /// remove-whole on each kept carrier, bookmarks / links / markup / field
    /// names stripped, and forms and annotations flattened.
    /// </summary>
    /// <remarks>
    /// The <see cref="CarrierPolicy"/> is <see cref="Operations.CarrierScrubMode.RemoveWhole"/>
    /// on the carriers that are KEPT under Standard, and left at
    /// <see cref="Operations.CarrierScrubMode.Strip"/> on the rest: <c>/Info</c>,
    /// XMP, JavaScript, embedded files and XFA are removed wholesale by the
    /// flags above, so asking for RemoveWhole there would only produce refusal
    /// rows (the XFA carrier refuses it by design) about carriers that are
    /// already gone.
    /// </remarks>
    public static RedactionOptions Maximum { get; } = new()
    {
        Profile = RedactionProfile.Maximum,
        RemoveBookmarks = true,
        RemoveLinkAnnotations = true,
        RemoveMarkupAnnotations = true,
        RemoveFieldNames = true,
        FlattenInteractiveContent = true,
        CarrierPolicy = Operations.CarrierScrubPolicy.Default.With(
            Operations.RedactionCarriers.Outlines
            | Operations.RedactionCarriers.Annotations
            | Operations.RedactionCarriers.FormFields
            | Operations.RedactionCarriers.StructTree
            | Operations.RedactionCarriers.ActionUris,
            Operations.CarrierScrubMode.RemoveWhole),
    };

    internal bool CloseWidth => Width == WidthPolicy.CloseGap;
}
