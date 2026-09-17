namespace Excise.Core.Xfa;

/// <summary>
/// Collects what a layout run left out or approximated, counted by kind, so
/// the result can say it instead of looking complete.
/// </summary>
internal sealed class XfaReport
{
    private readonly Dictionary<string, int> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _scriptEvents = new(StringComparer.Ordinal);

    public void Note(string what)
    {
        _notes.TryGetValue(what, out var count);
        _notes[what] = count + 1;
    }

    /// <summary>Record a script that did not run, by the event that would run it.</summary>
    public void Script(string activity)
    {
        _scriptEvents.TryGetValue(activity, out var count);
        _scriptEvents[activity] = count + 1;
    }

    public IReadOnlyList<string> Notes => _notes
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Key} (×{kv.Value})")
        .ToList();

    public IReadOnlyDictionary<string, int> ScriptEvents => _scriptEvents;
}
