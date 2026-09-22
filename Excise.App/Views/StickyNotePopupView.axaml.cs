using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// Code-behind for the interactive post-it card (#1788, reworked to a real
/// post-it look by #1794). Two things beyond <c>InitializeComponent</c>, both
/// view mechanics rather than business logic:
/// <list type="bullet">
/// <item>Give the text box focus the moment a note's card opens — a real
/// user always wants to start typing immediately on placement/reopen.</item>
/// <item>Route Escape to the already-existing <see cref="StickyNotePopupViewModel.CommitCommand"/>
/// — the same "handle the key in code-behind, route to a command" pattern
/// <c>MainWindow.OnSearchTextBoxKeyDown</c> already uses, rather than a
/// declarative <c>KeyBinding</c> (either is legitimate; this one matches the
/// existing precedent).</item>
/// </list>
/// </summary>
public partial class StickyNotePopupView : UserControl
{
    public StickyNotePopupView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext == null)
                return;

            var textBox = this.FindControl<TextBox>("NoteTextBox");
            if (textBox == null)
                return;

            // Deferred: the control must finish laying out under its new
            // DataContext before Focus()/SelectAll() land on real elements.
            Dispatcher.UIThread.Post(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            });
        };
    }

    /// <summary>Escape commits and collapses the card (#1794) — the keyboard-only out that replaced the old "Done" button.</summary>
    private void OnNoteTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        if (DataContext is not StickyNotePopupViewModel viewModel)
            return;

        viewModel.CommitCommand.Execute().Subscribe();
        e.Handled = true;
    }
}
