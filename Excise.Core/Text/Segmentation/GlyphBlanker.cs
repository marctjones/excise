using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// #1044 SPIKE — remove matched glyphs by overwriting their character codes in
/// place, instead of deleting the operator that contains them.
///
/// <code>
/// (Louise Anne Farrar) Tj   ->   (Louise Anne       ) Tj
/// </code>
///
/// <para><b>Why this might beat the current fallback.</b> When glyph removal
/// stalls, <c>RedactText</c> falls back to <c>RemoveIntersectingOperators</c>,
/// which deletes the whole text-showing operator — to remove one word it
/// removes the line. That is the mechanism behind #1038's 5–36% collateral.
/// Byte replacement is structurally bounded: the damage cannot exceed the
/// matched glyphs, because nothing else is touched. The operator, its
/// BT/ET bracketing, its <c>Tf</c> state and every other glyph stay exactly as
/// the producer wrote them.</para>
///
/// <para><b>⚠️ The trap this must not become.</b> "Invisible text" has two
/// meanings and one of them is the classic redaction failure:</para>
///
/// <list type="bullet">
///   <item>replace the CHARACTER CODE with a blank-drawing code — the original
///     codepoint is <b>gone from the file</b>. This is real removal.</item>
///   <item>set render mode <c>3 Tr</c>, or white fill, or cover with a box —
///     the codepoint <b>is still there</b>. This is the failure that makes the
///     news, and it is not what this does.</item>
/// </list>
///
/// <para><b>Scope, deliberately narrow.</b> Only single-byte codes in
/// non-CID fonts, where one code decodes to exactly one character, so the
/// decoded <c>CharacterIndex</c> maps 1:1 onto a byte offset in the operand.
/// Anything else — Type0/CID, multi-byte codes, a code decoding to a ligature —
/// is refused, and the caller keeps today's behaviour. A wrong byte offset
/// would corrupt a DIFFERENT glyph, which is worse than the collateral this
/// exists to reduce.</para>
/// </summary>
internal static class GlyphBlanker
{
    /// <summary>The blank code. 32 is <c>space</c> in every standard simple-font encoding.</summary>
    private const byte BlankCode = 0x20;

    /// <summary>
    /// A copy of <paramref name="op"/> with the matched glyphs' codes replaced
    /// by a blank, or null when this operator is not safely blankable.
    ///
    /// <para>Null is the honest answer, not a failure: the caller falls back to
    /// what it did before. Blanking the wrong byte is worse than not blanking.</para>
    /// </summary>
    public static ContentOperator? TryBlank(ContentOperator op, IReadOnlyList<LetterMatch> toRemove)
    {
        if (toRemove.Count == 0) return null;

        // Only Tj today. TJ carries an array of strings interleaved with
        // kerning numbers, so a decoded index spans elements and the mapping is
        // a separate problem — see the spike write-up.
        if (!string.Equals(op.Name, "Tj", StringComparison.Ordinal)) return null;
        if (op.Operands.Count != 1 || op.Operands[0] is not PdfString str) return null;

        foreach (var m in toRemove)
        {
            var letter = m.Letter;
            // 1:1 code-to-character is the whole precondition. Without it the
            // decoded index is not a byte offset.
            if (letter.IsCidFont || letter.CodeByteLength != 1) return null;
            if (letter.Value.Length != 1) return null;
            if (m.CharacterIndex < 0) return null;
        }

        var bytes = str.Bytes.ToArray();
        var blanked = 0;
        foreach (var m in toRemove)
        {
            if (m.CharacterIndex >= bytes.Length) return null;   // index outside the operand
            bytes[m.CharacterIndex] = BlankCode;
            blanked++;
        }
        if (blanked == 0) return null;

        return new ContentOperator(op.Name, new PdfObject[] { new PdfString(bytes, str.IsHex) })
        {
            BoundingBox = op.BoundingBox,
            TextContent = null,   // stale once the codes changed
        };
    }
}
