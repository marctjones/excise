using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Excise.App.Automation;

/// <summary>
/// One operation in a live GUI performance scenario (#1497).
/// </summary>
/// <remarks>
/// Deliberately a small closed set rather than a DSL. Every op maps onto a view
/// model member or a viewer API — never synthetic input and never accessibility,
/// because AX activity perturbs the very numbers the scenario exists to measure
/// (an AX <c>entire contents</c> query pegs the app at ~96% CPU, and AX traffic
/// muddied the first #1477 reading).
/// </remarks>
internal enum PerfStepOp
{
    /// <summary>Record a sample and a marker without doing anything else.</summary>
    Mark,

    /// <summary>Open a document by path through the view model's scripting load path.</summary>
    Open,

    /// <summary>Close the current document through <c>CloseDocumentCommand</c>.</summary>
    Close,

    /// <summary>Block until the viewer reports no renders in flight, or the timeout.</summary>
    WaitIdle,

    /// <summary>Sit still for a fixed wall duration (an idle-CPU window).</summary>
    Idle,

    /// <summary>Advance the current page index by <see cref="PerfScenarioStep.Count"/>.</summary>
    PageBy,

    /// <summary>Scroll the continuous viewport through <see cref="PerfScenarioStep.Count"/> page heights.</summary>
    ScrollPages,

    /// <summary>Set <c>ZoomLevel</c> to <see cref="PerfScenarioStep.Value"/>.</summary>
    Zoom,

    /// <summary>Set <c>ViewMode</c> (single | continuous).</summary>
    ViewMode,

    /// <summary>Set <c>SearchText</c> and let the search pipeline run.</summary>
    Search,

    /// <summary>Redact a term through the scripting redaction path.</summary>
    RedactText,

    /// <summary>
    /// Request a viewer cache trim at Background | Warn | Critical, in process.
    /// This is what replaces <c>sudo memory_pressure</c> in the runbook: the
    /// macOS pressure-signal delivery itself was verified live once on
    /// 2026-09-14 and is not re-tested here (#1497).
    /// </summary>
    Trim,
}

/// <summary>One step of a scenario. Unused fields stay null by design.</summary>
internal sealed record PerfScenarioStep(
    PerfStepOp Op,
    string? Label = null,
    string? Document = null,
    string? Text = null,
    double? Value = null,
    int? Count = null,
    string? Level = null,
    double? Seconds = null)
{
    /// <summary>The name this step carries in the journal and the metrics marker.</summary>
    internal string Name => Label ?? Op.ToString().ToLowerInvariant();
}

/// <summary>A named sequence of steps, with the reason it exists.</summary>
/// <remarks>
/// <paramref name="Why"/> is mandatory and mirrors
/// <c>tests/reference-performance/fixtures.json</c>: a scenario nobody can
/// explain is a scenario nobody will maintain.
/// </remarks>
internal sealed record PerfScenario(string Id, string Why, IReadOnlyList<PerfScenarioStep> Steps);

/// <summary>
/// Reads <c>tests/gui-perf-scenarios.json</c>. Hand-rolled over
/// <see cref="JsonDocument"/> rather than reflection-based deserialization so it
/// keeps working in the Native AOT build, the same reason
/// <c>MetricsJsonlSink</c> uses a source-generated context.
/// </summary>
internal static class PerfScenarioFile
{
    internal const int SchemaVersion = 1;

