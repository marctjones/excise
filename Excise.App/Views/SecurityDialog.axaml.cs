using Avalonia.Controls;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// "Document Security" dialog (#641): set/change/remove password
/// protection. All state and behavior live in
/// <see cref="SecurityDialogViewModel"/> — this code-behind only wires the
/// view model's <see cref="SecurityDialogViewModel.CloseRequested"/> event
/// to actually closing the window, matching
/// <see cref="MakeSearchableDialog"/>'s code-behind.
/// </summary>
public partial class SecurityDialog : Window
{
    public SecurityDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is SecurityDialogViewModel viewModel)
        {
            viewModel.CloseRequested += (_, _) => Close();
        }
    }
}
