using System;
using System.Collections.Generic;
using Excise.Core.Primitives;

namespace Excise.Core.Filters.Ccitt;

/// <summary>
/// What a CCITTFaxDecode stream ASKS FOR, and which of those excise supports.
/// </summary>
internal sealed class CcittCapabilityReport
{
    public CcittCapabilityReport(
        IReadOnlyList<string> features,
        IReadOnlyList<string> unsupportedFeatures,
        IReadOnlyList<string> diagnostics)
    {
        Features = features;
        UnsupportedFeatures = unsupportedFeatures;
        Diagnostics = diagnostics;
    }

    /// <summary>Spec features this stream uses, e.g. "Group4", "BlackIs1".</summary>
    public IReadOnlyList<string> Features { get; }

    /// <summary>Of those, the ones excise does not honour.</summary>
    public IReadOnlyList<string> UnsupportedFeatures { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>True when nothing this stream asks for is unimplemented.</summary>
    public bool FullySupported => UnsupportedFeatures.Count == 0;
}

/// <summary>
/// Reports which parts of CCITTFaxDecode (§7.4.6, ITU-T T.4/T.6) a given stream
/// depends on, and which of those excise does not implement.
///
/// WHY THIS EXISTS
///
/// "How complete is our CCITT support?" had no answer short of reading the
/// decoder. A blank page told you a page was blank, not whether the format
/// feature it needed was missing or merely mis-implemented — and those want
/// completely different work. JBIG2 has had this since #402
/// (Jbig2CapabilityClassifier) and it is why JBIG2's remaining gap can be named
/// exactly ("retained symbol-dictionary coding contexts, gating one corpus
/// file") while the other formats could only be described as "some pages are
/// blank".
///
/// Deliberately driven by /DecodeParms rather than by the bitstream. Unlike
/// JBIG2 — whose capability is carried in segment headers — everything a CCITT
/// stream needs is declared in its parameter dictionary, so a classifier that
/// parsed the bitstream would be re-implementing the decoder to learn nothing
/// extra.
///
/// The unsupported list is derived from what CcittFaxDecoder actually reads,
/// NOT from a reading of the spec. Adding a key here without implementing it
/// would report a capability excise does not have.
/// </summary>
internal static class CcittCapabilityClassifier
{
    /// <summary>
    /// Parameters §7.4.6 defines that the decoder does not consume, and that are
    /// unsupported however they are set.
    ///
    /// /DamagedRowsBeforeError asks the decoder to tolerate N corrupt rows
    /// before failing; excise has no partial-failure mode at all, so the request
    /// cannot be honoured for any value. No file in any of the four corpora sets
    /// it.
    ///
    /// /EndOfBlock is deliberately NOT in this list — see ClassifyEndOfBlock.
    /// </summary>
    private static readonly (string Key, string Feature)[] UnreadParameters =
    {
        ("DamagedRowsBeforeError", "DamagedRowsBeforeError"),
    };

