using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.Core.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.Services;

/// <summary>
/// In-memory full-text index for one open document. Extracts every
/// page's text and word list once at index-build time so subsequent
/// searches don't pay the per-page extraction cost on every keystroke.
///
/// Build is incremental and cancellable, so we can start it eagerly on
/// document open and report progress to the UI without blocking it.
/// </summary>
/// <remarks>
/// <para>The index keeps <see cref="IndexedWord"/>s — each word's text and
/// bounding box — not <see cref="Word"/>s, and it leaves no letters cached on
/// the pages it visits (#1485). A <see cref="Word"/> holds its
/// <see cref="Letter"/>s, and building the index used to prime every page's
/// letter cache as a side effect, so an indexed document retained all of its
/// letters for its lifetime. Measured with dotnet-gcdump on
/// irs-1040-instructions.pdf, opened and indexed: GC heap 136.1 MB with
/// 595,373 letters and 110,654 words live, and 42.4 MB with neither once the
/// index kept only this. Search reads exactly a word's text and box, so that
/// is all this keeps. Anything that needs letters — selection, the
/// accessibility MCID bridge, and redaction above all — reads
/// <see cref="PdfPage.Letters"/>, which walks the page's current bytes.</para>
/// <para>The index is a snapshot of the document when it was built. Every GUI
/// mutation restarts it through <c>DocumentTextIndexSession.Start</c>.</para>
/// </remarks>
public sealed class DocumentTextIndex
{
    private readonly PdfDocument _doc;
    private readonly ILogger _logger;
    private readonly string?[] _pageTexts;
    private readonly IReadOnlyList<IndexedWord>?[] _pageWords;
    private readonly object _buildLock = new();
    private Task? _buildTask;
    private int _pagesIndexed;
    private bool _ready;

    public DocumentTextIndex(PdfDocument doc, ILogger logger)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _logger = logger;
        _pageTexts = new string?[doc.PageCount];
        _pageWords = new IReadOnlyList<IndexedWord>?[doc.PageCount];
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
                // #1485: NOT page.Text + page.GetWords(). Those cache the
                // page's letters, and holding every page's letters is the
                // retention this index exists without.
                var (text, words) = page.ExtractTextAndWordsWithoutRetainingLetters(cancellationToken);
                // Words before text: IsPageIndexed keys off the text, and a
                // concurrent search must never see a page as indexed while its
                // words are still missing.
                _pageWords[i] = IndexedWord.FromWords(words);
                _pageTexts[i] = text;
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
    public IReadOnlyList<IndexedWord> GetPageWords(int pageIndex) =>
        _pageWords[pageIndex] ?? throw new InvalidOperationException(
            $"Page {pageIndex} not yet indexed");
}

/// <summary>
/// What search needs of a <see cref="Word"/>: its text and bounding box, and
/// not its letters (#1485).
/// </summary>
/// <remarks>
/// Both values are copied from the <see cref="Word"/> as they are — the same
/// string instance and the same four doubles — so a search over these returns
/// the same matches, boxes and order as a search over the words themselves.
/// </remarks>
public readonly record struct IndexedWord(string Text, PdfRectangle BoundingBox)
{
    internal static IndexedWord[] FromWords(IReadOnlyList<Word> words)
    {
        var result = new IndexedWord[words.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = new IndexedWord(words[i].Text, words[i].BoundingBox);
        return result;
    }
}
