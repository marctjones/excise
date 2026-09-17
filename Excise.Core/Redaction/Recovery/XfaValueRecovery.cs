using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1609 — the READ mirror of <c>XfaXmlCarrier</c>: field values an XFA form
/// still holds after a redaction rewrote the page.
///
/// <para><b>The failure mode.</b> An XFA form (§12.7.8) keeps its data in an
/// XML packet under <c>/AcroForm /XFA</c>, separate from both the page content
/// and the AcroForm field dictionaries. Redacting the page leaves the packet
/// holding the values, and a reader that renders the XFA form paints them
/// straight back.</para>
///
/// <para><b>The asymmetry this closes.</b> <c>XfaXmlCarrier.ScrubTerms</c> has
/// removed these values for some time; nothing read them, so <c>unredact</c>
/// reported nothing on a document whose XFA data still held the redacted
/// value. Every carrier the scrub side knows about should have a read mirror,
/// or the audit under-reports by construction. (#1599 is the same asymmetry in
/// the other direction: a carrier the audit reads and the scrubber cannot
/// remove.)</para>
///
/// <para><b>An unparseable packet is REPORTED, not skipped.</b> Same posture as
/// the prior-revision channel: a packet excise cannot parse may well be
/// readable by another tool, so the shortfall is counted rather than silently
/// treated as clean. The scrub side counts the same thing as
/// <c>UnexaminedPacketCount</c>.</para>
/// </summary>
public static class XfaValueRecovery
{
    /// <summary>
    /// Elements that structure an XDP document rather than hold user data.
    /// Their text is form design, not entered values, and reporting it would
    /// bury a real leak the way dumping bulk metadata would.
    /// </summary>
    private static readonly HashSet<string> StructuralElements = new(StringComparer.Ordinal)
    {
        "xdp", "datasets", "data", "template", "config", "localeSet",
        "xmpmeta", "RDF", "Description", "sourceSet", "connectionSet",
    };

    /// <param name="FieldPath">Dotted path to the value, e.g. "form1.personal.ssn".</param>
    public readonly record struct XfaValue(string FieldPath, string Value);

    /// <param name="PacketsExamined">XFA streams successfully parsed.</param>
    /// <param name="PacketsUnexamined">
    /// Streams that would not parse as XML. NOT evidence they are clean.
    /// </param>
    public readonly record struct XfaSummary(bool HasXfa, int PacketsExamined, int PacketsUnexamined);

    public static (IReadOnlyList<XfaValue> Values, XfaSummary Summary) Scan(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var values = new List<XfaValue>();

        if (!XfaXmlCarrier.TryGetXfa(document, out _, out var xfa))
            return (values, new XfaSummary(HasXfa: false, 0, 0));

        var streams = document.Resolve(xfa) switch
        {
            PdfStream single => new List<PdfStream> { single },
            PdfArray packets => XfaXmlCarrier.ResolvePacketStreams(document, packets),
            _ => new List<PdfStream>(),
        };
        if (streams.Count == 0)
            return (values, new XfaSummary(HasXfa: true, 0, 1));

        // The standard packet-array shape is ONE XDP document split across the
        // streams, so the concatenation is what parses -- the same reasoning
        // the scrub side uses. Fall back to per-stream parsing when it does not.
        var combined = streams.Count == 1 ? streams[0].DecodedData : XfaXmlCarrier.Concatenate(streams);
        if (XfaXmlCarrier.TryLoadXml(combined, out var document1, out _))
        {
            Collect(document1.Root, values);
            return (values, new XfaSummary(true, streams.Count, 0));
        }

        var examined = 0;
        var unexamined = 0;
        foreach (var stream in streams)
        {
            if (XfaXmlCarrier.TryLoadXml(stream.DecodedData, out var parsed, out _))
            {
                Collect(parsed.Root, values);
                examined++;
            }
            else
            {
                unexamined++;
            }
        }
        return (values, new XfaSummary(true, examined, unexamined));
    }

    /// <summary>
    /// Leaf elements carrying text, with their dotted path. Leaves only: an
    /// ancestor's concatenated text is every descendant's value run together,
    /// which is noise rather than a finding.
    /// </summary>
    private static void Collect(XElement? root, List<XfaValue> values, string prefix = "", int depth = 0)
    {
        if (root == null || depth > 64) return;

        foreach (var element in root.Elements())
        {
            var name = element.Name.LocalName;
            var path = StructuralElements.Contains(name) || string.IsNullOrEmpty(name)
                ? prefix
                : string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";

            if (element.HasElements)
            {
                Collect(element, values, path, depth + 1);
                continue;
            }

            var text = element.Value;
            if (string.IsNullOrWhiteSpace(text)) continue;
            values.Add(new XfaValue(
                string.IsNullOrEmpty(path) ? name : path,
                text.Trim()));
        }
    }
}
