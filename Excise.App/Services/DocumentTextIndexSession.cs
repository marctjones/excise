using Excise.Core.Document;
using Microsoft.Extensions.Logging;

namespace Excise.App.Services;

/// <summary>
/// Owns the derived text index and background-build lifetime for the current
/// document. Replacing or closing a document cancels the previous build before
/// its document can be released.
/// </summary>
internal sealed class DocumentTextIndexSession : IDisposable
{
    private static readonly TimeSpan DefaultBuildDelay = TimeSpan.FromMilliseconds(750);

    private readonly ILogger<DocumentTextIndexSession> _logger;
    private readonly TimeSpan _buildDelay;
    private readonly object _gate = new();
    private CancellationTokenSource? _buildCancellation;
    private DocumentTextIndex? _current;
    private Task _buildCompletion = Task.CompletedTask;
    private bool _disposed;

    public DocumentTextIndexSession(
        ILogger<DocumentTextIndexSession> logger,
        TimeSpan? buildDelay = null)
    {
        _logger = logger;
        _buildDelay = buildDelay ?? DefaultBuildDelay;
        if (_buildDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(buildDelay));
    }

    public DocumentTextIndex? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    internal Task BuildCompletion
    {
        get
        {
            lock (_gate) return _buildCompletion;
        }
    }

    public DocumentTextIndex Start(
        PdfDocument document,
        IProgress<(int Done, int Total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        CancellationTokenSource nextCancellation;
        DocumentTextIndex nextIndex;
        CancellationTokenSource? previousCancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            nextCancellation = new CancellationTokenSource();
            nextIndex = new DocumentTextIndex(document, _logger);
            previousCancellation = _buildCancellation;
            _buildCancellation = nextCancellation;
            _current = nextIndex;
            _buildCompletion = BuildInBackgroundAsync(
                nextIndex, progress, nextCancellation.Token);
        }

        CancelAndDispose(previousCancellation);
        return nextIndex;
    }

    public void Cancel() => Stop(markDisposed: false);

    public void Dispose() => Stop(markDisposed: true);

    private async Task BuildInBackgroundAsync(
        DocumentTextIndex index,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_buildDelay, cancellationToken).ConfigureAwait(false);
            await index.BuildAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected whenever another document replaces the current one.
        }
    }

    private void Stop(bool markDisposed)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = markDisposed;
            cancellation = _buildCancellation;
            _buildCancellation = null;
            _current = null;
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
