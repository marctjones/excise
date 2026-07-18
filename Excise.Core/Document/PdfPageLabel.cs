using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Enumeration of PDF page label styles.
/// ISO 32000-2:2020 §12.4.2.
/// </summary>
public enum PdfPageLabelStyle
{
    /// <summary>
    /// Decimal Arabic numerals (1, 2, 3, ...).
    /// Corresponds to /S /D in PDF.
    /// </summary>
    Decimal,

    /// <summary>
    /// Uppercase Roman numerals (I, II, III, ...).
    /// Corresponds to /S /R in PDF.
    /// </summary>
    UppercaseRoman,

    /// <summary>
    /// Lowercase Roman numerals (i, ii, iii, ...).
    /// Corresponds to /S /r in PDF.
    /// </summary>
    LowercaseRoman,

    /// <summary>
    /// Uppercase letters (A, B, C, ..., Z, AA, BB, ...).
    /// Corresponds to /S /A in PDF.
    /// </summary>
    UppercaseLetters,

    /// <summary>
    /// Lowercase letters (a, b, c, ..., z, aa, bb, ...).
    /// Corresponds to /S /a in PDF.
    /// </summary>
    LowercaseLetters,

    /// <summary>
    /// No style — prefix only (when /S is absent).
    /// </summary>
    None
}

/// <summary>
/// Represents a page label definition at a given page index.
/// PDF spec §12.4.2: contains optional style (/S), prefix (/P), and start number (/St).
/// </summary>
public sealed record PdfPageLabel(
    string? Prefix = null,
    PdfPageLabelStyle Style = PdfPageLabelStyle.None,
    int StartNumber = 1)
{
    /// <summary>
    /// Format this label definition as the label for a given page offset.
    /// <paramref name="pageOffset"/> is 0-based offset from the page where this label starts.
    /// Returns the formatted label string (e.g., "i", "1", "A-1").
    /// </summary>
    public string Format(int pageOffset)
    {
        var number = StartNumber + pageOffset;
        var numericPart = Style switch
        {
            PdfPageLabelStyle.Decimal => number.ToString(),
            PdfPageLabelStyle.UppercaseRoman => ToRomanNumeral(number, uppercase: true),
            PdfPageLabelStyle.LowercaseRoman => ToRomanNumeral(number, uppercase: false),
            PdfPageLabelStyle.UppercaseLetters => ToLetterSequence(number, uppercase: true),
            PdfPageLabelStyle.LowercaseLetters => ToLetterSequence(number, uppercase: false),
            _ => string.Empty  // None style
        };
        return (Prefix ?? string.Empty) + numericPart;
    }

    /// <summary>
    /// Convert a positive integer to Roman numerals (I, V, X, L, C, D, M).
    /// </summary>
    private static string ToRomanNumeral(int num, bool uppercase)
    {
        if (num <= 0) return string.Empty;

        var romanMap = new[] {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        };

        var sb = new System.Text.StringBuilder();
        foreach (var (value, numeral) in romanMap)
        {
            while (num >= value)
            {
                sb.Append(numeral);
                num -= value;
            }
        }

        var roman = sb.ToString();
        return uppercase ? roman : roman.ToLower();
    }

    /// <summary>
    /// Convert a positive integer to a letter sequence (A, B, ..., Z, AA, BB, ..., ZZ, AAA, ...).
    /// </summary>
    private static string ToLetterSequence(int num, bool uppercase)
    {
        if (num <= 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        num--;  // Make 0-based for easier calculation

        while (true)
        {
            sb.Insert(0, (char)((num % 26) + (uppercase ? 'A' : 'a')));
            num /= 26;
            if (num == 0) break;
            num--;  // Adjust for AA, BB, etc. (not just A, B after Z)
        }

        return sb.ToString();
    }
}
