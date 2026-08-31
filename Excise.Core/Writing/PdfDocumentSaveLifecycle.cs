using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Security;

namespace Excise.Core.Writing;

/// <summary>
/// Owns the document-level lifecycle that precedes each fresh serialization.
/// </summary>
/// <remarks>
/// Registered authoring policies finalize against the live document first.
/// The resulting <see cref="PdfDocumentSaveSession"/> then provides one
/// writer-facing view of the existing object store; it is not another
/// document graph or serializer.
/// </remarks>
internal sealed class PdfDocumentSaveLifecycle
{
    private readonly PdfDocumentObjectStore _objectStore;
    private readonly PdfDictionary _trailer;
    private readonly PdfDictionary _catalog;
    private readonly string _version;
    private readonly List<Action> _preSaveActions = [];

    internal PdfDocumentSaveLifecycle(
        PdfDocumentObjectStore objectStore,
        PdfDictionary trailer,
        PdfDictionary catalog,
        string version)
    {
        _objectStore = objectStore;
        _trailer = trailer;
        _catalog = catalog;
        _version = version;
    }

    internal PdfStandardSecurityHandler? SecurityHandler
        => _objectStore.SecurityHandler;

    internal void RegisterPreSaveAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _preSaveActions.Add(action);
    }

    internal PdfReference? GetReferenceTo(PdfObject obj)
        => _objectStore.GetReferenceTo(obj);

    internal PdfDocumentSaveSession BeginSave()
    {
        // Snapshot registrations before execution. An idempotent action may
        // register later work, but that work belongs to the next Save rather
        // than running unpredictably inside the current iteration.
        foreach (var action in _preSaveActions.ToArray())
            action();

        return new PdfDocumentSaveSession(
            _objectStore, _trailer, _catalog, _version);
    }
}

/// <summary>
/// One post-finalization, writer-facing view of a document save.
/// </summary>
/// <remarks>
/// Reachability and object enumeration are evaluated lazily at the same point
/// the writer needs them, then cached so compressed-format fallback cannot
/// traverse a different graph during the same write.
/// </remarks>
internal sealed class PdfDocumentSaveSession
{
    private readonly PdfDocumentObjectStore _objectStore;
    private IReadOnlyList<(int ObjectNumber, int Generation, PdfObject Object)>? _objects;

    internal PdfDocumentSaveSession(
        PdfDocumentObjectStore objectStore,
        PdfDictionary trailer,
        PdfDictionary catalog,
        string version)
    {
        _objectStore = objectStore;
        Catalog = catalog;
        Version = version;
        CatalogReference = trailer.Get<PdfReference>("Root");
        InfoReference = trailer.GetReferenceOrNull("Info");
        ExistingIdArray = trailer.TryGetArray("ID", out var idArray)
            ? idArray
            : null;
        NextFreeObjectNumber = objectStore.NextFreeObjectNumber;
    }

    internal PdfDictionary Catalog { get; }

    internal string Version { get; }

    internal PdfReference CatalogReference { get; }

    internal PdfReference? InfoReference { get; }

    internal PdfArray? ExistingIdArray { get; }

    internal int NextFreeObjectNumber { get; }

    internal IReadOnlyList<(int ObjectNumber, int Generation, PdfObject Object)> Objects
        => _objects ??= BuildReachableObjectSnapshot();

    internal PdfObject Resolve(PdfObject obj)
        => _objectStore.Resolve(obj);

    private IReadOnlyList<(int ObjectNumber, int Generation, PdfObject Object)>
        BuildReachableObjectSnapshot()
    {
        var roots = new List<PdfObject> { CatalogReference };
        if (InfoReference != null)
            roots.Add(InfoReference);

        var reachable = _objectStore.ComputeReachableObjects(roots);
        return _objectStore.GetAllObjects()
            .Where(item => reachable.Contains(item.ObjectNumber))
            .OrderBy(item => item.ObjectNumber)
            .ToArray();
    }
}
