using Avalonia.Controls;
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
        DataContextChanged += OnDataContextChanged;
    }

    // Commands live in the view model (#1992); this only turns its
    // CloseRequested into closing the window, as SecurityDialog does.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is LinuxPrintDialogViewModel viewModel)
            viewModel.CloseRequested += (_, _) => Close();
    }
}
