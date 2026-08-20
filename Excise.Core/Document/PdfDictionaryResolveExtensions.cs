using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Typed dictionary reads that RESOLVE indirect references (#1050).
///
/// <para><b>The defect these exist to end.</b> <see cref="PdfDictionary"/>'s
/// own typed accessors are bare type checks:</para>
///
/// <code>
/// _items.TryGetValue(key, out var v) &amp;&amp; v is PdfDictionary d ? d : null
/// </code>
///
/// <para>A value of <c>15 0 R</c> is a <see cref="PdfReference"/>, not a
/// <see cref="PdfDictionary"/>, so it returns <b>null — indistinguishable from
/// the key being absent</b>. Indirect references are not exotic; they are what
/// real producers emit. #1040 was exactly this: <c>/Resources /XObject</c> as
/// <c>15 0 R</c> in Nitro Pro output meant <c>ReferencesAnyForm</c> saw no
/// forms, the glyph remover never reached the text, and excise drew a black box
/// over an intact name and reported success.</para>
///
/// <para><b>Why the resolver is a parameter and not state on the dictionary.</b>
/// A resolver field would have to be null for the 110 places production
/// constructs a <c>PdfDictionary</c> by hand (the writer, form authoring,
/// security handlers). That gives two behaviour classes of one type — resolves
/// sometimes, silently returns null otherwise — which recreates the very
/// failure being fixed, inside the fix. Measured before choosing: 110
/// construction sites.</para>
///
/// <para><b>Why the old accessors were renamed rather than changed.</b>
/// <c>GetDictionaryOrNull</c> is now <c>GetDirectDictionaryOrNull</c>. The
/// rename is compiler-enforced, so every existing call site had to be visited
/// deliberately, and the name now states what it does: it reads a DIRECT value
/// and does not follow a reference. A silent behaviour change would have been
/// invisible; a rename cannot be.</para>
/// </summary>
public static class PdfDictionaryResolveExtensions
{
    /// <summary>
    /// The dictionary at <paramref name="key"/>, following an indirect
    /// reference. Null when the key is absent or resolves to something else.
    /// </summary>
    public static PdfDictionary? ResolveDictionary(
        this PdfDictionary dict, PdfDocument doc, string key)
    {
        var entry = dict.GetOptional(key);
        return entry == null ? null : doc.Resolve(entry) as PdfDictionary;
    }

    /// <summary>
    /// The array at <paramref name="key"/>, following an indirect reference.
    /// Null when the key is absent or resolves to something else.
    /// </summary>
    public static PdfArray? ResolveArray(
        this PdfDictionary dict, PdfDocument doc, string key)
    {
        var entry = dict.GetOptional(key);
        return entry == null ? null : doc.Resolve(entry) as PdfArray;
    }

    /// <summary>
    /// The stream at <paramref name="key"/>, following an indirect reference.
    /// Streams are ALWAYS indirect objects in a conforming file (§7.3.8), so a
    /// non-resolving read of a stream key is wrong essentially always.
    /// </summary>
    public static PdfStream? ResolveStream(
        this PdfDictionary dict, PdfDocument doc, string key)
    {
        var entry = dict.GetOptional(key);
        return entry == null ? null : doc.Resolve(entry) as PdfStream;
    }
}
