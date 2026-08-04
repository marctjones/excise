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
    /// Parameters §7.4.6 defines that the decoder does not consume.
    ///
    /// /EndOfBlock (default true) means "an EOFB pattern ends the data"; excise
    /// decodes to /Rows or to end-of-data instead, which reaches the same place
    /// on a well-formed stream and differs on a truncated one.
    ///
    /// /DamagedRowsBeforeError asks the decoder to tolerate N corrupt rows
    /// before failing; excise has no partial-failure mode.
    /// </summary>
    private static readonly (string Key, string Feature)[] UnreadParameters =
    {
        ("EndOfBlock", "EndOfBlock"),
        ("DamagedRowsBeforeError", "DamagedRowsBeforeError"),
    };

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
        if (decodeParms.GetBool("EncodedByteAlign", false)) features.Add("EncodedByteAlign");
        if (decodeParms.GetBool("EndOfLine", false)) features.Add("EndOfLine");

        var columns = decodeParms.GetInt("Columns", 1728);
        if (columns != 1728) features.Add($"Columns={columns}");
        if (columns <= 0)
        {
            diagnostics.Add($"/Columns {columns} is not a positive width");
            unsupported.Add("Columns");
        }

        var rows = decodeParms.GetInt("Rows", 0);
        if (rows > 0) features.Add("Rows");

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
