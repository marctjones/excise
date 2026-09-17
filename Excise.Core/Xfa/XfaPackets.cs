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

    private XfaPackets(XElement template, XElement? dataRoot)
    {
        Template = template;
        DataRoot = dataRoot;
    }

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

        // The usual shape: one XDP document split over the streams.
        if (XfaXmlCarrier.TryLoadXml(XfaXmlCarrier.Concatenate(streams), out var combined, out _)
            && combined.Root is { } root)
        {
            Collect(root, ref template, ref dataRoot);
        }
        else
        {
            // Some producers write each packet as its own well-formed document.
            foreach (var stream in streams)
            {
                if (XfaXmlCarrier.TryLoadXml(stream.DecodedData, out var packet, out _)
                    && packet.Root is { } packetRoot)
                {
                    Collect(packetRoot, ref template, ref dataRoot);
                }
            }
        }

        if (template == null)
        {
            reason = "The XFA data has no readable template packet.";
            return false;
        }

        packets = new XfaPackets(template, dataRoot);
        return true;
    }

    private static void Collect(XElement root, ref XElement? template, ref XElement? dataRoot)
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
            else if (dataRoot == null
                && packet.Name.LocalName == "datasets"
                && ns.StartsWith(DataNamespacePrefix, StringComparison.Ordinal))
            {
                var data = packet.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "data" && e.Name.NamespaceName == ns);
                dataRoot = data?.Elements().FirstOrDefault();
            }
        }
    }
}
