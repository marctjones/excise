using Avalonia.Controls;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// "Make Searchable" dialog (#658): language + force options, live OCR
/// progress, and a result summary. All state and behavior live in
/// <see cref="MakeSearchableDialogViewModel"/> — this code-behind only
/// wires the view model's <see cref="MakeSearchableDialogViewModel.CloseRequested"/>
/// event to actually closing the window, since a ViewModel can't close
/// its own view directly without a reference to it.
/// </summary>
public partial class MakeSearchableDialog : Window
{
    public MakeSearchableDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MakeSearchableDialogViewModel viewModel)
        {
            viewModel.CloseRequested += (_, _) => Close();
        }
    }
}
