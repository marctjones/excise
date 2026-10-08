using Avalonia.Controls;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// "Reduce File Size" dialog (#1550). Picks a preset; the caller reads
/// <see cref="ReduceFileSizeDialogViewModel.Confirmed"/> and
/// <see cref="ReduceFileSizeDialogViewModel.Selected"/> after it closes.
/// </summary>
internal partial class ReduceFileSizeDialog : Window
{
    public ReduceFileSizeDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Commands live in the view model (#1992); this only turns its
    // CloseRequested into closing the window, as SecurityDialog does.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ReduceFileSizeDialogViewModel viewModel)
            viewModel.CloseRequested += (_, _) => Close();
    }
}
