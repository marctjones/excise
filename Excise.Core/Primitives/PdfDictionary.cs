using System.Collections;
using System.Text;

namespace Excise.Core.Primitives;

/// <summary>
/// PDF dictionary object. Dictionaries are associative tables with name keys.
/// ISO 32000-2:2020 Section 7.3.7.
/// </summary>
public class PdfDictionary : PdfObject, IDictionary<PdfName, PdfObject>
{
    private readonly Dictionary<string, PdfObject> _items;

    /// <summary>
    /// Creates an empty PDF dictionary.
    /// </summary>
    public PdfDictionary()
    {
        _items = new Dictionary<string, PdfObject>();
    }

    /// <summary>
    /// Creates a PDF dictionary with the specified entries.
    /// </summary>
    public PdfDictionary(IEnumerable<KeyValuePair<PdfName, PdfObject>> entries)
    {
        _items = entries.ToDictionary(e => e.Key.Value, e => e.Value);
    }

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.Dictionary;

    /// <summary>
    /// Number of entries in the dictionary.
    /// </summary>
    public int Count => _items.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <summary>
    /// Get the keys in the dictionary.
    /// </summary>
    public ICollection<PdfName> Keys => _items.Keys.Select(k => new PdfName(k)).ToList();

    /// <summary>
    /// Get the values in the dictionary.
    /// </summary>
    public ICollection<PdfObject> Values => _items.Values;

    /// <summary>
    /// Get or set an entry by name.
    /// </summary>
    public PdfObject this[PdfName key]
    {
        get => _items.TryGetValue(key.Value, out var value) ? value : throw new KeyNotFoundException($"Key /{key.Value} not found");
        set
        {
            if (value is null or PdfNull)
                _items.Remove(key.Value);
            else
                _items[key.Value] = value;
        }
    }

