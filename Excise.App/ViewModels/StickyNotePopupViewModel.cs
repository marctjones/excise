using Excise.Core.Document;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

/// <summary>
/// The interactive post-it card (#1788, reworked to a real post-it look by
/// #1794) — bound by <c>Views/StickyNotePopupView.axaml</c>. One instance
/// exists while a note is being placed or edited in place; <see cref="MainWindowViewModel.StickyNotePopup"/>
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

    private double _cardWidthDips = 200;

    /// <summary>
    /// The card's on-screen size in DIPs (#1794) — set by MainWindow's
    /// code-behind from <c>PdfViewerControl.GetViewerPositionForPageRect</c>,
    /// the one coordinate-conversion boundary this feature crosses, so the
    /// editing overlay lands at exactly the size/position the RESTING card
    /// (SkiaRenderer's <c>RenderStickyNoteDefault</c>) rendered — "no visual
    /// jump" between resting and editing. Defaulted rather than nullable so
    /// the view has a sane size for the one frame before the host's first
    /// positioning pass runs.
    /// </summary>
    public double CardWidthDips
    {
        get => _cardWidthDips;
        set => this.RaiseAndSetIfChanged(ref _cardWidthDips, value);
    }

    private double _cardHeightDips = 150;

    /// <summary>See <see cref="CardWidthDips"/>.</summary>
    public double CardHeightDips
    {
        get => _cardHeightDips;
        set => this.RaiseAndSetIfChanged(ref _cardHeightDips, value);
    }

    private string _text;

    /// <summary>The text being typed, two-way bound to the card's TextBox.</summary>
    public string Text
    {
        get => _text;
        set => this.RaiseAndSetIfChanged(ref _text, value);
    }

    /// <summary>
    /// Commits the text (create if new, update if editing an existing note)
    /// and collapses the card back to its resting-state rendering. Click-away
    /// (<c>MainWindowViewModel.CommitOpenStickyNotePopup()</c>, called by
    /// MainWindow's light-dismiss handler for a press OUTSIDE this control's
    /// visual tree) and Escape (a KeyBinding in the view — #1794, replacing
    /// the old "Done" button as the keyboard-only out) both invoke this same
    /// command.
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
