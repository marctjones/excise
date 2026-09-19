using System;
using System.Collections.Generic;
using System.Threading;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1587 — runs every recovery channel that needs nothing but the PDF, and
/// assembles the one <see cref="RecoveryReport"/>.
///
/// <para><b>What is NOT here, and why that is deliberate.</b> The width-residue
/// channel needs a renderer's glyph positions to corroborate a gap, and the OCR
/// differential needs tesseract; both live outside this assembly. Rather than
/// pull those dependencies into the engine, the caller adds their findings to
/// the same builder. The report is one model either way — which is the point of
/// #1587 — and a caller that runs fewer channels says so through
/// <see cref="RecoveryReportBuilder.ChannelSkipped"/> instead of producing a
/// report that silently reads like full coverage.</para>
///
/// <para>Runs only when an audit asks. Nothing here is on the open or render
/// path: <see cref="PdfPage.Letters"/> and one content-stream walk per page are
/// not free, and a viewer must not pay for a tool it did not invoke.</para>
/// </summary>
public static class RecoveryScanner
{
    /// <summary>Channel names, so callers and tests agree on the vocabulary.</summary>
    public static class Channels
    {
        public const string HiddenText = "hidden-text";
        public const string Carrier = "carrier";
        public const string MarkedContent = "marked-content";
        public const string CoveredImage = "covered-image";
        public const string CoveredVector = "covered-vector";
        public const string ImageLayer = "image-layer";
        public const string FormField = "form-field";
        public const string Residue = "residue";
        public const string OcrDifferential = "ocr-differential";
        public const string PriorRevision = "prior-revision";
        public const string MarkRegion = "mark-region";
        public const string Thumbnail = "thumbnail";
        public const string Attachment = "attachment";
        public const string Xfa = "xfa";
    }

    /// <summary>
    /// Run the document-only channels into a builder the caller can add more to.
    /// </summary>
    /// <param name="pdfBytes">
    /// The file's literal bytes, enabling the PRIOR-REVISION channel. Optional
    /// only because an already-open <see cref="PdfDocument"/> cannot produce
    /// them — the channel needs the file's prefix to walk <c>/Prev</c>.
    ///
    /// <para>⚠️ When null the channel is declared SKIPPED, with the reason. It
    /// used to be neither run nor declared (#1665): one caller existed, in the
    /// CLI, and every library caller — including the bench's own real-world
    /// survey — silently got a report with no prior-revision channel in it and
    /// no way to notice. A report over thirteen channels must not read like one
    /// over fourteen, which is what ChannelsRun/ChannelsSkipped are for.</para>
    /// </param>
    public static RecoveryReportBuilder ScanInto(
        PdfDocument document,
        RecoveryReportBuilder builder,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? dictionary = null,
        byte[]? pdfBytes = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(builder);

        var marks = RedactionMarkDetector.Detect(document);
        builder.AddMarks(marks);

        AddHiddenText(document, builder, cancellationToken);
        AddMarkRegionText(document, marks, builder, cancellationToken);
        var visibleCarrierText = AddCarriers(document, builder, cancellationToken);
        AddMarkedContent(document, builder, cancellationToken);
        AddCoveredContent(document, builder, cancellationToken);
        AddFormFields(document, builder, visibleCarrierText, cancellationToken);
        AddResidualArtefacts(document, builder, cancellationToken);
        AddXfaValues(document, builder, cancellationToken);
        AddImageLayerLeaks(document, builder, cancellationToken);
        AddMarkFits(document, marks, builder, dictionary, cancellationToken);
        AddPriorRevision(pdfBytes, builder, cancellationToken);

        return builder;
    }

    /// <summary>
    /// Convenience overload. ⚠️ Declares the prior-revision channel SKIPPED,
    /// because a <see cref="PdfDocument"/> cannot supply the bytes it needs.
    /// Prefer the byte overload when you have them.
    /// </summary>
    public static RecoveryReport Scan(PdfDocument document, CancellationToken cancellationToken = default)
        => ScanInto(document, new RecoveryReportBuilder(), cancellationToken).Build();

