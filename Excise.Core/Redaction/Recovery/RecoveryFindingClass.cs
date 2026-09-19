using System;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1669 — how strongly a finding indicates a FAILED REDACTION, as opposed to
/// ordinary document structure.
///
/// <para><b>Why this exists.</b> Measured over 57 real court filings that leak
/// nothing, excise reported a finding on 28 of them — 50.9% specificity, where
/// x-ray scored 100%. The causes were not defects:</para>
/// <list type="bullet">
///   <item>38 link <c>/URI</c> actions — every brief cites something.</item>
///   <item>~37 XMP fields — <c>dc:creator</c>, <c>pdf:Keywords</c>, dates.</item>
///   <item>7 documents' OCR layers, one of them <b>13,686 findings</b>.</item>
/// </list>
/// <para>The first two categories are unambiguous and are furniture here. The
/// OCR layers are NOT — see the <c>HiddenText</c> case below for why they are
/// deliberately left as residue and what that costs.</para>
/// <para>Each is genuinely text a reader cannot see on the page, so the
/// existing <c>VisibleElsewhere</c> filter correctly lets it through. "Not on
/// the page" simply is not the same question as "a redaction leaked this".</para>
///
/// <para><b>⚠️ This RANKS, it does not FILTER.</b> Furniture is not innocent —
/// #608 is a term leaking into XMP, and #1155 is the same in an indirect
/// string. On a document where a term WAS redacted, a hit in the XMP is the
/// whole finding. So nothing here is suppressed; the class says how loudly to
/// report it, and the top-line verdict counts the classes that mean a
/// redaction failed. A tool that hid furniture would be trading one failure
/// (a flood) for a worse one (a silent leak).</para>
///
/// <para><b>Why not reuse <see cref="RedactionCarriers"/>.</b> That enum is the
/// SCRUB side's vocabulary — which carriers a term pass may touch. This is the
/// AUDIT side's question, which is different: <c>/Info</c> is one carrier there
/// and ordinary furniture here, while a structure-tree <c>/ActualText</c> is
/// one carrier there and the strongest possible signal here.</para>
/// </summary>
public enum RecoveryFindingClass
{
    /// <summary>
    /// Content that was covered or excised ON THE PAGE and survived — text
    /// under a mark, an image or vector under a box, a mark's own region.
    /// The thing a redaction exists to remove, found where it was removed.
    /// </summary>
    RedactionResidue,

    /// <summary>
    /// A carrier that RESTATES page content: <c>/ActualText</c>, <c>/Alt</c>,
    /// <c>/E</c>, a form field's <c>/V</c>, XFA datasets, a prior revision, a
    /// stale thumbnail, an embedded file, a retained image object.
    ///
    /// <para>Present BECAUSE of the page, so when the page was redacted and
    /// this was not, it is a leak — this is the #636 and #608 class.</para>
    /// </summary>
    ContentCarrier,

    /// <summary>
    /// Ordinary document furniture: document METADATA and NAVIGATION —
    /// <c>/Info</c>, XMP, link <c>/URI</c> targets, <c>/PieceInfo</c> private
    /// data, annotation authors.
    ///
    /// <para>The line is drawn there because it is the only place it can be
    /// drawn without guessing: none of these is page text. Anything ABOUT page
    /// text — visible, invisible, covered or restated — is a finding.</para>
    ///
    /// <para>⚠️ Present in documents nobody has ever redacted, which is why it
    /// dominates the false-positive column — and STILL worth reporting,
    /// because a redacted term appearing here is a real leak. Ranked low, never
    /// hidden.</para>
    /// </summary>
    DocumentFurniture,
}

/// <summary>Classifies a finding for <see cref="RecoveryFindingClass"/>.</summary>
public static class RecoveryFindingClassifier
{
    /// <summary>
    /// Classify one finding. <paramref name="isLinkedToAMark"/> is what
    /// separates an OCR layer from invisible text used to hide something: the
    /// same carrier means different things depending on whether somebody drew
    /// a redaction over it.
    /// </summary>
    public static RecoveryFindingClass Classify(
        string channel, string carrier, bool isLinkedToAMark)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(carrier);

