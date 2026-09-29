using Avalonia;
using Excise.Core.Editing;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Where a <see cref="TypewriterEditorBox"/> reports what the user did to it
/// (#1842, <c>docs/architecture/pdf-viewer-control-architecture.md</c> §3.2).
/// The viewer implements it: it owns the page geometry, the focus registry
/// (#1648) and the events the host listens to.
/// </summary>
internal interface ITypewriterEditSink
{
    /// <summary>The box's text changed (every keystroke).</summary>
    void TextEdited(PdfTypewriterTextOperation operation, string text);

    /// <summary>The delete button, or Esc on an empty box (#780).</summary>
    void DeleteRequested(PdfTypewriterTextOperation operation);

    /// <summary>A move or resize ended; <paramref name="dipRect"/> is the box on the layer, not yet clamped.</summary>
    void BoundsChanged(PdfTypewriterTextOperation operation, Rect dipRect);

    /// <summary>Clamp a box being moved or resized onto the page.</summary>
    Rect NormalizeDipRect(Rect dipRect);

    /// <summary>The box's editor took the keyboard focus.</summary>
    void FocusEntered(PdfTypewriterTextOperation operation);

    /// <summary>The box's editor lost the keyboard focus. The box drops its own chrome after this.</summary>
    void FocusLeft(PdfTypewriterTextOperation operation);

    /// <summary>Esc on a non-empty box: leave editing and keep the text (#780).</summary>
    void ReleaseEditorFocus();
}
