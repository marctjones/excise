namespace Excise.App.Models;

/// <summary>
/// Immutable location and preview for one document-search result.
/// </summary>
public sealed class SearchMatch
{
    public int PageIndex { get; init; }
    public string MatchedText { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string Context { get; init; } = string.Empty;
}
