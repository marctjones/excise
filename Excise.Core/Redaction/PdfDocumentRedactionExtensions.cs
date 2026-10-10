using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

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
/// <see cref="RedactText(PdfDocument, string, RedactionOptions, Action{int, int})"/>.
/// </para>
/// <para>
/// A black rectangle overlay is appended to each page's content stream
/// for visual confirmation. The overlay is purely cosmetic — the
/// <em>security</em> guarantee comes from the content-stream rewrite in
/// <see cref="PdfPageRedactionExtensions.RedactArea"/>, which deletes
/// the glyphs themselves. Callers that want pure structural removal with
/// no visual marker can set <see cref="RedactionOptions.DrawBox"/> to false.
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
    ///   <item><see cref="PdfPageRedactionExtensions.RedactArea(PdfPage, PdfRectangle, RedactionOptions)"/>
    ///     has no term to scrub and therefore does none of this. An area
    ///     redaction still needs its removed text collected and scrubbed
    ///     separately.</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="document">The PDF document to redact.</param>
    /// <param name="text">The text to redact.</param>
    /// <param name="options">What to match and what to remove (#1187). Attachments are
    /// removed unless <see cref="RedactionOptions.KeepAttachments"/> is set (#1572).</param>
    /// <param name="progress">Called with (pages done, page count) as the pages are visited.</param>
    /// <returns>What was located, removed and verified, per page and per carrier.</returns>
    public static RedactionReport RedactText(
        this PdfDocument document,
        string text,
        RedactionOptions options,
        Action<int, int>? progress = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        return RedactTextCore(document, text, options, progress, depth: 0);
    }

    private static RedactionReport RedactTextCore(
        PdfDocument document,
        string text,
        RedactionOptions options,
        Action<int, int>? progress,
        int depth)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        var pageResults = new List<PageRedactionResult>();
        var hyphenCandidates = new List<HyphenatedTermCandidate>();
        var wordWrapCandidates = new List<WordWrapTermCandidate>();
        var carrierResults = new List<CarrierResult>();
        var imageCounts = default(ImageRedactionCounts);   // #1187/#1195 surfacing
        var undecodableForms = new List<(Excise.Core.Primitives.PdfStream Form, int Page)>();   // #1863
        var nestedTextCarriers = new List<(Excise.Core.Text.NestedTextCarrier Carrier, int Page)>();

        if (string.IsNullOrEmpty(text))
            return new RedactionReport
            {
                Term = text ?? "",
                Pages = pageResults,
                Carriers = carrierResults,
                WholeWord = options.WholeWord,
                Profile = options.Profile,
            };

        int totalMatches = 0;

        // #1572: attachments. Everything that can refuse runs first, so a
        // refused redaction leaves the document exactly as it was: a portfolio
        // (whose attachments ARE the documents) when they would be removed,
        // and a nested PDF that cannot be redacted when they are kept.
        List<(Excise.Core.Document.PdfAttachmentGraph.Found File, Excise.Core.Document.AttachmentRedactionResult Result)>? keptAttachments = null;
        IReadOnlyList<Excise.Core.Document.AttachmentRedactionResult> removedAttachments =
            Array.Empty<Excise.Core.Document.AttachmentRedactionResult>();
        if (options.KeepAttachments)
        {
            // A nested PDF is redacted with exactly these options, its own
            // attachments included.
            keptAttachments = AttachmentCarrierScrubber.RedactKept(
                document, new[] { text }, options.CaseSensitive, options.WholeWord, depth,
                (nested, term) => RedactTextCore(nested, term, options, null, depth + 1));
        }
        else
        {
            Excise.Core.Document.PdfAttachmentGraph.ThrowIfPortfolio(document);
        }

        // #1547/#1574: an XFA packet restates the form — every page of a form
        // excise laid out, every field value of a static form — and XFA viewers
        // put it back on the page. It goes before anything else, whatever the
        // carrier scope: a surviving packet would undo this redaction in the
        // next viewer. #2037 (decision 17): first, the AcroForm widgets excise
        // generated for the form are stamped into their pages and removed, so a
        // hidden or duplicate widget keeps no copy of a value, and the stamped
        // glyphs are page content the page loop below locates and removes.
        foreach (var xfaRow in Excise.Core.Xfa.PdfXfaLayout.RemoveXfaFormForRedaction(document))
            carrierResults.Add(new CarrierResult(xfaRow, true, null));

        if (!options.KeepAttachments)
        {
            // #1572, decided 2026-09-17: redacted output carries no attachments.
            removedAttachments = Excise.Core.Document.PdfAttachmentGraph.RemoveAll(document);
            if (removedAttachments.Count > 0)
                document.RedactionLedger.RecordRemovedAttachments(removedAttachments);
        }

        // #1586: the output-profile removals. Before the page loop, so hidden
        // optional-content the profile deletes is not also walked for glyph
        // removal, and before the carrier term-scrub below, so a carrier this
        // deletes outright is not reported as having been scrubbed by term.
        var profileRemovals = new List<RedactedFeatureRemoval>();
        if (RedactionFeatureStripper.ApplyMetadataStrip(document, options) is { } metadataRow)
            profileRemovals.Add(metadataRow);
        profileRemovals.AddRange(RedactionFeatureStripper.Apply(document, options, carrierResults));

        var pageCount = document.PageCount;
        progress?.Invoke(0, pageCount);
        // #2041: which appearance streams this redaction has rewritten, across pages and passes.
        var termScrubState = new InteractiveRedactionScrubber.TermScrubState();
        for (int pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            var page = document.GetPage(pageNum);
            var pageLocated = 0;
            // Pattern cells, soft-mask groups and Type3 glyph procedures this
            // page draws BEFORE anything is removed: a removal can stop the
            // page drawing one while the saved file still holds it.
            _ = page.Letters;
            nestedTextCarriers.AddRange(page.NestedTextCarriers.Select(c => (c, pageNum)));
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
                var searchLetters = options.IncludeHiddenLayers
                    ? letters
                    : letters.Where(l => !l.IsInHiddenOptionalContent).ToList();

                if (searchLetters.Count == 0) break;

                var searchTextSnapshot = string.Concat(searchLetters.Select(l => l.Value));
                var matches = FindTextMatchLines(searchLetters, text, options.CaseSensitive, options.WholeWord);
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
                    var contentAreas = new List<GlyphArea>();
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
                    // #2041: the interactive matches of this pass, scrubbed
                    // together after the loop (one scrub per pass, so a field
                    // holding the term several times is rewritten once).
                    var interactiveAreas = new List<PdfRectangle>();
                    var drawingWidgets = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                    var unattributedAreas = new List<PdfRectangle>();
                    foreach (var (matchLetters, matchLines) in matches)
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

                        var interactiveOnly = IsInteractiveOnlyMatch(matchLetters);
                        if (interactiveOnly)
                        {
                            // TERM-aware (#1038). The area-only form deletes the
                            // whole field value; on issue18036.pdf that was 545
                            // of 568 characters to remove one word. #2041: a
                            // glyph a widget's appearance drew selects that
                            // widget's field by identity, wherever it landed;
                            // only letters no widget drew select by rectangle.
                            interactiveAreas.Add(bbox);
                            var unattributed = new List<Letter>();
                            foreach (var letter in matchLetters)
                            {
                                if (letter.SourceWidget is { } widget)
                                    drawingWidgets.Add(widget);
                                else
                                    unattributed.Add(letter);
                            }
                            if (unattributed.Count > 0)
                                unattributedAreas.Add(BoundingBoxOf(unattributed));
                        }

                        // #1791: one set of boxes per LINE of the match. A match
                        // that wraps spans two lines, and one box around it covers
                        // everything between them (#942).
                        foreach (var line in matchLines)
                        {
                            var lineBox = BoundingBoxOf(line);
                            if (!interactiveOnly)
                            {
                                contentAreas.Add(GlyphAreaOf(line, lineBox, options.Strategy));
                                imageAreas.Add(lineBox); // full height for the image pass (#1195)
                            }
                            // #1189: under the overshoot policy the covering box is
                            // widened out toward the surviving neighbours, so its
                            // width stops being a ruler for the removed string.
                            // #1755: under FixedMarker the box is a FIXED size —
                            // never derived from bbox's own width at all, unlike
                            // overshoot which still rounds UP from it.
                            markerAreas.Add(options.FixedMarker
                                ? FixedMarkerBoxFor(lineBox, line)
                                : options.Width == WidthPolicy.OvershootPreserveLayout
                                    ? OvershootBoxFor(lineBox, line, searchLetters, cropWindow)
                                    : lineBox);
                        }
                    }

                    if (interactiveAreas.Count > 0)
                        InteractiveRedactionScrubber.ScrubTerm(
                            page, interactiveAreas, drawingWidgets, unattributedAreas,
                            text, options.CaseSensitive, options.WholeWord, termScrubState);

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
                        imageCounts += page.RedactAreasInternal(contentAreas, imageAreas, options.Strategy, scrubDocumentCarriers: false, width: options.Width, removeAttachments: false,
                            removeWordDecorations: options.CloseWidth);   // #1753
                    }

                    // A box whose width equals the removed run is itself a
                    // width-residue oracle (#1140). Plain width-closing
                    // (CloseGap) therefore draws none at all (#1725: the
                    // resulting redaction has no visible mark). FixedMarker is
                    // the exception: its box is ALREADY content-independent —
                    // exactly the property this guard exists to protect — so it
                    // draws unconditionally, answering both #1715 and #1725
                    // instead of trading one for the other (#1755).
                    if (options.DrawBox && (options.FixedMarker || !options.CloseWidth))
                        foreach (var bbox in markerAreas) AppendBlackRectangle(page, bbox, options.BoxColor);

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
            // #1372/#1750/#1791: an occurrence split across a line break the
            // matcher does not join — a line-end hyphen, or a continuation that
            // is not the next line of the same block. Detected after removal so
            // it describes what is still in the output, and reported.
            FindWrappedCandidates(page.Letters, text, options.CaseSensitive, options.WholeWord, pageNum,
                hyphenCandidates, wordWrapCandidates);

            var remaining = CountOccurrences(page, text, options.CaseSensitive, options.IncludeHiddenLayers, options.WholeWord);
            undecodableForms.AddRange(page.UndecodableForms.Select(form => (form, pageNum)));
            nestedTextCarriers.AddRange(page.NestedTextCarriers.Select(c => (c, pageNum)));
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
        if (options.ScrubDocumentCarriers)
        {
            // #999: the scrubber ignores terms shorter than 3 characters. That
            // is deliberate -- excising 1-2 character fragments from every
            // metadata string would corrupt unrelated values -- but the old int
            // return could not SAY it, so a caller redacting "Ro" got a success
            // count while /Info, XMP, outlines and annotation /Contents kept the
            // term. Reported now, per the decided policy: surface, don't guess.
            const int minTermLength = 3;
            // #1586: a sub-floor term is no longer a blanket skip. The floor is
            // a STRIP rule — excising "of" from every /Alt corrupts unrelated
            // values — and it does not apply to a carrier the caller set to
            // RemoveWhole, which drops the value instead of cutting a fragment
            // out of it. Skipping the whole pass meant Maximum, whose promise is
            // that no carrier keeps the term, silently kept a 2-character one.
            // ScrubTerms itself reports the floor per carrier now.
            var anyRemoveWhole = Excise.Core.Operations.CarrierScrubPolicy.AllCarriers.Any(
                c => (options.Carriers & c) != 0
                     && options.CarrierPolicy.ModeFor(c) == Excise.Core.Operations.CarrierScrubMode.RemoveWhole);
            if (text.Length < minTermLength && !anyRemoveWhole)
            {
                foreach (var (carrier, _) in DocumentCarriers)
                    carrierResults.Add(new CarrierResult(carrier, false,
                        $"term is {text.Length} characters; the carrier scrub floor is {minTermLength}"));
            }
            else
            {
                var policy = options.CarrierPolicy;
                var outcome = Excise.Core.Operations.PdfDocumentSanitizer.ScrubTerms(
                    document, new[] { text }, options.CaseSensitive, options.Carriers, policy, options.WholeWord);

                // #1188/#1169: the report says WHICH POLICY RAN on each carrier,
                // not just "scrubbed". A ReportOnly carrier still holds the term
                // — reporting it as scrubbed would be the "reported success
                // anyway" failure this report type exists to end.
                foreach (var (carrier, flag) in DocumentCarriers)
                {
                    if ((options.Carriers & flag) == 0)
                    {
                        carrierResults.Add(new CarrierResult(carrier, false,
                            "carrier disabled via RedactionOptions.Carriers (#1188)"));
                        continue;
                    }

                    var row = outcome.For(flag);
                    if (row?.RefusedReason != null)
                    {
                        carrierResults.Add(new CarrierResult(carrier, false, row.RefusedReason));
                        continue;
                    }

                    var mode = row?.Mode ?? Excise.Core.Operations.CarrierScrubMode.Strip;
                    if (mode == Excise.Core.Operations.CarrierScrubMode.ReportOnly)
                    {
                        // A ReportOnly carrier that does NOT hold the term is a
                        // clean outcome and must carry no RefusedReason:
                        // IsCleanSuccess keys off that field, so flagging it
                        // would report a leak-free run as unclean and train the
                        // user to ignore the one field that matters.
                        carrierResults.Add(new CarrierResult(carrier, false,
                            row is { TermFound: true }
                                ? "ReportOnly (#1169): this carrier HOLDS THE TERM and was deliberately left unchanged"
                                : null));
                        continue;
                    }

                    carrierResults.Add(new CarrierResult(carrier, true, null));
                }

                // #1188: a carrier this report does not name individually can
                // still REFUSE a requested mode, or hold the term under
                // ReportOnly. Dropping those rows because the carrier is absent
                // from the summary list is exactly the silent skip the carrier
                // policy exists to prevent — append whatever needs attention.
                var named = DocumentCarriers.Aggregate(
                    Excise.Core.Operations.RedactionCarriers.None,
                    (acc, c) => acc | c.Flag);
                foreach (var extra in outcome.NeedingAttention)
                {
                    if ((extra.Carrier & named) != 0) continue;
                    carrierResults.Add(new CarrierResult(
                        $"/{extra.Carrier}",
                        false,
                        extra.RefusedReason
                            ?? $"{extra.Mode} (#1169): this carrier HOLDS THE TERM and was deliberately left unchanged"));
                }
            }
        }
        else
        {
            foreach (var (carrier, _) in DocumentCarriers)
                carrierResults.Add(new CarrierResult(carrier, false,
                    "scrubDocumentCarriers: false was requested by the caller"));
        }

        // #1493: an image region-redacted (or dropped) on one page is replaced on
        // THAT page only. Every page that still references the same image
        // object keeps the original pixels, including the redacted area's, and
        // the writer saves them. Reported per the carrier policy rather than
        // silently redacted on pages the caller did not ask about. Runs after
        // every page, so a page this call also redacted is not named.
        carrierResults.AddRange(SharedImageCarrierResults(document, imageCounts.TouchedImages));
        carrierResults.AddRange(UndecodableFormResults(undecodableForms));
        // The term inside a tiling-pattern cell, soft-mask group or Type3
        // glyph procedure: cut out of that stream in its own space, exactly as
        // a widget appearance's is (#2041), or reported when it cannot be.
        var rewroteNested = false;
        if (nestedTextCarriers.Count > 0)
            carrierResults.AddRange(NestedTextCarrierResults(nestedTextCarriers, text, options.CaseSensitive,
                document.ComputeReachableObjects(),
                rewrite: (carrier, pageNum) =>
                {
                    if (AppearanceStreamRedactor.RewrittenContent(document.GetPage(pageNum), carrier.Stream,
                            carrier.Resources, text, options.CaseSensitive, options.WholeWord) is not { } content)
                        return false;
                    carrier.Stream.DecodedData = content;
                    rewroteNested = true;
                    return true;
                }));
        // A page's letters do not hold the carrier's text, but its record of
        // what each carrier draws does: the next walk must read the rewrite.
        if (rewroteNested)
            foreach (var pageNum in nestedTextCarriers.Select(d => d.Page).Distinct())
                document.GetPage(pageNum).InvalidateTextExtractionCache();

        // #1599: a NAMED marked-content property list (/Span /P1 BDC) this
        // redaction could not scrub because a span that SURVIVES it still
        // references the same dictionary — scrubbing it would have corrupted
        // that surviving span's /ActualText, the over-removal #1182 deferred
        // this case over. Reported rather than silently left behind: the
        // report must not claim clean over a carrier the engine refused to
        // touch (rule 6).
        if (imageCounts.UnscrubbedSharedMarkedContentCarriers is { Count: > 0 } sharedNames)
        {
            foreach (var name in sharedNames)
            {
                carrierResults.Add(new CarrierResult(
                    $"marked-content /Properties /{name}",
                    false,
                    "a shared named property list (#1599): another span not covered by this " +
                    "redaction still references it, so its /ActualText/Alt/E was left in place " +
                    "to avoid corrupting that span"));
            }
        }

        // #1572: a kept attachment the term-based carrier scrub removed after
        // all (its name or description held the term) is reported as removed.
        // #1753: an underline, box or highlight sized to a removed word went with it.
        if (imageCounts.DecorationsRemoved > 0)
            profileRemovals.Add(new RedactedFeatureRemoval(
                "underline, box or highlight sized to a redacted word", imageCounts.DecorationsRemoved,
                "at the old width beside the closed gap it would state the removed word's width"));

        // #2043: a scrubbed field's pressed (/D) or hovered (/R) appearance that
        // could not be rewritten free of the term (its font reads as something
        // else, or it is not one stream) was dropped, not kept and counted clean.
        if (termScrubState.DroppedStateAppearances > 0)
            profileRemovals.Add(new RedactedFeatureRemoval(
                "pressed or hovered appearance of a redacted form field", termScrubState.DroppedStateAppearances,
                "its text could not be rewritten free of the term; the field shows its normal appearance instead"));

        // #2059: a scrubbed field's widget whose normal appearance neither read
        // the term nor could be shown free of it was dropped, not kept.
        if (termScrubState.DroppedUndecidedAppearances > 0)
            profileRemovals.Add(new RedactedFeatureRemoval(
                "appearance of a redacted form field's widget", termScrubState.DroppedUndecidedAppearances,
                "it could not be shown free of the term, or the field formats the value it shows"));

        var attachmentResults = keptAttachments != null
            ? AttachmentCarrierScrubber.Reconcile(document, keptAttachments)
            : removedAttachments.ToList();

        return new RedactionReport
        {
            Attachments = attachmentResults,
            Term = text,
            Pages = pageResults,
            Carriers = carrierResults,
            WholeWord = options.WholeWord,
            ImageRegionsRedacted = imageCounts.RegionEdited,
            ImagesDroppedWhole = imageCounts.RemovedWhole,
            HyphenatedCandidates = hyphenCandidates,
            WordWrapCandidates = wordWrapCandidates,
            Profile = options.Profile,
            Removals = profileRemovals,
            AccessibilityAndInteractivityRemoved =
                RedactionFeatureStripper.DestroysAccessibility(options),
        };
    }

    /// <summary>
    /// #1493: one "not scrubbed" carrier row for each image this call
    /// region-edited or removed that some page still references, naming those
    /// pages.
    /// </summary>
    /// <remarks>
    /// Reads each page's <c>/Resources /XObject</c> entries, not its content
    /// stream: a referenced image is written to the file, and is extractable
    /// with <c>mutool extract</c>, whether or not the page draws it. Images
    /// reached only through a form XObject's or an annotation appearance's own
    /// resources are not examined.
    /// </remarks>
    private static IEnumerable<CarrierResult> SharedImageCarrierResults(
        PdfDocument document,
        IReadOnlyList<Excise.Core.Primitives.PdfStream>? touchedImages)
    {
        if (touchedImages is not { Count: > 0 })
            yield break;

        var touched = new HashSet<Excise.Core.Primitives.PdfStream>(
            touchedImages, ReferenceEqualityComparer.Instance);
        var pagesByImage = new Dictionary<Excise.Core.Primitives.PdfStream, SortedSet<int>>(
            ReferenceEqualityComparer.Instance);
        for (int pageNum = 1; pageNum <= document.PageCount; pageNum++)
        {
            var xobjects = document.GetPage(pageNum).Resources?.ResolveDictionary(document, "XObject");
            if (xobjects == null)
                continue;
            foreach (var (_, value) in xobjects)
            {
                if (document.Resolve(value) is not Excise.Core.Primitives.PdfStream image
                    || !touched.Contains(image))
                    continue;
                if (!pagesByImage.TryGetValue(image, out var pages))
                    pagesByImage[image] = pages = new SortedSet<int>();
                pages.Add(pageNum);
            }
        }

        foreach (var pages in pagesByImage.Values.OrderBy(p => p.Min))
        {
            yield return new CarrierResult(
                $"image XObject still drawn on page(s) {string.Join(", ", pages)}",
                false,
                "an image this redaction region-edited or removed is shared with these page(s), which still " +
                "reference the original, including the redacted area's pixels, and the saved file keeps it; " +
                "redact those pages too (#1493)");
        }
    }

    /// <summary>
    /// #1863: one "not scrubbed" row per form a page draws that excise could
    /// not decode, naming the pages. None of its text reached a letter, so
    /// nothing in it was matched or removed; the form is kept and reported,
    /// never skipped in silence (CLAUDE.md rules 5 and 6).
    /// </summary>
    internal static IEnumerable<CarrierResult> UndecodableFormResults(
        IEnumerable<(Excise.Core.Primitives.PdfStream Form, int Page)> drawn) =>
        drawn.GroupBy(d => d.Form, d => d.Page)
            .Select(g => new CarrierResult(
                $"form XObject {g.Key.ObjectNumber ?? 0} {g.Key.GenerationNumber ?? 0} R on page(s) " +
                string.Join(", ", g.Distinct()),
                false,
                $"its /Filter {string.Join(" ", g.Key.Filters.Select(f => "/" + f))} could not be decoded" +
                (g.Key.DecodeFailureReason is { } why ? $" ({why})" : "") +
                ", so the text it draws was not examined and was left in place"));

    /// <summary>
    /// One "not scrubbed" row per tiling-pattern cell, soft-mask group or
    /// Type3 glyph procedure a page draws that itself draws text. Extraction
    /// does not make that text page letters, so the glyph pass can neither
    /// match nor remove it, and the saved file keeps it. Reported, never left
    /// behind in silence (CLAUDE.md rules 5 and 6).
    /// </summary>
    /// <param name="drawn">The carriers and the pages that draw them.</param>
    /// <param name="term">A term redaction's term: only carriers whose text contains it
    /// (or could not be read) are reported. Null for an area redaction, which
    /// has no term and does not map the carrier's text to page space, so every
    /// carrier with text on the page is reported.</param>
    /// <param name="caseSensitive">The term redaction's case rule.</param>
    /// <param name="reachable">Objects the saved file still holds; a carrier
    /// the redaction removed is not reported. Null keeps every row.</param>
    internal static IEnumerable<CarrierResult> NestedTextCarrierResults(
        IEnumerable<(Excise.Core.Text.NestedTextCarrier Carrier, int Page)> drawn,
        string? term, bool caseSensitive, ISet<int>? reachable,
        Func<Excise.Core.Text.NestedTextCarrier, int, bool>? rewrite = null)
    {
        bool Holds(Excise.Core.Text.NestedTextCarrier c)
        {
            if (c.Text == null || term == null) return true;
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            // Over-report rather than miss: the carrier's spacing is not the page's.
            return c.Text.Contains(term, comparison) ||
                   RemoveWhitespace(c.Text).Contains(RemoveWhitespace(term), comparison);
        }

        return drawn
            .Where(d => Holds(d.Carrier))
            .Where(d => reachable == null || d.Carrier.Stream.ObjectNumber is not { } n || reachable.Contains(n))
            .GroupBy(d => d.Carrier.Stream, d => d)
            .Select(g =>
            {
                var c = g.First().Carrier;
                var label = $"text inside {c.Kind} {c.Stream.ObjectNumber ?? 0} {c.Stream.GenerationNumber ?? 0} R on page(s) " +
                    string.Join(", ", g.Select(d => d.Page).Distinct().OrderBy(p => p));
                if (c.Text != null && rewrite != null && rewrite(c, g.Min(d => d.Page)))
                    return new CarrierResult(label, true, null);
                var what = c.Text == null
                    ? $"{c.Unread ?? "it could not be read"}, so the text it may draw was not examined"
                    : term == null
                        ? "excise does not read or rewrite text drawn inside it, and the text it draws may lie in the redacted area"
                        : "it draws text containing the term, and excise could not rewrite its stream without it";
                return new CarrierResult(label, false, what + "; it was left in place and the saved file keeps it");
            })
            .ToList();

        static string RemoveWhitespace(string s) => new(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
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
        ("marked-content /ActualText, /Alt, /E", Excise.Core.Operations.RedactionCarriers.MarkedContent),
        ("/PageLabels /P", Excise.Core.Operations.RedactionCarriers.PageLabels),
        ("/Names and /Dests keys", Excise.Core.Operations.RedactionCarriers.NameTreeKeys),
        ("signature dictionaries and certificates", Excise.Core.Operations.RedactionCarriers.Signatures),
        ("optional-content names and labels", Excise.Core.Operations.RedactionCarriers.OptionalContent),
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
        PdfPage page, string text, bool caseSensitive, bool includeHiddenLayers,
        bool wholeWord = false)
    {
        try
        {
            var letters = page.Letters;
            var searchLetters = includeHiddenLayers
                ? letters
                : letters.Where(l => !l.IsInHiddenOptionalContent).ToList();
            // #1052: the verification pass MUST use the same match rule as the
            // removal pass. A stricter re-read would report "gone" for a match
            // the removal never made.
            return FindTextMatches(searchLetters, text, caseSensitive, wholeWord).Count;
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
    /// <summary>
    /// #2055: the area one line of a match removes glyphs from. Upright text,
    /// and text a quarter turn turns, is the page-space box as before: the
    /// centreline (#942), or under <see cref="GlyphRemovalStrategy.FullyContained"/>
    /// the line's box. A line turned by any other angle keeps that box for the
    /// carrier passes, and carries the same centreline (or box) in its own line
    /// frame for the glyph decision: around a diagonal run the page-space box
    /// is a square, and it took glyphs of the lines above and below.
    /// </summary>
    private static GlyphArea GlyphAreaOf(IReadOnlyList<Letter> line, PdfRectangle lineBox, GlyphRemovalStrategy strategy)
    {
        var contained = strategy == GlyphRemovalStrategy.FullyContained;
        var box = contained ? lineBox : CenterlineBoxOf(line);
        if (ObliqueDirectionOf(line) is not double angle)
            return box;

        var cells = line.Select(l => TextSelectionEngine.LineFrame(l).Box.Normalize()).ToList();
        var frame = contained
            ? new PdfRectangle(cells.Min(c => c.Left), cells.Min(c => c.Bottom), cells.Max(c => c.Right), cells.Max(c => c.Top))
            : CenterlineBoxOf(cells);
        return new GlyphArea(box, angle, frame);
    }

    /// <summary>
    /// The direction a line of a match advances along, when every glyph of it
    /// shares one direction that is not a multiple of 90 degrees; null
    /// otherwise, and the line is removed through its page-space box.
    /// </summary>
    private static double? ObliqueDirectionOf(IReadOnlyList<Letter> line)
    {
        // Below this |sin 2θ| the turn is a quarter turn (or none), whose glyph
        // cells are their own page-space boxes.
        const double quarterTurnTolerance = 1e-3;
        if (line.Count == 0 || !line.All(TextSelectionEngine.IsTurned)
            || line.Any(l => TextSelectionEngine.DirectionChanges(line[0], l)))
            return null;
        var angle = line[0].BaselineAngle;
        return Math.Abs(Math.Sin(2 * angle)) > quarterTurnTolerance ? angle : null;
    }

    private static PdfRectangle CenterlineBoxOf(IReadOnlyList<Letter> letters) =>
        CenterlineBoxOf(letters.Select(l => l.GlyphRectangle));

    private static PdfRectangle CenterlineBoxOf(IEnumerable<PdfRectangle> glyphs)
    {
        const double padding = 0.01;
        var centers = glyphs.Select(g =>
        {
            var r = g.Normalize();
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
    /// The area overloads of <see cref="PdfPageRedactionExtensions"/> draw with it too (#1834).
    /// </summary>
    internal static void AppendBlackRectangle(PdfPage page, PdfRectangle rect, (double R, double G, double B)? boxColor = null)
    {
        var (r, g, b) = boxColor ?? (0.0, 0.0, 0.0);
        // Tracked spans: this appends five operators, so re-serializing the
        // whole page to do it is exactly the round-trip risk #1093 removes.
        var content = page.GetContentStream(trackSourceSpans: true);
        var ops = content.Operators.ToList();
        // #1586: marked as an ARTIFACT (§14.8.2.2). In a TAGGED document every
        // piece of content must be either tagged as real content or marked as
        // an artifact, and an untagged filled rectangle fails PDF/UA-1 clause
        // 7.1 — measured with veraPDF, which rejected an otherwise conformant
        // document purely because of this box ("Content shall be marked as
        // Artifact or tagged as real content"). A covering box is the textbook
        // artifact: it carries no meaning, and a screen reader that announced
        // it would be reading the redaction rather than the document.
        //
        // Emitted unconditionally, not only for tagged documents: marked
        // content in an untagged page is inert, and a conditional would mean
        // the box is accessible only where somebody remembered to check.
        ops.Add(new ContentOperator("BMC", new Excise.Core.Primitives.PdfObject[]
        {
            new Excise.Core.Primitives.PdfName("Artifact"),
        }));
        ops.Add(ContentOperator.SaveState());
        ops.Add(ContentOperator.SetFillRgb(r, g, b));
        ops.Add(ContentOperator.Rectangle(
            rect.Left, rect.Bottom, rect.Right - rect.Left, rect.Top - rect.Bottom));
        ops.Add(ContentOperator.Fill());
        ops.Add(ContentOperator.RestoreState());
        ops.Add(new ContentOperator("EMC"));
        page.SetContentStream(new ContentStream(ops) { SourceBytes = content.SourceBytes, SourceArrayBoundaries = content.SourceArrayBoundaries });
    }

    /// <summary>
    /// Report every occurrence of <paramref name="searchText"/> still on the
    /// page that a line break splits where <see cref="FindTextMatches"/> does
    /// not join it, so redaction cannot remove it: a line-end HYPHEN (#1372),
    /// or a continuation that is not the next line of the same block — the
    /// next column's first line, text to the right (#1750, #1791).
    /// </summary>
    /// <remarks>
    /// <para>The same search over the same text as <see cref="FindTextMatches"/>,
    /// with EVERY line change joined: an ordinary one by a space, a line-end
    /// hyphen by nothing. An occurrence that crosses a break the matcher does
    /// not bridge is a candidate; one whose every break it bridges was a match,
    /// removed or counted as surviving. So a phrase of any length, kerned or
    /// not, is seen here exactly as the matcher would see it.</para>
    ///
    /// <para>A hyphen is reported, never joined: it splits a word, not a phrase,
    /// and <c>well-</c> / <c>known</c> may be one hyphenated word. A hyphen
    /// INSIDE a line is content and never a break.</para>
    /// </remarks>
    internal static void FindWrappedCandidates(
        IReadOnlyList<Letter> letters, string searchText, bool caseSensitive, bool wholeWord, int pageNumber,
        List<HyphenatedTermCandidate> hyphenated, List<WordWrapTermCandidate> wordWrapped)
    {
        var lines = new PageLines(letters);
        var text = BuildSearchText(lines, lines.StreamOrder, joinEveryLineChange: true);
        foreach (var (_, from, to) in Locate(letters, text, searchText, caseSensitive, wholeWord, accept: _ => true))
        {
            // #1884: a break is judged between the glyphs either side of it. A
            // blank glyph left at the end of the line does not bridge it, for
            // the matcher or here.
            Letter? last = null;
            for (var v = text.CharToLetter[from]; v <= text.CharToLetter[to]; v++)
            {
                var next = text.View[v];
                if (string.IsNullOrWhiteSpace(next.Value)) continue;
                var previous = last;
                last = next;
                if (previous == null || lines.SameLine(previous, next)) continue;
                var hyphen = IsHyphen(previous.Value);
                if (!hyphen && lines.IsWrap(previous, next)) continue;

                var at = text.CharToLetter.IndexOf(v, from);
                if (at < 0 || at > to) break;
                var before = text.Text[from..at].TrimEnd();
                var after = text.Text[at..(to + 1)];
                if (hyphen) hyphenated.Add(new HyphenatedTermCandidate(pageNumber, before, after));
                else wordWrapped.Add(new WordWrapTermCandidate(pageNumber, before, after));
                break;
            }
        }

        // #1883: a line with a wide gap inside it may end before the gap (a
        // column of one line beside the block). Not a wrap excise can confirm,
        // and not one to call clean.
        foreach (var (block, wrap) in lines.GapWraps)
        {
            var gapText = BuildSearchText(lines, block, joinEveryLineChange: true);
            foreach (var (_, from, to) in Locate(letters, gapText, searchText, caseSensitive, wholeWord, accept: _ => true))
            {
                var at = gapText.CharToLetter.IndexOf(wrap, from);
                if (at <= from || at > to) continue;
                wordWrapped.Add(new WordWrapTermCandidate(pageNumber, gapText.Text[from..at].TrimEnd(), gapText.Text[at..(to + 1)]));
            }
        }
    }

    private static bool IsHyphen(string value) =>
        value == "-" || value == "‐" || value == "­";

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
    /// non-overlapping — greedy left-to-right. A match may wrap onto the next
    /// line of its block (#1791); <see cref="LinesOf"/> splits it for removal.
    /// A block whose lines the stream does not draw one after the other (two
    /// columns drawn row by row) is searched line after line as well (#1883);
    /// only a match across such a wrap is taken from that search.
    /// </remarks>
    internal static List<List<Letter>> FindTextMatches(
        IReadOnlyList<Letter> letters, string searchText, bool caseSensitive,
        bool wholeWord = false) =>   // #1052
        FindTextMatchLines(letters, searchText, caseSensitive, wholeWord).Select(m => m.Letters).ToList();

    /// <summary>
    /// <see cref="FindTextMatches"/>, each match with its lines
    /// (<see cref="LinesOf"/>) for removal, split by the same line model that
    /// matched it.
    /// </summary>
    internal static List<(List<Letter> Letters, List<List<Letter>> Lines)> FindTextMatchLines(
        IReadOnlyList<Letter> letters, string searchText, bool caseSensitive, bool wholeWord = false)
    {
        if (string.IsNullOrEmpty(searchText) || letters.Count == 0)
            return new List<(List<Letter>, List<List<Letter>>)>();
        var lines = new PageLines(letters);
        return MatchesOn(lines, letters, searchText, caseSensitive, wholeWord)
            .Select(m => (m, LinesOf(m, lines))).ToList();
    }

    private static List<List<Letter>> MatchesOn(
        PageLines lines, IReadOnlyList<Letter> letters, string searchText, bool caseSensitive, bool wholeWord)
    {
        var matches = Locate(letters, BuildSearchText(lines, lines.StreamOrder, joinEveryLineChange: false),
                searchText, caseSensitive, wholeWord, slice => IsSpatiallyCoherent(lines, slice))
            .Select(m => m.Letters).ToList();
        if (lines.OffStreamBlocks.Count == 0) return matches;

        var taken = new HashSet<Letter>(matches.SelectMany(m => m), ReferenceEqualityComparer.Instance);
        foreach (var block in lines.OffStreamBlocks)
        {
            var blockText = BuildSearchText(lines, block, joinEveryLineChange: false);
            foreach (var (slice, _, _) in Locate(letters, blockText, searchText, caseSensitive, wholeWord,
                         m => IsSpatiallyCoherent(lines, m) && lines.CrossesOffStreamWrap(m)))
            {
                if (slice.Exists(taken.Contains)) continue;
                matches.Add(slice);
                taken.UnionWith(slice);
            }
        }
        return matches;
    }

    /// <summary>A page's letters as one searchable string (<see cref="BuildSearchText"/>).</summary>
    /// <param name="View">The letters searched, in order, with overprinted copies collapsed (#1047).</param>
    /// <param name="SpanStart">Per view letter, the first original letter it stands for.</param>
    /// <param name="SpanEnd">Per view letter, the last original letter it stands for.</param>
    /// <param name="Text">The string searched.</param>
    /// <param name="CharToLetter">Per character of <paramref name="Text"/>, its view letter.</param>
    /// <param name="Lines">The page's line model.</param>
    private sealed record SearchText(
        List<Letter> View, List<int> SpanStart, List<int> SpanEnd, string Text, List<int> CharToLetter, PageLines Lines);

    /// <summary>
    /// The page's letters as the one string both the matcher and the wrap net
    /// search, each character mapped back to its letter.
    /// </summary>
    /// <remarks>
    /// <para>#1047: overprinted duplicates are collapsed. Faux-bold is drawn by
    /// stamping the same run several times at sub-point offsets; excise's letter
    /// model faithfully records every copy, so a 4x-stamped "Test test" reads as
    /// "TTTTeeeesssstttt" and a search for "test" matches NOTHING. The term then
    /// survives and RedactText reports success — Limitations #1, exactly.</para>
    ///
    /// <para>#1177: a space is INFERRED where a horizontal gap separates two
    /// same-line glyphs, exactly as JoinText does. Without it the search runs over
    /// the SPACELESS glyph concatenation, so "your software" reads as
    /// "yoursoftware" and a search for "yours" matches across the word boundary —
    /// foss-primer reported 29 "yours" (your+software/server/self) where the page
    /// shows 7.</para>
    ///
    /// <para>#1791: a space is also inferred at a line WRAP
    /// (<see cref="PageLines.IsWrap"/>), which stands in for the space a producer does
    /// not draw at the end of a line, so a phrase of any length matches across
    /// it. A line-end hyphen joins nothing there: it stays, as it reads. Any
    /// other line change joins nothing either: text continuing anywhere but the
    /// next line of its block is not read as the same phrase. With
    /// <paramref name="joinEveryLineChange"/> (the net,
    /// <see cref="FindWrappedCandidates"/>) every line change is joined, a
    /// line-end hyphen by dropping it.</para>
    ///
    /// <para>An inferred space maps to the PREVIOUS letter (it adds no real
    /// letter), so a match's removed slice is unchanged; a needle without that
    /// space simply cannot span the gap.</para>
    /// </remarks>
    /// <param name="lines">The page's line model.</param>
    /// <param name="order">The view letters to read, in order: the stream, or one block line after line (#1883).</param>
    /// <param name="joinEveryLineChange">Join every line change (the net) rather than only a wrap.</param>
    private static SearchText BuildSearchText(PageLines lines, IReadOnlyList<int> order, bool joinEveryLineChange)
    {
        var view = new List<Letter>(order.Count);
        var spanStart = new List<int>(order.Count);
        var spanEnd = new List<int>(order.Count);
        foreach (var v in order)
        {
            view.Add(lines.View[v]);
            spanStart.Add(lines.SpanStart[v]);
            spanEnd.Add(lines.SpanEnd[v]);
        }

        var sb = new StringBuilder(view.Count);
        var characterToLetter = new List<int>(view.Count);
        for (var letterIndex = 0; letterIndex < view.Count; letterIndex++)
        {
            if (letterIndex > 0)
            {
                var previous = view[letterIndex - 1];
                var current = view[letterIndex];
                var lineChange = !string.IsNullOrWhiteSpace(previous.Value)
                    && !string.IsNullOrWhiteSpace(current.Value)
                    && (joinEveryLineChange
                        ? !lines.SameLineAt(order[letterIndex - 1], order[letterIndex])
                        : lines.IsWrap(previous, current));
                if (lineChange && IsHyphen(previous.Value))
                {
                    if (joinEveryLineChange)
                    {
                        sb.Length -= previous.Value.Length;
                        characterToLetter.RemoveRange(characterToLetter.Count - previous.Value.Length, previous.Value.Length);
                    }
                }
                else if (lineChange || lines.IsInferredWordGapAt(order[letterIndex - 1], order[letterIndex]))
                {
                    sb.Append(' ');
                    characterToLetter.Add(letterIndex - 1);
                }
            }
            var value = view[letterIndex].Value;
            sb.Append(value);
            for (var charIndex = 0; charIndex < value.Length; charIndex++)
                characterToLetter.Add(letterIndex);
        }

        return new SearchText(view, spanStart, spanEnd, sb.ToString(), characterToLetter, lines);
    }

    /// <summary>
    /// Every occurrence of <paramref name="searchText"/> in <paramref name="text"/>
    /// that <paramref name="accept"/> takes, as the original letters it covers
    /// and its character span. Non-overlapping, greedy left-to-right.
    /// </summary>
    private static List<(List<Letter> Letters, int From, int To)> Locate(
        IReadOnlyList<Letter> letters, SearchText text, string searchText, bool caseSensitive, bool wholeWord,
        Func<List<Letter>, bool> accept)
    {
        var matches = new List<(List<Letter> Letters, int From, int To)>();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var (view, spanStart, spanEnd, fullText, characterToLetter, lines) = text;

        // Trim only the caller's needle. Trimming each candidate source window
        // lets matching start on an unrelated whitespace glyph, whose geometry
        // may be on another line or column. The resulting bounding box can span
        // most of a page and destroy remote text (#942).
        var needle = MatchingNormalization.Fold(searchText).Trim();
        if (needle.Length == 0) return matches;

        // NOTE: not `i <= fullText.Length - needle.Length` — normalization can
        // EXPAND raw text (a lam-alef ligature is one raw char but two needle
        // chars), so a raw window shorter than the needle can still match.
        int i = 0;
        while (i < fullText.Length)
        {
            var endIndex = TermMatch.MatchEnd(fullText, i, needle, comparison);
            if (endIndex >= 0)
            {
                // #1052: whole-word matching, when the user asked for it. #1000
                // decided substring stays the DEFAULT — it is right for a case
                // number inside a longer citation — precisely because the
                // alternative would be an explicit choice rather than a silent
                // global rule. This is that choice: a match must be bounded by a
                // non-word character (or the start/end of the run) on BOTH sides,
                // so redacting "Lee" no longer guts "Sleeman". #1834: a
                // neighbour on another line bounds it too. fullText has no
                // separator at a line change that is not a wrap (#1791), so
                // without this "Lee" ending one line reads as "Leetop".
                bool BoundedBy(int neighbour, int inside) =>
                    neighbour < 0 || neighbour >= fullText.Length
                    || !TermMatch.IsWordChar(fullText[neighbour])
                    || !lines.SameLine(view[characterToLetter[neighbour]], view[characterToLetter[inside]]);
                if (wholeWord && !(BoundedBy(i - 1, i) && BoundedBy(endIndex + 1, endIndex)))
                {
                    i++;
                    continue;
                }

                if (endIndex >= i && endIndex < characterToLetter.Count)
                {
                    var firstLetter = characterToLetter[i];
                    var lastLetter = characterToLetter[endIndex];

                    // Expand back to EVERY original letter the matched view
                    // covers, so all overprinted copies are removed. Removing
                    // only the representative would leave the other stamps
                    // drawn and extractable — a redaction that looks done.
                    // Letter by letter, never a range of the stream: a block
                    // read line after line (#1883) is not contiguous in it, and
                    // the range would sweep in the other column (#942).
                    var slice = new List<Letter>();
                    for (var position = firstLetter; position <= lastLetter; position++)
                        for (var letterIndex = spanStart[position]; letterIndex <= spanEnd[position]; letterIndex++)
                            slice.Add(letters[letterIndex]);
                    if (accept(slice))
                    {
                        matches.Add((slice, i, endIndex));
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
    /// #1755 — the covering box for one match under <see cref="WidthPolicy.FixedMarker"/>:
    /// a FIXED number of ems of the match's own font size, anchored at the
    /// removed run's left edge, with NO dependence on the removed run's actual
    /// width at all.
    /// </summary>
    /// <remarks>
    /// <para>This is the property <see cref="OvershootBoxFor"/> does not have:
    /// overshoot still ROUNDS UP from the removed width, so it merely coarsens
    /// the measurement into buckets — two candidates in the same bucket become
    /// indistinguishable, but the bucket itself still correlates with length.
    /// A fixed multiple of the font size carries no information about the
    /// removed string at all: every redaction on the same font size draws the
    /// identical mark.</para>
    /// <para><b>Anchored left, matching how <see cref="WidthPolicy.CloseGap"/>
    /// reflows the line</b> (FixedMarker closes the gap the same way — see
    /// <see cref="RedactionOptions.CloseWidth"/>): the surviving text that
    /// followed the removed run shifts left to sit exactly at
    /// <c>bbox.Left</c>, so anchoring the marker there places it right where
    /// the removed text was, with the reflowed continuation immediately after
    /// it.</para>
    /// <para><b>Known limit, stated rather than hidden:</b> when the fixed
    /// width is WIDER than the line's slack, the marker visually overlaps the
    /// reflowed neighbour — cosmetic only (the box is a drawn overlay; the
    /// content-stream removal that actually deletes the glyphs already ran
    /// unconditionally), but a real limit. Placing the marker by
    /// run-boundary/alignment-aware machinery is #1751/#1752/#1753, explicitly
    /// out of scope here.</para>
    /// </remarks>
    internal static PdfRectangle FixedMarkerBoxFor(
        PdfRectangle bbox,
        IReadOnlyList<Letter> matchLetters)
    {
        var box = bbox.Normalize();
        var height = box.Top - box.Bottom;
        if (height <= 0) return bbox;

        // Same "one em of the match's own font size" reference OvershootBoxFor
        // uses for its bucket — but here it IS the width, not a rounding unit,
        // so no removed-width measurement ever enters the computation at all.
        var em = matchLetters.Count > 0 ? matchLetters.Max(l => l.FontSize) : height;
        if (!(em > 0)) return bbox;

        var width = FixedMarkerEms * em;
        return new PdfRectangle(box.Left, box.Bottom, box.Left + width, box.Top);
    }

    /// <summary>
    /// Marker width for <see cref="FixedMarkerBoxFor"/>, in ems of the match's
    /// font size. A constant, not a <see cref="RedactionOptions"/> knob: making
    /// it caller-choosable would let a caller pick a size that happens to fit
    /// one particular candidate, which is the same shape of leak #1754's
    /// width-quantise option was found to have (a bucket is still a
    /// measurement). Two ems comfortably covers a short redacted run's own
    /// width without depending on what it was.
    /// </summary>
    private const double FixedMarkerEms = 2.0;

    /// <summary>
    /// #1189 — the covering box for one match, WIDENED so its width no longer
    /// measures the removed string.
    /// </summary>
    /// <remarks>
    /// <para>A box drawn to the exact extent of the removed run is a ruler
    /// (#1140): given a candidate list, the reader measures the box and discards
    /// every candidate whose rendered width differs. Rounding the box width UP
    /// to a whole em blurs that measurement — candidates whose widths fall in
    /// the same bucket become indistinguishable, which is exactly the case an
    /// attacker separating similar names is in.</para>
    /// <para><b>Measured, not assumed:</b> the first design here grew the box to
    /// fill the gap between the surviving neighbours. That does almost nothing
    /// in running text — the following words SHIFT with the secret's length, so
    /// the gap scales with the very quantity being hidden. The oracle test
    /// caught it (223 px apart on two secrets that should have matched).</para>
    /// <para>The bound is the surviving text itself: the box grows only into the
    /// gap between the nearest kept glyph on each side, minus a margin, and
    /// never past the page window. Eating a neighbour would destroy content the
    /// user did not ask to redact — the collateral this project has repeatedly
    /// been bitten by (#942, #1038). <b>Where that slack is smaller than the
    /// rounding needs, the box grows as far as it can and the bucket is NOT
    /// reached</b>; the residue is then only partly blurred. That limit is real
    /// and is pinned by a test rather than papered over.</para>
    /// <para>⚠️ It does NOT close the width channel in the FILE. Preserving
    /// layout means one TJ adjustment equal to the removed advance survives in
    /// the content stream. See <see cref="WidthPolicy.OvershootPreserveLayout"/>.
    /// </para>
    /// </remarks>
    internal static PdfRectangle OvershootBoxFor(
        PdfRectangle bbox,
        IReadOnlyList<Letter> matchLetters,
        IReadOnlyList<Letter> allLetters,
        PdfRectangle window)
    {
        var box = bbox.Normalize();
        var height = box.Top - box.Bottom;
        var width = box.Right - box.Left;
        if (height <= 0 || width <= 0) return bbox;

        // The bucket size. One em of the removed text: coarse enough that names
        // of similar length collapse together, small enough to fit in the slack
        // an ordinary word gap provides.
        var em = matchLetters.Count > 0 ? matchLetters.Max(l => l.FontSize) : height;
        if (!(em > 0)) return bbox;

        var midY = (box.Bottom + box.Top) / 2.0;
        var win = window.Normalize();

        // Reference identity: the match letters are the same objects as in the
        // page's letter list, and two glyphs can legitimately compare equal.
        var removed = new HashSet<Letter>(matchLetters, ReferenceEqualityComparer.Instance);

        // How far can the box grow before it touches surviving text on this
        // line? Start at the page window and pull in for every kept neighbour.
        // Whitespace glyphs are not neighbours — their space IS the slack.
        var leftLimit = win.Left;
        var rightLimit = win.Right;
        foreach (var letter in allLetters)
        {
            if (removed.Contains(letter)) continue;
            if (string.IsNullOrWhiteSpace(letter.Value)) continue;

            var r = letter.GlyphRectangle.Normalize();
            if (Math.Abs((r.Bottom + r.Top) / 2.0 - midY) > height / 2.0) continue;   // same line only

            if (r.Right <= box.Left) leftLimit = Math.Max(leftLimit, r.Right);
            else if (r.Left >= box.Right) rightLimit = Math.Min(rightLimit, r.Left);
        }

        // Leave a hairline so the box abuts rather than overlaps its neighbour.
        var margin = height * 0.05;
        var leftSlack = Math.Max(0, box.Left - (leftLimit + margin));
        var rightSlack = Math.Max(0, (rightLimit - margin) - box.Right);
        var slack = leftSlack + rightSlack;
        if (slack <= 0) return bbox;

        // Round the width UP to the next whole em, as far as the slack allows.
        var target = Math.Ceiling(width / em) * em;
        var grow = Math.Min(target - width, slack);
        if (grow <= 0) return bbox;

        // Grow right first, then left — an asymmetric box is fine; only its
        // WIDTH is the measurement being blurred.
        var growRight = Math.Min(grow, rightSlack);
        var growLeft = Math.Min(grow - growRight, leftSlack);
        return new PdfRectangle(box.Left - growLeft, box.Bottom, box.Right + growRight, box.Top);
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
    /// The page's line model, which the matcher and the wrap net share: the
    /// letters with overprinted copies collapsed (#1047), the way each glyph's
    /// line runs (#1882), and the line each line wraps onto (#1791, #1883).
    /// </summary>
    /// <remarks>
    /// <para>#1882: every predicate is read in the glyph's own writing
    /// direction, so text rotated by its matrix (landscape content drawn on a
    /// portrait page with <c>0 1 -1 0 612 0 cm</c>) has lines, word gaps and
    /// wraps exactly as upright text does. #2011: the direction is the one the
    /// content-stream walker computed from Tm × CTM and the sign of Th
    /// (<see cref="Letter.BaselineAngle"/>), at its exact angle, and each box is
    /// read in <see cref="TextSelectionEngine.Frame"/>, the line frame page
    /// text, Find and selection read. Glyphs share a line only when
    /// <see cref="TextSelectionEngine.WritingModeChanges"/> says they share a
    /// direction. Inferring the direction from glyph origins snapped to a
    /// quarter turn left a wrap in text turned 30 or 45 degrees unremoved
    /// (#1891). An upright glyph's box is its <see cref="Letter.GlyphRectangle"/>
    /// unchanged, so an ordinary page reads exactly as it did.</para>
    ///
    /// <para>#1883: wraps are found by geometry, not by stream order. A page
    /// that draws two columns row by row puts the other column's line between
    /// two lines of a block, and a stream-order rule joined a line to the other
    /// column's next line while never joining it to its own.</para>
    /// </remarks>
    private sealed class PageLines
    {
        /// <summary>The letters with overprinted copies collapsed, in stream order.</summary>
        public List<Letter> View { get; }

        /// <summary>Per view letter, the first original letter it stands for.</summary>
        public List<int> SpanStart { get; }

        /// <summary>Per view letter, the last original letter it stands for.</summary>
        public List<int> SpanEnd { get; }

        /// <summary>Every view letter, in stream order.</summary>
        public List<int> StreamOrder { get; }

        /// <summary>#1883: the view letters of each block, line after line, that
        /// wraps between lines the stream does not draw one after the other.</summary>
        public List<List<int>> OffStreamBlocks { get; } = new();

        /// <summary>#1883: a line with a wide gap inside it, read as if it ended
        /// before the gap and wrapped there: its view letters up to the gap, then
        /// the lines it wraps onto; <c>Break</c> is where the wrap starts in them.
        /// The text after the gap may be a column of one line beside the block, or
        /// the rest of a justified line; which is not knowable, so a match across
        /// this break is reported, never removed.</summary>
        public List<(List<int> Letters, int Break)> GapWraps { get; } = new();

        // Per view letter, its direction (DirectionsOf) and its box in its line's frame.
        private readonly int[] _dirAt;
        private readonly PdfRectangle[] _rectAt;

        // Every copy of a wrapping line's last glyph, and of the first glyph of
        // the line it wraps onto, keyed to that wrap.
        private readonly Dictionary<Letter, int> _wrapFrom = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Letter, int> _wrapTo = new(ReferenceEqualityComparer.Instance);
        private readonly List<bool> _wrapOffStream = new();

        // #1177: median left-to-right advance over SAME-LINE adjacent glyphs, so a
        // word gap is judged RELATIVE to the document's own spacing (JoinText's
        // WordGapAdvanceFactor rule). An absolute font-size fraction misfires on
        // uniformly loose spacing (a per-glyph-Tm fixture at 7pt pitch reads every
        // narrow glyph's trailing gap as a space); the relative rule does not.
        private readonly double _medianAdvance;

        public PageLines(IReadOnlyList<Letter> letters)
        {
            (View, SpanStart, SpanEnd) = CollapseOverprintedGlyphs(letters);
            StreamOrder = Enumerable.Range(0, View.Count).ToList();
            _dirAt = DirectionsOf(View);
            _rectAt = new PdfRectangle[View.Count];
            for (var v = 0; v < View.Count; v++)
                _rectAt[v] = FrameOf(View[v]);

            var advances = new List<double>();
            for (var k = 1; k < View.Count; k++)
            {
                if (string.IsNullOrWhiteSpace(View[k - 1].Value) || string.IsNullOrWhiteSpace(View[k].Value))
                    continue;
                if (!SameLineAt(k - 1, k)) continue;
                var adv = _rectAt[k].Left - _rectAt[k - 1].Left;
                if (adv > 0) advances.Add(adv);
            }
            _medianAdvance = MedianAdvance(advances);

            LinkWraps(letters);
        }

        /// <summary>
        /// #2011: per letter, a number shared by the letters written in one
        /// direction: 0 upright, 1 vertical writing, and from 2 one per direction
        /// a matrix turns text to, as <see cref="TextSelectionEngine.DirectionChanges"/>
        /// tells directions apart. Bucketed by angle, so a page of text on a
        /// curve (a direction per glyph) costs one lookup per glyph, not one per
        /// direction seen.
        /// </summary>
        private static int[] DirectionsOf(List<Letter> view)
        {
            const double tolerance = TextSelectionEngine.SameDirectionTolerance;
            var buckets = (long)Math.Ceiling(2 * Math.PI / tolerance);
            var first = new List<Letter>();
            var byBucket = new Dictionary<long, List<int>>();
            var directions = new int[view.Count];
            for (var v = 0; v < view.Count; v++)
            {
                var letter = view[v];
                if (letter.IsVerticalWriting) { directions[v] = 1; continue; }
                if (!TextSelectionEngine.IsTurned(letter)) continue;

                var angle = letter.BaselineAngle % (2 * Math.PI);
                if (angle < 0) angle += 2 * Math.PI;
                var bucket = (long)Math.Floor(angle / tolerance) % buckets;
                var found = -1;
                // Two directions within the tolerance are at most one bucket apart.
                for (var d = -1; d <= 1 && found < 0; d++)
                    if (byBucket.TryGetValue((bucket + d + buckets) % buckets, out var near))
                        foreach (var g in near)
                            if (!TextSelectionEngine.DirectionChanges(first[g], letter)) { found = g; break; }
                if (found < 0)
                {
                    found = first.Count;
                    first.Add(letter);
                    if (!byBucket.TryGetValue(bucket, out var list)) byBucket[bucket] = list = new List<int>();
                    list.Add(found);
                }
                directions[v] = 2 + found;
            }
            return directions;
        }

        /// <summary>The glyph box in its line's frame, x along the line and y up
        /// the glyph: the frame Find and selection read (#2011).</summary>
        private static PdfRectangle FrameOf(Letter letter) => TextSelectionEngine.Frame(letter).Normalize();

        /// <summary>A line of the stream: view letters <see cref="Start"/> to
        /// <see cref="End"/> (trailing blanks included), <see cref="First"/> and
        /// <see cref="Last"/> its first and last glyph that is not blank, and its
        /// extent along the line.</summary>
        private readonly record struct Line(int Start, int End, int First, int Last, double Left, double Right);

        /// <summary>
        /// Link each line to the line it wraps onto (#1791, #1883).
        /// </summary>
        /// <remarks>
        /// <para>A line is a stream run of glyphs next to each other on one line.
        /// A run that jumps along its line is a line of its own only where it heads
        /// a column: a line start aligned with it one line above or below. So a
        /// column drawn row by row splits off its neighbour, and a wide justified
        /// gap does not split a line.</para>
        /// <para>A line wraps onto a line below that starts wholly left of where it
        /// ends (<see cref="IsLineWrapAt"/>) and shares its extent: the nearest such,
        /// and of those the best aligned with its start, the stream's next line
        /// on a tie. A line in the other column never shares the extent, so it is
        /// never the wrap. At most one line wraps onto each: the nearest, then the
        /// one ending farther along its row, so the text after a superscript
        /// footnote mark wraps, not the text before it.</para>
        /// </remarks>
        private void LinkWraps(IReadOnlyList<Letter> letters)
        {
            var runs = new List<Line>();
            var jumped = new List<bool>();
            int start = 0, first = -1, last = -1;
            var jump = false;
            for (var v = 0; v < View.Count; v++)
            {
                if (string.IsNullOrWhiteSpace(View[v].Value)) continue;
                if (last < 0) first = v;
                else if (!SameLineAt(last, v) || !Adjacent(View[last], View[v]))
                {
                    runs.Add(LineOf(start, v - 1, first, last));
                    jumped.Add(jump);
                    jump = SameLineAt(last, v);
                    start = first = v;
                }
                last = v;
            }
            if (last < 0) return;
            runs.Add(LineOf(start, View.Count - 1, first, last));
            jumped.Add(jump);

            var runHeads = new Heads(this, runs);
            bool HeadsAColumn(int r)
            {
                var head = runs[r].First;
                return runHeads.Near(head, above: true).Concat(runHeads.Near(head, above: false)).Any(q =>
                {
                    var other = runs[q].First;
                    var size = Math.Max(SizeAt(head), SizeAt(other));
                    return Math.Abs(CentreAt(other) - CentreAt(head)) > 0.5 * size
                        && Math.Abs(runs[q].Left - runs[r].Left) <= size;
                });
            }
            var lines = new List<Line>(runs.Count);
            var gaps = new List<(int Line, int End)>();
            for (var r = 0; r < runs.Count; r++)
            {
                if (jumped[r] && lines.Count > 0 && !HeadsAColumn(r))
                {
                    gaps.Add((lines.Count - 1, runs[r].Start - 1));
                    lines[^1] = LineOf(lines[^1].Start, runs[r].End, lines[^1].First, runs[r].Last);
                }
                else
                    lines.Add(runs[r]);
            }

            var heads = new Heads(this, lines);
            var wrapsOnto = new Dictionary<int, (int From, double Drop)>();
            for (var s = 0; s < lines.Count; s++)
            {
                var end = lines[s].Last;
                var candidates = new List<(int To, double Drop, double Shift)>();
                foreach (var t in heads.Near(end, above: false))
                {
                    var next = lines[t].First;
                    if (t == s || !IsLineWrapAt(end, next)
                        || lines[t].Right <= lines[s].Left || lines[t].Left >= lines[s].Right)
                        continue;
                    candidates.Add((t, CentreAt(end) - CentreAt(next), Math.Abs(lines[t].Left - lines[s].Left)));
                }
                if (candidates.Count == 0) continue;

                var tolerance = 0.5 * SizeAt(end);
                var nearest = candidates.Min(c => c.Drop);
                var row = candidates.Where(c => c.Drop <= nearest + tolerance).OrderBy(c => c.Shift).ToList();
                var streamNext = row.FindIndex(c => c.To == s + 1 && c.Shift <= row[0].Shift + tolerance);
                var best = row[Math.Max(streamNext, 0)];
                // Two lines above one: the nearer, and on one row the one that
                // ends farther along it (text after a superscript, not before).
                if (!wrapsOnto.TryGetValue(best.To, out var held)
                    || best.Drop < held.Drop - tolerance
                    || (best.Drop <= held.Drop + tolerance && lines[s].Right > lines[held.From].Right))
                    wrapsOnto[best.To] = (s, best.Drop);
            }

            var successor = Enumerable.Repeat(-1, lines.Count).ToArray();
            foreach (var (to, (from, _)) in wrapsOnto)
            {
                var id = _wrapOffStream.Count;
                _wrapOffStream.Add(to != from + 1);
                for (var i = SpanStart[lines[from].Last]; i <= SpanEnd[lines[from].Last]; i++) _wrapFrom[letters[i]] = id;
                for (var i = SpanStart[lines[to].First]; i <= SpanEnd[lines[to].First]; i++) _wrapTo[letters[i]] = id;
                successor[from] = to;
            }

            // Each block, line after line, from its first line (one nothing
            // wraps onto). A wrap always goes down its line, so this ends.
            List<int> Block(int from, int start, int end, out bool offStream)
            {
                var block = new List<int>();
                offStream = false;
                for (var l = from; l >= 0; l = successor[l])
                {
                    for (var v = l == from ? start : lines[l].Start; v <= (l == from ? end : lines[l].End); v++)
                        block.Add(v);
                    offStream |= successor[l] >= 0 && successor[l] != l + 1;
                }
                return block;
            }
            for (var s = 0; s < lines.Count; s++)
            {
                if (successor[s] < 0 || wrapsOnto.ContainsKey(s)) continue;
                var block = Block(s, lines[s].Start, lines[s].End, out var offStream);
                if (offStream) OffStreamBlocks.Add(block);
            }
            foreach (var (line, end) in gaps)
            {
                if (successor[line] < 0) continue;
                var block = Block(line, lines[line].Start, end, out _);
                GapWraps.Add((block, block.IndexOf(lines[successor[line]].First)));
            }
        }

        private Line LineOf(int start, int end, int first, int last)
        {
            var right = double.MinValue;
            for (var v = first; v <= last; v++)
                if (!string.IsNullOrWhiteSpace(View[v].Value))
                    right = Math.Max(right, _rectAt[v].Right);
            return new Line(start, end, first, last, _rectAt[first].Left, right);
        }

        /// <summary>The first glyphs of a page's lines, by direction and height, to find
        /// the lines within wrapping distance of a glyph.</summary>
        private sealed class Heads
        {
            private readonly PageLines _page;
            private readonly Dictionary<int, List<(double Centre, int Line)>> _byDirection = new();
            private readonly double _reach;

            public Heads(PageLines page, List<Line> lines)
            {
                _page = page;
                for (var l = 0; l < lines.Count; l++)
                {
                    var head = lines[l].First;
                    if (!_byDirection.TryGetValue(page._dirAt[head], out var list))
                        _byDirection[page._dirAt[head]] = list = new List<(double, int)>();
                    list.Add((page.CentreAt(head), l));
                    _reach = Math.Max(_reach, 2.5 * page.SizeAt(head));
                }
                foreach (var list in _byDirection.Values) list.Sort();
            }

            /// <summary>Lines whose first glyph lies above (or below) view letter
            /// <paramref name="glyph"/>, within the farthest a line can wrap.</summary>
            public IEnumerable<int> Near(int glyph, bool above)
            {
                if (!_byDirection.TryGetValue(_page._dirAt[glyph], out var list)) yield break;
                var centre = _page.CentreAt(glyph);
                var (lo, hi) = above ? (centre, centre + _reach) : (centre - _reach, centre);
                var i = list.BinarySearch((lo, int.MinValue));
                for (i = i < 0 ? ~i : i; i < list.Count && list[i].Centre <= hi; i++)
                    yield return list[i].Line;
            }
        }

        /// <summary>Whether <paramref name="next"/> is the first glyph of the line the
        /// line ending in <paramref name="last"/> wraps onto.</summary>
        public bool IsWrap(Letter last, Letter next) =>
            _wrapFrom.TryGetValue(last, out var from) && _wrapTo.TryGetValue(next, out var to) && from == to;

        /// <summary>Whether a match crosses a wrap between lines the stream does not draw one after the other.</summary>
        public bool CrossesOffStreamWrap(IReadOnlyList<Letter> match)
        {
            Letter? last = null;
            foreach (var letter in match)
            {
                if (string.IsNullOrWhiteSpace(letter.Value)) continue;
                if (last != null && _wrapFrom.TryGetValue(last, out var from)
                    && _wrapTo.TryGetValue(letter, out var to) && from == to && _wrapOffStream[from])
                    return true;
                last = letter;
            }
            return false;
        }

        private double CentreAt(int v) => (_rectAt[v].Bottom + _rectAt[v].Top) / 2;

        /// <summary>The size a wrap is measured in: the larger of the font size and the glyph
        /// height, because a unit font scaled by <c>Tm</c> reports a size of 1.</summary>
        private double SizeAt(int v) => Math.Max(View[v].FontSize, _rectAt[v].Height);

        /// <summary>Do the two glyphs sit on one line: one direction, centres within half a font size.</summary>
        public bool SameLine(Letter a, Letter b) =>
            !TextSelectionEngine.WritingModeChanges(a, b)
            && OneLine(FrameOf(a), FrameOf(b), Math.Max(a.FontSize, b.FontSize));

        /// <summary><see cref="SameLine"/> for two view letters.</summary>
        public bool SameLineAt(int a, int b) =>
            _dirAt[a] == _dirAt[b] && OneLine(_rectAt[a], _rectAt[b], Math.Max(View[a].FontSize, View[b].FontSize));

        private static bool OneLine(PdfRectangle a, PdfRectangle b, double fontSize) =>
            !(Math.Abs((a.Bottom + a.Top) / 2 - (b.Bottom + b.Top) / 2) > 0.5 * fontSize);

        /// <summary>
        /// #1791: whether view letter <paramref name="next"/> could start the line
        /// after the one view letter <paramref name="last"/> ends, in the same block.
        /// </summary>
        /// <remarks>
        /// The next line of a block starts back at its left edge, one line pitch
        /// down: its first glyph lies wholly left of the previous line's last glyph,
        /// and no more than 2.5 sizes lower (double spacing). A continuation that
        /// jumps up (the next column), to the right (another column or cell) or
        /// straight down (a glyph stacked under the last in upright text) is not a
        /// wrap. Left and down are the line's own (#1882).
        /// </remarks>
        private bool IsLineWrapAt(int last, int next)
        {
            if (_dirAt[last] != _dirAt[next]) return false;
            var size = Math.Max(SizeAt(last), SizeAt(next));
            var drop = CentreAt(last) - CentreAt(next);
            return drop > 0.5 * size && drop <= 2.5 * size && _rectAt[next].Right <= _rectAt[last].Left;
        }

        /// <summary>
        /// #1177: a word gap between two SAME-LINE glyphs, along their line (#1882)
        /// — the boundary JoinText inserts a space at (§ ~0.25em, matching poppler).
        /// Neither glyph is already whitespace (a real space glyph separates on its
        /// own). A line change is NOT a word gap: a wrap is <see cref="IsWrap"/>'s (#1791).
        /// Both are view letters.
        /// </summary>
        public bool IsInferredWordGapAt(int previous, int current)
        {
            var (prev, cur) = (View[previous], View[current]);
            var medianAdvance = _medianAdvance;
            if (string.IsNullOrWhiteSpace(prev.Value) || string.IsNullOrWhiteSpace(cur.Value))
                return false;
            var fontSize = Math.Max(prev.FontSize, cur.FontSize);
            if (fontSize <= 0) return false;
            // Same line only — a line change is not a word gap.
            if (!SameLineAt(previous, current)) return false;
            var a = _rectAt[previous];
            var b = _rectAt[current];
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
    }

    private static double MedianAdvance(List<double> advances)
    {
        if (advances.Count == 0) return 0;
        advances.Sort();
        return advances[advances.Count / 2];
    }

    /// <summary>
    /// Reject text created only by concatenating distant reading-order runs.
    /// Reconstruction can reorder runs in the extracted sequence, and an
    /// iterative redaction pass must not combine "You" in one column with an
    /// unrelated "r" on another line into a synthetic "your" (#942).
    /// Consecutive glyphs must be adjacent, or a line wrap
    /// (<see cref="PageLines.IsWrap"/>, #1791): a phrase continues on the next line.
    /// Whitespace between them may also jump along the line (a justified gap),
    /// but not onto another line: a space glyph left at the foot of a column
    /// does not join the next column's head, exactly as when there is none (#1884).
    /// </summary>
    private static bool IsSpatiallyCoherent(PageLines lines, IReadOnlyList<Letter> letters)
    {
        Letter? last = null;
        var blank = false;
        foreach (var letter in letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                blank = last != null;
                continue;
            }
            if (last != null && !Adjacent(last, letter) && !lines.IsWrap(last, letter)
                && !(blank && lines.SameLine(last, letter)))
                return false;
            last = letter;
            blank = false;
        }

        return true;
    }

    /// <summary>Whether two glyphs lie within two glyph sizes of each other.</summary>
    private static bool Adjacent(Letter first, Letter second)
    {
        var a = first.GlyphRectangle.Normalize();
        var b = second.GlyphRectangle.Normalize();
        var dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        var dy = Math.Max(0, Math.Max(a.Bottom - b.Top, b.Bottom - a.Top));
        var scale = Math.Max(1, Math.Max(
            Math.Max(a.Width, a.Height),
            Math.Max(b.Width, b.Height)));
        return Math.Sqrt(dx * dx + dy * dy) <= scale * 2;
    }

    /// <summary>
    /// #1791: a match split into its lines, so each gets its own removal box.
    /// One box around a match on two lines covers everything between them,
    /// which is #942. A line ends where the line model says the match wraps
    /// (#2011), or where the next glyph is not adjacent: two vertical columns
    /// a column pitch apart are adjacent glyph to glyph, and one box across
    /// both took the glyph after the term. A run of whitespace alone is
    /// dropped: there is nothing in it to remove.
    /// </summary>
    private static List<List<Letter>> LinesOf(IReadOnlyList<Letter> match, PageLines lines)
    {
        var result = new List<List<Letter>>();
        var run = new List<Letter>();
        Letter? ink = null;
        foreach (var letter in match)
        {
            var blank = string.IsNullOrWhiteSpace(letter.Value);
            if (run.Count > 0 && (!Adjacent(run[^1], letter) || (!blank && ink != null && lines.IsWrap(ink, letter))))
            {
                if (run.Exists(l => !string.IsNullOrWhiteSpace(l.Value))) result.Add(run);
                run = new List<Letter>();
            }
            run.Add(letter);
            if (!blank) ink = letter;
        }
        if (run.Exists(l => !string.IsNullOrWhiteSpace(l.Value))) result.Add(run);
        return result;
    }
}