    internal static IReadOnlyList<PerfScenario> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"gui-perf scenarios: schemaVersion must be {SchemaVersion}.");
        }

        if (!root.TryGetProperty("scenarios", out var scenarios) ||
            scenarios.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("gui-perf scenarios: 'scenarios' array missing.");
        }

        var parsed = new List<PerfScenario>();
        foreach (var scenario in scenarios.EnumerateArray())
        {
            var id = RequireString(scenario, "id");
            var why = RequireString(scenario, "why");

            if (!scenario.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"gui-perf scenario '{id}': 'steps' array missing.");

            var stepList = steps.EnumerateArray().Select(step => ParseStep(id, step)).ToList();

            // A scenario with no steps would launch, quit, and report a row of
            // numbers that measured nothing — the vacuous-pass shape
            // ReferencePerformanceBench refuses for an empty fixture set.
            if (stepList.Count == 0)
                throw new InvalidOperationException($"gui-perf scenario '{id}': no steps.");

            parsed.Add(new PerfScenario(id, why, stepList));
        }

        if (parsed.Count == 0)
            throw new InvalidOperationException("gui-perf scenarios: the file defines none.");

        var duplicate = parsed.GroupBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"gui-perf scenarios: duplicate id '{duplicate.Key}'.");

        return parsed;
    }

    /// <summary>
    /// Resolve one scenario by id. An id that matches nothing is a TYPO, not
    /// "run everything" — the same rule <c>--fixture</c> enforces in the
    /// reference-performance bench.
    /// </summary>
    internal static PerfScenario Select(IReadOnlyList<PerfScenario> scenarios, string id)
    {
        var match = scenarios.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (match == null)
        {
            throw new InvalidOperationException(
                $"gui-perf scenario '{id}' is not defined. Known: {string.Join(", ", scenarios.Select(s => s.Id))}");
        }

        return match;
    }

    private static PerfScenarioStep ParseStep(string scenarioId, JsonElement step)
    {
        var opText = RequireString(step, "op");
        if (!TryParseOp(opText, out var op))
        {
            throw new InvalidOperationException(
                $"gui-perf scenario '{scenarioId}': unknown op '{opText}'. Known: " +
                string.Join(", ", Enum.GetNames<PerfStepOp>().Select(n => n.ToLowerInvariant())));
        }

        var parsed = new PerfScenarioStep(
            op,
            Label: OptionalString(step, "label"),
            Document: OptionalString(step, "document"),
            Text: OptionalString(step, "text"),
            Value: OptionalDouble(step, "value"),
            Count: OptionalInt(step, "count"),
            Level: OptionalString(step, "level"),
            Seconds: OptionalDouble(step, "seconds"));

        // Fail at parse time, not three minutes into a launched scenario.
        switch (op)
        {
            case PerfStepOp.Open when string.IsNullOrWhiteSpace(parsed.Document):
                throw new InvalidOperationException($"gui-perf scenario '{scenarioId}': 'open' needs a document.");
            case PerfStepOp.Idle when parsed.Seconds is null or <= 0:
                throw new InvalidOperationException($"gui-perf scenario '{scenarioId}': 'idle' needs seconds > 0.");
            case PerfStepOp.PageBy or PerfStepOp.ScrollPages when parsed.Count is null or 0:
                throw new InvalidOperationException($"gui-perf scenario '{scenarioId}': '{opText}' needs a non-zero count.");
            case PerfStepOp.Zoom when parsed.Value is null or <= 0:
                throw new InvalidOperationException($"gui-perf scenario '{scenarioId}': 'zoom' needs value > 0.");
            case PerfStepOp.ViewMode when string.IsNullOrWhiteSpace(parsed.Level):
                throw new InvalidOperationException($"gui-perf scenario '{scenarioId}': 'viewmode' needs level single|continuous.");
            case PerfStepOp.Search or PerfStepOp.RedactText when string.IsNullOrWhiteSpace(parsed.Text):
                throw new InvalidOperationException($"gui-perf scenario '{scenarioId}': '{opText}' needs text.");
            case PerfStepOp.Trim when string.IsNullOrWhiteSpace(parsed.Level):
                throw new InvalidOperationException($"gui-perf scenario '{scenarioId}': 'trim' needs level background|warn|critical.");
        }

        return parsed;
    }

    private static bool TryParseOp(string text, out PerfStepOp op) =>
        Enum.TryParse(text.Replace("-", string.Empty).Replace("_", string.Empty), ignoreCase: true, out op);

    private static string RequireString(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"gui-perf scenarios: '{name}' is required and must be non-empty.");
        return value;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? OptionalDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static int? OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

}
