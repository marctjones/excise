namespace Excise.Core.Primitives;

/// <summary>
/// Base class for all PDF objects.
/// PDF has 8 basic types: Boolean, Integer, Real, String, Name, Array, Dictionary, Stream, and Null.
/// Indirect objects are referenced via PdfReference.
/// </summary>
public abstract class PdfObject
{
    /// <summary>
    /// Gets the type of this PDF object.
    /// </summary>
    public abstract PdfObjectType ObjectType { get; }

    /// <summary>
    /// If this object is an indirect object, its object number.
    /// </summary>
    public int? ObjectNumber { get; set; }

    /// <summary>
    /// If this object is an indirect object, its generation number.
    /// </summary>
    public int? GenerationNumber { get; set; }

    /// <summary>
    /// Whether this object is an indirect object (has object/generation numbers).
    /// </summary>
    public bool IsIndirect => ObjectNumber.HasValue;

    /// <summary>
    /// Try to cast this object to a specific type.
    /// </summary>
    public T? As<T>() where T : PdfObject => this as T;

    /// <summary>
    /// Cast this object to a specific type, throwing if the cast fails.
    /// </summary>
    public T Expect<T>() where T : PdfObject =>
        this as T ?? throw new InvalidCastException($"Expected {typeof(T).Name}, got {GetType().Name}");
}

/// <summary>
/// Types of PDF objects per ISO 32000-2:2020 Section 7.3.
/// </summary>
public enum PdfObjectType
{
    /// <summary>PDF null object.</summary>
    Null,
    /// <summary>PDF boolean (true/false).</summary>
    Boolean,
    /// <summary>PDF integer number.</summary>
    Integer,
    /// <summary>PDF real (floating point) number.</summary>
    Real,
    /// <summary>PDF string (literal or hex).</summary>
    String,
    /// <summary>PDF name (starts with /).</summary>
    Name,
    /// <summary>PDF array ([...]).</summary>
    Array,
    /// <summary>PDF dictionary (&lt;&lt;...&gt;&gt;).</summary>
    Dictionary,
    /// <summary>PDF stream (dictionary + byte data).</summary>
    Stream,
    /// <summary>Indirect object reference (n g R).</summary>
    Reference
}
