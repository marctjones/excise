using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;
using Excise.Core.Xfa;

namespace Excise.Core.Text.Segmentation;

public static partial class CarrierTextRecovery
{
    private const string XfaTemplateNamespacePrefix = "http://www.xfa.org/schema/xfa-template/";
    private const string XfaDataNamespacePrefix = "http://www.xfa.org/schema/xfa-data/";

    // XFA value content elements (XFA 3.3 "value" children) that hold text.
    private static readonly HashSet<string> XfaValueElements = new(StringComparer.Ordinal)
    {
        "text", "exData", "integer", "decimal", "float", "date", "dateTime", "time", "boolean",
    };

    /// <summary>
    /// XFA form XML (§12.7.8): the datasets packet holds every filled value,
    /// and the template holds default values, captions, list items, tooltips
    /// and scripts. Loaded through <see cref="XfaXmlCarrier.TryLoadXml"/> (DTD
    /// prohibited, no resolver), bounded by <see cref="XfaBudget.MaxPacketBytes"/>.
    /// </summary>
    /// <remarks>
    /// Not via <c>XfaPackets.TryRead</c>: that refuses a form without a template
    /// packet, and a datasets-only XFA carries values all the same.
    /// </remarks>
    private static void ScanXfa(PdfDocument doc, Collector c)
    {
        if (doc.Resolve(doc.Catalog?.GetOptional("AcroForm") ?? PdfNull.Instance) is not PdfDictionary acro
            || acro.GetOptional("XFA") is not { } xfa)
            return;

        var xfaObj = xfa is PdfReference r ? r.ObjectNum : 0;
        var streams = doc.Resolve(xfa) switch
        {
            PdfStream single => new List<PdfStream> { single },
            PdfArray array => XfaXmlCarrier.ResolvePacketStreams(doc, array),
            _ => new List<PdfStream>(),
        };
        if (streams.Count == 0)
        {
            c.Presence("XFA", "/XFA entry present but holds no packet streams", 0, xfaObj);
            return;
        }

        long total = 0;
        foreach (var s in streams)
        {
            var decoded = SafeDecoded(s);
            if (decoded is null)
            {
                c.Presence("XFA", "an XFA packet stream could not be decoded", 0, xfaObj);
                return;
            }
            total += decoded.LongLength;
        }
        if (total > XfaBudget.MaxPacketBytes)
        {
            c.Presence("XFA", $"XFA packets ({total} bytes) exceed the {XfaBudget.MaxPacketBytes} byte scan bound", 0, xfaObj);
            return;
        }

        var roots = new List<XElement>();
        if (XfaXmlCarrier.TryLoadXml(XfaXmlCarrier.Concatenate(streams), out var combined, out _)
            && combined.Root is { } combinedRoot)
        {
            roots.Add(combinedRoot);
        }
        else
        {
            foreach (var s in streams)
            {
                if (XfaXmlCarrier.TryLoadXml(s.DecodedData, out var packet, out _) && packet.Root is { } packetRoot)
                    roots.Add(packetRoot);
                else
                    c.Presence("XFA", "an XFA packet is not readable XML", 0, xfaObj);
            }
        }

        foreach (var root in roots)
        {
            IEnumerable<XElement> packets = root.Name.LocalName == "xdp" ? root.Elements() : new[] { root };
            foreach (var packet in packets)
            {
                c.Token.ThrowIfCancellationRequested();
                var ns = packet.Name.NamespaceName;
                if (packet.Name.LocalName == "datasets" && ns.StartsWith(XfaDataNamespacePrefix, StringComparison.Ordinal))
                    ScanXfaDatasets(packet, c, xfaObj);
                else if (packet.Name.LocalName == "template" && ns.StartsWith(XfaTemplateNamespacePrefix, StringComparison.Ordinal))
                    ScanXfaTemplate(packet, c, xfaObj);
            }
        }
    }

    private static void ScanXfaDatasets(XElement datasets, Collector c, int xfaObj)
    {
        var guard = 0;
        foreach (var element in datasets.Descendants())
        {
            if (guard++ > WalkGuard || c.Full) return;
            if (element.HasElements) continue;
            c.Text("XFA datasets value", element.Value, 0, xfaObj, ElementPath(element, datasets));
        }
    }

    private static void ScanXfaTemplate(XElement template, Collector c, int xfaObj)
    {
        var guard = 0;
        foreach (var element in template.Descendants())
        {
            if (guard++ > WalkGuard || c.Full) return;
            var local = element.Name.LocalName;
            var parent = element.Parent?.Name.LocalName;
            string? carrier = null;
            if (XfaValueElements.Contains(local) && parent is "value" or "items")
            {
                carrier = parent == "items"
                    ? "XFA template list item"
                    : element.Ancestors().Any(a => a.Name.LocalName == "caption")
                        ? "XFA template caption"
                        : "XFA template default value";
            }
            else if (local is "toolTip" or "speak")
            {
                carrier = "XFA template " + local;
            }
            else if (local == "script")
            {
                carrier = "XFA template script";
            }

            if (carrier != null)
                c.Text(carrier, element.Value, 0, xfaObj, XfaOwnerName(element));
        }
    }

    /// <summary>The nearest named field/draw/subform, for a readable location.</summary>
    private static string? XfaOwnerName(XElement element)
    {
        foreach (var a in element.Ancestors())
        {
            if (a.Name.LocalName is "field" or "draw" or "subform" or "exclGroup"
                && a.Attribute("name")?.Value is { Length: > 0 } name)
                return $"{a.Name.LocalName} '{name}'";
        }
        return null;
    }

    private static string ElementPath(XElement element, XElement stop)
    {
        var parts = new List<string>();
        for (var e = element; e != null && e != stop; e = e.Parent)
            parts.Add(e.Name.LocalName);
        parts.Reverse();
        return string.Join('/', parts);
    }
}
