using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Xfa;

/// <summary>
/// Static-XFA support for #1898: which of the XFA template's own
/// <c>&lt;field&gt;</c> elements the <c>&lt;textEdit&gt;</c> UI marks
/// explicitly multi-line (<c>multiLine="1"</c>). A static XFA form (usable
/// AcroForm fields alongside the template, #1547
/// <see cref="PdfXfaFormKind.Static"/>) is filled through its AcroForm
/// fields, never <see cref="PdfXfaLayout"/> — that only lays out a Dynamic
/// form — so nothing else in excise reads the template for these.
/// AcroForm's own <c>/Ff</c> bit 12 is set by whatever tool generated the
/// shadow field and is not guaranteed to agree with the template it was
/// generated from.
/// </summary>
/// <remarks>
/// Matched by the field's own name (the last SOM segment, e.g. <c>f1_09</c>),
/// not the full dotted path: a static-XFA-generated AcroForm field's name is
/// the template SOM path with each segment's array index appended literally
/// (e.g. <c>topmostSubform[0].Page1[0].f1_09[0]</c>, confirmed against the
/// IRS W-9 fixture), and building that full indexed path would need the same
/// data-binding machinery <see cref="XfaMerge"/> uses for a Dynamic form's
/// repeated subforms. A bare-name collision across sibling subforms is rare
/// enough in a form designed to work through its AcroForm fields already
/// that this trades a small, theoretical over-match for not duplicating
/// that machinery.
/// </remarks>
internal static class XfaMultilineFieldNames
{
    private static readonly TimeSpan ResolveTimeLimit = TimeSpan.FromSeconds(2);
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    /// <summary>
    /// The template field names whose textEdit UI is explicitly multi-line.
    /// Empty when the document carries no XFA template, or the template
    /// fails to parse or resolve — a signal this can't read is simply
    /// absent, never a document-open failure.
    /// </summary>
    /// <remarks>
    /// Memoized per document (#1626): every <see cref="PdfDocument.GetAcroForm"/>
    /// asks, and text extraction parses the form once per page, so an XFA form
    /// re-parsed and re-resolved its whole template for every page it read
    /// (irs-w4: 13.5 to 62.7 MB of text extraction over 5 pages). The memo is
    /// keyed by the packet streams and the very byte arrays they hold now: any
    /// write to a packet replaces its array (or the stream, or the /XFA entry),
    /// so a changed template is never answered from the memo. An identical
    /// re-read under a new array is only a miss.
    /// </remarks>
    public static IReadOnlySet<string> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var streams = XfaPackets.ReadPacketStreams(document);
        if (streams == null)
            return Empty;

        var sources = new (PdfStream Stream, byte[] Encoded, byte[] Decoded)[streams.Count];
        try
        {
            // The same running-total refusal XfaPackets.TryRead applies, at the
            // same packet, so the key never decodes past the packet budget.
            long total = 0;
            for (var i = 0; i < streams.Count; i++)
            {
                sources[i] = (streams[i], streams[i].EncodedData, streams[i].DecodedData);
                total += sources[i].Decoded.LongLength;
                if (total > XfaBudget.MaxPacketBytes)
                    return Empty;
            }
        }
        catch (InvalidOperationException)
        {
            // A packet that cannot be decoded: no memo, read it as before.
            return ReadUncached(streams);
        }

        if (document.XfaMultilineNamesMemo is { } memo && memo.Matches(sources))
            return memo.Names;

        var names = ReadUncached(streams);
        document.XfaMultilineNamesMemo = new Memo(sources, names);
        return names;
    }

    /// <summary>
    /// The names <see cref="Read"/> returned for exactly these packet streams
    /// and byte arrays (compared by reference).
    /// </summary>
    internal sealed class Memo((PdfStream Stream, byte[] Encoded, byte[] Decoded)[] sources, IReadOnlySet<string> names)
    {
        public IReadOnlySet<string> Names { get; } = names;

        public bool Matches((PdfStream Stream, byte[] Encoded, byte[] Decoded)[] current)
        {
            if (current.Length != sources.Length) return false;
            for (var i = 0; i < current.Length; i++)
                if (!ReferenceEquals(current[i].Stream, sources[i].Stream)
                    || !ReferenceEquals(current[i].Encoded, sources[i].Encoded)
                    || !ReferenceEquals(current[i].Decoded, sources[i].Decoded))
                    return false;
            return true;
        }
    }

    private static IReadOnlySet<string> ReadUncached(List<PdfStream> streams)
    {
        if (!XfaPackets.TryRead(streams, out var packets, out _) || packets == null)
            return Empty;

        try
        {
            var budget = new XfaBudget(ResolveTimeLimit, CancellationToken.None);
            var resolvedRoot = new XfaTemplate(packets.Template, budget, new XfaReport()).Root;
            var ns = packets.TemplateNamespace;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in resolvedRoot.DescendantsAndSelf(ns + "field"))
            {
                var name = field.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name))
                    continue;

                var widget = field.Element(ns + "ui")?.Elements()
                    .FirstOrDefault(w => w.Name.LocalName is not ("extras" or "picture"));
                if (widget?.Name.LocalName == "textEdit"
                    && widget.Attribute("multiLine")?.Value?.Trim() == "1")
                {
                    names.Add(name);
                }
            }
            return names;
        }
        catch (XfaLayoutException)
        {
            // A malformed prototype chain or a resource bound hit: no signal,
            // not a document-open failure.
            return Empty;
        }
    }
}
