namespace Excise.Core.Automation;

/// <summary>
/// Stable semantic metadata for a excise command used by GUI accessibility,
/// automation adapters, CLI metadata, and regression tests.
/// </summary>
public sealed class PdfCommandMetadata
{
    public PdfCommandMetadata(
        string id,
        string label,
        string description,
        string category,
        string? shortcut = null,
        string? cliCommand = null,
        bool requiresDocument = false,
        bool isDestructive = false,
        bool isSecuritySensitive = false,
        string? disabledReason = null,
        IReadOnlyList<PdfCommandParameterMetadata>? parameters = null,
        IReadOnlyList<string>? resultFields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        Id = id;
        Label = label;
        Description = description;
        Category = category;
        Shortcut = shortcut;
        CliCommand = cliCommand;
        RequiresDocument = requiresDocument;
        IsDestructive = isDestructive;
        IsSecuritySensitive = isSecuritySensitive;
        DisabledReason = disabledReason ?? "This command is not available in the current document state.";
        Parameters = parameters ?? Array.Empty<PdfCommandParameterMetadata>();
        ResultFields = resultFields ?? Array.Empty<string>();
    }

    public string Id { get; }
    public string Label { get; }
    public string Description { get; }
    public string Category { get; }
    public string? Shortcut { get; }
    public string? CliCommand { get; }
    public bool RequiresDocument { get; }
    public bool IsDestructive { get; }
    public bool IsSecuritySensitive { get; }
    public string DisabledReason { get; }
    public IReadOnlyList<PdfCommandParameterMetadata> Parameters { get; }
    public IReadOnlyList<string> ResultFields { get; }
}
