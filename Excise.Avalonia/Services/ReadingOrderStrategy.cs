namespace Excise.Avalonia.Services;

/// <summary>
/// How <see cref="TextSelectionEngine.SortReadingOrder(System.Collections.Generic.IEnumerable{Excise.Core.Text.Letter}, ReadingOrderStrategy)"/>
/// linearises a page's glyphs into the sequence used for text selection and
/// copy. The strategy is a user preference (#774): the default gives the
/// highest-quality copy on multi-column layouts, the others expose the older /
/// rawer behaviours for documents where the reader knows better.
/// </summary>
public enum ReadingOrderStrategy
{
    /// <summary>
    /// DEFAULT. Detect vertical column gutters and emit each column
    /// top-to-bottom before moving right to the next, so a cross-column or
    /// whole-page copy comes out column-by-column rather than interleaved.
    /// Single-column pages are byte-identical to <see cref="Simple"/>.
    /// </summary>
    ColumnAware = 0,

    /// <summary>
    /// Purely geometric: group glyphs into lines by vertical centre, order
    /// lines top-to-bottom and glyphs left-to-right within a line. Interleaves
    /// columns that share a vertical band (the pre-#774 behaviour).
    /// </summary>
    Simple = 1,

    /// <summary>
    /// Emit glyphs in the order Excise.Core produced them (content-stream /
    /// logical order, already bidi-reordered upstream). No geometric sort.
    /// </summary>
    RawStream = 2,
}
