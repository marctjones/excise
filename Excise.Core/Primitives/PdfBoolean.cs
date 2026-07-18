namespace Excise.Core.Primitives;

/// <summary>
/// PDF boolean object (true or false).
/// ISO 32000-2:2020 Section 7.3.2.
/// </summary>
public sealed class PdfBoolean : PdfObject
{
    /// <summary>
    /// The singleton true instance.
    /// </summary>
    public static readonly PdfBoolean True = new(true);

    /// <summary>
    /// The singleton false instance.
    /// </summary>
    public static readonly PdfBoolean False = new(false);

    /// <summary>
    /// The boolean value.
    /// </summary>
    public bool Value { get; }

    private PdfBoolean(bool value) => Value = value;

    /// <summary>
    /// Get the PdfBoolean for the specified value.
    /// </summary>
    public static PdfBoolean Get(bool value) => value ? True : False;

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.Boolean;

    /// <inheritdoc />
    public override string ToString() => Value ? "true" : "false";

    /// <summary>
    /// Implicit conversion from bool.
    /// </summary>
    public static implicit operator bool(PdfBoolean b) => b.Value;

    /// <summary>
    /// Implicit conversion to PdfBoolean.
    /// </summary>
    public static implicit operator PdfBoolean(bool b) => Get(b);
}