    /// <summary>
    /// Get or set an entry by string name.
    /// </summary>
    public PdfObject this[string key]
    {
        get => _items.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"Key /{key} not found");
        set
        {
            if (value is null or PdfNull)
                _items.Remove(key);
            else
                _items[key] = value;
        }
    }

    /// <summary>
    /// Check if the dictionary contains a key.
    /// </summary>
    public bool ContainsKey(PdfName key) => _items.ContainsKey(key.Value);

    /// <summary>
    /// Check if the dictionary contains a key by string.
    /// </summary>
    public bool ContainsKey(string key) => _items.ContainsKey(key);

    /// <summary>
    /// Try to get a value by name.
    /// </summary>
    public bool TryGetValue(PdfName key, out PdfObject value) =>
        _items.TryGetValue(key.Value, out value!);

    /// <summary>
    /// Try to get a value by string name.
    /// </summary>
    public bool TryGetValue(string key, out PdfObject value) =>
        _items.TryGetValue(key, out value!);

    /// <summary>
    /// Get an optional value, returning null if not found.
    /// </summary>
    public PdfObject? GetOptional(string key) =>
        _items.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Get a value cast to the specified type.
    /// </summary>
    public T Get<T>(string key) where T : PdfObject =>
        _items.TryGetValue(key, out var value) && value is T t
            ? t
            : throw new KeyNotFoundException($"Key /{key} not found or not of type {typeof(T).Name}");

    /// <summary>
    /// Try to get a value cast to the specified type.
    /// </summary>
    public T? TryGet<T>(string key) where T : PdfObject =>
        _items.TryGetValue(key, out var value) ? value as T : null;

    /// <summary>
    /// Get a number value.
    /// </summary>
    public double GetNumber(string key) =>
        _items.TryGetValue(key, out var value)
            ? value.GetNumber()
            : throw new KeyNotFoundException($"Key /{key} not found");

    /// <summary>
    /// Get an optional number value with default.
    /// </summary>
    public double GetNumber(string key, double defaultValue) =>
        _items.TryGetValue(key, out var value) && value.TryGetNumber(out var n)
            ? n
            : defaultValue;

    /// <summary>
    /// Get an integer value.
    /// </summary>
    public int GetInt(string key) =>
        _items.TryGetValue(key, out var value)
            ? value.GetInt()
            : throw new KeyNotFoundException($"Key /{key} not found");

    /// <summary>
    /// Get an optional integer value with default.
    /// </summary>
    public int GetInt(string key, int defaultValue) =>
        _items.TryGetValue(key, out var value) && value.TryGetNumber(out var n)
            ? (int)n
            : defaultValue;

    /// <summary>
    /// Get a string value.
    /// </summary>
    public string GetString(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfString s
            ? s.Value
            : throw new KeyNotFoundException($"Key /{key} not found or not a string");

    /// <summary>
    /// Get an optional string value.
    /// </summary>
    public string? GetStringOrNull(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfString s ? s.Value : null;

    /// <summary>
    /// Try to get a string value.
    /// </summary>
    public bool TryGetString(string key, out string value)
    {
        if (_items.TryGetValue(key, out var obj) && obj is PdfString s)
        {
            value = s.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Get a name value (without leading /).
    /// </summary>
    public string GetName(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfName n
            ? n.Value
            : throw new KeyNotFoundException($"Key /{key} not found or not a name");

    /// <summary>
    /// Get an optional name value.
    /// </summary>
    public string? GetNameOrNull(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfName n ? n.Value : null;

    /// <summary>
    /// Get a boolean value.
    /// </summary>
    public bool GetBool(string key, bool defaultValue = false) =>
        _items.TryGetValue(key, out var value) && value is PdfBoolean b
            ? b.Value
            : defaultValue;

    /// <summary>
    /// Get an array value.
    /// </summary>
    public PdfArray GetArray(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfArray a
            ? a
            : throw new KeyNotFoundException($"Key /{key} not found or not an array");

    /// <summary>
    /// Get an optional array value.
    /// </summary>
    public PdfArray? GetArrayOrNull(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfArray a ? a : null;

    /// <summary>
    /// Try to get an array value.
    /// </summary>
    public bool TryGetArray(string key, out PdfArray value)
    {
        if (_items.TryGetValue(key, out var obj) && obj is PdfArray a)
        {
            value = a;
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>
    /// Get a dictionary value.
    /// </summary>
    public PdfDictionary GetDictionary(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfDictionary d
            ? d
            : throw new KeyNotFoundException($"Key /{key} not found or not a dictionary");

    /// <summary>
    /// Get an optional dictionary value.
    /// </summary>
    public PdfDictionary? GetDictionaryOrNull(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfDictionary d ? d : null;

    /// <summary>
    /// Get a reference value.
    /// </summary>
    public PdfReference GetReference(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfReference r
            ? r
            : throw new KeyNotFoundException($"Key /{key} not found or not a reference");

    /// <summary>
    /// Get an optional reference value.
    /// </summary>
    public PdfReference? GetReferenceOrNull(string key) =>
        _items.TryGetValue(key, out var value) && value is PdfReference r ? r : null;

    /// <summary>
    /// Add an entry.
    /// </summary>
    public void Add(PdfName key, PdfObject value)
    {
        if (value is null or PdfNull)
            return; // Don't add null values
        _items.Add(key.Value, value);
    }

    /// <summary>
    /// Add an entry by string key.
    /// </summary>
    public void Add(string key, PdfObject value)
    {
        if (value is null or PdfNull)
            return;
        _items.Add(key, value);
    }

    /// <summary>
    /// Set an entry (add or update).
    /// </summary>
    public void Set(string key, PdfObject value)
    {
        if (value is null or PdfNull)
            _items.Remove(key);
        else
            _items[key] = value;
    }

    /// <summary>
    /// Set a name entry.
    /// </summary>
    public void SetName(string key, string value) => _items[key] = new PdfName(value);

    /// <summary>
    /// Set a string entry.
    /// </summary>
    public void SetString(string key, string value) => _items[key] = new PdfString(value);

    /// <summary>
    /// Set a number entry.
    /// </summary>
    public void SetNumber(string key, double value) => _items[key] = new PdfReal(value);

    /// <summary>
    /// Set an integer entry.
    /// </summary>
    public void SetInt(string key, int value) => _items[key] = new PdfInteger(value);

    /// <summary>
    /// Set a boolean entry.
    /// </summary>
    public void SetBool(string key, bool value) => _items[key] = PdfBoolean.Get(value);

    /// <summary>
    /// Remove an entry by name.
    /// </summary>
    public bool Remove(PdfName key) => _items.Remove(key.Value);

    /// <summary>
    /// Remove an entry by string key.
    /// </summary>
    public bool Remove(string key) => _items.Remove(key);

    /// <summary>
    /// Remove an entry.
    /// </summary>
    bool IDictionary<PdfName, PdfObject>.Remove(PdfName key) => Remove(key);

    /// <summary>
    /// Clear all entries.
    /// </summary>
    public void Clear() => _items.Clear();

    /// <summary>
    /// Check if contains an entry.
    /// </summary>
    bool ICollection<KeyValuePair<PdfName, PdfObject>>.Contains(KeyValuePair<PdfName, PdfObject> item) =>
        _items.TryGetValue(item.Key.Value, out var value) && Equals(value, item.Value);

    /// <summary>
    /// Add an entry.
    /// </summary>
    void ICollection<KeyValuePair<PdfName, PdfObject>>.Add(KeyValuePair<PdfName, PdfObject> item) =>
        Add(item.Key, item.Value);

    /// <summary>
    /// Remove an entry.
    /// </summary>
    bool ICollection<KeyValuePair<PdfName, PdfObject>>.Remove(KeyValuePair<PdfName, PdfObject> item) =>
        _items.Remove(item.Key.Value);

    /// <summary>
    /// Copy to array.
    /// </summary>
    void ICollection<KeyValuePair<PdfName, PdfObject>>.CopyTo(KeyValuePair<PdfName, PdfObject>[] array, int arrayIndex)
    {
        foreach (var kvp in _items)
        {
            array[arrayIndex++] = new KeyValuePair<PdfName, PdfObject>(new PdfName(kvp.Key), kvp.Value);
        }
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<PdfName, PdfObject>> GetEnumerator()
    {
        foreach (var kvp in _items)
        {
            yield return new KeyValuePair<PdfName, PdfObject>(new PdfName(kvp.Key), kvp.Value);
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder("<<\n");
        foreach (var kvp in _items.OrderBy(k => k.Key))
        {
            sb.Append("  /").Append(kvp.Key).Append(' ').Append(kvp.Value).Append('\n');
        }
        sb.Append(">>");
        return sb.ToString();
    }

    /// <summary>
    /// Create a copy of this dictionary.
    /// </summary>
    public PdfDictionary Clone()
    {
        var clone = new PdfDictionary();
        foreach (var kvp in _items)
        {
            clone._items[kvp.Key] = kvp.Value; // Shallow copy
        }
        return clone;
    }
}
