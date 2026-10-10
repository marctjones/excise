using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Excise.App.Models;
using Excise.App.Services.Printing;
using Excise.Core.Editing;
using Excise.Core.Operations;
using Excise.Core.Security;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;

namespace Excise.App.Converters;

/// <summary>
/// Plain-language labels for the option enums shown in dialogs and Preferences
/// (#1998). Bindings still select the enum value, so stored settings and CLI
/// flags keep the identifiers; only what the user reads changes. UI wording
/// lives here, in the App layer, never in Excise.Core.
/// </summary>
internal sealed class EnumDisplayConverter : IValueConverter
{
    public static readonly EnumDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? null : Label(value);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>The label for <paramref name="value"/>; its identifier when no label is defined.</summary>
    public static string Label(object value) => TryLabel(value) ?? value.ToString() ?? string.Empty;

    /// <summary>The defined label, or null when <paramref name="value"/> has none (a new enum member).</summary>
    public static string? TryLabel(object value) => value switch
    {
        DocumentOpenMode v => v switch
        {
            DocumentOpenMode.Automatic => "Automatic (default)",
            DocumentOpenMode.NewWindow => "New window",
            DocumentOpenMode.NewTab => "New tab",
            DocumentOpenMode.ReplaceCurrent => "Replace current document",
            _ => null,
        },
        AppearanceMode v => v switch
        {
            AppearanceMode.System => "Match system (default)",
            AppearanceMode.Light => "Light",
            AppearanceMode.Dark => "Dark",
            _ => null,
        },
        PerformancePreset v => v switch
        {
            PerformancePreset.LowMemory => "Low memory",
            PerformancePreset.Balanced => "Balanced (default)",
            PerformancePreset.Fast => "Fast",
            PerformancePreset.Custom => "Custom",
            _ => null,
        },
        ReadingOrderStrategy v => v switch
        {
            ReadingOrderStrategy.ColumnAware => "Column by column (default)",
            ReadingOrderStrategy.Simple => "Top to bottom, left to right",
            ReadingOrderStrategy.RawStream => "As stored in the file",
            _ => null,
        },
        WhitespaceMode v => v switch
        {
            WhitespaceMode.Smart => "Keep paragraphs and lists (default)",
            WhitespaceMode.LineFaithful => "One line per visual line",
            _ => null,
        },
        CarrierScrubMode v => v switch
        {
            CarrierScrubMode.Strip => "Cut out the redacted text (default)",
            CarrierScrubMode.RemoveWhole => "Remove the whole value",
            CarrierScrubMode.ReportOnly => "Leave it and report it",
            _ => null,
        },
        RedactionProfile v => v switch
        {
            RedactionProfile.Standard => "Standard (default)",
            RedactionProfile.Maximum => "Maximum",
            _ => null,
        },
        WidthPolicy v => v switch
        {
            WidthPolicy.FixedMarker => "Fixed-size marker, line reflows",
            WidthPolicy.CollapsePreserveLayout => "Exact-width box, layout kept",
            WidthPolicy.OvershootPreserveLayout => "Widened box, layout kept (default)",
            WidthPolicy.CloseGap => "Close the gap, no box",
            WidthPolicy.QuantizeGap => "Gap rounded to a whole em, no box",
            _ => null,
        },
        PrintScalingMode v => v switch
        {
            PrintScalingMode.ShrinkOversized => "Shrink oversized pages (default)",
            PrintScalingMode.FitToPage => "Fit to page",
            PrintScalingMode.ActualSize => "Actual size",
            _ => null,
        },
        BatesPosition v => v switch
        {
            BatesPosition.TopLeft => "Top left",
            BatesPosition.TopCenter => "Top centre",
            BatesPosition.TopRight => "Top right",
            BatesPosition.BottomLeft => "Bottom left",
            BatesPosition.BottomCenter => "Bottom centre",
            BatesPosition.BottomRight => "Bottom right",
            _ => null,
        },
        PdfEncryptionAlgorithm v => v switch
        {
            PdfEncryptionAlgorithm.Aes256 => "AES-256 (recommended)",
            PdfEncryptionAlgorithm.Aes128 => "AES-128 (older readers)",
            _ => null,
        },
        _ => null,
    };
}
