namespace Excise.Core.Document;

/// <summary>
/// Text-engine compatibility members exposed on the document page model.
/// Kept as partial members to preserve the established public API while the
/// implementation and cache ownership remain in <c>core-text</c>.
/// </summary>
/// <remarks>
/// <para><b>The letter cache is bounded per document (#1485).</b> A page's
/// <see cref="Letters"/> and <see cref="GetWords"/> are cached, but only for
/// the <see cref="PdfDocument.PageLetterCacheCapacity"/> most recently used
/// pages of the document; older pages drop them and re-walk their content
/// bytes on next access. Before this every page kept its letters for the
/// document's lifetime once anything had touched it — the text index touches
/// every page, and an accessibility walk or a long selection does too — which
/// was 595,373 <see cref="Excise.Core.Text.Letter"/> objects (76 MB) on
/// irs-1040-instructions.pdf. Eviction only ever DROPS a cache: a re-read walks
/// the page's current bytes through the same <see cref="Excise.Core.Text.TextExtractor"/>,
/// so every consumer, redaction included, sees exactly the letters it would
/// have seen from the cache. What changes is object identity across an
/// eviction, so a caller that compares letters by reference must take them
/// from one read.</para>
/// <para><see cref="Text"/> is not bounded: it is a single string per page and
/// is what a caller that only needs text should keep.</para>
/// </remarks>
public partial class PdfPage
{
    // Written only under _document.TextCacheGate; read lock-free on the hit
    // path (a reference read is atomic, and a stale null only costs a walk).
    private IReadOnlyList<Excise.Core.Text.Letter>? _cachedLetters;
    private string? _cachedText;
    private IReadOnlyList<Excise.Core.Text.Word>? _cachedWords;

    // Bumped by InvalidateTextExtractionCache. An extraction that started
    // before a content change must not store its result after it: that is the
    // "stale letters survive a redaction" failure, reachable whenever a walk
    // and a mutation overlap (the background text index and a GUI redaction).
    private int _textCacheGeneration;

    // Content-stream walks this facade has run for this page. Test seam for
    // "a repeated search walks no page twice" (#1485); production never reads it.
    private int _textWalkCount;

    /// <summary>
    /// The page's text-content generation: bumped by every content rewrite and
    /// every <see cref="InvalidateTextExtractionCache"/>. A caller that keeps
    /// data derived from this page's text stamps it with the generation it was
    /// walked under and must not serve it once this has moved on (#1485).
    /// </summary>
    internal int TextContentGeneration => Volatile.Read(ref _textCacheGeneration);

    /// <summary>Walks run through this facade for this page. For tests (#1485).</summary>
    internal int TextWalkCount => Volatile.Read(ref _textWalkCount);

    /// <summary>
    /// Get the extracted text content from the page.
    /// Cached on first access; subsequent calls return the cached result.
    /// </summary>
    /// <remarks>
    /// Excludes letters positioned entirely above or below the page's
    /// <see cref="CropBox"/> (#649): producers routinely place production
    /// metadata — filename slugs, proofing notes, workflow IDs — far off-canvas
    /// (e.g. Y &gt; 900 on a 792pt-tall page) using ordinary content-stream text
    /// operators with no reliable tag to distinguish it. Two rejected approaches,
    /// both found by direct measurement against mutool (the parity oracle):
    /// (1) a <c>/Artifact</c>-tag-based filter — on real documents
    /// (irs-1040-instructions.pdf p1) the same tag covers both the off-page junk
    /// AND the genuinely visible, on-page running footer ("Department of the
    /// Treasury..."), so filtering by tag alone hid real, searchable content;
    /// (2) a full bounding-box (X and Y) filter — excise's own X-position
    /// calculation has known drift on some real documents (#90; horizontal
    /// advance-width accumulation, unlike Y which comes from explicit line
    /// operators), and on scotus-trump-v-us.pdf p56 that drift alone pushed
    /// genuinely visible footnote text (confirmed present in mutool's output)
    /// up to ~100pt past the right edge — an X-bounds filter would have deleted
    /// real content to paper over an unrelated, pre-existing position bug.
    /// Every off-page slug measured across the smoke corpus is a pure vertical
    /// violation (Y entirely outside the CropBox, X untouched), so the filter
    /// checks Y only — narrow enough to remove the slug, too narrow to be
    /// tripped by X-axis drift. <see cref="Letters"/> itself is NOT filtered —
    /// redaction reads letters directly and must keep full reach into off-page
    /// content, so only this derived, display/search-facing view is narrowed.
    /// </remarks>
    public string Text
    {
        get
        {
            var cached = _cachedText;
            if (cached != null)
                return cached;

            var generation = Volatile.Read(ref _textCacheGeneration);
            var text = BuildPageText(Letters);
            StoreText(text, generation);
            return text;
        }
    }

