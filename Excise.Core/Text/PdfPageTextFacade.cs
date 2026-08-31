namespace Excise.Core.Document;

/// <summary>
/// Text-engine compatibility members exposed on the document page model.
/// Kept as partial members to preserve the established public API while the
/// implementation and cache ownership remain in <c>core-text</c>.
/// </summary>
public partial class PdfPage
{
    private IReadOnlyList<Excise.Core.Text.Letter>? _cachedLetters;
    private string? _cachedText;
    private IReadOnlyList<Excise.Core.Text.Word>? _cachedWords;

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
            if (_cachedText != null)
                return _cachedText;

            var cropBox = CropBox.Normalize();
            var visible = new List<Excise.Core.Text.Letter>(Letters.Count);
            foreach (var letter in Letters)
            {
                var glyphBox = letter.GlyphRectangle.Normalize();
                if (glyphBox.Top <= cropBox.Bottom || glyphBox.Bottom >= cropBox.Top)
                    continue;
                visible.Add(letter);
            }

            var reading = Excise.Core.Text.TextSelectionEngine.SortPageTextOrder(visible);
            _cachedText = Excise.Core.Text.TextSelectionEngine.JoinText(
                reading,
                Excise.Core.Text.WhitespaceMode.LineFaithful);
            return _cachedText;
        }
    }

    /// <summary>
    /// Get all letters extracted from the page with position information.
    /// Cached on first access; subsequent calls return the cached result.
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
            return _cachedLetters;

        var extractor = new Excise.Core.Text.TextExtractor(this);
        _cachedLetters = extractor.ExtractLetters(cancellationToken);
        return _cachedLetters;
    }

    /// <summary>
    /// Get all words extracted from the page.
    /// A word is a sequence of letters separated by whitespace.
    /// Cached on first access; subsequent calls return the cached result.
    /// </summary>
    /// <returns>List of words with their letters and bounding boxes.</returns>
    public IReadOnlyList<Excise.Core.Text.Word> GetWords()
    {
        if (_cachedWords != null)
            return _cachedWords;

        _cachedWords = Excise.Core.Text.TextExtractor.BuildWords(Letters);
        return _cachedWords;
    }

    /// <summary>
    /// Clear cached text extraction after page-adjacent structures such as
    /// annotations or form fields change without rewriting /Contents.
    /// </summary>
    internal void InvalidateTextExtractionCache()
    {
        _cachedLetters = null;
        _cachedText = null;
        _cachedWords = null;
    }
}
