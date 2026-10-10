using Excise.Core.Text.Segmentation;
using Excise.Ocr;

namespace Excise.Cli.Commands;

/// <summary>
/// Turns <c>excise redact</c>'s flags into one <see cref="RedactionOptions"/>
/// and hands the file to <see cref="TermRedactionRunner"/>, the workflow the
/// GUI runs too (#1501). Both the interactive command and automation batches
/// call this boundary.
/// </summary>
internal static class RedactCommandHandler
{
    internal static TermRedactionResult Execute(
        RedactCommandRequest request,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        // #1089/#1187: the flags become one RedactionOptions; the workflow
        // itself is TermRedactionRunner, shared with the GUI (#1501).
        var profileOptions = RedactionOptions.ForProfile(request.Profile);
        var options = profileOptions with
        {
            CaseSensitive = request.CaseSensitive,
            WholeWord = request.WholeWord,   // #1052
            DrawBox = request.DrawBox,
            // A width flag is an explicit opt-in to its own trade-off; with
            // none, the PROFILE's policy stands (Standard keeps the layout,
            // Maximum closes the width: Refs #1715). The default lives in
            // RedactionOptions only.
            Width = request.CloseWidth ? WidthPolicy.CloseGap
                : request.OvershootBox ? WidthPolicy.OvershootPreserveLayout   // #1189
                : request.FixedMarker ? WidthPolicy.FixedMarker   // #1755
                : request.PreserveLayout ? WidthPolicy.CollapsePreserveLayout
                : request.QuantizeGap ? WidthPolicy.QuantizeGap   // #1754
                : profileOptions.Width,
            BoxColor = request.BoxColor,
            // #1188/#1169: per-carrier mode. An explicit --carrier-policy wins;
            // otherwise the PROFILE's policy stands. Falling back to
            // CarrierScrubPolicy.Default here would have silently downgraded
            // --profile maximum's RemoveWhole back to Strip on every carrier,
            // which is most of what that profile is (#1586).
            CarrierPolicy = request.CarrierPolicy ?? profileOptions.CarrierPolicy,
            KeepAttachments = request.KeepAttachments,   // #1572
        };

        return TermRedactionRunner.Execute(
            new TermRedactionRequest(request.InputPath, request.OutputPath, request.Text, options)
            {
                Password = request.Password,
                AllowDecrypt = request.AllowDecrypt,
                Strict = request.Strict,
                AllowLowConfidence = request.AllowLowConfidence,
                OcrImageText = request.OcrImageText,
                FlattenOcr = request.FlattenOcr,
            },
            progress,
            cancellationToken);
    }

    private static void Validate(RedactCommandRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrEmpty(request.Text);

        if (!request.DrawBox && request.BoxColor != null)
        {
            throw new ArgumentException(
                "--no-box and --box-color are mutually exclusive: --no-box draws no box to colour.");
        }

        // #1755/#1754/#1715: --close-width, --overshoot-box, --fixed-marker,
        // --quantize-gap and --preserve-layout are different, mutually
        // exclusive answers to the same width-policy question; picking more
        // than one is not "pick the last one wins".
        var widthFlagCount = (request.CloseWidth ? 1 : 0) + (request.OvershootBox ? 1 : 0) +
                             (request.FixedMarker ? 1 : 0) + (request.QuantizeGap ? 1 : 0) +
                             (request.PreserveLayout ? 1 : 0);
        if (widthFlagCount > 1)
        {
            throw new ArgumentException(
                "--close-width, --overshoot-box, --fixed-marker, --quantize-gap and --preserve-layout are mutually exclusive width policies.");
        }

        if (request.FlattenOcr &&
            (request.OcrImageText || !request.DrawBox || request.BoxColor != null ||
             request.CloseWidth || request.OvershootBox || request.FixedMarker || request.QuantizeGap ||
             request.PreserveLayout ||
             request.Strict || request.AllowLowConfidence || request.KeepAttachments))
        {
            throw new ArgumentException(
                "--flatten-ocr cannot be combined with structural-redaction box, width, confidence, OCR-layer, or attachment options.");
        }
    }
}

internal readonly record struct RedactCommandRequest(
    string InputPath,
    string OutputPath,
    string Text,
    bool CaseSensitive = false,
    bool AllowDecrypt = false,
    bool Strict = false,
    bool AllowLowConfidence = false,
    string? Password = null,
    bool CloseWidth = false,
    bool DrawBox = true,
    (double R, double G, double B)? BoxColor = null,
    bool OcrImageText = false,
    bool FlattenOcr = false,
    Excise.Core.Operations.CarrierScrubPolicy? CarrierPolicy = null,   // #1188/#1169
    bool WholeWord = false,   // #1052
    bool OvershootBox = false,   // #1189
    // #1755: opt IN to WidthPolicy.FixedMarker (Maximum's policy) under any
    // profile: closes the width, reflows the line, draws a fixed-size box.
    bool FixedMarker = false,
    bool KeepAttachments = false,   // #1572 — opt out of removing every attachment
    // #1586 — the output profile. Standard is the default on every path; the
    // CLI must not be the one front end that quietly ships less.
    Excise.Core.Text.Segmentation.RedactionProfile Profile
        = Excise.Core.Text.Segmentation.RedactionProfile.Standard,
    bool QuantizeGap = false,   // #1754: opt IN to WidthPolicy.QuantizeGap
    bool PreserveLayout = false);   // #1715: opt back IN to WidthPolicy.CollapsePreserveLayout
