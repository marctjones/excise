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

        builder.AddMarks(RedactionMarkDetector.Detect(document));

        AddHiddenText(document, builder, cancellationToken);
        AddCarriers(document, builder, cancellationToken);
        AddMarkedContent(document, builder, cancellationToken);
        AddCoveredContent(document, builder, cancellationToken);
        AddFormFields(document, builder, cancellationToken);

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
