using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Excise.App.Services.Printing;

/// <summary>
/// One CUPS destination (#1710): what <c>lp -d</c> takes and what the printer
/// chooser shows.
/// </summary>
/// <param name="Name">The queue name, exactly as CUPS spells it.</param>
/// <param name="State">What <c>lpstat -p</c> said about it, or null when it said nothing.</param>
/// <param name="IsDefault">Whether <c>lpstat -d</c> named this queue.</param>
internal sealed record CupsPrintQueue(string Name, string? State = null, bool IsDefault = false)
{
    /// <summary>What the chooser lists: the name, its state, and "(default)".</summary>
    internal string DisplayName =>
        Name +
        (string.IsNullOrWhiteSpace(State) ? string.Empty : $" — {State}") +
        (IsDefault ? "  (default)" : string.Empty);

    /// <summary>
    /// What the chooser's combo box shows. The list binds to the record
    /// itself, as the Reduce File Size dialog's choices do, so the item
    /// template needs no second data type.
    /// </summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// Reads CUPS destinations out of <c>lpstat</c> output (#1710). Pure text in,
/// data out: every branch is unit-tested without a printer, a scheduler or a
/// subprocess.
/// </summary>
/// <remarks>
/// Three <c>lpstat</c> outputs are parsed, and each one alone is insufficient:
/// <c>-e</c> enumerates every destination including DNS-SD discovered ones but
/// says nothing about state, <c>-p</c> gives state but omits discovered queues
/// on some versions, and <c>-d</c> is the only place the default appears. The
/// list is the union, so a queue seen by either enumerator is offered.
/// </remarks>
internal static class CupsPrintQueues
{
    /// <summary>
    /// Merge the three outputs into the list the chooser shows. Ordered with
    /// the default first, then alphabetically, so the preselected entry is at
    /// the top.
    /// </summary>
    internal static IReadOnlyList<CupsPrintQueue> Parse(string? lpstatE, string? lpstatP, string? lpstatD)
    {
        var states = ParsePrinterStates(lpstatP);
        var names = new List<string>();
        foreach (var name in ParseDestinationNames(lpstatE).Concat(states.Keys))
        {
            if (!names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }

        string? defaultName = ParseDefaultDestination(lpstatD);
        return names
            .Select(name => new CupsPrintQueue(
                name,
                states.TryGetValue(name, out var state) ? state : null,
                string.Equals(name, defaultName, StringComparison.Ordinal)))
            .OrderByDescending(queue => queue.IsDefault)
            .ThenBy(queue => queue.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// <c>lpstat -e</c>: one destination name per line, nothing else.
    /// </summary>
    internal static IReadOnlyList<string> ParseDestinationNames(string? lpstatE)
    {
        if (string.IsNullOrWhiteSpace(lpstatE))
            return Array.Empty<string>();

        var names = new List<string>();
        foreach (var raw in lpstatE.Split('\n'))
        {
            var name = raw.Trim();
            // A destination name has no spaces (RFC 8011 / CUPS forbids them),
            // so a line with one is a message, not a queue.
            if (name.Length == 0 || name.Any(char.IsWhiteSpace))
                continue;
            if (!names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// <c>lpstat -p</c>: "printer NAME is idle.  enabled since ..." or
    /// "printer NAME disabled since ...". The value is the short state word,
    /// which is all the chooser needs; everything after the first sentence is
    /// a timestamp.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParsePrinterStates(string? lpstatP)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(lpstatP))
            return states;

        foreach (var raw in lpstatP.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("printer ", StringComparison.Ordinal))
                continue;
            var rest = line["printer ".Length..].TrimStart();
            int space = rest.IndexOf(' ');
            if (space <= 0)
                continue;
            var name = rest[..space];
            var tail = rest[(space + 1)..].Trim();

            string state;
            if (tail.StartsWith("is ", StringComparison.Ordinal))
            {
                // "is idle.  enabled since ..." / "is printing job 3."
                var after = tail["is ".Length..];
                int stop = after.IndexOf('.');
                state = (stop >= 0 ? after[..stop] : after).Trim();
            }
            else if (tail.StartsWith("disabled", StringComparison.Ordinal))
            {
                state = "disabled";
            }
            else
            {
                int stop = tail.IndexOf('.');
                state = (stop >= 0 ? tail[..stop] : tail).Trim();
            }

            if (name.Length > 0 && state.Length > 0)
                states[name] = state;
        }
        return states;
    }

    /// <summary>
    /// <c>lpstat -d</c>: "system default destination: NAME", or a sentence
    /// saying there is none.
    /// </summary>
    internal static string? ParseDefaultDestination(string? lpstatD)
    {
        if (string.IsNullOrWhiteSpace(lpstatD))
            return null;

        foreach (var raw in lpstatD.Split('\n'))
        {
            var line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            if (!line[..colon].Contains("default", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = line[(colon + 1)..].Trim();
            if (name.Length > 0 && !name.Any(char.IsWhiteSpace))
                return name;
        }
        return null;
    }

    /// <summary>
    /// The job identifier out of <c>lp</c>'s "request id is Cups-PDF-1 (1
    /// file(s))", or null when the line is not there. Logged, never asserted
    /// on: a queue is free to word this differently.
    /// </summary>
    internal static string? ParseRequestId(string? lpOutput)
    {
        if (string.IsNullOrWhiteSpace(lpOutput))
            return null;

        const string marker = "request id is ";
        foreach (var raw in lpOutput.Split('\n'))
        {
            var line = raw.Trim();
            int start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                continue;
            var rest = line[(start + marker.Length)..].Trim();
            int space = rest.IndexOf(' ');
            var id = (space > 0 ? rest[..space] : rest).Trim();
            if (id.Length > 0)
                return id;
        }
        return null;
    }
}

/// <summary>
/// Parses the page-range box of the Linux print dialog (#1710) — "1-3,7",
/// "all", "" — into the same <see cref="PrintPageRange"/> list the Windows
/// path uses, and back into the value CUPS's <c>page-ranges</c> takes.
/// </summary>
internal static class PrintPageRangeText
{
    /// <summary>
    /// Parse <paramref name="text"/> against a document of
    /// <paramref name="pageCount"/> pages. An empty or whitespace input means
    /// every page and yields an empty list. Returns false with
    /// <paramref name="error"/> set for anything that is not a list of
    /// in-range numbers and N-M spans.
    /// </summary>
    internal static bool TryParse(
        string? text,
        int pageCount,
        out IReadOnlyList<PrintPageRange> ranges,
        out string? error)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        ranges = Array.Empty<PrintPageRange>();
        error = null;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        var parsed = new List<PrintPageRange>();
        foreach (var rawPart in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                continue;

            int dash = part.IndexOf('-');
            string fromText = dash < 0 ? part : part[..dash].Trim();
            string toText = dash < 0 ? part : part[(dash + 1)..].Trim();
            if (!TryPage(fromText, pageCount, out int from) || !TryPage(toText, pageCount, out int to))
            {
                error = $"'{part}' is not a page or page range of this {pageCount}-page document.";
                return false;
            }
            parsed.Add(from <= to ? new PrintPageRange(from, to) : new PrintPageRange(to, from));
        }

        if (parsed.Count == 0)
        {
            error = "Type page numbers like 1-3,7, or leave the box empty to print every page.";
            return false;
        }

        ranges = parsed;
        return true;
    }

    private static bool TryPage(string text, int pageCount, out int page)
    {
        page = 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page))
            return false;
        return page >= 1 && page <= pageCount;
    }

    /// <summary>
    /// The ranges as CUPS's <c>page-ranges</c> value, or null for "every page".
    /// </summary>
    internal static string? ToCupsValue(IReadOnlyList<PrintPageRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count == 0)
            return null;
        return string.Join(',', ranges.Select(range =>
            range.From == range.To
                ? range.From.ToString(CultureInfo.InvariantCulture)
                : $"{range.From.ToString(CultureInfo.InvariantCulture)}-{range.To.ToString(CultureInfo.InvariantCulture)}"));
    }
}
