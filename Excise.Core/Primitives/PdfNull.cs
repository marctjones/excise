namespace Excise.Core.Primitives;

/// <summary>
/// PDF null object. There is only one null object in PDF.
/// ISO 32000-2:2020 Section 7.3.9.
/// </summary>
public sealed class PdfNull : PdfObject
{
    /// <summary>
    /// The singleton null instance.
    /// </summary>
    public static readonly PdfNull Instance = new();

    private PdfNull() { }

    /// <inheritdoc />
    public override PdfObjectType ObjectType => PdfObjectType.Null;

    /// <inheritdoc />
    public override string ToString() => "null";
}
