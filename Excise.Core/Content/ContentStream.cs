using Excise.Core.Document;

namespace Excise.Core.Content;

/// <summary>
/// Represents a parsed PDF content stream as a sequence of operators.
/// ISO 32000-2:2020 Section 7.8.2.
/// </summary>
/// <remarks>
/// ContentStream is immutable. All modification methods return new instances.
/// This supports functional-style manipulation of content streams.
/// </remarks>
public class ContentStream
{
    private readonly List<ContentOperator> _operators;

    /// <summary>
    /// The operators in this content stream.
    /// </summary>
    public IReadOnlyList<ContentOperator> Operators => _operators.AsReadOnly();

    /// <summary>
    /// Creates a new content stream with the specified operators.
    /// </summary>
    public ContentStream(IEnumerable<ContentOperator> operators)
    {
        _operators = operators?.ToList() ?? new List<ContentOperator>();
    }

    /// <summary>
    /// Creates an empty content stream.
    /// </summary>
    public ContentStream() : this(Enumerable.Empty<ContentOperator>())
    {
    }

    /// <summary>
    /// Number of operators in this content stream.
    /// </summary>
    public int Count => _operators.Count;

    /// <summary>
    /// Get an operator by index.
    /// </summary>
    public ContentOperator this[int index] => _operators[index];

    #region Filtering and Selection

    /// <summary>
    /// Filter operators by predicate, returning a new content stream.
    /// </summary>
    public ContentStream Filter(Func<ContentOperator, bool> predicate)
    {
        return new ContentStream(_operators.Where(predicate));
    }

    /// <summary>
    /// Remove operators that intersect with a rectangle.
    /// </summary>
    public ContentStream RemoveIntersecting(PdfRectangle area)
    {
        return Filter(op => !op.IntersectsWith(area));
    }

    /// <summary>
    /// Remove operators that are contained within a rectangle.
    /// </summary>
    public ContentStream RemoveContainedIn(PdfRectangle area)
    {
        return Filter(op => !op.IsContainedIn(area));
    }

    /// <summary>
    /// Remove operators by category.
    /// </summary>
    public ContentStream RemoveCategory(OperatorCategory category)
    {
        return Filter(op => op.Category != category);
    }

    /// <summary>
    /// Keep only operators that match a predicate.
    /// </summary>
    public ContentStream Where(Func<ContentOperator, bool> predicate)
    {
        return Filter(predicate);
    }

    /// <summary>
    /// Get all operators of a specific category.
    /// </summary>
    public IEnumerable<ContentOperator> OfCategory(OperatorCategory category)
    {
        return _operators.Where(op => op.Category == category);
    }

    /// <summary>
    /// Get all text-showing operators.
    /// </summary>
    public IEnumerable<ContentOperator> TextOperators =>
        OfCategory(OperatorCategory.TextShowing);

    /// <summary>
    /// Get all path-related operators.
    /// </summary>
    public IEnumerable<ContentOperator> PathOperators =>
        _operators.Where(op =>
            op.Category == OperatorCategory.PathConstruction ||
            op.Category == OperatorCategory.PathPainting);

    /// <summary>
    /// Get all XObject operators.
    /// </summary>
    public IEnumerable<ContentOperator> XObjectOperators =>
        OfCategory(OperatorCategory.XObject);

    #endregion

    #region Modification (Immutable - Returns New Instance)

    /// <summary>
    /// Append a single operator, returning a new content stream.
    /// </summary>
    public ContentStream Append(ContentOperator op)
    {
        var newOps = new List<ContentOperator>(_operators) { op };
        return new ContentStream(newOps);
    }

    /// <summary>
    /// Append multiple operators, returning a new content stream.
    /// </summary>
    public ContentStream Append(IEnumerable<ContentOperator> ops)
    {
        var newOps = new List<ContentOperator>(_operators);
        newOps.AddRange(ops);
        return new ContentStream(newOps);
    }

    /// <summary>
    /// Prepend a single operator, returning a new content stream.
    /// </summary>
    public ContentStream Prepend(ContentOperator op)
    {
        var newOps = new List<ContentOperator> { op };
        newOps.AddRange(_operators);
        return new ContentStream(newOps);
    }

    /// <summary>
    /// Prepend multiple operators, returning a new content stream.
    /// </summary>
    public ContentStream Prepend(IEnumerable<ContentOperator> ops)
    {
        var newOps = new List<ContentOperator>(ops);
        newOps.AddRange(_operators);
        return new ContentStream(newOps);
    }

    /// <summary>
    /// Insert an operator at a specific index, returning a new content stream.
    /// </summary>
    public ContentStream Insert(int index, ContentOperator op)
    {
        var newOps = new List<ContentOperator>(_operators);
        newOps.Insert(index, op);
        return new ContentStream(newOps);
    }

    /// <summary>
    /// Replace an operator at a specific index, returning a new content stream.
    /// </summary>
    public ContentStream Replace(int index, ContentOperator op)
    {
        var newOps = new List<ContentOperator>(_operators);
        newOps[index] = op;
        return new ContentStream(newOps);
    }

    /// <summary>
    /// Concatenate two content streams.
    /// </summary>
    public ContentStream Concat(ContentStream other)
    {
        return Append(other.Operators);
    }

    /// <summary>
    /// Transform all operators using a function, returning a new content stream.
    /// </summary>
    public ContentStream Transform(Func<ContentOperator, ContentOperator> transformer)
    {
        return new ContentStream(_operators.Select(transformer));
    }

    #endregion

    #region Redaction Helpers

    /// <summary>
    /// Remove all operators that intersect with an area and add a visual marker.
    /// </summary>
    /// <param name="area">The area to redact.</param>
    /// <param name="markerColor">RGB color for the marker (0-1 range).</param>
    /// <returns>New content stream with redaction applied.</returns>
    public ContentStream Redact(PdfRectangle area, (double R, double G, double B)? markerColor = null)
    {
        // Filter out intersecting operators
        var filtered = RemoveIntersecting(area);

        // Add visual marker if requested
        if (markerColor.HasValue)
        {
            var (r, g, b) = markerColor.Value;
            filtered = filtered
                .Append(ContentOperator.SaveState())
                .Append(ContentOperator.SetFillRgb(r, g, b))
                .Append(ContentOperator.Rectangle(area))
                .Append(ContentOperator.Fill())
                .Append(ContentOperator.RestoreState());
        }

        return filtered;
    }

    /// <summary>
    /// Remove all operators that intersect with an area and add a black marker.
    /// </summary>
    public ContentStream RedactWithBlackMarker(PdfRectangle area)
    {
        return Redact(area, (0, 0, 0));
    }

    /// <summary>
    /// Find all text operators that contain specific text.
    /// </summary>
    public IEnumerable<ContentOperator> FindText(string searchText, StringComparison comparison = StringComparison.Ordinal)
    {
        return TextOperators.Where(op =>
            op.TextContent != null &&
            op.TextContent.Contains(searchText, comparison));
    }

    #endregion

    #region Enumeration

    /// <summary>
    /// Get an enumerator for the operators.
    /// </summary>
    public IEnumerator<ContentOperator> GetEnumerator() => _operators.GetEnumerator();

    #endregion

    /// <inheritdoc />
    public override string ToString()
    {
        return $"ContentStream[{_operators.Count} operators]";
    }
}
