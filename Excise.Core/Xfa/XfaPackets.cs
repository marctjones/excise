using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;

namespace Excise.Core.Xfa;

/// <summary>
/// The XFA packets excise lays out, read from <c>/AcroForm /XFA</c>.
/// </summary>
/// <remarks>
/// Packets are identified by NAMESPACE. Local names are not enough: the config
/// packet carries its own <c>&lt;template&gt;</c> element (the Designer base
/// path), and picking it would lay out nothing.
/// </remarks>
internal sealed class XfaPackets
{
    private const string TemplateNamespacePrefix = "http://www.xfa.org/schema/xfa-template/";
    private const string DataNamespacePrefix = "http://www.xfa.org/schema/xfa-data/";

    private const string XmlDsigNamespace = "http://www.w3.org/2000/09/xmldsig#";

    private XfaPackets(XElement template, XElement? dataRoot, XElement? datasets, bool hasXmlSignature)
    {
        Template = template;
        DataRoot = dataRoot;
        Datasets = datasets;
        HasXmlSignature = hasXmlSignature;
    }

    /// <summary>
    /// The <c>&lt;xfa:datasets&gt;</c> element, whose children other than <c>xfa:data</c> (IRCC's
    /// <c>LOVFile</c>) feed <c>bindItems</c> (XFA 3.3 p212, p624). Null when there is no datasets packet.
    /// </summary>
    public XElement? Datasets { get; }

    /// <summary>
    /// The form carries an XML digital signature (XFA 3.3 p559-562): a <c>signature</c> packet, or an
    /// XML-DSig <c>Signature</c> element in the datasets. Respecting the signed state means changing no
    /// data it covers, so no field is editable (#2027).
    /// </summary>
    public bool HasXmlSignature { get; }

    /// <summary>The <c>&lt;template&gt;</c> packet root.</summary>
    public XElement Template { get; }

    /// <summary>The template namespace (it carries the XFA version).</summary>
    public XNamespace TemplateNamespace => Template.Name.Namespace;

    /// <summary>
    /// The root data group: the single element child of
    /// <c>&lt;xfa:datasets&gt;&lt;xfa:data&gt;</c>. Null when the form has no data.
    /// </summary>
    public XElement? DataRoot { get; }

    /// <summary>Read and parse the packets, or say why not.</summary>
    public static bool TryRead(PdfDocument document, out XfaPackets? packets, out string reason)
    {
        packets = null;
        reason = string.Empty;

        if (document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is not PdfDictionary acroForm
            || acroForm.GetOptional("XFA") is not { } xfa)
        {
            reason = "The document has no /AcroForm /XFA entry.";
            return false;
        }

        var streams = document.Resolve(xfa) switch
        {
            PdfStream single => new List<PdfStream> { single },
            PdfArray array => XfaXmlCarrier.ResolvePacketStreams(document, array),
            _ => new List<PdfStream>(),
        };
        if (streams.Count == 0)
        {
            reason = "The /XFA entry holds no packet streams.";
            return false;
        }

        long total = 0;
        foreach (var stream in streams)
        {
            total += stream.DecodedData.LongLength;
            if (total > XfaBudget.MaxPacketBytes)
            {
                reason = $"The XFA packets are larger than {XfaBudget.MaxPacketBytes / (1024 * 1024)} MB.";
                return false;
            }
        }

        XElement? template = null;
        XElement? dataRoot = null;
        XElement? datasets = null;
        bool signed = false;

        // The usual shape: one XDP document split over the streams.
        if (XfaXmlCarrier.TryLoadXml(XfaXmlCarrier.Concatenate(streams), out var combined, out _)
            && combined.Root is { } root)
        {
            Collect(root, ref template, ref dataRoot, ref datasets, ref signed);
        }
        else
        {
            // Some producers write each packet as its own well-formed document.
            foreach (var stream in streams)
            {
                if (XfaXmlCarrier.TryLoadXml(stream.DecodedData, out var packet, out _)
                    && packet.Root is { } packetRoot)
                {
                    Collect(packetRoot, ref template, ref dataRoot, ref datasets, ref signed);
                }
            }
        }

        if (template == null)
        {
            reason = "The XFA data has no readable template packet.";
            return false;
        }

        packets = new XfaPackets(template, dataRoot, datasets, signed);
        return true;
    }

    private static void Collect(
        XElement root, ref XElement? template, ref XElement? dataRoot, ref XElement? datasets, ref bool signed)
    {
        // A packet is the root itself (single-packet stream) or a child of
        // <xdp:xdp>. Look no deeper: packets do not nest.
        IEnumerable<XElement> candidates = root.Name.LocalName == "xdp"
            ? root.Elements()
            : new[] { root };

        foreach (var packet in candidates)
        {
            var ns = packet.Name.NamespaceName;
            if (template == null
                && packet.Name.LocalName == "template"
                && ns.StartsWith(TemplateNamespacePrefix, StringComparison.Ordinal))
            {
                template = packet;
            }
            else if (datasets == null
                && packet.Name.LocalName == "datasets"
                && ns.StartsWith(DataNamespacePrefix, StringComparison.Ordinal))
            {
                datasets = packet;
                var data = packet.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "data" && e.Name.NamespaceName == ns);
                dataRoot = data?.Elements().FirstOrDefault();
                // An XML signature enveloped in the data (XFA 3.3 p559-562).
                if (packet.Descendants().Any(e => e.Name.NamespaceName == XmlDsigNamespace && e.Name.LocalName == "Signature"))
                    signed = true;
            }
            else if (ns == XmlDsigNamespace)
            {
                // The detached-signature packet, <signature xmlns="...xmldsig#"> (XFA 3.3 p1040).
                signed = true;
            }
        }
    }
}
