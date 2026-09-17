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
    public static RecoveryReportBuilder ScanInto(
        PdfDocument document,
        RecoveryReportBuilder builder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(builder);

        var marks = RedactionMarkDetector.Detect(document);
        builder.AddMarks(marks);

        AddHiddenText(document, builder, cancellationToken);
        AddMarkRegionText(document, marks, builder, cancellationToken);
        AddCarriers(document, builder, cancellationToken);
        AddMarkedContent(document, builder, cancellationToken);
        AddCoveredContent(document, builder, cancellationToken);
        AddFormFields(document, builder, cancellationToken);
        AddResidualArtefacts(document, builder, cancellationToken);
        AddXfaValues(document, builder, cancellationToken);

        return builder;
    }

    /// <summary>Convenience for a caller running only the document-only channels.</summary>
    public static RecoveryReport Scan(PdfDocument document, CancellationToken cancellationToken = default)
        => ScanInto(document, new RecoveryReportBuilder(), cancellationToken).Build();

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

    private static void AddCarriers(
        PdfDocument document, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        builder.ChannelRan(Channels.Carrier);
        foreach (var carrier in CarrierTextRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Page 0 means the carrier could not be placed. It keeps no
            // location, so the report files it document-level -- an honest
            // "this text is in the file somewhere" rather than a position the
            // channel does not have.
            var location = carrier.PageNumber > 0 && carrier.Rect is { } rect
                ? new RecoveryLocation(carrier.PageNumber, rect,
                    carrier.Carrier.StartsWith("annotation", StringComparison.Ordinal)
                        ? "annotation /Rect" : "MCID content")
                : null;
            builder.AddFinding(RecoveredFinding.Certain(
                Channels.Carrier, carrier.Carrier, carrier.Text, location));
        }
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
                $"{summary.PacketsUnexamined} XFA packet(s) would not parse as XML");
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

    private static void AddFormFields(
        PdfDocument document, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        builder.ChannelRan(Channels.FormField);
        foreach (var field in FormFieldValueRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = field.PageNumber > 0 && field.Rect is { } rect
                ? new RecoveryLocation(field.PageNumber, rect, "widget /Rect")
                : null;
            builder.AddFinding(RecoveredFinding.Certain(
                Channels.FormField, $"form field /V ({field.FieldName})", field.Value, location));
        }
    }
}
