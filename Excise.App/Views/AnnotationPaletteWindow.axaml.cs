using Avalonia.Controls;

namespace Excise.App.Views;

/// <summary>
/// #1789 — the floating annotation tool palette. Pure view: a compact column
/// of icon buttons bound (in the .axaml) to the same ICommand properties on
/// MainWindowViewModel that the Annotate menu and the optional annotation
/// toolbar row use. No command logic lives here; MainWindow's code-behind
/// owns creating, positioning, showing, hiding, and closing this window (see
/// MainWindow.axaml.cs's SetAnnotationPaletteVisibility), matching how it
/// owns other view-only mechanics like BeginMoveDrag for its own title bar.
/// </summary>
public partial class AnnotationPaletteWindow : Window
{
    public AnnotationPaletteWindow()
    {
        InitializeComponent();
    }
}
