using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// The output-profile removals (#1586): the parts of a document that a
/// redaction takes out WHOLE, because a term scrub cannot make them safe.
/// </summary>
/// <remarks>
/// <para><b>Why removal and not a scrub.</b> Each carrier here fails the term
/// scrub for a structural reason, not because nobody got round to it:</para>
/// <list type="bullet">
///   <item><b>JavaScript</b> can compute the redacted string rather than
///     contain it (<c>"Zan" + "zibar"</c>), so no substring match can find
///     it — and #1581 measured four separate places the scrub never even
///     reached.</item>
///   <item><b>Launch / SubmitForm / ImportData / GoToR / GoToE</b> name
///     something outside the file; the name itself is the leak (#1581's
///     <c>launch-action</c> trap) and a stripped filename reveals its own
///     length and shape.</item>
///   <item><b>/PieceInfo</b> is a producer's PRIVATE dictionary with no
///     schema — there is nothing to parse, and #1583 measured a draft of the
///     page text sitting in one.</item>
///   <item><b>/Thumb</b> is a picture of the page as it was BEFORE the
///     redaction, and nothing regenerates it.</item>
///   <item><b>Content in OFF optional-content layers</b> and <b>appearance
///     streams of hidden annotations</b> are invisible to the person doing the
///     review and fully readable to every tool.</item>
///   <item><b>/Info and XMP</b> admit custom keys and custom schemas, so a
///     targeted scrub must know names a producer is free to invent — #1583's
///     <c>info-custom-key</c> trap is exactly that.</item>
/// </list>
///
/// <para><b>One stripper, both entry points.</b> <c>RedactText</c> and
/// <c>RedactArea</c>/<c>RedactAreas</c> call this, so the profile cannot be
/// honoured on one path and forgotten on the other — the #896 lesson (a safe
/// option that existed in one front end meant every other caller silently got
/// the unsafe one).</para>
///
/// <para><b>Everything is counted and reported.</b> These removals happen
/// without a term match, so the only thing standing between them and "the tool
/// mangled my document" is the report — see
/// <see cref="RedactedFeatureRemoval"/>.</para>
/// </remarks>
internal static class RedactionFeatureStripper
{
    /// <summary>Actions whose whole point is to reach outside this document.</summary>
    private static readonly string[] ExternalActionTypes =
        { "Launch", "SubmitForm", "ImportData", "GoToR", "GoToE" };

    /// <summary>§12.5.3 Table 165: Hidden (bit 2) and NoView (bit 6).</summary>
    private const int AnnotationFlagHidden = 1 << 1;
    private const int AnnotationFlagNoView = 1 << 5;

    /// <summary>
    /// §12.5.6.2 markup annotations, plus the Popup that belongs to one. A
    /// Redact annotation is NOT here: it is the instruction, and removing it is
    /// the job of the redaction that applies it.
    /// </summary>
    private static readonly HashSet<string> MarkupSubtypes = new(StringComparer.Ordinal)
    {
        "Text", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine",
        "Highlight", "Underline", "Squiggly", "StrikeOut", "Stamp", "Caret",
        "Ink", "Popup", "FileAttachment", "Sound", "Movie",
    };

