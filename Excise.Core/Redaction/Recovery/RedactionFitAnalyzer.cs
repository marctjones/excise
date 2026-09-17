using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Excise.Core.Fonts;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1589 — what could fit a redaction mark: how many characters, which pattern
/// classes, which dictionary words, and how many bits of uncertainty are left.
///
/// <para><b>This is measurement, not recovery.</b> Nothing here reads a removed
/// character. It states a CONSTRAINT the removed text must satisfy, and the
/// size of the set that satisfies it. Even at one surviving candidate the
/// output is "one candidate fits, 0 bits remain", never "the answer is X" —
/// the epistemic difference between a constraint and a claim is the whole
/// safety property (#1131/#1126), and it is why this returns a
/// <see cref="RecoveryConfidence.Candidate"/> finding and not a certain one.</para>
///
/// <para><b>Why bits are the security number.</b> A candidate list is only as
/// interesting as it is short. "14 of 90,000 surnames fit" is a statement about
/// how much the redaction leaked, and it stays true whether or not a reader
/// finds the right one in the list. Context ranking reorders the list; it never
/// changes membership, so the bit count is a property of the width bound alone
/// and is reproducible.</para>
/// </summary>
public static class RedactionFitAnalyzer
{
    /// <summary>
    /// Pattern classes worth naming, in the order a reader cares about. Each is
    /// a fixed-shape string whose width can be measured directly, so "does an
    /// SSN fit" is answered by measurement rather than by a character count.
    /// </summary>
    private static readonly (string Name, string Sample)[] FixedFormats =
    {
        ("US SSN (ddd-dd-dddd)", "000-00-0000"),
        ("US phone ((ddd) ddd-dddd)", "(000) 000-0000"),
        ("date (dd/dd/dddd)", "00/00/0000"),
        ("date (dddd-dd-dd)", "0000-00-00"),
        ("currency amount ($d,ddd.dd)", "$0,000.00"),
    };

    /// <param name="MinCharacters">Using the font's WIDEST glyph — fewest that can fill the span.</param>
    /// <param name="MaxCharacters">Using the font's NARROWEST glyph — most that can fit.</param>
    /// <param name="Confidence">"fits exactly" | "narrowed" | "wide open".</param>
    public sealed record FitReport(
        double WidthPt,
        string Font,
        double FontSizePt,
        int MinCharacters,
        int MaxCharacters,
        IReadOnlyList<string> PatternClasses,
        IReadOnlyList<Candidate> Candidates,
        int CandidatesConsidered,
        double BitsLeaked,
        string Confidence,
        string? MetricNote);

    /// <param name="ErrorPt">Rendered width minus the budget. Negative = narrower than the gap.</param>
    public readonly record struct Candidate(string Text, double WidthPt, double ErrorPt);

    /// <summary>
    /// Analyse one mark's width budget.
    /// </summary>
    /// <param name="widthPt">The span the removed text occupied.</param>
    /// <param name="baseFont">/BaseFont name; metrics are keyed by it, not the resource name.</param>
    /// <param name="dictionary">Candidate words. Empty is fine — the length and pattern analysis stands alone.</param>
    /// <param name="tolerancePt">How close a candidate's rendered width must be.</param>
    /// <param name="maxCandidates">Bound on the returned list; the COUNT is still reported in full.</param>
    /// <param name="paddingPt">
    /// How much of <paramref name="widthPt"/> may be BOX PADDING rather than
    /// text. A redaction box is drawn around the run it covers, not flush to
    /// the glyphs, so a mark's width is an UPPER bound on the removed text's
    /// width and a candidate is admissible when it is up to this much narrower.
    /// Zero for a true glyph-to-glyph gap, where the budget is an equality.
    /// Getting this wrong in the strict direction is not a safe default: it
    /// silently rejects the right answer, which reads as "nothing fits" and
    /// understates the leak.
    /// </param>
    public static FitReport Analyse(
        double widthPt,
        string baseFont,
        double fontSizePt,
        IReadOnlyList<string>? dictionary = null,
        double tolerancePt = 0.5,
        int maxCandidates = 50,
        double paddingPt = 0)
    {
        if (widthPt <= 0 || fontSizePt <= 0)
            return Empty(widthPt, baseFont, fontSizePt, "degenerate width or font size");

        var (narrowest, widest) = ExtremeGlyphWidths(baseFont, fontSizePt);
        if (narrowest <= 0 || widest <= 0)
        {
            // No metrics for this font: the width is still a real measurement,
            // but nothing can be said about how many characters fill it. Saying
            // so beats inventing a range from a default font.
            return Empty(widthPt, baseFont, fontSizePt,
                $"no standard metrics for /{baseFont}; character range not derivable");
        }

        // The character range uses the FULL width as its upper bound and the
        // padding-adjusted width as its lower one.
        var minChars = Math.Max(1, (int)Math.Floor(Math.Max(0, widthPt - paddingPt) / widest));
        var maxChars = Math.Max(minChars, (int)Math.Ceiling(widthPt / narrowest));

        var patterns = FittingPatternClasses(widthPt, baseFont, fontSizePt, tolerancePt + paddingPt);

        var considered = dictionary?.Count ?? 0;
        var candidates = new List<Candidate>();
        foreach (var word in dictionary ?? Array.Empty<string>())
        {
            var width = MeasureWidth(word, baseFont, fontSizePt);
            if (width < 0) continue;
            var error = width - widthPt;
            // Narrower than the budget by up to the padding allowance is fine;
            // WIDER than it by more than the tolerance is not -- the text
            // cannot have been wider than the box drawn around it.
            if (error <= tolerancePt && error >= -(paddingPt + tolerancePt))
                candidates.Add(new Candidate(word, Math.Round(width, 2), Math.Round(error, 2)));
        }
        candidates.Sort((a, b) => Math.Abs(a.ErrorPt).CompareTo(Math.Abs(b.ErrorPt)));

        // log2 of the admissible set. With no dictionary there is no set to be
        // uncertain about, so the character range carries the statement instead
        // and the bit count is reported as 0 with the note saying why.
        var fitCount = candidates.Count;
        var bits = fitCount > 0 ? Math.Log2(fitCount) : 0;

        return new FitReport(
            Math.Round(widthPt, 2), baseFont, fontSizePt,
            minChars, maxChars, patterns,
            candidates.Take(maxCandidates).ToList(),
            considered,
            Math.Round(bits, 2),
            DescribeConfidence(fitCount, considered),
            considered == 0 ? "no dictionary supplied; bits reflect the pattern analysis only" : null);
    }

