using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Excise.Core.Content;
using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Document-level redaction helpers built on top of the per-page
/// <see cref="PdfPageRedactionExtensions.RedactArea"/> primitive. Locates
/// text by searching the extracted letter sequence of each page and
/// removes every occurrence from the content stream.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth for text-search-based redaction:
/// both the GUI (<c>Excise.App.Services.RedactionService.RedactText</c>)
/// and the <c>excise</c> CLI <c>redact</c> command go through
/// <see cref="RedactText(PdfDocument, string, bool, GlyphRemovalStrategy, bool)"/>.
/// </para>
/// <para>
/// A black rectangle overlay is appended to each page's content stream
/// for visual confirmation. The overlay is purely cosmetic — the
/// <em>security</em> guarantee comes from the content-stream rewrite in
/// <see cref="PdfPageRedactionExtensions.RedactArea"/>, which deletes
/// the glyphs themselves. Callers that want pure structural removal with
/// no visual marker can pass <c>drawBlackRect: false</c>.
/// </para>
/// </remarks>
public static class PdfDocumentRedactionExtensions
{
    /// <summary>
    /// Redact every occurrence of <paramref name="text"/> in
    /// <paramref name="document"/> — from page content AND from the
    /// document-level text carriers that restate it (<c>/Info</c>, the XMP
    /// <c>/Metadata</c> packet, outline titles, annotation <c>/Contents</c>).
    /// The document is mutated in place; call
    /// <see cref="PdfDocument.Save(string)"/> to persist.
    /// </summary>
    /// <remarks>
    /// The carrier scrub is ON by default and that is deliberate (#896). It used
    /// to live in the GUI's save workflow, which meant the GUI was complete and
    /// every other consumer silently was not: <c>excise redact</c> and batch
    /// <c>redaction.apply</c> left the term in seven of eight carriers while
    /// reporting success. A redaction API whose safe form is opt-in produces
    /// exactly that outcome the first time someone writes a new front end.
    /// <para>
    /// Two limits worth knowing rather than discovering:
    /// <list type="bullet">
    ///   <item>Terms shorter than 3 characters are redacted from page content
    ///     but NOT from document-level carriers — excising 1-2 character
    ///     fragments from every metadata string corrupts unrelated values for
    ///     no security benefit.</item>
    ///   <item><see cref="PdfPageRedactionExtensions.RedactArea(PdfPage, PdfRectangle, GlyphRemovalStrategy)"/>
    ///     has no term to scrub and therefore does none of this. An area
    ///     redaction still needs its removed text collected and scrubbed
    ///     separately.</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="document">The PDF document to redact.</param>
    /// <param name="text">The text to redact.</param>
    /// <param name="caseSensitive">Whether matching is case-sensitive.</param>
    /// <param name="strategy">Strategy for selecting glyphs to remove when bounding boxes overlap.</param>
    /// <param name="drawBlackRect">Whether to append a visual black rectangle overlay.</param>
    /// <param name="includeHiddenLayers">Whether to include text in Optional Content Groups
    /// (OCGs) that are OFF by default. When true, this closes a security gap where content
    /// on hidden layers is invisible in the default view but fully extractable via other tools.
    /// Defaults to true for security (redact even hidden content).</param>
    /// <param name="scrubDocumentCarriers">Whether to also remove the term from
    /// <c>/Info</c>, the XMP <c>/Metadata</c> packet, outline titles and annotation
    /// <c>/Contents</c> (#896). Defaults to true — those carriers restate page text
    /// and are invisible to a content-stream check, which is how three separate
    /// leaks shipped past a green suite. Pass false only when the caller performs
    /// the scrub itself.</param>
    /// <returns>
    /// Total number of matches removed across all pages.
    /// </returns>
    public static RedactionReport RedactText(
        this PdfDocument document,
        string text,
        bool caseSensitive = false,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap,
        bool drawBlackRect = true,
        bool includeHiddenLayers = true,
        bool scrubDocumentCarriers = true)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        var pageResults = new List<PageRedactionResult>();
        var carrierResults = new List<CarrierResult>();

