using System.Runtime.CompilerServices;
using Excise.Core.Document;

namespace Excise.App.Services;

/// <summary>
/// Each page's text and compact words (<see cref="IndexedWord"/>), kept once
/// the page has been walked, for as long as the page object lives (#1485).
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Letters are bounded to a few pages per
/// document, so a search that read <c>page.GetWords()</c> re-walked every
/// evicted page on every keystroke: repeat live search on
/// irs-1040-instructions.pdf went from ~25 ms to ~485 ms. Search needs only a
/// word's text and box, which is ~5 MB for that document where its letters
/// were ~80 MB, so those are kept for every walked page and the letters are
/// not. The document text index reads the same entries, so an indexed
/// document holds one copy, not two.</para>
/// <para><b>Invalidation.</b> Every entry is stamped with the page's
/// <see cref="PdfPage.TextContentGeneration"/> at the start of the walk that
/// produced it, and is served only while the page is still at that
/// generation. A content rewrite bumps the generation — the same event that
/// drops the page's cached letters — so the rewritten page is walked again on
/// its next read and a stale entry is replaced, never returned.</para>
/// <para>Redaction does not read this: it reads <c>page.Letters</c>, which
/// walks the page's current bytes.</para>
/// <para>Thread-safe: <see cref="ConditionalWeakTable{TKey,TValue}"/> is, and
/// a lost race only costs a walk, because a stale stamp is never served.</para>
/// </remarks>
internal static class PageWordStore
{
    private sealed record Entry(int Generation, string Text, IndexedWord[] Words);

    // Keyed weakly by the page object: an entry lives exactly as long as its
    // PdfPage, which lives as long as its document's page collection.
    private static readonly ConditionalWeakTable<PdfPage, Entry> Entries = new();

    internal static (string Text, IReadOnlyList<IndexedWord> Words) Get(
        PdfPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (Entries.TryGetValue(page, out var entry) && entry.Generation == page.TextContentGeneration)
            return (entry.Text, entry.Words);

        var (text, words, generation) = page.ExtractTextAndWordsWithoutRetainingLetters(cancellationToken);
        var fresh = new Entry(generation, text, IndexedWord.FromWords(words));
        Entries.AddOrUpdate(page, fresh);
        return (fresh.Text, fresh.Words);
    }
}