    /// <summary>
    /// Every document-only channel INCLUDING prior-revision. This is the
    /// overload a caller with the file should use (#1665).
    /// </summary>
    public static RecoveryReport Scan(byte[] pdfBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        using var document = PdfDocument.Open(pdfBytes);
        return ScanInto(document, new RecoveryReportBuilder(), cancellationToken, null, pdfBytes).Build();
    }

    /// <summary>
    /// #1592 — text earlier revisions still hold. An incremental update leaves
    /// the pre-redaction document whole at the front of the file.
    ///
    /// <para>Lived in the CLI handler until #1665. Moved here so there is ONE
    /// implementation: the CLI had the only correct call, and the library had
    /// none.</para>
    /// </summary>
    private static void AddPriorRevision(
        byte[]? pdfBytes, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        if (pdfBytes == null)
        {
            builder.ChannelSkipped(Channels.PriorRevision,
                "needs the file bytes to walk /Prev; call RecoveryScanner.Scan(byte[]) " +
                "or pass pdfBytes to ScanInto");
            return;
        }

        var (findings, summary) = PriorRevisionRecovery.Scan(pdfBytes, cancellationToken);
        builder.ChannelRan(Channels.PriorRevision);

        // An earlier revision excise cannot open is NOT evidence that it is
        // clean -- another tool may well read it -- so the shortfall is
        // reported rather than swallowed.
        if (summary.RevisionsUnreadable > 0)
        {
            builder.ChannelSkipped(
                Channels.PriorRevision + " (partial)",
                $"{summary.RevisionsUnreadable} of {summary.RevisionCount} revision(s) would not parse; " +
                $"{summary.RevisionsParsed} read, {summary.PagesRemoved} page(s) gone since the earliest");
        }

        foreach (var finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AddFinding(RecoveredFinding.Certain(
                Channels.PriorRevision,
                $"revision {finding.RevisionIndex} of {summary.RevisionCount}",
                finding.Text,
                new RecoveryLocation(finding.PageNumber, finding.Rect, "prior-revision glyph boxes")));
        }
    }

    private static void AddHiddenText(
        PdfDocument document, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        builder.ChannelRan(Channels.HiddenText);
        foreach (var hit in HiddenTextDetector.Scan(document, includeVisibleFailedRedactions: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AddFinding(
                RecoveredFinding.Certain(
                    Channels.HiddenText, hit.HiddenBy, hit.Text,
                    new RecoveryLocation(hit.PageNumber, hit.BoundingBox, "glyph boxes")),
                // The detector knows exactly which fill covered this run; the
                // hint keeps the link exact instead of re-deriving it.
                hit.ObstructionBox);
        }
    }

    /// <summary>
    /// #1606 — text still inside an ANNOTATION-derived mark. The one channel
    /// that reads the page instead of watching draw order, which is what makes
    /// it the only one that can see an unapplied /Redact annotation or a box
    /// drawn as an annotation.
    /// </summary>
    private static void AddMarkRegionText(
        PdfDocument document,
        IReadOnlyList<RedactionMark> marks,
        RecoveryReportBuilder builder,
        CancellationToken cancellationToken)
    {
        builder.ChannelRan(Channels.MarkRegion);
        foreach (var hit in MarkRegionTextRecovery.Scan(document, marks))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AddFinding(RecoveredFinding.Certain(
                Channels.MarkRegion, hit.Kind, hit.Text,
                new RecoveryLocation(hit.PageNumber, hit.Rect, "glyph boxes inside the mark")));
        }
    }