        if (string.IsNullOrEmpty(text))
            return new RedactionReport
            {
                Term = text ?? "",
                Pages = pageResults,
                Carriers = carrierResults,
            };

        int totalMatches = 0;

        for (int pageNum = 1; pageNum <= document.PageCount; pageNum++)
        {
            var page = document.GetPage(pageNum);
            var pageLocated = 0;
            string? previousSearchText = null;

            for (var pass = 0; pass < 10; pass++)
            {
                var letters = page.Letters;
                if (letters.Count == 0) break;

                // Filter letters based on includeHiddenLayers setting
                var searchLetters = includeHiddenLayers
                    ? letters
                    : letters.Where(l => !l.IsInHiddenOptionalContent).ToList();

                if (searchLetters.Count == 0) break;

                var searchTextSnapshot = string.Concat(searchLetters.Select(l => l.Value));
                var matches = FindTextMatches(searchLetters, text, caseSensitive);
                if (matches.Count == 0) break;

                // #1090: a stalled page STOPS. It used to fall back to
                // deleting every operator overlapping the match box, which
                // removed the term and an unbounded amount of its neighbours.
                // Stopping leaves the match in place and #1089's verification
                // reports it as RemovalUnverified — the caller is told the
                // truth instead of handed a quietly mutilated document.
                var stalled = searchTextSnapshot == previousSearchText;
                previousSearchText = searchTextSnapshot;
                if (stalled)
                    break;

                {
                    var contentAreas = new List<PdfRectangle>();
                    var markerAreas = new List<PdfRectangle>();
                    foreach (var matchLetters in matches)
                    {
                        var bbox = BoundingBoxOf(matchLetters);
                        if (IsInteractiveOnlyMatch(matchLetters))
                            // TERM-aware (#1038). The area-only form deletes the
                            // whole field value; on issue18036.pdf that was 545
                            // of 568 characters to remove one word.
                            InteractiveRedactionScrubber.ScrubTerm(
                                page, bbox, text, caseSensitive);
                        else
                            contentAreas.Add(strategy == GlyphRemovalStrategy.FullyContained
                                ? bbox
                                : CenterlineBoxOf(matchLetters));
                        markerAreas.Add(bbox);
                    }

                    if (contentAreas.Count > 0)
                    {
                        // scrubDocumentCarriers: false — RedactText owns its own
                        // carrier policy and applies it once, at the end, BY TERM
                        // (#896). RedactArea's default is the WHOLESALE strip,
                        // which exists for callers who have only a rectangle
                        // (#897); letting it fire here would silently override
                        // this method's own opt-out and destroy /Info and XMP on
                        // every RedactText call — including the documented case
                        // where a term below the sanitizer's 3-character floor
                        // deliberately leaves carriers alone.
                        page.RedactAreas(contentAreas, strategy, scrubDocumentCarriers: false);
                    }

                    if (drawBlackRect)
                        foreach (var bbox in markerAreas) AppendBlackRectangle(page, bbox);
                }

                totalMatches += matches.Count;
                pageLocated += matches.Count;
            }

            // #1089 VERIFICATION. Re-read the page and count what is STILL
            // findable. This is the difference between "excise tried" and
            // "excise checked", and the whole reason the old int return was a
            // lie: it reported attempts.
            var remaining = CountOccurrences(page, text, caseSensitive, includeHiddenLayers);
            pageResults.Add(new PageRedactionResult(
                pageNum,
                pageLocated,
                remaining,
                remaining > 0 ? RedactionOutcome.RemovalUnverified
                : pageLocated > 0 ? RedactionOutcome.RemovedVerified
                : RedactionOutcome.NothingToRemove));
        }

