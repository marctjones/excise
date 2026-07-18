namespace Excise.Core.Automation;

/// <summary>
/// Describes one stable parameter accepted by a excise semantic command.
/// </summary>
public sealed class PdfCommandParameterMetadata
{
    public PdfCommandParameterMetadata(
        string name,
        string description,
        string type,
        bool required = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        Name = name;
        Description = description;
        Type = type;
        Required = required;
    }

    public string Name { get; }
    public string Description { get; }
    public string Type { get; }
    public bool Required { get; }
}
