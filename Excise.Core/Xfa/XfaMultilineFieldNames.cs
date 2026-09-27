using System.Xml.Linq;
using Excise.Core.Document;

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
    public static IReadOnlySet<string> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!XfaPackets.TryRead(document, out var packets, out _) || packets == null)
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