        // #896: document-level carriers are part of redaction, not part of a
        // caller's save workflow.
        //
        // Everything above this line rewrites PAGE CONTENT. A PDF restates the
        // same string in /Info, the XMP packet, outline titles and annotation
        // /Contents — the four carriers #608 was filed for after they shipped a
        // leak past a fully green suite. Scrubbing them lived in Excise.App, so
        // the GUI was complete and every other consumer was not: `excise redact`
        // and batch `redaction.apply` left the term in SEVEN of eight carriers
        // while reporting success.
        //
        // Doing it here rather than in each caller is the actual fix. A
        // guarantee re-established by every front end is a guarantee that holds
        // until someone writes a new front end.
        //
        // Runs even when totalMatches is 0: "redact this term" means remove it
        // from the document, and a term present only in the title is exactly
        // the case a page-content match count cannot see.
        //
        // NOTE: ScrubTerms ignores terms shorter than 3 characters — excising
        // 1-2 character fragments from every metadata string would corrupt
        // unrelated values for no security benefit. Page content is still
        // redacted for such terms; their document-level carriers are not.
        if (scrubDocumentCarriers)
        {
            // #999: the scrubber ignores terms shorter than 3 characters. That
            // is deliberate -- excising 1-2 character fragments from every
            // metadata string would corrupt unrelated values -- but the old int
            // return could not SAY it, so a caller redacting "Ro" got a success
            // count while /Info, XMP, outlines and annotation /Contents kept the
            // term. Reported now, per the decided policy: surface, don't guess.
            const int minTermLength = 3;
            if (text.Length < minTermLength)
            {
                foreach (var carrier in DocumentCarriers)
                    carrierResults.Add(new CarrierResult(carrier, false,
                        $"term is {text.Length} characters; the carrier scrub floor is {minTermLength}"));
            }
            else
            {
                Excise.Core.Operations.PdfDocumentSanitizer.ScrubTerms(
                    document, new[] { text }, caseSensitive);
                foreach (var carrier in DocumentCarriers)
                    carrierResults.Add(new CarrierResult(carrier, true, null));
            }
        }
        else
        {
            foreach (var carrier in DocumentCarriers)
                carrierResults.Add(new CarrierResult(carrier, false,
                    "scrubDocumentCarriers: false was requested by the caller"));
        }

