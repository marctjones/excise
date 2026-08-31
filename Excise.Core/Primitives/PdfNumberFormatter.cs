using System.Globalization;

namespace Excise.Core.Primitives;

/// <summary>
/// Deterministic real-number formatting for PDF output. See issue #762.
/// </summary>
/// <remarks>
/// <para>
/// .NET's <c>"G"</c> format emits the shortest string that round-trips the
/// exact <see cref="double"/> bit pattern, which faithfully reproduces
/// accumulated floating-point noise (e.g. <c>216.01600000000002</c>,
/// <c>49.343999999999994</c>). Because the noise digits depend on how the
/// value was computed, the emitted bytes differ across platforms — and on
/// Windows a coordinate's digit run coincidentally matched a redacted
/// number, tripping the carrier-agnostic saved-bytes redaction check
/// (a false positive, not a leak; #762).
/// </para>
/// <para>
/// This formatter instead rounds to six decimal places and trims trailing
/// zeros (<c>216.016</c>, <c>49.344</c>). Six decimals keeps any coordinate
/// perturbation below 5e-7 pt — orders of magnitude under a rendered pixel,
/// so redaction rectangle bounds and visual baselines are unaffected —
/// while float noise only appears well past the sixth decimal. The format
/// never uses exponent notation (invalid in PDF syntax) and always uses
/// <see cref="CultureInfo.InvariantCulture"/>.
/// </para>
/// </remarks>
internal static class PdfNumberFormatter
{
    /// <summary>
    /// Format a PDF real number deterministically: invariant culture, at
    /// most six decimal places, trailing zeros trimmed, no exponent
    /// notation. Whole values format without a decimal point.
    /// </summary>
    internal static string Format(double value)
    {
        // Non-finite values are invalid PDF regardless of format; preserve
        // the legacy "G" spelling rather than emitting "∞" from "0.######".
        if (!double.IsFinite(value))
            return value.ToString("G", CultureInfo.InvariantCulture);

        var result = value.ToString("0.######", CultureInfo.InvariantCulture);

        // "0.######" renders values in (-5e-7, 0) as "-0"; normalize.
        return result == "-0" ? "0" : result;
    }
}
