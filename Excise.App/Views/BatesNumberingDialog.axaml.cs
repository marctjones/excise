using Avalonia.Controls;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// "Bates Numbering" dialog (#1306). Collects the stamp settings; the caller
/// reads <see cref="BatesNumberingDialogViewModel.Confirmed"/> and
/// <see cref="BatesNumberingDialogViewModel.ToOptions"/> after it closes.
/// </summary>
/// <remarks>
/// No logic here beyond closing when the view model asks — the stamp itself
/// is applied by <c>MainWindowViewModel</c> through
/// <c>BatesNumberingService</c>.
/// </remarks>
internal partial class BatesNumberingDialog : Window
{
    public BatesNumberingDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Commands live in the view model (#1992); this only turns its
    // CloseRequested into closing the window, as SecurityDialog does.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is BatesNumberingDialogViewModel viewModel)
            viewModel.CloseRequested += (_, _) => Close();
    }
}