    /// <summary>
    /// Get all letters extracted from the page with position information.
    /// Cached on first access; subsequent calls return the cached result
    /// while the page stays among the document's most recently used pages
    /// (see the type remarks, #1485).
    /// </summary>
    public IReadOnlyList<Excise.Core.Text.Letter> Letters => GetLetters();

    /// <summary>
    /// <see cref="Letters"/> with a cancellation token, so a caller with a
    /// timeout can abandon extraction of a hostile or very large page instead
    /// of blocking until it finishes (#982; CLAUDE.md Pitfall 3). A cancelled
    /// call throws <see cref="OperationCanceledException"/> and caches nothing,
    /// so a later call re-runs the extraction rather than returning a partial
    /// letter list.
    /// </summary>
    public IReadOnlyList<Excise.Core.Text.Letter> GetLetters(
        CancellationToken cancellationToken = default)
    {
        if (_cachedLetters != null)
        {
            lock (_document.TextCacheGate)
            {
                // Re-read under the gate: an eviction between the check and
                // here must fall through to a walk, not return null.
                if (_cachedLetters is { } cached)
                {
                    _document.MarkPageLettersUsedLocked(this);
                    return cached;
                }
            }
        }

        var generation = Volatile.Read(ref _textCacheGeneration);
        Interlocked.Increment(ref _textWalkCount);
        var extractor = new Excise.Core.Text.TextExtractor(this);
        var letters = extractor.ExtractLetters(cancellationToken);

        lock (_document.TextCacheGate)
        {
            if (generation != _textCacheGeneration)
                return letters; // the content changed during the walk; cache nothing

            // A concurrent reader may have stored first. Return ITS list so
            // every caller that got a cached result holds the same objects.
            _cachedLetters ??= letters;
            _document.MarkPageLettersUsedLocked(this);
            return _cachedLetters;
        }
    }

    /// <summary>
    /// Get all words extracted from the page.
    /// A word is a sequence of letters separated by whitespace.
    /// Cached on first access; subsequent calls return the cached result
    /// while the page's letters stay cached (see the type remarks, #1485).
    /// </summary>
    /// <returns>List of words with their letters and bounding boxes.</returns>
    public IReadOnlyList<Excise.Core.Text.Word> GetWords()
    {
        if (_cachedWords != null)
        {
            lock (_document.TextCacheGate)
            {
                if (_cachedWords is { } cached)
                {
                    _document.MarkPageLettersUsedLocked(this);
                    return cached;
                }
            }
        }

        var letters = Letters;
        var words = Excise.Core.Text.TextExtractor.BuildWords(letters);
        lock (_document.TextCacheGate)
        {
            // Words hold letters, so they are cached only alongside the exact
            // letter list they were built from. Storing them after that list
            // was evicted or invalidated would retain letters outside the
            // bound, or serve words from bytes that no longer exist.
            if (!ReferenceEquals(_cachedLetters, letters))
                return words;
            _cachedWords ??= words;
            return _cachedWords;
        }
    }

