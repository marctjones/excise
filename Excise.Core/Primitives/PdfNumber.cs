using System.Globalization;

namespace Excise.Core.Primitives;

/// <summary>
/// PDF integer number object.
/// ISO 32000-2:2020 Section 7.3.3.
/// </summary>
public sealed class PdfInteger : PdfObject
{
    /// <summary>
    /// The integer value.
    /// </summary>
    public long Value { get; }

    /// <summary>
    /// Creates a new PDF integer.
    /// </summary>
    public PdfInteger(long value) => Value = value;

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.Integer;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Implicit conversion from long.
    /// </summary>
    public static implicit operator long(PdfInteger i) => i.Value;

    /// <summary>
    /// Implicit conversion from int.
    /// </summary>
    public static implicit operator int(PdfInteger i) => (int)i.Value;

    /// <summary>
    /// Implicit conversion to double (for arithmetic with reals).
    /// </summary>
    public static implicit operator double(PdfInteger i) => i.Value;

    /// <summary>
    /// Implicit conversion to PdfInteger.
    /// </summary>
    public static implicit operator PdfInteger(long l) => new(l);

    /// <summary>
    /// Implicit conversion to PdfInteger.
    /// </summary>
    public static implicit operator PdfInteger(int i) => new(i);

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is PdfInteger other && Value == other.Value;

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// PDF real (floating point) number object.
/// ISO 32000-2:2020 Section 7.3.3.
/// </summary>
public sealed class PdfReal : PdfObject
{
    /// <summary>
    /// The real value.
    /// </summary>
    public double Value { get; }

    /// <summary>
    /// Creates a new PDF real number.
    /// </summary>
    public PdfReal(double value) => Value = value;

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.Real;

    /// <inheritdoc />
    public override string ToString()
    {
        // Format without trailing zeros, use "0" for very small numbers
        string result = Value.ToString("G15", CultureInfo.InvariantCulture);
        // Ensure decimal point for real numbers
        if (!result.Contains('.') && !result.Contains('E') && !result.Contains('e'))
        {
            result += ".0";
        }
        return result;
    }

    /// <summary>
    /// Implicit conversion from double.
    /// </summary>
    public static implicit operator double(PdfReal r) => r.Value;

    /// <summary>
    /// Implicit conversion to PdfReal.
    /// </summary>
    public static implicit operator PdfReal(double d) => new(d);

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is PdfReal other && Math.Abs(Value - other.Value) < 1e-10;

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// Extension methods for working with PDF numbers.
/// </summary>
public static class PdfNumberExtensions
{
    /// <summary>
    /// Get the numeric value of a PdfObject as a double.
    /// </summary>
    public static double GetNumber(this PdfObject obj)
    {
        return obj switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            _ => throw new InvalidCastException($"Expected number, got {obj.ObjectType}")
        };
    }

    /// <summary>
    /// Get the numeric value of a PdfObject as an integer.
    /// </summary>
    public static int GetInt(this PdfObject obj)
    {
        return obj switch
        {
            PdfInteger i => (int)i.Value,
            PdfReal r => (int)r.Value,
            _ => throw new InvalidCastException($"Expected number, got {obj.ObjectType}")
        };
    }

    /// <summary>
    /// Get the numeric value of a PdfObject as a long.
    /// </summary>
    public static long GetLong(this PdfObject obj)
    {
        return obj switch
        {
            PdfInteger i => i.Value,
            PdfReal r => (long)r.Value,
            _ => throw new InvalidCastException($"Expected number, got {obj.ObjectType}")
        };
    }

    /// <summary>
    /// Try to get the numeric value of a PdfObject.
    /// </summary>
    public static bool TryGetNumber(this PdfObject? obj, out double value)
    {
        switch (obj)
        {
            case PdfInteger i:
                value = i.Value;
                return true;
            case PdfReal r:
                value = r.Value;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
