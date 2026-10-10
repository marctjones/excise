using System.Globalization;
using System.Text.RegularExpressions;

namespace Excise.Core.Redaction;

/// <summary>
/// The human-readable reading of a redaction's note list (#2057). The structured
/// report keeps one entry per affected line; a person reading a 161-hit
/// redaction needs the lines that mean "this is still readable" in front of
/// them, not buried under a page of layout bookkeeping. This type only ever
/// CONDENSES the layout notes. It never reorders, rewrites, or drops any other
/// note, and it never changes the list it is given.
/// </summary>
internal static partial class RedactionNoteSummary
{
    /// <summary>Prefix of one width-closure note (#1751), as added to the safety report.</summary>
    internal const string WidthNotePrefix = "WIDTH NOT CLOSED: ";

    /// <summary>Prefix of the closing verdict a non-clean redaction ends its notes with.</summary>
    internal const string NotCleanClosingPrefix = "This redaction was NOT clean";

    /// <summary>
    /// Condense the per-line width notes into one summary line. Every other note,
    /// including a width note that states a shift in the file (a different kind
    /// of problem from layout), is kept as it is, in its original order, ahead
    /// of the summary. The summary goes in front of a closing "NOT clean" verdict
    /// when there is one, otherwise last. With <paramref name="expandWidthNotes"/>
    /// the list is returned unchanged.
    /// </summary>
    /// <param name="detailHint">Appended to the summary to say where the per-line detail is, or null.</param>
    internal static IReadOnlyList<string> Condense(
        IReadOnlyList<string> notes, bool expandWidthNotes = false, string? detailHint = null)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (expandWidthNotes) return notes;

        var kept = new List<string>(notes.Count);
        var rolled = new List<string>();
        foreach (var note in notes)
        {
            if (IsLayoutWidthNote(note)) rolled.Add(note);
            else kept.Add(note);
        }

        if (rolled.Count == 0) return notes;

        var summary = Summarise(rolled, detailHint);
        var closing = kept.FindIndex(n => n.StartsWith(NotCleanClosingPrefix, StringComparison.Ordinal));
        if (closing < 0) kept.Add(summary);
        else kept.Insert(closing, summary);
        return kept;
    }

    /// <summary>
    /// A width note about alignment only: the text is gone and the line sits a
    /// measured distance from where a closed gap would put it. A note that
    /// says a shift was written into the file is NOT one of these.
    /// </summary>
    private static bool IsLayoutWidthNote(string note) =>
        note.StartsWith(WidthNotePrefix, StringComparison.Ordinal) &&
        (note.Contains("its edge moved by", StringComparison.Ordinal) ||
         note.Contains("short of its right margin", StringComparison.Ordinal));

    private static string Summarise(List<string> rolled, string? detailHint)
    {
        var pages = new HashSet<int>();
        double worst = 0;
        foreach (var note in rolled)
        {
            var page = PageRegex().Match(note);
            if (page.Success) pages.Add(int.Parse(page.Groups[1].Value, CultureInfo.InvariantCulture));
            foreach (Match pt in PointsRegex().Matches(note))
            {
                var text = pt.Groups[1].Value.Replace(',', '.');
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    worst = Math.Max(worst, Math.Abs(value));
            }
        }

        var lines = rolled.Count == 1 ? "1 line" : $"{rolled.Count} lines";
        var where = pages.Count == 0
            ? ""
            : pages.Count == 1 ? " on 1 page" : $" on {pages.Count} pages";
        return $"{WidthNotePrefix}{lines}{where} keep an edge up to {worst.ToString("F1", CultureInfo.InvariantCulture)} pt " +
               "from where it was, because the removed text could not be closed up in a justified, centred " +
               "or marker-bound line. The text is removed; only the line's spacing differs from the original." +
               (detailHint == null ? "" : " " + detailHint);
    }

    [GeneratedRegex(@"page (\d+),")]
    private static partial Regex PageRegex();

    [GeneratedRegex(@"(-?\d+[.,]\d+) pt")]
    private static partial Regex PointsRegex();
}
