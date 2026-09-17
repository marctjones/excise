using Avalonia.Controls;
using Avalonia.Interactivity;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// "Reduce File Size" dialog (#1550). Picks a preset; the caller reads
/// <see cref="ReduceFileSizeDialogViewModel.Confirmed"/> and
/// <see cref="ReduceFileSizeDialogViewModel.Selected"/> after it closes.
/// </summary>
public partial class ReduceFileSizeDialog : Window
{
    public ReduceFileSizeDialog()
    {
        InitializeComponent();
    }

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ReduceFileSizeDialogViewModel viewModel)
            viewModel.Confirm();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