        return new RedactionReport
        {
            Term = text,
            Pages = pageResults,
            Carriers = carrierResults,
        };
    }

    /// <summary>The document-level carriers #608 was filed for.</summary>
    private static readonly string[] DocumentCarriers =
        { "/Info", "XMP /Metadata", "/Outlines titles", "annotation /Contents" };

    /// <summary>
    /// Occurrences of <paramref name="text"/> still findable on the page AFTER
    /// redaction -- the verification half of #1089.
    ///
    /// <para>⚠️ This is excise reading its own output, which the no-self-oracle
    /// rule says cannot PROVE removal. It does not claim to: it catches removal
    /// that DID NOT LAND, a different and very common failure. Text excise
    /// could never see is bounded by extraction coverage (Limitations #1) and
    /// needs an independent extractor -- #1094.</para>
    /// </summary>
    private static int CountOccurrences(
        PdfPage page, string text, bool caseSensitive, bool includeHiddenLayers)
    {
        try
        {
            var letters = page.Letters;
            var searchLetters = includeHiddenLayers
                ? letters
                : letters.Where(l => !l.IsInHiddenOptionalContent).ToList();
            return FindTextMatches(searchLetters, text, caseSensitive).Count;
        }
        catch
        {
            // A page that will not re-extract cannot be verified. Report it as
            // survived rather than as success: assuming the term is still there
            // is the safe direction.
            return 1;
        }
    }

    /// <summary>
    /// Bounding box that encloses all <paramref name="letters"/>.
    /// </summary>
    private static PdfRectangle BoundingBoxOf(IReadOnlyList<Letter> letters)
    {
        return new PdfRectangle(
            letters.Min(l => l.GlyphRectangle.Left),
            letters.Min(l => l.GlyphRectangle.Bottom),
            letters.Max(l => l.GlyphRectangle.Right),
            letters.Max(l => l.GlyphRectangle.Top));
    }

    /// <summary>
    /// A narrow rectangle through every matched glyph's center. Search already
    /// identified the exact glyphs, so using their full union with AnyOverlap
    /// can wrongly catch the next line when producer glyph boxes overlap due to
    /// tight leading. The small padding keeps single-glyph and axis-aligned
    /// horizontal/vertical matches non-degenerate (#942).
    /// </summary>
    private static PdfRectangle CenterlineBoxOf(IReadOnlyList<Letter> letters)
    {
        const double padding = 0.01;
        var centers = letters.Select(l =>
        {
            var r = l.GlyphRectangle.Normalize();
            return (X: (r.Left + r.Right) * 0.5, Y: (r.Bottom + r.Top) * 0.5);
        }).ToList();

        return new PdfRectangle(
            centers.Min(p => p.X) - padding,
            centers.Min(p => p.Y) - padding,
            centers.Max(p => p.X) + padding,
            centers.Max(p => p.Y) + padding);
    }

    /// <summary>
    /// True when every letter in a match was synthesized from something OTHER
    /// than the content stream — an AcroForm widget value or FreeText
    /// annotation content (#660) — meaning there is no content-stream glyph
    /// for <see cref="PdfPage.RedactArea"/>'s glyph/image passes to find.
    /// These route to <see cref="InteractiveRedactionScrubber"/> directly
    /// instead, which removes the underlying field value/appearance or
    /// annotation object.
    /// </summary>
    private static bool IsInteractiveOnlyMatch(IReadOnlyList<Letter> letters) =>
        letters.Count > 0 &&
        letters.All(l => l.FontName.StartsWith("AcroForm:", StringComparison.Ordinal) ||
                          l.FontName.StartsWith("Annotation:", StringComparison.Ordinal));

    /// <summary>
    /// Append a filled black rectangle at <paramref name="rect"/> to the
    /// page's content stream — the standard
    /// <c>q 0 0 0 rg X Y W H re f Q</c> sequence. Used as a cosmetic
    /// overlay on top of structural glyph removal.
    /// </summary>
    private static void AppendBlackRectangle(PdfPage page, PdfRectangle rect)
    {
        var content = page.GetContentStream();
        var ops = content.Operators.ToList();
        ops.Add(ContentOperator.SaveState());
        ops.Add(ContentOperator.SetFillRgb(0, 0, 0));
        ops.Add(ContentOperator.Rectangle(
            rect.Left, rect.Bottom, rect.Right - rect.Left, rect.Top - rect.Bottom));
        ops.Add(ContentOperator.Fill());
        ops.Add(ContentOperator.RestoreState());
        page.SetContentStream(new ContentStream(ops));
    }

    /// <summary>
    /// Find every occurrence of <paramref name="searchText"/> in the
    /// concatenated letter sequence of a page and return the letter-slices
    /// that spell each match.
    /// </summary>
    /// <remarks>
    /// The letter sequence is already in reading order (rotation-aware via
    /// <c>TextExtractor</c>). Text is normalized (curly→straight quotes,
    /// en/em dash→hyphen, whitespace collapse) before comparison so
    /// typographic variation doesn't block a match. Matches are
    /// non-overlapping — greedy left-to-right.
    /// </remarks>
    internal static List<List<Letter>> FindTextMatches(
        IReadOnlyList<Letter> letters, string searchText, bool caseSensitive)
    {
        var matches = new List<List<Letter>>();
        if (string.IsNullOrEmpty(searchText) || letters.Count == 0)
            return matches;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // #1047: match against a view with OVERPRINTED duplicates collapsed.
        // Faux-bold is drawn by stamping the same run several times at
        // sub-point offsets; excise's letter model faithfully records every
        // copy, so a 4x-stamped "Test test" reads as "TTTTeeeesssstttt" and a
        // search for "test" matches NOTHING. The term then survives and
        // RedactText reports success — Limitations #1, exactly.
        var (view, spanStart, spanEnd) = CollapseOverprintedGlyphs(letters);

        var sb = new StringBuilder(view.Count);
        var characterToLetter = new List<int>(view.Count);
        for (var letterIndex = 0; letterIndex < view.Count; letterIndex++)
        {
            var value = view[letterIndex].Value;
            sb.Append(value);
            for (var charIndex = 0; charIndex < value.Length; charIndex++)
                characterToLetter.Add(letterIndex);
        }
        var fullText = sb.ToString();

        // Trim only the caller's needle. Trimming each candidate source window
        // lets matching start on an unrelated whitespace glyph, whose geometry
        // may be on another line or column. The resulting bounding box can span
        // most of a page and destroy remote text (#942).
        var needle = NormalizeText(searchText).Trim();
        if (needle.Length == 0) return matches;

        // NOTE: not `i <= fullText.Length - needle.Length` — normalization can
        // EXPAND raw text (a lam-alef ligature is one raw char but two needle
        // chars), so a raw window shorter than the needle can still match.
        int i = 0;
        while (i < fullText.Length)
        {
            // Normalize may collapse whitespace and SHRINK raw text
            // (decomposed accents compose: e + U+0301 → é, up to several raw
            // marks per folded char), so a window of 4× needle length is the
            // safe upper bound on "does the text here start with needle?"
            var windowLen = Math.Min(needle.Length * 4, fullText.Length - i);
            var normWindow = NormalizeText(fullText.Substring(i, windowLen));

            if (normWindow.StartsWith(needle, comparison))
            {
                // Expand one original character at a time until the
                // normalized prefix equals the needle — that's the minimum
                // letter span covering the match.
                int endIndex = i;
                while (endIndex < fullText.Length)
                {
                    var cur = NormalizeText(fullText.Substring(i, endIndex - i + 1));
                    if (cur.Equals(needle, comparison)) break;
                    if (cur.Length >= needle.Length) break;
                    endIndex++;
                }

                // Absorb trailing raw combining marks: the last matched
                // letter's canonical cluster may continue past the minimal
                // span (needle "café" against raw "cafe" + U+0301 — the
                // expansion stops at the 'e' when the raw length reaches the
                // needle length, but the accent belongs to the matched
                // cluster and must be removed with it).
                while (endIndex + 1 < fullText.Length &&
                       MatchingNormalization.IsCombiningMark(fullText[endIndex + 1]))
                {
                    endIndex++;
                }

                if (endIndex >= i && endIndex < characterToLetter.Count)
                {
                    var firstLetter = characterToLetter[i];
                    var lastLetter = characterToLetter[endIndex];

                    // Expand back to EVERY original letter the matched view
                    // covers, so all overprinted copies are removed. Removing
                    // only the representative would leave the other stamps
                    // drawn and extractable — a redaction that looks done.
                    var from = spanStart[firstLetter];
                    var to = spanEnd[lastLetter];
                    var slice = new List<Letter>(to - from + 1);
                    for (var letterIndex = from; letterIndex <= to; letterIndex++)
                        slice.Add(letters[letterIndex]);
                    if (IsSpatiallyCoherent(slice))
                    {
                        matches.Add(slice);
                        i = endIndex + 1;
                        continue;
                    }
                }
            }

            i++;
        }

        return matches;
    }

    /// <summary>
    /// Collapse OVERPRINTED glyphs — runs of adjacent letters with the same
    /// value stamped on top of one another — into a single representative,
    /// returning the view plus, for each view index, the first and last
    /// original letter indices it stands for (#1047).
    /// </summary>
    /// <remarks>
    /// <para>Faux-bold and drop-shadow effects are produced by drawing the same
    /// text several times at sub-point offsets. The letter model records every
    /// stamp, correctly — but the matcher reads the letters in order, so a
    /// 4x-stamped line reads <c>TTTTeeeesssstttt</c> and no search for
    /// <c>test</c> can match it. The term survives and RedactText reports
    /// success, which is the failure mode CLAUDE.md's Limitations #1 describes:
    /// excise cannot redact what excise cannot read.</para>
    ///
    /// <para>The discriminator is geometric, not textual, because a genuine
    /// double letter must NOT collapse. In <c>letter</c> the two <c>t</c>s sit a
    /// full glyph-width apart; overprinted copies sit on top of each other. So
    /// two same-valued neighbours merge only when their glyph rectangles are
    /// nearly coincident — measured against glyph SIZE, so it holds at any
    /// scale.</para>
    ///
    /// <para>Collapsing is only ever a MATCHING view. Every original letter is
    /// restored before removal, so all stamps are deleted; keeping one would
    /// leave the text drawn and extractable.</para>
    /// </remarks>
    internal static (List<Letter> View, List<int> SpanStart, List<int> SpanEnd)
        CollapseOverprintedGlyphs(IReadOnlyList<Letter> letters)
    {
        var view = new List<Letter>(letters.Count);
        var spanStart = new List<int>(letters.Count);
        var spanEnd = new List<int>(letters.Count);

        for (var i = 0; i < letters.Count; i++)
        {
            view.Add(letters[i]);
            spanStart.Add(i);

            var last = i;
            while (last + 1 < letters.Count && IsOverprintOf(letters[last], letters[last + 1]))
                last++;

            spanEnd.Add(last);
            i = last;
        }

        return (view, spanStart, spanEnd);
    }

    /// <summary>
    /// Whether <paramref name="b"/> is the same glyph as <paramref name="a"/>
    /// stamped essentially on top of it.
    /// </summary>
    private static bool IsOverprintOf(Letter a, Letter b)
    {
        if (!string.Equals(a.Value, b.Value, StringComparison.Ordinal)) return false;
        if (a.Value.Length == 0 || char.IsWhiteSpace(a.Value[0])) return false;

        // Tolerance from glyph size, so it scales with the type. A quarter of a
        // glyph is far below the ~1 advance width separating real neighbours
        // and far above the sub-point offsets faux-bold uses (observed: 0.2pt
        // horizontal and 0.4pt vertical on a 10pt glyph).
        var w = Math.Max(Math.Abs(a.Width), 0.01);
        var h = Math.Max(Math.Abs(a.GlyphRectangle.Height), 0.01);
        var tolX = w * 0.25;
        var tolY = h * 0.25;

        return Math.Abs(a.StartX - b.StartX) <= tolX
            && Math.Abs(a.StartY - b.StartY) <= tolY;
    }

    /// <summary>
    /// Reject text created only by concatenating distant reading-order runs.
    /// Reconstruction can reorder runs in the extracted sequence, and an
    /// iterative redaction pass must not combine "You" in one column with an
    /// unrelated "r" on another line into a synthetic "your" (#942).
    /// Whitespace boundaries are allowed to jump so wrapped phrase searches
    /// retain their existing behavior.
    /// </summary>
    private static bool IsSpatiallyCoherent(IReadOnlyList<Letter> letters)
    {
        for (var i = 1; i < letters.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(letters[i - 1].Value) ||
                string.IsNullOrWhiteSpace(letters[i].Value))
                continue;

            var a = letters[i - 1].GlyphRectangle.Normalize();
            var b = letters[i].GlyphRectangle.Normalize();
            var dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
            var dy = Math.Max(0, Math.Max(a.Bottom - b.Top, b.Bottom - a.Top));
            var scale = Math.Max(1, Math.Max(
                Math.Max(a.Width, a.Height),
                Math.Max(b.Width, b.Height)));

            if (Math.Sqrt(dx * dx + dy * dy) > scale * 2)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Normalize typographic variants (curly quotes, en/em dashes), fold
    /// Arabic presentation forms to base letters, and collapse whitespace so
    /// that string comparison isn't defeated by inconsequential differences
    /// between the search term and the text as encoded in the PDF.
    /// </summary>
    private static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Arabic can be stored as shaped presentation forms (U+FB50–U+FDFF,
        // U+FE70–U+FEFF — #632), Latin text as ligature code points
        // (U+FB00–U+FB06, e.g. "oﬃce" — #722), and accented text in either
        // canonical spelling ("café" vs "cafe" + U+0301 — #724) while the
        // user types plain/precomposed letters; fold both sides of the
        // comparison into the canonical matching space. Note the fold can
        // change length in BOTH directions (lam-alef 1 char → 2, ﬃ 1 → 3
        // expand; e + U+0301 → é shrinks 2 → 1), so normalized length may
        // differ from raw length either way — FindTextMatches accounts for
        // that.
        var normalized = MatchingNormalization.Fold(text)
            .Replace('’', '\'')  // right single quote
            .Replace('‘', '\'')  // left single quote
            .Replace('ʼ', '\'')  // modifier letter apostrophe
            .Replace('′', '\'')  // prime
            .Replace('–', '-')   // en dash
            .Replace('—', '-')   // em dash
            .Replace('−', '-');  // minus sign

        return Regex.Replace(normalized, @"\s+", " ");
    }
}