    /// <summary>
    /// Which named formats fit the budget, plus the open-ended classes the
    /// character range admits. A fixed format is tested by MEASURING its sample
    /// rather than counting characters, because a proportional font makes
    /// "ten digits" and "ten letters" very different widths.
    /// </summary>
    private static IReadOnlyList<string> FittingPatternClasses(
        double widthPt, string baseFont, double fontSizePt, double tolerancePt)
    {
        var fitting = new List<string>();

        foreach (var (name, sample) in FixedFormats)
        {
            var width = MeasureWidth(sample, baseFont, fontSizePt);
            if (width >= 0 && Math.Abs(width - widthPt) <= tolerancePt) fitting.Add(name);
        }

        // Open-ended classes: stated as ranges, since any length in the range
        // is admissible. Digits are measured separately because most text fonts
        // give them a single tabular width, which makes "digits only" a much
        // tighter statement than the letter range.
        var digitWidth = MeasureWidth("0", baseFont, fontSizePt);
        if (digitWidth > 0)
        {
            var digits = (int)Math.Round(widthPt / digitWidth);
            if (digits >= 1 && Math.Abs(digits * digitWidth - widthPt) <= tolerancePt)
                fitting.Add($"exactly {digits} digit(s)");
        }

        return fitting;
    }

    /// <summary>Rendered width in points, or -1 when any character lacks a metric.</summary>
    internal static double MeasureWidth(string text, string baseFont, double fontSizePt)
    {
        double total = 0;
        foreach (var ch in text)
        {
            if (!StandardFontMetrics.TryGetWidth(baseFont, ch, out var w)) return -1;
            total += w;
        }
        return total / 1000.0 * fontSizePt;
    }

    /// <summary>
    /// Narrowest and widest printable glyph widths in points. These bound the
    /// character count: nothing can fit more than width/narrowest, and nothing
    /// fewer than width/widest can fill the span.
    /// </summary>
    private static (double Narrowest, double Widest) ExtremeGlyphWidths(string baseFont, double fontSizePt)
    {
        double narrowest = double.MaxValue, widest = 0;
        for (var c = '!'; c <= '~'; c++)
        {
            if (!StandardFontMetrics.TryGetWidth(baseFont, c, out var w) || w <= 0) continue;
            var pt = w / 1000.0 * fontSizePt;
            if (pt < narrowest) narrowest = pt;
            if (pt > widest) widest = pt;
        }
        return narrowest == double.MaxValue ? (0, 0) : (narrowest, widest);
    }

    /// <summary>
    /// Plain wording for the report. Deliberately not a percentage: "narrowed"
    /// is honest about a shortlist without implying a probability nothing here
    /// measured.
    /// </summary>
    private static string DescribeConfidence(int fitCount, int considered) => considered == 0
        ? "no dictionary — length and pattern constraints only"
        : fitCount switch
        {
            0 => "nothing in the dictionary fits",
            1 => "fits exactly",
            <= 10 => "narrowed",
            _ => "wide open",
        };

    private static FitReport Empty(double widthPt, string font, double size, string note) => new(
        Math.Round(widthPt, 2), font, size, 0, 0,
        Array.Empty<string>(), Array.Empty<Candidate>(), 0, 0, "not analysable", note);

    /// <summary>One-line human summary, the shape #1589 asks the CLI to print.</summary>
    public static string Describe(FitReport report)
    {
        if (report.MinCharacters == 0)
            return string.Create(CultureInfo.InvariantCulture,
                $"{report.WidthPt}pt gap — {report.MetricNote}");

        var patterns = report.PatternClasses.Count > 0
            ? string.Join("; ", report.PatternClasses)
            : "no named pattern fits";
        var top = report.Candidates.Count > 0
            ? $", top: {string.Join(", ", report.Candidates.Take(3).Select(c => c.Text))}"
            : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{report.WidthPt}pt in {report.Font} {report.FontSizePt}pt — " +
            $"{report.MinCharacters}-{report.MaxCharacters} characters; {patterns}; " +
            $"{report.Candidates.Count} of {report.CandidatesConsidered} dictionary word(s) fit " +
            $"({report.BitsLeaked} bits, {report.Confidence}){top}");
    }
}