        // Furniture first: these are recognisable by carrier regardless of
        // channel, and every document has them.
        if (carrier.StartsWith("/Info ", StringComparison.Ordinal)) return RecoveryFindingClass.DocumentFurniture;
        if (carrier.StartsWith("XMP ", StringComparison.Ordinal)) return RecoveryFindingClass.DocumentFurniture;
        if (carrier.StartsWith("/PieceInfo", StringComparison.Ordinal)) return RecoveryFindingClass.DocumentFurniture;
        if (carrier.StartsWith("action /URI", StringComparison.Ordinal)) return RecoveryFindingClass.DocumentFurniture;
        if (carrier.StartsWith("annotation /T", StringComparison.Ordinal)) return RecoveryFindingClass.DocumentFurniture;

        return channel switch
        {
            // Found where a redaction was applied.
            RecoveryScanner.Channels.HiddenText when isLinkedToAMark => RecoveryFindingClass.RedactionResidue,
            RecoveryScanner.Channels.MarkRegion => RecoveryFindingClass.RedactionResidue,
            RecoveryScanner.Channels.CoveredImage => RecoveryFindingClass.RedactionResidue,
            RecoveryScanner.Channels.CoveredVector => RecoveryFindingClass.RedactionResidue,
            RecoveryScanner.Channels.Residue => RecoveryFindingClass.RedactionResidue,

            // Restates page content, so a leak when the page was redacted.
            RecoveryScanner.Channels.Carrier => RecoveryFindingClass.ContentCarrier,
            RecoveryScanner.Channels.MarkedContent => RecoveryFindingClass.ContentCarrier,
            RecoveryScanner.Channels.FormField => RecoveryFindingClass.ContentCarrier,
            RecoveryScanner.Channels.Xfa => RecoveryFindingClass.ContentCarrier,
            RecoveryScanner.Channels.PriorRevision => RecoveryFindingClass.ContentCarrier,
            RecoveryScanner.Channels.Thumbnail => RecoveryFindingClass.ContentCarrier,
            RecoveryScanner.Channels.Attachment => RecoveryFindingClass.ContentCarrier,
            RecoveryScanner.Channels.ImageLayer => RecoveryFindingClass.ContentCarrier,
            RecoveryScanner.Channels.OcrDifferential => RecoveryFindingClass.RedactionResidue,

            // ⚠️ UNLINKED hidden text — invisible or low-contrast, with no
            // mark over it — is RESIDUE, deliberately, and this is the line
            // that cost two wrong attempts.
            //
            // It is genuinely ambiguous: a scanned page's OCR layer and a
            // "redaction" done by setting 3 Tr on the sensitive words are
            // STRUCTURALLY IDENTICAL. The registry already concluded as much —
            // ocr-layer-left-in-place is `partial` because judging one from the
            // other is the reader's call.
            //
            // Two heuristics were tried and both ate the mode they were meant
            // to separate from: suppressing it when the page carries no mark
            // took text-render-mode-3 from 3/3 to 0/3, because that mode IS
            // invisible text with no mark. So the ambiguity is not resolved
            // here; it is resolved in the SAFE direction. For a security audit
            // a false positive costs attention and a false negative costs a
            // leak, and the cost is not symmetric.
            //
            // The price is measured and accepted: 7 of 57 clean filings report
            // their OCR layer, one with 13,686 findings. That is the honest
            // number, and #1669 tracks making the REPORT rank it rather than
            // making the classifier guess.
            RecoveryScanner.Channels.HiddenText => RecoveryFindingClass.RedactionResidue,

            _ => RecoveryFindingClass.ContentCarrier,
        };
    }

    /// <summary>
    /// Convenience over a finding. A finding carrying a <c>MarkId</c> is linked.
    /// </summary>
    public static RecoveryFindingClass Classify(RecoveredFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return Classify(finding.Channel, finding.Carrier, finding.MarkId != null);
    }

    /// <summary>
    /// Does this class mean a redaction failed? The top-line verdict counts
    /// these; furniture is reported below the line.
    /// </summary>
    public static bool IndicatesAFailedRedaction(RecoveryFindingClass c) =>
        c is RecoveryFindingClass.RedactionResidue or RecoveryFindingClass.ContentCarrier;
}
