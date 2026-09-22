using Excise.Core.Document;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

/// <summary>
/// The interactive yellow-note popup (#1788) — bound by
/// <c>Views/StickyNotePopupView.axaml</c>. One instance exists while a note
/// is being placed or reopened for editing; <see cref="MainWindowViewModel.StickyNotePopup"/>
/// is null the rest of the time and the view collapses to nothing.
///
/// <para>Deliberately holds no reference to <see cref="MainWindowViewModel"/>
/// or to <c>Excise.Core</c>'s document mutation surface — it only carries the
/// text being typed and where the note lives, and calls back into whatever
/// <see cref="MainWindowViewModel"/> supplied at construction. That keeps the
/// popup's own logic (a text box and a commit gesture) independent of how the
/// host chooses to persist it.</para>
/// </summary>
public sealed class StickyNotePopupViewModel : ReactiveObject
{
    /// <summary>1-based page the note lives on.</summary>
    public int PageNumber { get; }

    /// <summary>The note's own /Rect — the identity <see cref="AnnotationWorkflowService"/>'s
    /// rect-match uses to find the corresponding annotation in the save document.</summary>
    public PdfRectangle Rect { get; }

    private string _text;

    /// <summary>The text being typed, two-way bound to the popup's TextBox.</summary>
    public string Text
    {
        get => _text;
        set => this.RaiseAndSetIfChanged(ref _text, value);
    }

    /// <summary>
    /// Commits the text (create if new, update if editing an existing note)
    /// and collapses the popup back to just the icon — bound to the view's
    /// "Done" button. Click-away does the same thing through a different
    /// path: <c>MainWindowViewModel.CommitOpenStickyNotePopup()</c>, called
    /// by MainWindow's light-dismiss handler rather than through this
    /// command, since that trigger is a press OUTSIDE this control's own
    /// visual tree.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CommitCommand { get; }

    public StickyNotePopupViewModel(
        int pageNumber, PdfRectangle rect, string initialText, Func<string, Task> onCommit)
    {
        ArgumentNullException.ThrowIfNull(onCommit);
        PageNumber = pageNumber;
        Rect = rect;
        _text = initialText;
        CommitCommand = ReactiveCommand.CreateFromTask(() => onCommit(Text));
    }
}
