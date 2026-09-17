using System.Collections.Generic;
using Excise.Core.Writing;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// One choice in the Reduce File Size dialog (#1550).
/// </summary>
/// <param name="Preset">The optimizer preset.</param>
/// <param name="Label">What the list shows.</param>
/// <param name="Description">What the preset does, in the user's terms.</param>
public sealed record ReduceFileSizeChoice(PdfOptimizationPreset Preset, string Label, string Description)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// ViewModel for the "Reduce File Size" dialog (#1550). Picks a preset and
/// nothing else; it never touches a document, the same decoupling
/// <see cref="BatesNumberingDialogViewModel"/> uses.
/// </summary>
public sealed class ReduceFileSizeDialogViewModel : ReactiveObject
{
    /// <summary>The presets, safest first.</summary>
    public static IReadOnlyList<ReduceFileSizeChoice> Choices { get; } =
    [
        new(PdfOptimizationPreset.Lossless, "Lossless",
            "Recompress and deduplicate data and drop page thumbnails. Pages look exactly the same."),
        new(PdfOptimizationPreset.High, "High quality (300 dpi)",
            "Also downsample images sharper than 375 dpi to 300 dpi. Suitable for printing."),
        new(PdfOptimizationPreset.Standard, "Standard (150 dpi)",
            "Also downsample images sharper than 188 dpi to 150 dpi. Good for email and upload portals."),
        new(PdfOptimizationPreset.Screen, "Screen (96 dpi)",
            "Also downsample images sharper than 120 dpi to 96 dpi. Smallest; images look soft when printed."),
    ];

    private ReduceFileSizeChoice _selected = Choices[0];

    /// <summary>The chosen preset. Lossless unless the user picks another.</summary>
    public ReduceFileSizeChoice Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value ?? Choices[0]);
    }

    /// <summary>True when the user pressed Continue rather than Cancel.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Record the user's confirmation. Called by the dialog.</summary>
    public void Confirm() => Confirmed = true;
}
