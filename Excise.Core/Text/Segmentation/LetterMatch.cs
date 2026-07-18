namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Represents a match between a letter from text extraction and a character in a text operation.
/// Used for accurate glyph-level redaction with position information.
/// </summary>
public class LetterMatch
{
    /// <summary>
    /// The letter from PDF text extraction (with accurate position).
    /// </summary>
    public required Letter Letter { get; init; }

    /// <summary>
    /// The character index within the text operation's string.
    /// </summary>
    public required int CharacterIndex { get; init; }

    /// <summary>
    /// Raw bytes from the PDF content stream for this glyph.
    /// Essential for CJK fonts where Unicode text doesn't preserve original encoding.
    /// </summary>
    public byte[]? RawBytes { get; set; }

    /// <summary>
    /// The text operation this letter belongs to.
    /// Useful for tracking which operation a letter came from during segmentation.
    /// </summary>
    public object? SourceOperation { get; set; }
}