    /// <summary>
    /// Apply <paramref name="options"/>' profile removals to
    /// <paramref name="document"/>, in place. Returns one row per feature that
    /// was actually present and removed; a feature the document does not have
    /// produces no row.
    /// </summary>
    /// <remarks>
    /// ⚠️ Never throws for a malformed document: a removal that cannot be made
    /// is skipped and the row is simply absent, exactly as if the feature were
    /// not there. That is the one place this differs from the carrier scrub,
    /// which reports a refusal — here there is no "the term is still in it" to
    /// report, because there was no term.
    /// </remarks>
    internal static IReadOnlyList<RedactedFeatureRemoval> Apply(
        PdfDocument document, RedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var rows = new List<RedactedFeatureRemoval>();
        var invalidate = PdfDocumentDerivedStateScope.None;

        void Row(string feature, int count, string? detail = null)
        {
            if (count > 0) rows.Add(new RedactedFeatureRemoval(feature, count, detail));
        }

        if (options.RemoveScripts || options.RemoveExternalActions)
        {
            var (scripts, external, reanchored) = RemoveActions(
                document, options.RemoveScripts, options.RemoveExternalActions,
                options.KeepAttachments);
            Row("JavaScript action(s)", scripts);
            Row("external-effect action(s)", external,
                "Launch/SubmitForm/ImportData/GoToR/GoToE; internal navigation kept");
            Row("embedded file(s) re-anchored at document level", reanchored,
                "their /GoToE or /Launch action was removed and KeepAttachments was requested");
            if (scripts + external > 0)
                invalidate |= PdfDocumentDerivedStateScope.CatalogActionsAndNames;
            if (reanchored > 0)
                invalidate |= PdfDocumentDerivedStateScope.Attachments;
        }

        if (options.RemovePieceInfo)
            Row("/PieceInfo private application data", RemovePieceInfo(document));

        if (options.RemoveThumbnails)
            Row("page thumbnail image(s)", RemoveThumbnails(document));

        if (options.RemoveHiddenAnnotationAppearances)
            Row("hidden annotation appearance stream(s)", RemoveHiddenAppearances(document));

        // Gated on IncludeHiddenLayers as well: that flag is the caller saying
        // whether a hidden layer is in scope at all. A caller who asked NOT to
        // reach into hidden layers (because the layer is legitimate content
        // they want kept) must not have those layers DELETED instead — that
        // would be the opposite of what they asked for, which is worse than
        // either answer on its own.
        if (options.RemoveHiddenLayerContent && options.IncludeHiddenLayers)
        {
            var (spans, groups) = RemoveHiddenOptionalContent(document);
            Row("hidden optional-content span(s)", spans,
                "content in layers that are OFF in the default configuration");
            Row("hidden optional-content group(s)", groups);
            if (spans + groups > 0)
                invalidate |= PdfDocumentDerivedStateScope.OptionalContent;
        }

        // ── Maximum ─────────────────────────────────────────────────────────
        if (options.RemoveBookmarks && document.Catalog.GetOptional("Outlines") != null)
        {
            document.Catalog.Remove("Outlines");
            Row("document outline (bookmarks)", 1);
        }

        if (options.RemoveLinkAnnotations || options.RemoveMarkupAnnotations)
        {
            var (links, markup) = RemoveAnnotations(
                document, options.RemoveLinkAnnotations, options.RemoveMarkupAnnotations);
            Row("link annotation(s)", links);
            Row("comment/markup annotation(s)", markup);
        }

        if (options.RemoveFieldNames)
            Row("form field name(s) and tooltip(s)", RemoveFieldNames(document));

        if (options.FlattenInteractiveContent)
            Row("interactive object(s) flattened into the page", FlattenInteractive(document));

        if (invalidate != PdfDocumentDerivedStateScope.None)
            document.InvalidateDerivedState(invalidate);

        return rows;
    }

    /// <summary>
    /// The wholesale <c>/Info</c> + XMP strip, as its OWN phase (#1586).
    /// Returns the report row, or null when there was nothing to remove.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is not part of <see cref="Apply"/>.</b> Two orderings
    /// pull in opposite directions. The metadata strip must run EARLY — before
    /// the carrier term-scrub, which would otherwise report having scrubbed
    /// values this deletes outright, and before #1499's per-widget appearance
    /// decision, which reads <c>PdfDocument.TargetsPdfA</c> from the packet
    /// this rewrites. The rest of the strip must run LATE on the area path,
    /// because removing an external action can ORPHAN the embedded file it
    /// pointed at (a <c>/GoToE</c> link's <c>/T</c> target), and a caller who
    /// passed <c>KeepAttachments</c> is owed that file in the attachment
    /// report rather than quietly short by one. Measured: it made
    /// <c>AttachmentRedactionTests</c> see 5 files where the fixture has
    /// 6.</para>
    /// <para><c>ScrubDocumentCarriers: false</c> means "the caller handles the
    /// document-level carriers itself" (#896), and <c>/Info</c> and XMP are
    /// two of them — so this respects that opt-out rather than deleting
    /// metadata behind the back of a caller who said it would do the scrub.
    /// Nothing else in this class is a carrier scrub, so nothing else is gated
    /// on it.</para>
    /// </remarks>
    internal static RedactedFeatureRemoval? ApplyMetadataStrip(
        PdfDocument document, RedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.StripDocumentMetadata || !options.ScrubDocumentCarriers) return null;
        if (!HasDocumentMetadata(document)) return null;

