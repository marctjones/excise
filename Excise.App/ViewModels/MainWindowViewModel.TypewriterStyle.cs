using System;
using Avalonia.Media;
using Excise.Core.Editing;
using Excise.Core.Graphics;
using ReactiveUI;

namespace Excise.App.ViewModels;

public partial class MainWindowViewModel
{
    // #781: styling UI for the type-over tool. The engine
    // (PdfTypewriterTextStyle + PdfTypewriterTextOperation.WithStyle) already
    // supports size/color/alignment; this wires a small inspector for the
    // ACTIVE box onto that engine. "Active" is the box the user last created,
    // typed into, or moved — there is no separate focus/select event, so the
    // OnTypewriterText* handlers set it (see MainWindowViewModel.Typewriter.cs).

    private Guid? _activeTypewriterOperationId;
    private double _typewriterFontSize = PdfTypewriterTextStyle.Default.FontSize;
    private Color _typewriterColor = Colors.Black;
    private int _typewriterAlignmentIndex; // 0 Left, 1 Center, 2 Right
    // Guards the sync-from-active-box path so pushing the active box's current
    // style into the inspector properties does not re-apply it back onto the op
    // (which would flood the undo stack with no-op style changes).
    private bool _suppressTypewriterStyleApply;

    /// <summary>
    /// The style inspector is shown only in typewriter mode and only when a box
    /// is active, so it never floats over a document with nothing to style.
    /// </summary>
    public bool IsTypewriterStyleInspectorVisible =>
        IsTypewriterMode && _activeTypewriterOperationId.HasValue;

    public double TypewriterFontSize
    {
        get => _typewriterFontSize;
        set
        {
            var clamped = Math.Clamp(value, 4, 96);
            if (Math.Abs(_typewriterFontSize - clamped) < 0.0001)
                return;
            this.RaiseAndSetIfChanged(ref _typewriterFontSize, clamped);
            ApplyInspectorStyleToActiveBox();
        }
    }

    public Color TypewriterColor
    {
        get => _typewriterColor;
        set
        {
            if (_typewriterColor == value)
                return;
            this.RaiseAndSetIfChanged(ref _typewriterColor, value);
            this.RaisePropertyChanged(nameof(TypewriterColorBrush));
            ApplyInspectorStyleToActiveBox();
        }
    }

    /// <summary>Preview swatch for the current type-over colour.</summary>
    public IBrush TypewriterColorBrush => new SolidColorBrush(_typewriterColor);

    public int TypewriterAlignmentIndex
    {
        get => _typewriterAlignmentIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (_typewriterAlignmentIndex == clamped)
                return;
            this.RaiseAndSetIfChanged(ref _typewriterAlignmentIndex, clamped);
            ApplyInspectorStyleToActiveBox();
        }
    }

    /// <summary>
    /// Command target for the preset colour swatches. Parameter is a hex string
    /// (e.g. <c>#D0021B</c>); keeps the colour picker dependency-free.
    /// </summary>
    public void SetTypewriterColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return;
        if (Color.TryParse(hex, out var color))
            TypewriterColor = color;
    }

    /// <summary>
    /// Marks a box as the inspector's target and pulls its current style into
    /// the inspector fields. Passing <c>null</c> (e.g. on delete or on leaving
    /// the mode) hides the inspector. Callers are the OnTypewriterText*
    /// handlers, so styling always follows the box the user is working with.
    /// </summary>
    private void SetActiveTypewriterOperation(Guid? operationId)
    {
        _activeTypewriterOperationId = operationId;

        if (operationId is Guid id)
        {
            var index = IndexOfTypewriterOperation(id);
            if (index >= 0)
                SyncInspectorFromStyle(TypewriterTextOperations[index].Style);
        }

        this.RaisePropertyChanged(nameof(IsTypewriterStyleInspectorVisible));
    }

    private void ClearActiveTypewriterOperation() => SetActiveTypewriterOperation(null);

    private void SyncInspectorFromStyle(PdfTypewriterTextStyle style)
    {
        _suppressTypewriterStyleApply = true;
        try
        {
            TypewriterFontSize = style.FontSize;
            TypewriterColor = ToAvaloniaColor(style.Color);
            TypewriterAlignmentIndex = (int)style.Alignment;
        }
        finally
        {
            _suppressTypewriterStyleApply = false;
        }
    }

    /// <summary>
    /// Style built from the current inspector values. Used both to restyle the
    /// active box and as the initial style for newly-drawn boxes, so "set the
    /// style, then draw" carries the chosen look forward.
    /// </summary>
    private PdfTypewriterTextStyle BuildInspectorStyle(PdfTypewriterTextStyle basedOn)
    {
        return new PdfTypewriterTextStyle(
            fontName: basedOn.FontName,
            fontSize: _typewriterFontSize,
            color: ToPdfColor(_typewriterColor),
            alignment: (Excise.Core.Graphics.TextAlignment)_typewriterAlignmentIndex,
            lineSpacing: basedOn.LineSpacing);
    }

    private PdfTypewriterTextStyle BuildInspectorStyle() =>
        BuildInspectorStyle(PdfTypewriterTextStyle.Default);

    private void ApplyInspectorStyleToActiveBox()
    {
        if (_suppressTypewriterStyleApply)
            return;
        if (_activeTypewriterOperationId is not Guid id)
            return;

        var index = IndexOfTypewriterOperation(id);
        if (index < 0)
            return;

        var current = TypewriterTextOperations[index].Style;
        var next = BuildInspectorStyle(current);
        if (StylesEqual(current, next))
            return;

        RecordTypewriterEdit("Change text style", () =>
        {
            var i = IndexOfTypewriterOperation(id);
            if (i < 0)
                return;
            TypewriterTextOperations[i] = TypewriterTextOperations[i].WithStyle(next);
            RefreshTypewriterEditState();
        });
    }

    private static bool StylesEqual(PdfTypewriterTextStyle a, PdfTypewriterTextStyle b) =>
        a.FontName == b.FontName
        && Math.Abs(a.FontSize - b.FontSize) < 0.0001
        && a.Color.Equals(b.Color)
        && a.Alignment == b.Alignment
        && Math.Abs(a.LineSpacing - b.LineSpacing) < 0.0001;

    private static Color ToAvaloniaColor(PdfColor color)
    {
        static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
        return Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B));
    }

    private static PdfColor ToPdfColor(Color color) =>
        new(color.R / 255.0, color.G / 255.0, color.B / 255.0);
}
