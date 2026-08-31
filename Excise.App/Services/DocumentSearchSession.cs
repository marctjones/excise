using Excise.App.Models;
using Excise.Core.Document;

namespace Excise.App.Services;

/// <summary>
/// Owns the lifetime and source selection for the current window's document
/// search. A replacement request cancels and invalidates the previous request,
/// so stale worker progress and results cannot be published by the UI adapter.
/// </summary>
internal sealed class DocumentSearchSession : IDisposable
{
    private readonly PdfSearchService _searchService;
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private bool _disposed;

    public DocumentSearchSession(PdfSearchService searchService)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
    }

    public DocumentSearchRequest Begin(DocumentSearchQuery query)
    {
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource nextCancellation;
        long generation;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previousCancellation = _activeCancellation;
            nextCancellation = new CancellationTokenSource();
            _activeCancellation = nextCancellation;
            generation = ++_generation;
        }

        CancelAndDispose(previousCancellation);
        return new DocumentSearchRequest(generation, query, nextCancellation.Token);
    }

    public bool IsCurrent(DocumentSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            return !_disposed &&
                   _activeCancellation is { IsCancellationRequested: false } &&
                   _generation == request.Generation;
        }
    }

    public DocumentSearchResult Execute(
        DocumentSearchRequest request,
        DocumentTextIndex? textIndex,
        PdfDocument? document,
        string? filePath,
        IProgress<PdfSearchService.SearchProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfStale(request);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<SearchMatch> matches;
        if (textIndex is { IsReady: true })
        {
            matches = _searchService.Search(
                textIndex,
                request.Query.Text,
                request.Query.CaseSensitive,
                request.Query.WholeWords,
                request.Query.UseRegex,
                request.CancellationToken,
                progress);
        }
        else if (document is not null)
        {
            matches = _searchService.Search(
                document,
                request.Query.Text,
                request.Query.CaseSensitive,
                request.Query.WholeWords,
                request.Query.UseRegex,
                request.CancellationToken,
                progress);
        }
        else if (!string.IsNullOrEmpty(filePath))
        {
            matches = _searchService.Search(
                filePath,
                request.Query.Text,
                request.Query.CaseSensitive,
                request.Query.WholeWords,
                request.Query.UseRegex,
                progress);
        }
        else
        {
            return DocumentSearchResult.NoSource;
        }

        stopwatch.Stop();
        ThrowIfStale(request);
        return new DocumentSearchResult(true, matches, stopwatch.ElapsedMilliseconds);
    }

    public void Cancel() => Stop(markDisposed: false);

    public void Dispose() => Stop(markDisposed: true);

    private void ThrowIfStale(DocumentSearchRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(request))
            throw new OperationCanceledException(request.CancellationToken);
    }

    private void Stop(bool markDisposed)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = markDisposed;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _generation++;
        }

        CancelAndDispose(cancellation);
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }
}

internal readonly record struct DocumentSearchQuery(
    string Text,
    bool CaseSensitive,
    bool WholeWords,
    bool UseRegex);

internal sealed record DocumentSearchRequest(
    long Generation,
    DocumentSearchQuery Query,
    CancellationToken CancellationToken);

internal sealed record DocumentSearchResult(
    bool HasSource,
    IReadOnlyList<SearchMatch> Matches,
    long WorkerElapsedMilliseconds)
{
    public static DocumentSearchResult NoSource { get; } = new(
        false,
        Array.Empty<SearchMatch>(),
        0);
}
