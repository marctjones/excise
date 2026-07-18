using Excise.Core.Document;

namespace Excise.Core.Editing;

/// <summary>
/// Broad categories of user-visible edits. This is intentionally small:
/// concrete features add their own payloads while sharing lifecycle semantics.
/// </summary>
public enum PdfEditOperationKind
{
    Unknown = 0,
    FormFill,
    TypewriterText,
    PageOrganization,
    Redaction,
    Annotation
}

/// <summary>
/// Lifecycle state for an edit operation before it is saved or flattened.
/// </summary>
public enum PdfEditOperationStatus
{
    Pending = 0,
    Applied,
    Discarded
}

/// <summary>
/// Small immutable descriptor for future office-editing operations. It does
/// not apply edits itself; services own the PDF mutation and can use this as a
/// common scheduling, dirty-state, undo, and flattening surface.
/// </summary>
public sealed record PdfEditOperation
{
    private PdfEditOperation(
        Guid id,
        PdfEditOperationKind kind,
        int pageNumber,
        PdfRectangle bounds,
        PdfEditOperationStatus status,
        bool canFlatten,
        string? description)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Operation id must not be empty.", nameof(id));
        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number is 1-based and must be positive.");
        if (bounds.Right < bounds.Left || bounds.Top < bounds.Bottom)
            throw new ArgumentException("Bounds must be normalized.", nameof(bounds));

        Id = id;
        Kind = kind;
        PageNumber = pageNumber;
        Bounds = bounds;
        Status = status;
        CanFlatten = canFlatten;
        Description = description;
    }

    public Guid Id { get; }
    public PdfEditOperationKind Kind { get; }
    public int PageNumber { get; }
    public PdfRectangle Bounds { get; }
    public PdfEditOperationStatus Status { get; }
    public bool CanFlatten { get; }
    public string? Description { get; }
    public bool IsPending => Status == PdfEditOperationStatus.Pending;

    public static PdfEditOperation Create(
        PdfEditOperationKind kind,
        int pageNumber,
        PdfRectangle bounds,
        bool canFlatten = true,
        string? description = null)
    {
        return new PdfEditOperation(
            Guid.NewGuid(),
            kind,
            pageNumber,
            bounds,
            PdfEditOperationStatus.Pending,
            canFlatten,
            description);
    }

    public PdfEditOperation WithStatus(PdfEditOperationStatus status) =>
        new(Id, Kind, PageNumber, Bounds, status, CanFlatten, Description);

    public PdfEditOperation WithBounds(PdfRectangle bounds) =>
        new(Id, Kind, PageNumber, bounds, Status, CanFlatten, Description);

    public PdfEditOperation WithPageAndBounds(int pageNumber, PdfRectangle bounds) =>
        new(Id, Kind, pageNumber, bounds, Status, CanFlatten, Description);
}
