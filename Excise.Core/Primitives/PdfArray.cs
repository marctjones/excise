using System.Collections;

namespace Excise.Core.Primitives;

/// <summary>
/// PDF array object. Arrays are one-dimensional collections of objects.
/// ISO 32000-2:2020 Section 7.3.6.
/// </summary>
public sealed class PdfArray : PdfObject, IList<PdfObject>
{
    private readonly List<PdfObject> _items;

    /// <summary>
    /// Creates an empty PDF array.
    /// </summary>
    public PdfArray()
    {
        _items = new List<PdfObject>();
    }

    /// <summary>
    /// Creates a PDF array with the specified items.
    /// </summary>
    public PdfArray(IEnumerable<PdfObject> items)
    {
        _items = new List<PdfObject>(items);
    }

    /// <summary>
    /// Creates a PDF array with the specified items.
    /// </summary>
    public PdfArray(params PdfObject[] items)
    {
        _items = new List<PdfObject>(items);
    }

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.Array;

    /// <summary>
    /// Number of items in the array.
    /// </summary>
    public int Count => _items.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <summary>
    /// Get or set an item at the specified index.
    /// </summary>
    public PdfObject this[int index]
    {
        get => _items[index];
        set => _items[index] = value ?? PdfNull.Instance;
    }

    /// <summary>
    /// Get an item cast to the specified type.
    /// </summary>
    public T Get<T>(int index) where T : PdfObject =>
        _items[index] as T ?? throw new InvalidCastException(
            $"Item at index {index} is {_items[index].ObjectType}, expected {typeof(T).Name}");

    /// <summary>
    /// Try to get an item cast to the specified type.
    /// </summary>
    public T? TryGet<T>(int index) where T : PdfObject =>
        index >= 0 && index < _items.Count ? _items[index] as T : null;

    /// <summary>
    /// Get a number value at the specified index.
    /// </summary>
    public double GetNumber(int index) => _items[index].GetNumber();

    /// <summary>
    /// Get an integer value at the specified index.
    /// </summary>
    public int GetInt(int index) => _items[index].GetInt();

    /// <summary>
    /// Get a string value at the specified index.
    /// </summary>
    public string GetString(int index) => (_items[index] as PdfString)?.Value
        ?? throw new InvalidCastException($"Item at index {index} is not a string");

    /// <summary>
    /// Get a name value at the specified index.
    /// </summary>
    public string GetName(int index) => (_items[index] as PdfName)?.Value
        ?? throw new InvalidCastException($"Item at index {index} is not a name");

    /// <summary>
    /// Add an item to the array.
    /// </summary>
    public void Add(PdfObject item) => _items.Add(item ?? PdfNull.Instance);

    /// <summary>
    /// Add a number to the array.
    /// </summary>
    public void Add(int value) => _items.Add(new PdfInteger(value));

    /// <summary>
    /// Add a number to the array.
    /// </summary>
    public void Add(double value) => _items.Add(new PdfReal(value));

    /// <summary>
    /// Add a string to the array.
    /// </summary>
    public void Add(string value) => _items.Add(new PdfString(value));

    /// <summary>
    /// Insert an item at the specified index.
    /// </summary>
    public void Insert(int index, PdfObject item) => _items.Insert(index, item ?? PdfNull.Instance);

    /// <summary>
    /// Remove an item.
    /// </summary>
    public bool Remove(PdfObject item) => _items.Remove(item);

    /// <summary>
    /// Remove the item at the specified index.
    /// </summary>
    public void RemoveAt(int index) => _items.RemoveAt(index);

    /// <summary>
    /// Clear all items.
    /// </summary>
    public void Clear() => _items.Clear();

    /// <summary>
    /// Check if the array contains an item.
    /// </summary>
    public bool Contains(PdfObject item) => _items.Contains(item);

    /// <summary>
    /// Get the index of an item.
    /// </summary>
    public int IndexOf(PdfObject item) => _items.IndexOf(item);

    /// <summary>
    /// Copy to an array.
    /// </summary>
    public void CopyTo(PdfObject[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public IEnumerator<PdfObject> GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Convert to an array of numbers (for rectangles, matrices, etc.).
    /// </summary>
    public double[] ToDoubleArray()
    {
        var result = new double[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            result[i] = _items[i].GetNumber();
        }
        return result;
    }

    /// <summary>
    /// Convert to an array of integers.
    /// </summary>
    public int[] ToIntArray()
    {
        var result = new int[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            result[i] = _items[i].GetInt();
        }
        return result;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return "[" + string.Join(" ", _items.Select(i => i.ToString())) + "]";
    }

    /// <summary>
    /// Create a PdfArray from numbers (for rectangles).
    /// </summary>
    public static PdfArray FromRectangle(double left, double bottom, double right, double top) =>
        new(new PdfReal(left), new PdfReal(bottom), new PdfReal(right), new PdfReal(top));

    /// <summary>
    /// Create a PdfArray from a matrix.
    /// </summary>
    public static PdfArray FromMatrix(double a, double b, double c, double d, double e, double f) =>
        new(new PdfReal(a), new PdfReal(b), new PdfReal(c), new PdfReal(d), new PdfReal(e), new PdfReal(f));
}
