namespace Excise.Core.Primitives;

/// <summary>
/// PDF indirect object reference. References other objects by object and generation number.
/// ISO 32000-2:2020 Section 7.3.10.
/// </summary>
/// <remarks>
/// Written as "n g R" where n is the object number and g is the generation number.
/// </remarks>
public sealed class PdfReference : PdfObject, IEquatable<PdfReference>
{
    /// <summary>
    /// The object number being referenced.
    /// </summary>
    public int ObjectNum { get; }

    /// <summary>
    /// The generation number (usually 0).
    /// </summary>
    public int Generation { get; }

    /// <summary>
    /// Creates a new PDF reference.
    /// </summary>
    public PdfReference(int objectNum, int generation = 0)
    {
        if (objectNum < 0)
            throw new ArgumentOutOfRangeException(nameof(objectNum), "Object number must be non-negative");
        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation), "Generation must be non-negative");

        ObjectNum = objectNum;
        Generation = generation;
    }

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.Reference;

    /// <inheritdoc />
    public override string ToString() => $"{ObjectNum} {Generation} R";

    /// <summary>
    /// Parse a reference string like "5 0 R".
    /// </summary>
    public static PdfReference Parse(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[2] != "R")
            throw new FormatException($"Invalid reference format: {s}");

        return new PdfReference(int.Parse(parts[0]), int.Parse(parts[1]));
    }

    /// <inheritdoc />
    public bool Equals(PdfReference? other) =>
        other is not null && ObjectNum == other.ObjectNum && Generation == other.Generation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PdfReference other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ObjectNum, Generation);

    /// <summary>
    /// Equality operator.
    /// </summary>
    public static bool operator ==(PdfReference? left, PdfReference? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>
    /// Inequality operator.
    /// </summary>
    public static bool operator !=(PdfReference? left, PdfReference? right) => !(left == right);
}

/// <summary>
/// Represents an indirect object (object with object number and generation).
/// </summary>
public sealed class PdfIndirectObject
{
    /// <summary>
    /// The object number.
    /// </summary>
    public int ObjectNumber { get; }

    /// <summary>
    /// The generation number.
    /// </summary>
    public int Generation { get; }

    /// <summary>
    /// The actual PDF object.
    /// </summary>
    public PdfObject Value { get; }

    /// <summary>
    /// Creates a new indirect object.
    /// </summary>
    public PdfIndirectObject(int objectNumber, int generation, PdfObject value)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
        Value = value ?? PdfNull.Instance;

        // Set object numbers on the value too
        Value.ObjectNumber = objectNumber;
        Value.GenerationNumber = generation;
    }

    /// <summary>
    /// Gets a reference to this indirect object.
    /// </summary>
    public PdfReference Reference => new(ObjectNumber, Generation);

    /// <inheritdoc />
    public override string ToString() =>
        $"{ObjectNumber} {Generation} obj\n{Value}\nendobj";
}
