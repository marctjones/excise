namespace Excise.Core.Text;

/// <summary>
/// How <see cref="TextSelectionEngine.JoinText(System.Collections.Generic.IReadOnlyList{Excise.Core.Text.Letter}, WhitespaceMode)"/>
/// turns a run of selected glyphs into copied plain text — specifically what
/// whitespace it inserts between lines. Word spacing (the space between glyphs
/// on the same line) is identical in every mode; only the treatment of the
/// vertical dimension differs. The mode is a user preference that extends the
/// #774 reading-order/selection settings.
/// </summary>
public enum WhitespaceMode
{
    /// <summary>
    /// DEFAULT. Line-faithful word spacing plus two heuristic additions:
    /// <list type="bullet">
    /// <item>A blank line (<c>\n\n</c>) is emitted between two body lines whose
    /// vertical gap is meaningfully larger than the block's typical leading —
    /// i.e. a detected PARAGRAPH break.</item>
    /// <item>Lines beginning with a bullet/number marker (•, -, –, *, or
    /// <c>N.</c>/<c>N)</c>) are kept as tight, own-line LIST items and their
    /// left indentation is preserved as leading spaces so the copy still reads
    /// as a list.</item>
    /// </list>
    /// Both additions are heuristic — see
    /// <c>docs/copy-whitespace-reliability.md</c> for measured reliability and
    /// the concrete cases where detection is wrong. Wrapped lines within a
    /// paragraph are NOT reflowed (they keep their hard <c>\n</c>); mid-paragraph
    /// wrap-joining is deliberately out of the default because justified and
    /// hyphenated text make it unreliable.
    /// </summary>
    Smart = 0,

    /// <summary>
    /// The pre-existing behaviour: a single <c>\n</c> on every visual line
    /// change and a space on a same-line word gap. No paragraph or list
    /// detection. Byte-identical to the copy output shipped before this feature —
    /// choose it when the heuristics mis-read a document's layout.
    /// </summary>
    LineFaithful = 1,
}
