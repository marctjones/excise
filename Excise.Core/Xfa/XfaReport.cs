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

    /// <summary>Scripts found and not run, by event: those that were counted, minus those that ran.</summary>
    public IReadOnlyDictionary<string, int> ScriptEvents => _scriptEvents
        .Select(kv => (kv.Key, Left: kv.Value - (_scriptsRun.TryGetValue(kv.Key, out var ran) ? ran : 0)))
        .Where(t => t.Left > 0)
        .ToDictionary(t => t.Key, t => t.Left, StringComparer.Ordinal);

    private readonly Dictionary<string, int> _scriptsRun = new(StringComparer.Ordinal);
    private readonly List<string> _scriptFailures = new();
    private readonly HashSet<string> _scriptWrites = new(StringComparer.Ordinal);

    /// <summary>A FormCalc script ran to the end.</summary>
    public void ScriptRan(string activity)
    {
        _scriptsRun.TryGetValue(activity, out var n);
        _scriptsRun[activity] = n + 1;
    }

    /// <summary>A FormCalc script failed and its writes were undone.</summary>
    public void ScriptFailed(string what)
    {
        if (_scriptFailures.Count < 50) _scriptFailures.Add(what);
    }

    /// <summary>A script wrote a value onto the named field. Recorded because it is derived data.</summary>
    public void ScriptWrote(string field)
    {
        if (_scriptWrites.Count < 200) _scriptWrites.Add(field);
    }

    public IReadOnlyDictionary<string, int> ScriptsRun => _scriptsRun;
    public IReadOnlyList<string> ScriptFailures => _scriptFailures;
    public IReadOnlyCollection<string> ScriptWrites => _scriptWrites;
}