    /// <summary>
    /// Runs the carrier scan and returns the values it judged ALREADY VISIBLE to
    /// a reader, so a later channel does not report the same string as hidden.
    /// </summary>
    private static HashSet<string> AddCarriers(
        PdfDocument document, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        builder.ChannelRan(Channels.Carrier);
        foreach (var carrier in CarrierTextRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The carrier scan classifies what it finds, and the recovery model
            // has a confidence class for each. Collapsing them all to Certain
            // would report a document TITLE and an unopened attachment as
            // recovered text, and set the "text was recovered" exit code on a
            // document with no leak at all.
            if (carrier.VisibleElsewhere)
            {
                // The reader can already see it. Remember the string: the
                // form-field channel looks at the same values from the other
                // side and has no visibility model of its own.
                visible.Add(carrier.Text);
                continue;
            }

            // Page 0 means the carrier could not be placed. It keeps no
            // location, so the report files it document-level -- an honest
            // "this text is in the file somewhere" rather than a position the
            // channel does not have.
            var location = carrier.PageNumber > 0 && carrier.Area is { } rect
                ? new RecoveryLocation(carrier.PageNumber, rect,
                    carrier.Carrier.StartsWith("annotation", StringComparison.Ordinal)
                        ? "annotation /Rect" : "MCID content")
                : null;

            // Presence is "something is there", never a decoded value — which is
            // exactly what PresentOnly means here.
            builder.AddFinding(carrier.Kind == CarrierTextRecovery.CarrierFindingKind.Presence
                // PresentOnly deliberately has no Text: a description of what is
                // there is not a recovered value, and putting it in the text slot
                // is how a description becomes an "answer". It rides in the
                // carrier label instead.
                ? RecoveredFinding.PresentOnly(
                    Channels.Carrier, $"{carrier.Carrier}: {carrier.Text}", location)
                : RecoveredFinding.Certain(Channels.Carrier, carrier.Carrier, carrier.Text, location));
        }

        return visible;
    }

    private static void AddMarkedContent(
        PdfDocument document, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        builder.ChannelRan(Channels.MarkedContent);
        foreach (var hit in MarkedContentTextRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var carrier = hit.NamedPropertyList
                // Worth saying out loud in the report: the scrub side cannot
                // currently remove this form (#1599), so a leak reported here
                // will still be there after a re-redaction.
                ? $"{hit.Carrier} (named property list)"
                : hit.Carrier;
            var location = hit.Enclosed is { } box
                ? new RecoveryLocation(hit.PageNumber, box, "enclosed glyph boxes")
                : null;
            builder.AddFinding(RecoveredFinding.Certain(
                Channels.MarkedContent, carrier, hit.Text, location));
        }
    }

    private static void AddCoveredContent(
        PdfDocument document, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        builder.ChannelRan(Channels.CoveredImage);
        builder.ChannelRan(Channels.CoveredVector);
        foreach (var hit in CoveredContentRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = hit.Kind == "image" ? Channels.CoveredImage : Channels.CoveredVector;
            builder.AddFinding(
                RecoveredFinding.PresentOnly(
                    channel, hit.Description,
                    new RecoveryLocation(hit.PageNumber, hit.Covered, "covered content box")),
                hit.Obstruction);
        }
    }

    /// <summary>
    /// #1592 — containers whose contents predate the redaction: page
    /// thumbnails and embedded files. Reported present-only; the channel names
    /// the leak and does not decode it.
    /// </summary>
    private static void AddResidualArtefacts(
        PdfDocument document, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        builder.ChannelRan(Channels.Thumbnail);
        builder.ChannelRan(Channels.Attachment);
        foreach (var artefact in ResidualArtefactRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = artefact.Kind == "thumbnail" ? Channels.Thumbnail : Channels.Attachment;
            // A thumbnail covers its whole page, so it gets no rectangle: a box
            // the size of the page would link to every mark on it and say
            // nothing. It is the PAGE that leaks, and the report says so.
            builder.AddFinding(RecoveredFinding.PresentOnly(
                channel, artefact.Description,
                artefact.PageNumber > 0
                    ? new RecoveryLocation(artefact.PageNumber, document.GetPage(artefact.PageNumber).CropBox,
                        "whole page")
                    : null));
        }
    }

