using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>A number tree (§7.9.7): the <see cref="PdfNameTree"/> walk over /Nums.</summary>
internal static class PdfNumberTree
{
    internal static IEnumerable<(PdfObject Key, PdfObject Value)> Enumerate(PdfDocument doc, PdfObject? root) =>
        PdfNameTree.Pairs(doc, root, "Nums");
}