        var preserved = document.ScrubMetadataPreservingPdfAIdentity(scrubAttachments: false);
        return new RedactedFeatureRemoval(
            "document /Info dictionary and XMP metadata packet", 1,
            preserved
                ? "the PDF/A and/or PDF/UA identification was retained (#1507/#1586)"
                : null);
    }

    /// <summary>
    /// Whether <paramref name="options"/> removes the accessibility and
    /// interactive structure — what
    /// <see cref="RedactionReport.AccessibilityAndInteractivityRemoved"/>
    /// reports. Read from the FLAGS, not from
    /// <see cref="RedactionOptions.Profile"/>, so a hand-built option set that
    /// does the same damage says so too.
    /// </summary>
    internal static bool DestroysAccessibility(RedactionOptions options) =>
        options.FlattenInteractiveContent || options.RemoveFieldNames
        || options.RemoveBookmarks || options.RemoveLinkAnnotations
        || options.RemoveMarkupAnnotations;

    /// <summary>
    /// Whether the document has anything for the wholesale metadata strip to
    /// take. Checked so a document with no metadata produces no removal row —
    /// a report row for a removal that removed nothing trains people to ignore
    /// the rows.
    /// </summary>
    private static bool HasDocumentMetadata(PdfDocument document)
    {
        if (document.Catalog.GetOptional("Metadata") != null) return true;
        if (document.Info is not { } info) return false;
        // /CreationDate and /ModDate alone are what the strip's own key list
        // clears; any entry counts, because a producer can put text in any key
        // (#1583's info-custom-key trap is /CaseName).
        return info.Count > 0;
    }

    // ───────────────────────── actions ─────────────────────────

    /// <summary>
    /// Remove every removable action reachable from the document, by WALKING
    /// THE OBJECT GRAPH rather than visiting a list of known locations.
    /// </summary>
    /// <remarks>
    /// <para>The enumerate-the-locations approach is what #1581 is: the old
    /// <c>PdfDocumentSanitizer.ScrubJavaScript</c> knew about the document
    /// JavaScript name tree, <c>/OpenAction</c> and the catalog <c>/AA</c>, and
    /// four traps sat in the places it did not know about — a widget's
    /// <c>/AA /K</c>, a widget's <c>/AA /F</c> holding the script as a STREAM,
    /// a non-terminal field's <c>/A</c>, and a link's <c>/Launch /F</c>. A new
    /// location invented by a producer would have made a fifth.</para>
    /// <para>So: every reachable dictionary is asked whether it holds an action
    /// slot (<c>/A</c>, <c>/AA</c>, <c>/OpenAction</c>), and the <c>/Next</c>
    /// chain of each SURVIVING action is pruned too — a kept <c>/GoTo</c>
    /// whose <c>/Next</c> is a script would otherwise run the script.</para>
    /// </remarks>
    /// <param name="keepAttachments">
    /// When true, an embedded file that the REMOVED action was the only route
    /// to is re-anchored on the catalog <c>/AF</c> array instead of being
    /// silently orphaned.
    /// </param>
    /// <remarks>
    /// <para><b>Why the re-anchoring exists.</b> A <c>/GoToE</c> link's
    /// <c>/T</c> target names an embedded file, and dropping the action drops
    /// the only reference to it — so the file falls out of the saved document.
    /// For the default (attachments are removed anyway) that is fine. For a
    /// caller who passed <c>KeepAttachments</c> it is a silent loss of the
    /// thing they explicitly asked to keep: measured on
    /// <c>AttachmentRedactionTests</c>' all-routes fixture, the attachment
    /// report went from 6 files to 5 with nothing saying which one went or
    /// why.</para>
    /// <para>Re-anchoring keeps both promises — the action is gone, the file
    /// is still there and still listed — and it is REPORTED, because moving an
    /// attachment from an action target to a document-level associated file is
    /// a structural change the caller did not ask for.</para>
    /// </remarks>
    private static (int Scripts, int External, int ReanchoredFiles) RemoveActions(
        PdfDocument document, bool removeScripts, bool removeExternal, bool keepAttachments)
    {
        int scripts = 0, external = 0;
        // Filespecs reached only through an action we are about to remove.
        var orphanedFileSpecs = new List<PdfObject>();

        void Salvage(PdfDictionary action)
        {
            if (!keepAttachments) return;
            foreach (var spec in FileSpecsUnder(document, action))
                orphanedFileSpecs.Add(spec);
        }

        bool Removable(PdfDictionary action, out bool isScript)
        {
            isScript = action.GetNameOrNull("S") == "JavaScript";
            if (isScript) return removeScripts;
            var type = action.GetNameOrNull("S");
            return removeExternal && type != null && ExternalActionTypes.Contains(type, StringComparer.Ordinal);
        }

        void Count(bool isScript) { if (isScript) scripts++; else external++; }

        // A kept action's /Next chain (§12.6.1) — a list or a single action.
        void PruneNext(PdfDictionary action, int depth)
        {
            if (depth > 32) return;
            var next = action.GetOptional("Next");
            if (next == null) return;
            switch (Resolve(document, next))
            {
                case PdfDictionary single when single.GetOptional("S") != null:
                    if (Removable(single, out var isScript))
                    {
                        Salvage(single);
                        // Drop the rest of the chain with it: the actions after
                        // a removed one were sequenced to run after it, and
                        // re-splicing them would change what the document does
                        // in a way nobody asked for.
                        action.Remove("Next");
                        Count(isScript);
                    }
                    else PruneNext(single, depth + 1);
                    break;

                case PdfArray array:
                    var keep = new List<PdfObject>();
                    foreach (var item in array)
                    {
                        if (Resolve(document, item) is PdfDictionary a && a.GetOptional("S") != null)
                        {
                            if (Removable(a, out var s)) { Salvage(a); Count(s); continue; }
                            PruneNext(a, depth + 1);
                        }
                        keep.Add(item);
                    }
                    if (keep.Count == 0) action.Remove("Next");
                    else if (keep.Count != array.Count) action["Next"] = new PdfArray(keep);
                    break;
            }
        }

        // One action slot: owner[key] is an action, or (for /AA) a dictionary of
        // event name -> action.
        void PruneSlot(PdfDictionary owner, string key, bool isAdditionalActions)
        {
            var value = owner.GetOptional(key);
            if (value == null) return;
            if (Resolve(document, value) is not PdfDictionary dict) return;

            if (dict.GetOptional("S") != null)
            {
                // A real action. /OpenAction may instead be a destination
                // ARRAY, which Resolve gives us as PdfArray and we never reach.
                if (Removable(dict, out var isScript)) { Salvage(dict); owner.Remove(key); Count(isScript); }
                else PruneNext(dict, 0);
                return;
            }

            if (!isAdditionalActions) return;

            // /AA: §12.6.3 Table 197-200 event names. Prune each slot; drop the
            // dictionary when nothing is left, so no empty /AA survives to look
            // like a document that never had one.
            foreach (var eventKey in dict.Keys.Select(k => k.Value).ToList())
                PruneSlot(dict, eventKey, isAdditionalActions: false);
            if (dict.Count == 0) owner.Remove(key);
        }

        foreach (var dict in ReachableDictionaries(document))
        {
            PruneSlot(dict, "A", isAdditionalActions: false);
            PruneSlot(dict, "AA", isAdditionalActions: true);
            PruneSlot(dict, "OpenAction", isAdditionalActions: false);
        }

        // The document-level JavaScript name tree (§12.6.4.17 /Names
        // /JavaScript): its VALUES are actions the walk above already removed
        // from their own dictionaries, but the tree still names them. Drop the
        // tree — an empty name tree is not a carrier and not navigation.
        if (removeScripts
            && Resolve(document, document.Catalog.GetOptional("Names") ?? PdfNull.Instance)
                is PdfDictionary names
            && names.GetOptional("JavaScript") != null)
        {
            names.Remove("JavaScript");
            scripts++;
            if (names.Count == 0) document.Catalog.Remove("Names");
        }

        return (scripts, external, ReanchorOrphanedFiles(document, orphanedFileSpecs));
    }

    /// <summary>
    /// Every <c>/EF</c>-bearing file specification reachable from
    /// <paramref name="action"/> — a <c>/GoToE</c> <c>/T</c> target, a
    /// <c>/Launch</c> or <c>/ImportData</c> <c>/F</c>, or a <c>/D</c>
    /// destination that names one.
    /// </summary>
    private static IEnumerable<PdfObject> FileSpecsUnder(PdfDocument document, PdfDictionary action)
    {
        var found = new List<PdfObject>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(PdfObject? value, int depth)
        {
            if (depth > 16 || value == null) return;
            var resolved = Resolve(document, value);
            if (!visited.Add(resolved)) return;
            switch (resolved)
            {
                case PdfDictionary dict:
                    if (dict.GetOptional("EF") != null) { found.Add(value); return; }
                    foreach (var (_, inner) in dict) Walk(inner, depth + 1);
                    break;
                case PdfArray array:
                    foreach (var item in array) Walk(item, depth + 1);
                    break;
            }
        }
        Walk(action, 0);
        return found;
    }

    /// <summary>
    /// Append <paramref name="specs"/> to the catalog <c>/AF</c> array so the
    /// files stay reachable, skipping any that already are.
    /// </summary>
    private static int ReanchorOrphanedFiles(PdfDocument document, List<PdfObject> specs)
    {
        if (specs.Count == 0) return 0;

        var af = Resolve(document, document.Catalog.GetOptional("AF") ?? PdfNull.Instance) as PdfArray;
        var existing = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (af != null)
            foreach (var item in af) existing.Add(Resolve(document, item));

        var added = 0;
        var target = af ?? new PdfArray();
        foreach (var spec in specs)
        {
            if (!existing.Add(Resolve(document, spec))) continue;
            target.Add(spec);
            added++;
        }
        if (added > 0 && af == null) document.Catalog["AF"] = target;
        return added;
    }

    // ───────────────────────── /PieceInfo, /Thumb ─────────────────────────

    private static int RemovePieceInfo(PdfDocument document)
    {
        var removed = 0;
        if (document.Catalog.Remove("PieceInfo")) removed++;
        foreach (var page in SafePages(document))
            if (page.Dictionary.Remove("PieceInfo")) removed++;

        // A form XObject carries /PieceInfo too (§14.5 Table 95), and nothing
        // above reaches one.
        foreach (var dict in ReachableDictionaries(document))
            if (dict.GetNameOrNull("Subtype") == "Form" && dict.Remove("PieceInfo"))
                removed++;

        return removed;
    }

    private static int RemoveThumbnails(PdfDocument document)
    {
        var removed = 0;
        foreach (var page in SafePages(document))
            if (page.Dictionary.Remove("Thumb")) removed++;
        return removed;
    }

    // ───────────────────────── hidden appearances ─────────────────────────

    private static int RemoveHiddenAppearances(PdfDocument document)
    {
        var removed = 0;
        foreach (var annot in Annotations(document))
        {
            var flags = annot.GetInt("F", 0);
            var hidden = (flags & (AnnotationFlagHidden | AnnotationFlagNoView)) != 0;
            // An annotation in an OFF layer is hidden the same way, and by the
            // same argument (invisible to the reviewer, readable to every tool).
            if (!hidden && annot.GetOptional("OC") is { } oc)
                hidden = !OptionalContentVisibility.IsVisibleByDefault(document, oc);
            if (hidden && annot.Remove("AP")) removed++;
        }
        return removed;
    }

    // ───────────────────────── hidden optional content ─────────────────────

    /// <summary>
    /// Drop page content inside <c>/OC</c> marked-content spans whose group is
    /// OFF in the default configuration, XObject invocations whose <c>/OC</c>
    /// is OFF, and annotations in an OFF layer; then remove the now-unused
    /// groups from <c>/OCProperties</c>.
    /// </summary>
    /// <remarks>
    /// <para>No new parser: this consumes the walker's operators through
    /// <c>page.GetContentStream</c> and writes them back through
    /// <c>SetContentStream</c>, the same shape as
    /// <see cref="ObstructionStripper"/> (CLAUDE.md "One walk, many sinks").</para>
    /// <para><b>Nesting is counted, not assumed.</b> A hidden span can contain
    /// further <c>BDC</c>/<c>BMC</c> pairs, so the skip runs to the EMC that
    /// balances the one that opened it — dropping at the first EMC would leak
    /// the tail of the layer back into the page.</para>
    /// </remarks>
    private static (int Spans, int Groups) RemoveHiddenOptionalContent(PdfDocument document)
    {
        // No /OCProperties means no optional content and nothing to do — and,
        // importantly, no cost on the overwhelming majority of documents.
        if (document.Catalog.GetOptional("OCProperties") == null) return (0, 0);

        var spans = 0;
        var hiddenGroups = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        foreach (var page in SafePages(document))
        {
            var properties = Resolve(document, page.Resources?.GetOptional("Properties") ?? PdfNull.Instance)
                as PdfDictionary;
            var xobjects = Resolve(document, page.Resources?.GetOptional("XObject") ?? PdfNull.Instance)
                as PdfDictionary;

            ContentStream content;
            try { content = page.GetContentStream(trackSourceSpans: true); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (content.Operators.Count == 0) continue;

            var kept = new List<ContentOperator>(content.Operators.Count);
            var skipDepth = 0;          // >0 while inside a hidden span
            var markedDepth = 0;        // BDC/BMC nesting while skipping
            var pageSpans = 0;

            foreach (var op in content.Operators)
            {
                if (skipDepth > 0)
                {
                    if (op.Name is "BDC" or "BMC") markedDepth++;
                    else if (op.Name == "EMC")
                    {
                        markedDepth--;
                        if (markedDepth == 0) skipDepth = 0;
                    }
                    continue;   // the opening BDC and closing EMC go too
                }

                if (op.Name == "BDC" && IsHiddenOcSpan(document, properties, op, hiddenGroups))
                {
                    skipDepth = 1;
                    markedDepth = 1;
                    pageSpans++;
                    continue;
                }

                if (op.Name == "Do" && IsHiddenXObject(document, xobjects, op, hiddenGroups))
                {
                    pageSpans++;
                    continue;
                }

                kept.Add(op);
            }

            if (pageSpans == 0) continue;
            spans += pageSpans;
            try
            {
                page.SetContentStream(new ContentStream(kept)
                {
                    SourceBytes = content.SourceBytes,
                    SourceArrayBoundaries = content.SourceArrayBoundaries,
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { spans -= pageSpans; }
        }

        // Annotations on a hidden layer: their appearance is already gone if
        // RemoveHiddenAnnotationAppearances ran, but the annotation itself can
        // still carry /Contents, /T and an action. Drop it outright.
        var annotationsRemoved = 0;
        foreach (var page in SafePages(document))
        {
            if (Resolve(document, page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance)
                is not PdfArray annots) continue;
            var keep = new List<PdfObject>();
            foreach (var item in annots)
            {
                if (Resolve(document, item) is PdfDictionary annot
                    && annot.GetOptional("OC") is { } oc
                    && !OptionalContentVisibility.IsVisibleByDefault(document, oc))
                {
                    annotationsRemoved++;
                    continue;
                }
                keep.Add(item);
            }
            if (keep.Count != annots.Count) page.Dictionary["Annots"] = new PdfArray(keep);
        }
        spans += annotationsRemoved;

        return (spans, RemoveHiddenGroupDefinitions(document, hiddenGroups));
    }

    /// <summary>
    /// Whether this <c>BDC</c> opens a span whose optional content is hidden by
    /// default. Records the group so its definition can be removed after.
    /// </summary>
    private static bool IsHiddenOcSpan(
        PdfDocument document, PdfDictionary? properties, ContentOperator op,
        HashSet<PdfDictionary> hiddenGroups)
    {
        if (op.Operands.Count < 2) return false;
        if (op.Operands[0] is not PdfName tag || tag.Value != "OC") return false;

        // The property is either named in /Properties (the common form) or an
        // inline dictionary (§8.11.3.2 permits both).
        PdfObject? property = op.Operands[1] switch
        {
            PdfName name => properties?.GetOptional(name.Value),
            PdfDictionary inline => inline,
            _ => null,
        };
        if (property == null) return false;
        if (OptionalContentVisibility.IsVisibleByDefault(document, property)) return false;

        RecordHiddenGroups(document, property, hiddenGroups);
        return true;
    }

    private static bool IsHiddenXObject(
        PdfDocument document, PdfDictionary? xobjects, ContentOperator op,
        HashSet<PdfDictionary> hiddenGroups)
    {
        if (op.Operands.Count < 1 || op.Operands[0] is not PdfName name) return false;
        if (Resolve(document, xobjects?.GetOptional(name.Value) ?? PdfNull.Instance)
            is not PdfStream stream) return false;
        var oc = stream.GetOptional("OC");
        if (oc == null || OptionalContentVisibility.IsVisibleByDefault(document, oc)) return false;
        RecordHiddenGroups(document, oc, hiddenGroups);
        return true;
    }

    private static void RecordHiddenGroups(
        PdfDocument document, PdfObject property, HashSet<PdfDictionary> hiddenGroups)
    {
        if (Resolve(document, property) is not PdfDictionary dict) return;
        if (dict.GetNameOrNull("Type") == "OCG") { hiddenGroups.Add(dict); return; }
        // An OCMD names its groups in /OCGs (one, or an array).
        var ocgs = dict.GetOptional("OCGs");
        if (ocgs == null) return;
        switch (Resolve(document, ocgs))
        {
            case PdfDictionary single when single.GetNameOrNull("Type") == "OCG":
                hiddenGroups.Add(single);
                break;
            case PdfArray array:
                foreach (var item in array)
                    if (Resolve(document, item) is PdfDictionary g && g.GetNameOrNull("Type") == "OCG")
                        hiddenGroups.Add(g);
                break;
        }
    }

    /// <summary>
    /// Remove the OCG DEFINITIONS whose content this pass deleted. Their
    /// <c>/Name</c> is itself a carrier — a layer called "Confidential draft —
    /// Quillfeather" names what was on it — and a group listing content that no
    /// longer exists is noise in a reader's layers pane.
    /// </summary>
    private static int RemoveHiddenGroupDefinitions(
        PdfDocument document, HashSet<PdfDictionary> hiddenGroups)
    {
        if (hiddenGroups.Count == 0) return 0;
        if (Resolve(document, document.Catalog.GetOptional("OCProperties") ?? PdfNull.Instance)
            is not PdfDictionary props) return 0;

        var removed = 0;
        void Filter(PdfDictionary owner, string key)
        {
            if (Resolve(document, owner.GetOptional(key) ?? PdfNull.Instance) is not PdfArray array) return;
            var keep = array.Where(item =>
                Resolve(document, item) is not PdfDictionary d || !hiddenGroups.Contains(d)).ToList();
            if (keep.Count != array.Count) owner[key] = new PdfArray(keep);
        }

        Filter(props, "OCGs");
        foreach (var configKey in new[] { "D" })
        {
            if (Resolve(document, props.GetOptional(configKey) ?? PdfNull.Instance)
                is not PdfDictionary config) continue;
            foreach (var arrayKey in new[] { "OFF", "ON", "Order", "Locked", "RBGroups", "AS" })
                Filter(config, arrayKey);
        }

        // The /Name strings go with the dictionaries; a group nothing references
        // any more falls out of the saved file (Save serialises only what is
        // reachable from the trailer). Count them as removed.
        removed += hiddenGroups.Count;

        // /OCProperties with no groups left describes nothing.
        if (Resolve(document, props.GetOptional("OCGs") ?? PdfNull.Instance)
            is PdfArray { Count: 0 })
            document.Catalog.Remove("OCProperties");

        return removed;
    }

    // ───────────────────────── Maximum removals ─────────────────────────

    private static (int Links, int Markup) RemoveAnnotations(
        PdfDocument document, bool removeLinks, bool removeMarkup)
    {
        int links = 0, markup = 0;
        foreach (var page in SafePages(document))
        {
            if (Resolve(document, page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance)
                is not PdfArray annots) continue;

            var keep = new List<PdfObject>();
            foreach (var item in annots)
            {
                if (Resolve(document, item) is PdfDictionary annot)
                {
                    var subtype = annot.GetNameOrNull("Subtype");
                    if (removeLinks && subtype == "Link") { links++; continue; }
                    if (removeMarkup && subtype != null && MarkupSubtypes.Contains(subtype))
                    {
                        markup++;
                        continue;
                    }
                }
                keep.Add(item);
            }
            if (keep.Count != annots.Count) page.Dictionary["Annots"] = new PdfArray(keep);
        }
        return (links, markup);
    }

    /// <summary>
    /// Remove <c>/T</c> and <c>/TU</c> from every AcroForm field node.
    /// </summary>
    /// <remarks>
    /// Walks the field TREE from <c>/AcroForm /Fields</c>, not the page's
    /// widgets, because a non-terminal field node (§12.7.3.2 — <c>/T</c> plus
    /// <c>/Kids</c>, no <c>/FT</c>, no <c>/Subtype /Widget</c>) has a name and
    /// no widget of its own. There are 6 of them in irs-w4 and 30 in irs-1040,
    /// and a type-marker test misses every one.
    /// </remarks>
    private static int RemoveFieldNames(PdfDocument document)
    {
        if (Resolve(document, document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance)
            is not PdfDictionary acroForm) return 0;

        var removed = 0;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        void Walk(PdfObject? node, int depth)
        {
            if (depth > 64) return;
            switch (Resolve(document, node ?? PdfNull.Instance))
            {
                case PdfArray array:
                    foreach (var item in array) Walk(item, depth + 1);
                    break;
                case PdfDictionary field when seen.Add(field):
                    if (field.Remove("T")) removed++;
                    if (field.Remove("TU")) removed++;
                    Walk(field.GetOptional("Kids"), depth + 1);
                    break;
            }
        }

        Walk(acroForm.GetOptional("Fields"), 0);
        return removed;
    }

    /// <summary>
    /// Flatten the AcroForm into page content so no widget survives to carry
    /// text, using the existing <see cref="AcroFormFlattener"/>.
    /// </summary>
    /// <remarks>
    /// Returns the number of fields that were flattened — 0 when the document
    /// has no form, which is why this is a count and not a bool. Annotations
    /// other than widgets are handled by the Maximum annotation removals
    /// above; there is deliberately no separate "burn every annotation into the
    /// page" step, because painting a comment's appearance into the content
    /// stream would make its text part of the page rather than remove it.
    /// </remarks>
    private static int FlattenInteractive(PdfDocument document)
    {
        PdfAcroForm? form;
        try { form = document.GetAcroForm(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return 0; }
        if (form == null || form.Fields.Count == 0) return 0;

        var count = form.Fields.Count;
        try { AcroFormFlattener.Flatten(document, form); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return 0; }
        return count;
    }

    // ───────────────────────── shared helpers ─────────────────────────

    private static PdfObject Resolve(PdfDocument document, PdfObject obj)
    {
        try { return document.Resolve(obj); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return PdfNull.Instance; }
    }

    private static IEnumerable<PdfPage> SafePages(PdfDocument document)
    {
        for (var i = 1; i <= document.PageCount; i++)
        {
            PdfPage page;
            try { page = document.GetPage(i); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            yield return page;
        }
    }

    private static IEnumerable<PdfDictionary> Annotations(PdfDocument document)
    {
        foreach (var page in SafePages(document))
        {
            if (Resolve(document, page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance)
                is not PdfArray annots) continue;
            foreach (var item in annots)
                if (Resolve(document, item) is PdfDictionary annot)
                    yield return annot;
        }
    }

    /// <summary>
    /// Every dictionary reachable from the trailer, once each — including
    /// stream dictionaries and dictionaries nested directly inside another
    /// object. Same walk <c>PdfAttachmentGraph.ReachableFileSpecs</c> uses
    /// (#1582): enumerate the reachable OBJECT NUMBERS, then descend through
    /// the direct objects inside each.
    /// </summary>
    private static List<PdfDictionary> ReachableDictionaries(PdfDocument document)
    {
        var result = new List<PdfDictionary>();
        HashSet<int> reachable;
        try { reachable = document.ComputeReachableObjects(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return result; }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(PdfObject obj, int depth)
        {
            // A reference is a separate object, enumerated by number below.
            if (depth > 64 || obj is PdfReference || !visited.Add(obj)) return;
            switch (obj)
            {
                // PdfStream derives from PdfDictionary, so the dictionary case
                // covers a stream's own dictionary entries too.
                case PdfDictionary dict:
                    result.Add(dict);
                    foreach (var (_, value) in dict) Walk(value, depth + 1);
                    break;
                case PdfArray array:
                    foreach (var item in array) Walk(item, depth + 1);
                    break;
            }
        }

        // The catalog is reachable by definition; include the trailer's own
        // dictionary chain so /Info and /Encrypt are not missed.
        Walk(document.Catalog, 0);
        foreach (var objectNumber in reachable.OrderBy(n => n))
        {
            PdfObject obj;
            try { obj = document.GetObject(objectNumber); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            Walk(obj, 0);
        }
        return result;
    }
}