    /// <summary>
    /// Page text and words for a caller that keeps its own compact copy and
    /// must not leave this page's letters cached behind it — the App's
    /// document text index, which visits every page (#1485).
    /// </summary>
    /// <remarks>
    /// Same bytes, same walker, same word builder and the same text as
    /// <see cref="Text"/> and <see cref="GetWords"/>: when the page's letters
    /// are already cached they are reused, otherwise the page is walked and the
    /// letters are dropped after the words are built. Neither path touches the
    /// document's most-recently-used order, so a background build does not
    /// evict the pages a reader is working on. The text is cached (it is one
    /// string); the letters and words are not.
    /// </remarks>
    internal (string Text, IReadOnlyList<Excise.Core.Text.Word> Words, int Generation) ExtractTextAndWordsWithoutRetainingLetters(
        CancellationToken cancellationToken = default)
    {
        // Read BEFORE the letters, and returned: a rewrite that lands during
        // or after the walk moves the generation past this value, so a caller
        // that stamps its copy with it can never serve that copy afterwards.
        var generation = Volatile.Read(ref _textCacheGeneration);
        var letters = _cachedLetters;
        if (letters == null)
        {
            Interlocked.Increment(ref _textWalkCount);
            letters = new Excise.Core.Text.TextExtractor(this).ExtractLetters(cancellationToken);
        }
        var words = _cachedWords is { } cachedWords && ReferenceEquals(letters, _cachedLetters)
            ? cachedWords
            : Excise.Core.Text.TextExtractor.BuildWords(letters);

        var text = _cachedText;
        if (text == null)
        {
            text = BuildPageText(letters);
            StoreText(text, generation);
        }
        return (text, words, generation);
    }

    /// <summary>
    /// True while this page holds its letter list. For tests of the #1485
    /// bound; production code reads <see cref="Letters"/>.
    /// </summary>
    internal bool HasCachedLetters => Volatile.Read(ref _cachedLetters) != null;

    private string BuildPageText(IReadOnlyList<Excise.Core.Text.Letter> letters)
    {
        var cropBox = CropBox.Normalize();
        var visible = new List<Excise.Core.Text.Letter>(letters.Count);
        foreach (var letter in letters)
        {
            var glyphBox = letter.GlyphRectangle.Normalize();
            if (glyphBox.Top <= cropBox.Bottom || glyphBox.Bottom >= cropBox.Top)
                continue;
            visible.Add(letter);
        }

        var reading = Excise.Core.Text.TextSelectionEngine.SortPageTextOrder(visible);
        return Excise.Core.Text.TextSelectionEngine.JoinText(
            reading,
            Excise.Core.Text.WhitespaceMode.LineFaithful);
    }

    private void StoreText(string text, int generation)
    {
        lock (_document.TextCacheGate)
        {
            if (generation == _textCacheGeneration)
                _cachedText ??= text;
        }
    }

    /// <summary>
    /// Drop this page's letters and words because the document's bound
    /// evicted it (#1485). Not a content change: the text stays, and the
    /// generation is untouched so an in-flight walk of the same bytes may
    /// still store its result.
    /// </summary>
    internal void ReleaseLetterCacheLocked()
    {
        _cachedLetters = null;
        _cachedWords = null;
    }

    /// <summary>
    /// Clear cached text extraction after this page's content bytes, or
    /// page-adjacent structures such as annotations or form fields, change.
    /// </summary>
    /// <remarks>
    /// Redaction depends on this: every <c>SetContentStream</c> /
    /// <c>SetContentStreamBytes</c> calls it, so the next <see cref="Letters"/>
    /// read after a rewrite walks the rewritten bytes. The generation bump
    /// also stops a walk that started before the rewrite from storing its
    /// now-stale letters afterwards (#1485).
    /// </remarks>
    internal void InvalidateTextExtractionCache()
    {
        lock (_document.TextCacheGate)
        {
            _textCacheGeneration++;
            _cachedLetters = null;
            _cachedText = null;
            _cachedWords = null;
            _document.ForgetPageLettersLocked(this);
        }
    }
}
