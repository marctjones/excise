using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// The form-side lists that name a widget (and the fields above it): the arrays of
/// <c>/AcroForm /Fields</c>, each field's <c>/Kids</c>, and <c>/AcroForm /CO</c>, as they were before
/// the widget was deleted (#2015).
/// </summary>
/// <remarks>
/// A save cuts every reference to a deleted annotation (#2012), including its place in the field
/// tree, and a field left without a widget goes with it. Undoing the delete puts the widget back on
/// its page; without this it stays out of the field tree, an orphan widget that other readers do not
/// list as a field. <see cref="Restore"/> puts each captured list back exactly as it was (order
/// included), so it is the inverse of the cut whether or not a save happened in between. Only lists
/// that name the widget or one of its ancestor fields are captured, so the cost is one walk of the
/// field tree per deleted widget.
/// </remarks>
internal sealed class FieldTreeLinkSnapshot
{
    private readonly PdfDocument _document;
    private readonly List<(PdfArray Array, int? Owner, PdfObject[] Items)> _lists;

    private FieldTreeLinkSnapshot(PdfDocument document, List<(PdfArray, int?, PdfObject[])> lists)
    {
        _document = document;
        _lists = lists;
    }

    /// <summary>Number of lists captured; zero when the annotation is not a form widget.</summary>
    internal int ListCount => _lists.Count;

    /// <summary>
    /// Capture the lists that name <paramref name="annotation"/> (a page annotation dictionary) or a
    /// field above it. Call before the annotation is removed.
    /// </summary>
    internal static FieldTreeLinkSnapshot Capture(PdfDocument document, PdfDictionary annotation)
    {
        var lists = new List<(PdfArray, int?, PdfObject[])>();
        var snapshot = new FieldTreeLinkSnapshot(document, lists);

        var chain = new HashSet<int>();
        var node = annotation;
        for (var depth = 0; node != null && depth < 64; depth++)
        {
            if (document.GetReferenceTo(node) is { } reference && !chain.Add(reference.ObjectNum))
                break;
            node = node.GetOptional("Parent") is { } parent ? document.Resolve(parent) as PdfDictionary : null;
        }
        if (chain.Count == 0
            || document.Catalog.GetOptional("AcroForm") is not { } acroFormRaw
            || document.Resolve(acroFormRaw) is not PdfDictionary acroForm)
        {
            return snapshot;
        }

        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var acroFormOwner = acroFormRaw is PdfReference afRef
            ? afRef.ObjectNum
            : document.Trailer.GetReferenceOrNull("Root")?.ObjectNum;

        void Visit(PdfObject? raw, int? holder, int depth)
        {
            if (raw == null || depth > 64 || document.Resolve(raw) is not PdfArray array)
                return;
            var owner = raw is PdfReference r ? r.ObjectNum : holder;
            if (array.Any(item => item is PdfReference ir && chain.Contains(ir.ObjectNum)))
                lists.Add((array, owner, array.ToArray()));
            foreach (var item in array)
            {
                if (document.Resolve(item) is PdfDictionary field && seen.Add(field))
                {
                    var fieldOwner = item is PdfReference fr ? fr.ObjectNum : owner;
                    Visit(field.GetOptional("Kids"), fieldOwner, depth + 1);
                }
            }
        }

        Visit(acroForm.GetOptional("Fields"), acroFormOwner, 0);
        Visit(acroForm.GetOptional("CO"), acroFormOwner, 0);
        return snapshot;
    }

    /// <summary>Put every captured list back as it was.</summary>
    internal void Restore()
    {
        var edited = new HashSet<int>();
        foreach (var (array, owner, items) in _lists)
        {
            while (array.Count > 0)
                array.RemoveAt(array.Count - 1);
            foreach (var item in items)
                array.Add(item);
            if (owner is { } number)
                edited.Add(number);
        }

        // An edit inside an array nested in a parsed dictionary does not clear that dictionary's
        // pristine flag, and the object store may evict a pristine object and re-parse the original:
        // re-register each owner, as the scrub that cut these references did.
        foreach (var number in edited)
            _document.ReplaceIndirectObject(number, _document.GetObject(number));
    }
}
