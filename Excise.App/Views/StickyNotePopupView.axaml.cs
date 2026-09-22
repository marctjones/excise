using Avalonia.Controls;
using Avalonia.Threading;

namespace Excise.App.Views;

/// <summary>
/// Code-behind for the interactive sticky-note popup (#1788). The one thing
/// this does beyond <c>InitializeComponent</c> is give the text box focus the
/// moment a note's popup opens — a real user always wants to start typing
/// immediately on placement/reopen, and that is view behavior (where the
/// caret goes), not business logic.
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
}