    /// <summary>
    /// #1609 — XFA field values. The read mirror of XfaXmlCarrier's scrub:
    /// every carrier the scrub side knows about needs one, or the audit
    /// under-reports by construction.
    /// </summary>
    private static void AddXfaValues(
        PdfDocument document, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        var (values, summary) = XfaValueRecovery.Scan(document);
        if (!summary.HasXfa)
        {
            builder.ChannelSkipped(Channels.Xfa, "document has no /AcroForm /XFA");
            return;
        }

        builder.ChannelRan(Channels.Xfa);
        // A packet excise cannot parse may still be readable by another tool,
        // so the shortfall is reported rather than treated as clean.
        if (summary.PacketsUnexamined > 0)
        {
            builder.ChannelSkipped(
                Channels.Xfa + " (partial)",
                $"{summary.PacketsUnexamined} of {summary.PacketsExamined + summary.PacketsUnexamined} " +
                "XFA packet(s) would not parse as XML");
        }

        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Document-level: an XFA value has no laid-out box until #1547's
            // layout can supply one, and inventing a position would be worse
            // than admitting there is none.
            builder.AddFinding(RecoveredFinding.Certain(
                Channels.Xfa, $"XFA field {value.FieldPath}", value.Value, location: null));
        }
    }

    /// <summary>
    /// #1608 — image data still in the file but not shown: an orphaned
    /// original left by a replace-rather-than-remove edit, or one hidden behind
    /// a fully transparent soft mask. Neither is drawn, so the covered-image
    /// channel cannot see them.
    /// </summary>
    /// <summary>
    /// #1589 — for each mark, what could fit it. The font and size come from
    /// the surviving glyphs nearest the mark on the same line, because a mark
    /// carries no font of its own and the removed run almost certainly used its
    /// neighbours'.
    /// </summary>
    private static void AddMarkFits(
        PdfDocument document,
        IReadOnlyList<RedactionMark> marks,
        RecoveryReportBuilder builder,
        IReadOnlyList<string>? dictionary,
        CancellationToken cancellationToken)
    {
        foreach (var byPage in marks.GroupBy(m => m.PageNumber))
        {
            IReadOnlyList<Text.Letter> letters;
            try { letters = document.GetPage(byPage.Key).Letters; }
            catch { continue; }
            if (letters.Count == 0) continue;

            foreach (var mark in byPage)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var anchor = NearestAnchor(letters, mark.Rect);
                if (anchor == null) continue;

                var baseFont = ResolveBaseFont(document, byPage.Key, anchor.FontName);
                var size = anchor.FontSize > 0 ? anchor.FontSize : 12;

                // A redaction box is drawn AROUND the run it covers, so its
                // width is an upper bound on the removed text. Half an em of
                // padding is the usual producer habit; treating the box width
                // as an equality silently rejects the right answer.
                var budget = RedactionFitAnalyzer.WidthBudget.FromMark(mark.Rect.Width, size);

                // #1589's neighbour-shift half: the surviving glyphs on either
                // side bound the same span independently, and tighter — a box
                // can be drawn generously, a glyph cannot be drawn where the
                // next one already is. Intersecting the two is where excise's
                // exact content-stream positions beat a pixel measurement.
                var gap = NeighbourGap(letters, mark.Rect);
                if (gap.WidthPt > 0)
                {
                    budget = RedactionFitAnalyzer.WidthBudget.Intersect(budget,
                        RedactionFitAnalyzer.WidthBudget.FromNeighbourGap(
                            gap.WidthPt,
                            RedactionFitAnalyzer.SpaceAdvancePt(baseFont, size),
                            gap.OpenSides));
                }

                var fit = RedactionFitAnalyzer.Analyse(budget, baseFont, size, dictionary);
                builder.AddFit(mark.Id, fit);
            }
        }
    }

    /// <summary>
    /// The surviving glyph nearest the mark on roughly its baseline. Vertical
    /// proximity first: a glyph on the same line is the one whose font the
    /// removed run shared, even if a closer glyph sits on the line above.
    /// </summary>
    private static Text.Letter? NearestAnchor(
        IReadOnlyList<Text.Letter> letters, Document.PdfRectangle mark)
    {
        var m = mark.Normalize();
        var midY = (m.Bottom + m.Top) / 2.0;
        return letters
            .Where(l => l.GlyphRectangle.Bottom <= m.Top && l.GlyphRectangle.Top >= m.Bottom)
            .OrderBy(l => Math.Min(
                Math.Abs(l.GlyphRectangle.Right - m.Left),
                Math.Abs(l.GlyphRectangle.Left - m.Right)))
            .FirstOrDefault()
            ?? letters
                .OrderBy(l => Math.Abs((l.GlyphRectangle.Bottom + l.GlyphRectangle.Top) / 2.0 - midY))
                .FirstOrDefault();
    }

    /// <summary>
    /// #1589 — the glyph-to-glyph gap across the mark: from the right edge of
    /// the last surviving glyph that ENDS before it to the left edge of the
    /// first that BEGINS after it, both on the mark's own baseline.
    ///
    /// <para>Returns 0 unless the mark is genuinely bracketed. A mark at the
    /// start or end of a line has only one neighbour, and inventing the other
    /// from the page box would produce a budget far looser than the mark's own
    /// width while looking like a tighter measurement.</para>
    /// </summary>
    private readonly record struct MarkGap(double WidthPt, int OpenSides);

    private static MarkGap NeighbourGap(
        IReadOnlyList<Text.Letter> letters, Document.PdfRectangle mark)
    {
        var m = mark.Normalize();
        var onLine = letters
            .Where(l => l.GlyphRectangle.Bottom <= m.Top && l.GlyphRectangle.Top >= m.Bottom)
            .ToList();
        if (onLine.Count == 0) return default;

        double? left = null, right = null;
        var leftIsSpace = false;
        var rightIsSpace = false;
        foreach (var l in onLine)
        {
            var r = l.GlyphRectangle.Normalize();
            if (r.Right <= m.Left + 0.01 && (left == null || r.Right > left))
            {
                left = r.Right;
                leftIsSpace = IsWhitespace(l.Value);
            }
            if (r.Left >= m.Right - 0.01 && (right == null || r.Left < right))
            {
                right = r.Left;
                rightIsSpace = IsWhitespace(l.Value);
            }
        }

        if (left == null || right == null) return default;
        var gap = right.Value - left.Value;
        if (gap <= 0) return default;

        // A side bounded by a SURVIVING space needs no slack: the space is
        // still drawn, so the removed run began exactly where it ends. Only a
        // side bounded by real ink might have had a space eaten with the text.
        var open = (leftIsSpace ? 0 : 1) + (rightIsSpace ? 0 : 1);
        return new MarkGap(gap, open);
    }

    private static bool IsWhitespace(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsWhiteSpace);

    /// <summary>
    /// Letter.FontName is the RESOURCE name (/F1); metrics are keyed by
    /// /BaseFont (Helvetica). Resolving is what keeps the fit analysis off the
    /// no-metrics path for every standard-14 document.
    /// </summary>
    private static string ResolveBaseFont(PdfDocument document, int pageNumber, string resourceName)
    {
        try
        {
            foreach (var (name, font) in document.GetPage(pageNumber).GetFonts())
            {
                if (!string.Equals(name, resourceName, StringComparison.Ordinal)) continue;
                var baseFont = font.GetNameOrNull("BaseFont");
                if (!string.IsNullOrEmpty(baseFont)) return baseFont!;
            }
        }
        catch { /* fall through to the resource name */ }
        return resourceName;
    }

    private static void AddImageLayerLeaks(
        PdfDocument document, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        builder.ChannelRan(Channels.ImageLayer);
        foreach (var leak in ImageLayerRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No page location: an orphan is referenced by no page, and a
            // masked original's position tells you nothing useful about a leak
            // whose subject is the object itself.
            builder.AddFinding(RecoveredFinding.PresentOnly(
                Channels.ImageLayer, leak.Description, location: null));
        }
    }

    private static void AddFormFields(
        PdfDocument document, RecoveryReportBuilder builder,
        IReadOnlySet<string> visibleCarrierText, CancellationToken cancellationToken)
    {
        builder.ChannelRan(Channels.FormField);
        foreach (var field in FormFieldValueRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A field whose value its own widget paints is not hidden — it is a
            // filled form. The carrier scan already decided that (it compares the
            // value against what the widget draws); this channel reads /V
            // directly and would otherwise call every filled field a leak.
            if (visibleCarrierText.Contains(field.Value)) continue;

            var location = field.PageNumber > 0 && field.Rect is { } rect
                ? new RecoveryLocation(field.PageNumber, rect, "widget /Rect")
                : null;
            builder.AddFinding(RecoveredFinding.Certain(
                Channels.FormField, $"form field /V ({field.FieldName})", field.Value, location));
        }
    }
}