    /// <summary>
    /// /EndOfBlock, classified by VALUE rather than by presence (#893).
    ///
    /// The first version reported it unsupported whenever the key appeared. That
    /// was wrong for the commonest case and wrong in the direction that matters:
    /// TRUE is the spec default, and it means "an EOFB pattern terminates the
    /// data". excise stops at /Rows or at end-of-data, which on a well-formed
    /// stream is the same place. Nothing is unsupported, and saying otherwise
    /// tells a user their file cannot be handled when it demonstrably can.
    ///
    /// This is the same over-reporting bug found in the JBIG2 classifier (#656),
    /// where a fabricated "unsupported" nearly bought an implementation of a
    /// feature the file did not use. These reports drive what gets built, so a
    /// false alarm is not a harmless conservatism.
    ///
    /// FALSE is the one that asks for non-default behaviour: /Rows becomes
    /// authoritative and any EOFB is to be ignored rather than obeyed. excise
    /// still reaches the same place on a well-formed stream — measured on
    /// pdfjs/ccitt_EndOfBlock_false.pdf, the only corpus file that sets it, which
    /// renders at parity with mutool, pdftocairo and Ghostscript (ink 0.648 /
    /// 0.651 / 0.654) — and can diverge on a TRUNCATED one, where excise runs to
    /// end-of-data instead of stopping at the row count. That residual risk is
    /// what gets reported, with a diagnostic that says what it actually is.
    /// </summary>
    private static void ClassifyEndOfBlock(
        PdfDictionary decodeParms,
        ISet<string> features,
        ISet<string> unsupported,
        ICollection<string> diagnostics)
    {
        if (decodeParms.GetOptional("EndOfBlock") == null)
            return; // absent: the default, and the default is what excise does

        features.Add("EndOfBlock");

        if (decodeParms.GetBool("EndOfBlock", true))
            return; // true: also the default

        unsupported.Add("EndOfBlock");
        diagnostics.Add(
            "/EndOfBlock false asks the decoder to ignore an EOFB pattern and trust /Rows; " +
            "excise decodes to /Rows or to end-of-data, which agrees on a well-formed stream " +
            "and may over-read a truncated one");
    }

    public static CcittCapabilityReport Analyze(PdfDictionary? decodeParms)
    {
        var features = new SortedSet<string>(StringComparer.Ordinal);
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<string>();

        if (decodeParms == null)
        {
            // All defaults: K=0 (Group 3 one-dimensional), 1728 columns.
            features.Add("Group3-1D");
            features.Add("DefaultParameters");
            return new CcittCapabilityReport(
                new List<string>(features), new List<string>(unsupported), diagnostics);
        }

        var k = decodeParms.GetInt("K", 0);
        if (k < 0) features.Add("Group4");
        else if (k == 0) features.Add("Group3-1D");
        else features.Add("Group3-2D");

        if (decodeParms.GetBool("BlackIs1", false)) features.Add("BlackIs1");
        if (decodeParms.GetBool("EndOfLine", false)) features.Add("EndOfLine");

        // /EncodedByteAlign is honoured for GROUP 4 ONLY.
        //
        // The first version of this classifier reported it as supported
        // unconditionally, and the classifier caught its own overclaim:
        // DecodeGroup4 passes the flag through to a per-row AlignToByte(), while
        // DecodeGroup3_1D and DecodeGroup3_2D do not even TAKE the parameter —
        // it is dropped at the call site. So for every K >= 0, including the
        // DEFAULT K = 0, the flag does nothing.
        //
        // Reported truthfully rather than fixed. A two-line fix mirroring Group
        // 4 was written and REVERTED: no file in any of the four corpora sets
        // this flag, so there is no witness, and a synthetic fixture could not
        // be made to discriminate — TrySkipEOL already consumes the pad bits on
        // anything simple enough to hand-author, which a mutation test proved by
        // passing with the fix disabled. Shipping a decoder change that no test
        // can distinguish from doing nothing is how unverified behaviour
        // accumulates. Tracked in #893; implement when a real file needs it.
        if (decodeParms.GetBool("EncodedByteAlign", false))
        {
            features.Add("EncodedByteAlign");
            if (k >= 0)
                unsupported.Add("EncodedByteAlign");
        }

        var columns = decodeParms.GetInt("Columns", 1728);
        if (columns != 1728) features.Add($"Columns={columns}");
        if (columns <= 0)
        {
            diagnostics.Add($"/Columns {columns} is not a positive width");
            unsupported.Add("Columns");
        }

        var rows = decodeParms.GetInt("Rows", 0);
        if (rows > 0) features.Add("Rows");

        ClassifyEndOfBlock(decodeParms, features, unsupported, diagnostics);

        foreach (var (key, feature) in UnreadParameters)
        {
            if (decodeParms.GetOptional(key) == null) continue;
            features.Add(feature);
            unsupported.Add(feature);
        }

        return new CcittCapabilityReport(
            new List<string>(features), new List<string>(unsupported), diagnostics);
    }
}
