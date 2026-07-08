using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using Pdfe.Core.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEditor.Services;

/// <summary>
/// In-memory full-text index for one open document. Extracts every
/// page's text and word list once at index-build time so subsequent
/// searches don't pay the per-page extraction cost on every keystroke.
///
/// Build is incremental and cancellable, so we can start it eagerly on
/// document open and report progress to the UI without blocking it.
/// </summary>
/// <remarks>
/// Word objects hold references back into Pdfe.Core.Text data, so the
/// index is tied to the lifetime of the underlying <see cref="PdfDocument"/>.
/// Dispose the index when its document is replaced.
/// </remarks>
public sealed class DocumentTextIndex
{
    private readonly PdfDocument _doc;
    private readonly ILogger _logger;
    private readonly string?[] _pageTexts;
    private readonly IReadOnlyList<Word>?[] _pageWords;
    private readonly object _buildLock = new();
    private Task? _buildTask;
    private int _pagesIndexed;
    private bool _ready;

    public DocumentTextIndex(PdfDocument doc, ILogger logger)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _logger = logger;
        _pageTexts = new string?[doc.PageCount];
        _pageWords = new IReadOnlyList<Word>?[doc.PageCount];
    }

    public int PageCount => _doc.PageCount;
    public int PagesIndexed => _pagesIndexed;
    public bool IsReady => _ready;

    /// <summary>
    /// Walk every page once and cache its extracted text + words. Reports
    /// progress as <c>(pagesDone, totalPages)</c> for status-bar binding.
    /// Idempotent — calling again after Ready returns immediately.
    /// </summary>
    public Task BuildAsync(IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_ready) return Task.CompletedTask;

        lock (_buildLock)
        {
            if (_ready) return Task.CompletedTask;
            if (_buildTask is { IsCompleted: false })
                return _buildTask.WaitAsync(cancellationToken);

            _buildTask = Task.Run(() => BuildCore(progress, cancellationToken), cancellationToken);
            return _buildTask;
        }
    }

    private void BuildCore(IProgress<(int Done, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            for (int i = 0; i < _doc.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_pageTexts[i] != null) continue;
                var page = _doc.GetPage(i + 1);
                _pageTexts[i] = page.Text ?? string.Empty;
                _pageWords[i] = page.GetWords();
                Interlocked.Increment(ref _pagesIndexed);
                progress?.Report((_pagesIndexed, _doc.PageCount));
            }
            _ready = true;
            _logger.LogInformation("Text index built ({Pages} pages)", _doc.PageCount);
        }
        catch (OperationCanceledException) { /* expected on doc switch */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Text index build failed at page {Page}", _pagesIndexed);
        }
    }

    /// <summary>
    /// True once page <paramref name="pageIndex"/> is in the cache. Search
    /// for a partially-built index can fall back to live extraction for
    /// the un-indexed tail.
    /// </summary>
    public bool IsPageIndexed(int pageIndex) =>
        pageIndex >= 0 && pageIndex < _pageTexts.Length && _pageTexts[pageIndex] != null;

    /// <summary>Cached page text. Throws if the page hasn't been indexed yet.</summary>
    public string GetPageText(int pageIndex) =>
        _pageTexts[pageIndex] ?? throw new InvalidOperationException(
            $"Page {pageIndex} not yet indexed");

    /// <summary>Cached page words. Throws if the page hasn't been indexed yet.</summary>
    public IReadOnlyList<Word> GetPageWords(int pageIndex) =>
        _pageWords[pageIndex] ?? throw new InvalidOperationException(
            $"Page {pageIndex} not yet indexed");
}
