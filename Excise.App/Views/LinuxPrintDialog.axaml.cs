using Avalonia.Controls;
using Avalonia.Interactivity;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// The Linux printer chooser (#1710). Picks a CUPS queue, copies and a page
/// range; the caller reads
/// <see cref="LinuxPrintDialogViewModel.Ticket"/> after it closes, which is
/// null when the user cancelled.
/// </summary>
/// <remarks>
/// Print keeps the window open when the view model refuses what was typed, so
/// a bad page range is corrected rather than silently turned into a different
/// job. All the validation lives in the view model.
/// </remarks>
internal partial class LinuxPrintDialog : Window
{
    public LinuxPrintDialog()
    {
        InitializeComponent();
    }

    private void OnPrint(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LinuxPrintDialogViewModel viewModel && !viewModel.TryConfirm())
            return;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
