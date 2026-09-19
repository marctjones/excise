using System;
using System.Collections.Generic;
using System.Threading;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// #1667 — EVERY place a PDF can hang an embedded file, walked once.
///
/// <para><b>Why this exists.</b> Two recovery channels asked the same question
/// and got different answers. <c>CarrierTextRecovery.Attachments</c> walked the
/// catalog name tree plus catalog, page AND annotation <c>/AF</c>;
/// <c>ResidualArtefactRecovery</c> called
/// <see cref="PdfDocument.GetEmbeddedFiles"/>, which is catalog-only. A file
/// hung off a page's <c>/AF</c> was reported by one and not the other, so a
/// consumer filtering on the attachment channel saw two of the three shapes —
/// and the attachment channel is the one carrying the description a reader
/// actually needs.</para>
///
/// <para><b>Why not widen <see cref="PdfDocument.GetEmbeddedFiles"/>.</b> It is
/// public API with catalog-only semantics other callers may depend on —
/// including the portfolio check, where "what is in the name tree" is the
/// question rather than "what files exist anywhere". Widening it would change
/// an answer somebody is already relying on. This is a second, explicitly
/// broader enumeration; the two names say which question they answer.</para>
///
/// <para>⚠️ §7.11.4 and §14.13: <c>/AF</c> may appear on the catalog, a page, an
/// annotation, an XObject or a structure element. The last two are NOT walked
/// here — no fixture exercises them and guessing at coverage is what this type
/// exists to stop. When one turns up, add it here and both channels get it.</para>
/// </summary>
internal static class EmbeddedFileEnumerator
{
    /// <param name="page">
    /// The 1-based page the specification hangs off, or 0 for document level.
    /// </param>
    internal readonly record struct Found(PdfEmbeddedFile File, int PageNumber, string Source);

    /// <summary>
    /// Every embedded-file specification reachable from the catalog, the pages
    /// and their annotations. De-duplicated by dictionary identity, because the
    /// same specification is routinely referenced from two places — an
    /// <c>/AF</c> array conventionally points back at a name-tree entry
    /// (§14.13), and reporting it twice would read as two attachments.
    /// </summary>
    internal static IReadOnlyList<Found> All(
        PdfDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var found = new List<Found>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        try
        {
            foreach (var file in document.GetEmbeddedFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen.Add(file.RawDictionary))
                    found.Add(new Found(file, file.PageNumber ?? 0, "name tree"));
            }
        }
        catch
        {
            // A damaged name tree is not proof there are no attachments — the
            // /AF walk below may still find them, so this does not return.
        }

        void FromAssociatedFiles(PdfDictionary? owner, int page, string source)
        {
            if (owner == null) return;
            if (document.Resolve(owner.GetOptional("AF") ?? PdfNull.Instance) is not PdfArray af) return;

            foreach (var entry in af)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document.Resolve(entry) is not PdfDictionary fs || !seen.Add(fs)) continue;
                var parsed = PdfEmbeddedFileParser.ParseFileSpecification(document, fs, "");
                if (parsed != null) found.Add(new Found(parsed, page, source));
            }
        }

        FromAssociatedFiles(document.Catalog, 0, "catalog /AF");

        for (var i = 1; i <= document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPage page;
            try { page = document.GetPage(i); }
            catch { continue; }

            FromAssociatedFiles(page.Dictionary, i, "page /AF");

            if (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is PdfArray annots)
                foreach (var a in annots)
                    if (document.Resolve(a) is PdfDictionary annot)
                        FromAssociatedFiles(annot, i, "annotation /AF");
        }

        return found;
    }
}
