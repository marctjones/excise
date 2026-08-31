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
    /// <param name="drawBlackRect">Whether to append a visual covering rectangle overlay.</param>
    /// <param name="boxColor">Fill color (RGB components 0..1) of the covering rectangle when
    /// <paramref name="drawBlackRect"/> is true. Null (the default) draws black. The box is
    /// cosmetic — glyph removal is unconditional (#1158).</param>
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
    /// <summary>
    /// Redact <paramref name="text"/> under a unified <see cref="RedactionOptions"/>
    /// surface (#1187). Equivalent to the parameter overload; this is the
    /// recommended entry point. The per-parameter overload is kept for source
    /// compatibility and delegates here in spirit (it constructs the same call).
    /// </summary>
    public static RedactionReport RedactText(
        this PdfDocument document,
        string text,
        RedactionOptions options,
        Action<int, int>? progress = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        return document.RedactText(
            text,
            options.CaseSensitive,
            options.Strategy,
            options.DrawBox,
            options.IncludeHiddenLayers,
            options.ScrubDocumentCarriers,
            options.CloseWidth,
            options.BoxColor,
            options.Carriers,
            progress);
    }

    public static RedactionReport RedactText(
        this PdfDocument document,
        string text,
        bool caseSensitive = false,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap,
        bool drawBlackRect = true,
        bool includeHiddenLayers = true,
        bool scrubDocumentCarriers = true,
        bool closeWidth = false,   // #1145 — opt-in width-closing (destroys the residue channel)
        (double R, double G, double B)? boxColor = null,   // #1158 — covering-box fill, RGB 0..1; null = black
        Excise.Core.Operations.RedactionCarriers carriers
            = Excise.Core.Operations.RedactionCarriers.All,  // #1188 — per-carrier scrub scope
        Action<int, int>? progress = null)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        var pageResults = new List<PageRedactionResult>();
        var carrierResults = new List<CarrierResult>();
        var imageCounts = default(ImageRedactionCounts);   // #1187/#1195 surfacing

        if (string.IsNullOrEmpty(text))
            return new RedactionReport
            {
                Term = text ?? "",
                Pages = pageResults,
                Carriers = carrierResults,
            };

        int totalMatches = 0;

        var pageCount = document.PageCount;
        progress?.Invoke(0, pageCount);
        for (int pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            var page = document.GetPage(pageNum);
            var pageLocated = 0;
            // #1101: the window this page actually shows. Letters stays
            // unclipped so REMOVAL keeps full reach into off-page content (a
            // string in the content stream is extractable and therefore a leak,
            // even where this page's window does not show it) — but the COUNT a
            // user acts on must be what a reader sees, not the full shared
            // canvas. On a tiled document (issue1350.pdf: three pages are
            // byte-identical copies of one canvas cropped to three different
            // MediaBox windows) the unclipped tally counted the same canvas once
            // per page and reported 36 for a term mutool — and a human paging
            // through — sees 9 times.
            var cropWindow = page.CropBox.Normalize();
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
                    // #1195: the image pass needs the full glyph bbox (real
                    // height), not the thin glyph-match centreline in
                    // contentAreas — else region blackout zeroes a 1-sample strip
                    // and the term stays readable in a scanned image.
                    var imageAreas = new List<PdfRectangle>();
                    var markerAreas = new List<PdfRectangle>();
                    var pageVisibleMatches = 0;
                    // #1101: centers of matches already tallied on this pass, so
                    // an OVERPRINT — the identical run drawn twice at the same
                    // position (issue1350.pdf draws "…your ID" at 252.0 76.976 Td
                    // twice, faux-bold) — is counted once. This is distinct from
                    // the cross-page tiling the crop window handles, and from
                    // issue14297's genuine tiled copies, which sit at DIFFERENT
                    // positions and stay separate. Two different visible words
                    // can never share a position, so coincidence ⟺ overprint.
                    var countedCenters = new List<(double X, double Y)>();
                    foreach (var matchLetters in matches)
                    {
                        var bbox = BoundingBoxOf(matchLetters);

                        // #1101: tally only matches VISIBLE in this page's
                        // window; removal below is unconditional. Center-in-box,
                        // not full containment — robust to the horizontal
                        // advance-width drift (#90) that an edge test would trip
                        // on, and enough to separate the well-gapped tiled
                        // windows here. A match off this window is still shown
                        // (and counted) on whichever page's window does show it.
                        var cx = (bbox.Left + bbox.Right) / 2.0;
                        var cy = (bbox.Bottom + bbox.Top) / 2.0;
                        if (cx >= cropWindow.Left && cx <= cropWindow.Right &&
                            cy >= cropWindow.Bottom && cy <= cropWindow.Top)
                        {
                            // #1101: count coincident boxes once (overprint).
                            // Tolerance is 2 pt — overprints are pixel-exact
                            // (same Td), while distinct occurrences of one term
                            // are line-height or word-width apart. Removal below
                            // is unconditional regardless of this tally.
                            const double overprintTol = 2.0;
                            var overprint = countedCenters.Any(c =>
                                Math.Abs(c.X - cx) <= overprintTol &&
                                Math.Abs(c.Y - cy) <= overprintTol);
                            if (!overprint)
                            {
                                countedCenters.Add((cx, cy));
                                pageVisibleMatches++;
                            }
                        }

                        if (IsInteractiveOnlyMatch(matchLetters))
                            // TERM-aware (#1038). The area-only form deletes the
                            // whole field value; on issue18036.pdf that was 545
                            // of 568 characters to remove one word.
                            InteractiveRedactionScrubber.ScrubTerm(
                                page, bbox, text, caseSensitive);
                        else
                        {
                            contentAreas.Add(strategy == GlyphRemovalStrategy.FullyContained
                                ? bbox
                                : CenterlineBoxOf(matchLetters));
                            imageAreas.Add(bbox); // full height for the image pass (#1195)
                        }
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
                        imageCounts += page.RedactAreasInternal(contentAreas, imageAreas, strategy, scrubDocumentCarriers: false, closeWidth: closeWidth);
                    }

                    // A box whose width equals the removed run is itself a
                    // width-residue oracle (#1140). Width-closing therefore
                    // cannot draw one: its contract is to destroy that channel,
                    // accepting the visual/layout trade-off explicitly chosen by
                    // the caller.
                    if (drawBlackRect && !closeWidth)
                        foreach (var bbox in markerAreas) AppendBlackRectangle(page, bbox, boxColor);

                    // #1101: count what this page's window shows, not the full
                    // shared canvas. Removal above already took every match.
                    totalMatches += pageVisibleMatches;
                    pageLocated += pageVisibleMatches;
                }
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
            progress?.Invoke(pageNum, pageCount);
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
                foreach (var (carrier, _) in DocumentCarriers)
                    carrierResults.Add(new CarrierResult(carrier, false,
                        $"term is {text.Length} characters; the carrier scrub floor is {minTermLength}"));
            }
            else
            {
                Excise.Core.Operations.PdfDocumentSanitizer.ScrubTerms(
                    document, new[] { text }, caseSensitive, carriers);
                foreach (var (carrier, flag) in DocumentCarriers)
                    carrierResults.Add((carriers & flag) != 0
                        ? new CarrierResult(carrier, true, null)
                        : new CarrierResult(carrier, false,
                            "carrier disabled via RedactionOptions.Carriers (#1188)"));
            }
        }
        else
        {
            foreach (var (carrier, _) in DocumentCarriers)
                carrierResults.Add(new CarrierResult(carrier, false,
                    "scrubDocumentCarriers: false was requested by the caller"));
        }

        return new RedactionReport
        {
            Term = text,
            Pages = pageResults,
            Carriers = carrierResults,
            ImageRegionsRedacted = imageCounts.RegionEdited,
            ImagesDroppedWhole = imageCounts.RemovedWhole,
        };
    }

    /// <summary>The document-level carriers the term is scrubbed from and
    /// reported on: #608's set (/Info, XMP, outline titles, annotation
    /// /Contents) plus link-action URIs (#1155).</summary>
    // The document-level carriers RedactText REPORTS on, each mapped to its
    // #1188 scope flag. (A representative subset — ScrubTerms scrubs more; this
    // is what the report names.)
    private static readonly (string Name, Excise.Core.Operations.RedactionCarriers Flag)[] DocumentCarriers =
    {
        ("/Info", Excise.Core.Operations.RedactionCarriers.Info),
        ("XMP /Metadata", Excise.Core.Operations.RedactionCarriers.Xmp),
        ("/Outlines titles", Excise.Core.Operations.RedactionCarriers.Outlines),
        ("annotation /Contents", Excise.Core.Operations.RedactionCarriers.Annotations),
        ("link /A /URI", Excise.Core.Operations.RedactionCarriers.ActionUris),
    };

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
    internal static PdfRectangle BoundingBoxOf(IReadOnlyList<Letter> letters)
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
    private static void AppendBlackRectangle(PdfPage page, PdfRectangle rect, (double R, double G, double B)? boxColor = null)
    {
        var (r, g, b) = boxColor ?? (0.0, 0.0, 0.0);
        var content = page.GetContentStream();
        var ops = content.Operators.ToList();
        ops.Add(ContentOperator.SaveState());
        ops.Add(ContentOperator.SetFillRgb(r, g, b));
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

        // #1177: insert an INFERRED word space where a horizontal gap separates
        // two same-line glyphs, exactly as JoinText does. Without it the search
        // runs over the SPACELESS glyph concatenation, so "your software" reads as
        // "yoursoftware" and a search for "yours" matches across the word boundary
        // — foss-primer reported 29 "yours" (your+software/server/self) where the
        // page shows 7. The inferred space maps to the PREVIOUS letter (it adds no
        // real letter), so a match's removed slice is unchanged; a needle without
        // that space simply cannot span the gap, and a multi-word needle now CAN
        // match a space-glyph-less PDF.
        // #1177: median left-to-right advance over SAME-LINE adjacent glyphs, so a
        // word gap is judged RELATIVE to the document's own spacing (JoinText's
        // WordGapAdvanceFactor rule). An absolute font-size fraction misfires on
        // uniformly loose spacing (a per-glyph-Tm fixture at 7pt pitch reads every
        // narrow glyph's trailing gap as a space); the relative rule does not.
        var advances = new List<double>();
        for (var k = 1; k < view.Count; k++)
        {
            if (string.IsNullOrWhiteSpace(view[k - 1].Value) || string.IsNullOrWhiteSpace(view[k].Value))
                continue;
            var pa = view[k - 1].GlyphRectangle.Normalize();
            var ca = view[k].GlyphRectangle.Normalize();
            if (Math.Abs((pa.Bottom + pa.Top) / 2 - (ca.Bottom + ca.Top) / 2)
                > 0.5 * Math.Max(view[k - 1].FontSize, view[k].FontSize)) continue;   // same line only
            var adv = ca.Left - pa.Left;
            if (adv > 0) advances.Add(adv);
        }
        var medianAdvance = MedianAdvance(advances);

        var sb = new StringBuilder(view.Count);
        var characterToLetter = new List<int>(view.Count);
        for (var letterIndex = 0; letterIndex < view.Count; letterIndex++)
        {
            if (letterIndex > 0 && IsInferredWordGap(view[letterIndex - 1], view[letterIndex], medianAdvance))
            {
                sb.Append(' ');
                characterToLetter.Add(letterIndex - 1);
            }
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
    /// <summary>
    /// #1177: a horizontal word gap between two SAME-LINE glyphs — the boundary
    /// JoinText inserts a space at (§ ~0.25em, matching poppler). Neither glyph is
    /// already whitespace (a real space glyph separates on its own). A vertical gap
    /// (line wrap) is NOT a word gap: a term wrapped across a line must still match.
    /// </summary>
    private static bool IsInferredWordGap(Letter prev, Letter cur, double medianAdvance)
    {
        if (string.IsNullOrWhiteSpace(prev.Value) || string.IsNullOrWhiteSpace(cur.Value))
            return false;
        var a = prev.GlyphRectangle.Normalize();
        var b = cur.GlyphRectangle.Normalize();
        var fontSize = Math.Max(prev.FontSize, cur.FontSize);
        if (fontSize <= 0) return false;
        // Same line only — a line wrap is not a word gap (a wrapped term must match).
        if (Math.Abs((a.Bottom + a.Top) / 2 - (b.Bottom + b.Top) / 2) > 0.5 * fontSize) return false;
        // Must be a real forward gap (overlapping/overprinted stamps are never a gap).
        if (b.Left <= a.Right) return false;
        // A font's glyph bounds do not tile perfectly: normal adjacent glyphs
        // can have a sub-point gap (canvas.pdf's "e" → "s" is 0.1pt).  The
        // left-to-left advance is naturally wider after a wide glyph, so it
        // cannot by itself prove a word boundary.  Require meaningful blank
        // space between the painted bounds before applying the relative
        // advance rule (#1198).
        // FontSize is not a dependable scale here: a text matrix can make it
        // report 1 while the painted glyph is several points wide. Require a
        // gap that is material relative to the preceding painted glyph too.
        // This preserves a word split across text operators with a small
        // positioning adjustment (freeculture.pdf's visible "th" + "at")
        // without allowing a genuine word-sized gap to concatenate words.
        var minimumBlank = Math.Max(0.25 * fontSize, 0.5 * Math.Abs(a.Width));
        if (b.Left - a.Right <= minimumBlank) return false;
        // Relative to the line's own advance. Keep this aligned with
        // JoinText's WordGapAdvanceFactor (1.5): the source can split one
        // visible word across text-showing operators and apply a modest
        // positioning adjustment at that split (freeculture.pdf's "th" +
        // "at"). A lower threshold invents a space there, so a term that
        // mutool correctly reads as "that" becomes unmatchable (#1198).
        // fall back to a font-size fraction only when no median is available.
        var advance = b.Left - a.Left;
        return medianAdvance > 0
            ? advance > medianAdvance * 1.5
            : b.Left - a.Right > 0.25 * fontSize;
    }

    private static double MedianAdvance(List<double> advances)
    {
        if (advances.Count == 0) return 0;
        advances.Sort();
        return advances[advances.Count / 2];
    }

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
