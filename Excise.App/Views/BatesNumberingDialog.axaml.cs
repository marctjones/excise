using Avalonia.Controls;
using Avalonia.Interactivity;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// "Bates Numbering" dialog (#1306). Collects the stamp settings; the caller
/// reads <see cref="BatesNumberingDialogViewModel.Confirmed"/> and
/// <see cref="BatesNumberingDialogViewModel.ToOptions"/> after it closes.
/// </summary>
/// <remarks>
/// No logic here beyond recording which button was pressed — the stamp itself
/// is applied by <c>MainWindowViewModel</c> through
/// <c>BatesNumberingService</c>.
/// </remarks>
public partial class BatesNumberingDialog : Window
{
    public BatesNumberingDialog()
    {
        InitializeComponent();
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BatesNumberingDialogViewModel viewModel)
            viewModel.Confirm();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
