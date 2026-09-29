using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Excise.Core.Document;
using Excise.Core.Text;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The one place a field becomes an input control, shared by the single-page
/// overlay and the continuous view's per-slot overlay (#1807) so the two can
/// never fill a field differently. An input reports its edits to the
/// <see cref="IFormFieldEditSink"/> it was built with (#1842).
/// </summary>
internal static class FormFieldInputFactory
{
    /// <summary>An input for <paramref name="field"/>, sized and dressed; null for a field type with no input.</summary>
    internal static Control? Build(PdfField field, double dipW, double dipH, int tabIndex, IFormFieldEditSink sink)
    {
        Control? input = field.FieldType switch
        {
            PdfFieldType.Text   => CreateTextFieldInput(field, dipW, dipH, sink),
            PdfFieldType.Choice => CreateChoiceFieldInput(field, sink),
            PdfFieldType.Button => CreateButtonFieldInput(field, sink),
            _ => null,
        };
        if (input == null) return null;

        input.Width = dipW;
        input.Height = dipH;
        ApplyFormFieldChrome(input, field, tabIndex);
        return input;
    }

    /// <summary>Order fields the way a person reads a form: top to bottom, then left to right.</summary>
    internal static List<PdfField> OrderFormFieldsForTabbing(IEnumerable<PdfField> fields) => fields
        .Where(field => field.Rect.HasValue)
        .OrderByDescending(field => field.Rect!.Value.Top)
        .ThenBy(field => field.Rect!.Value.Left)
        .ThenBy(field => field.FullName, StringComparer.Ordinal)
        .ToList();

    /// <summary>Names and rectangles only: a typed value must not look like a changed page.</summary>
    internal static int FormFieldSetSignature(IEnumerable<PdfField> fields)
    {
        var hash = new HashCode();
        foreach (var field in fields)
        {
            hash.Add(field.FullName, StringComparer.Ordinal);
            hash.Add(field.Rect);
        }
        return hash.ToHashCode();
    }

    // #1897: a text field authored as a small box (a form designer's way of
    // drawing a checkbox the user fills with an "x") reads oddly when its
    // content sticks to the left edge. Thresholds are in PDF points (the
    // field's own /Rect, not the viewer's zoomed dip size, so the box shape
    // that decides this doesn't change as the user zooms) and were picked
    // from a real one: the IRS W-9's classification-letter box is 28.8x11pt.
    // A generous margin above that keeps ordinary short fields (SSN/EIN
    // comb cells run 24pt tall) out: 40pt wide, 20pt tall.
    private const double CheckboxStyledFieldMaxWidthPt = 40;
    private const double CheckboxStyledFieldMaxHeightPt = 20;

    // #1898: a genuinely multi-line field (e.g. the W-9's "Requester's name
    // and address", 186x38pt) was getting FontSize = h*0.6 — sized as if the
    // whole box were one line, so a 38pt-tall box got a ~23pt font — with no
    // TextWrapping, so it still scrolled instead of wrapping. A multi-line
    // field uses its own /DA point size (falling back to a plain 10pt, the
    // same floor single-line fields already use) and wraps; box-height
    // scaling stays for single-line fields, unaffected by this fix.
    // The point size is converted to the overlay's own units (w/h are layer
    // dips, zoom- and DPI-scaled), read off the field's /Rect: a raw point
    // value rendered at 60% of its size in single-page view and did not
    // follow zoom in continuous view.
    private const double DefaultMultilineFontSizePt = 10;

    private static double LayerUnitsPerPoint(PdfField field, double layerHeight) =>
        field.Rect is { Height: > 0 } rect ? layerHeight / rect.Height : 1.0;

    private static TextBox CreateTextFieldInput(PdfField field, double w, double h, IFormFieldEditSink sink)
    {
        bool looksLikeCheckbox = field.Rect is { } rect
            && rect.Width <= CheckboxStyledFieldMaxWidthPt
            && rect.Height <= CheckboxStyledFieldMaxHeightPt;

        var box = new TextBox
        {
            Text = field.Value ?? string.Empty,
            IsReadOnly = field.IsReadOnly,
            AcceptsReturn = field.IsMultiline,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0x80)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xCC, 0xAA, 0x00)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            FontSize = field.IsMultiline
                ? (field.DefaultAppearanceFontSize ?? DefaultMultilineFontSizePt) * LayerUnitsPerPoint(field, h)
                : Math.Max(10, h * 0.6),
            TextWrapping = field.IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = field.IsMultiline
                ? global::Avalonia.Layout.VerticalAlignment.Top
                : global::Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = looksLikeCheckbox
                ? global::Avalonia.Layout.HorizontalAlignment.Center
                : global::Avalonia.Layout.HorizontalAlignment.Left,
            TextAlignment = looksLikeCheckbox ? TextAlignment.Center : TextAlignment.Left,
        };

        // Commit on Enter (single-line), Ctrl+Enter (multiline), or focus loss.
        // Escape restores the last committed value.
        //
        // Focus loss commits only text the user changed since the last commit.
        // Without that, Undo (which rebuilds the page and so blurs the old box)
        // re-applied the box's stale text and undid the undo (#1660).
        var committedText = box.Text ?? string.Empty;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !field.IsMultiline)
            {
                CommitTextBox();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && field.IsMultiline && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                CommitTextBox();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.Text = field.Value ?? string.Empty;
                committedText = box.Text ?? string.Empty;
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CommitTextBox();
        return box;

        // A refused value must not stay on screen as if it had been stored.
        void CommitTextBox()
        {
            if (string.Equals(box.Text ?? string.Empty, committedText, StringComparison.Ordinal))
                return;
            if (!CommitFieldEdit(field, box.Text, sink))
                box.Text = field.Value ?? string.Empty;
            committedText = box.Text ?? string.Empty;
        }
    }

    // #1446: an option is document-authored text. Show it escaped; SelectedItem stays the raw string.
    private static readonly global::Avalonia.Controls.Templates.FuncDataTemplate<string> EscapedLabelTemplate =
        new((option, _) => new TextBlock { Text = UnicodeTextSafety.EscapeForDisplay(option) });

    private static Control CreateChoiceFieldInput(PdfField field, IFormFieldEditSink sink)
    {
        var combo = new ComboBox
        {
            ItemsSource = field.Options,
            ItemTemplate = EscapedLabelTemplate,
            SelectedItem = field.Value,
            IsEnabled = !field.IsReadOnly,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0x80, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x88, 0xAA)),
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string s && !CommitFieldEdit(field, s, sink))
                combo.SelectedItem = field.Value;
        };
        combo.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                combo.SelectedItem = field.Value;
                e.Handled = true;
            }
        };
        return combo;
    }

    private static Control CreateButtonFieldInput(PdfField field, IFormFieldEditSink sink)
    {
        if (field.ButtonExportValues.Count > 1)
        {
            var options = field.ButtonExportValues;
            var combo = new ComboBox
            {
                ItemsSource = options,
                ItemTemplate = EscapedLabelTemplate,
                SelectedItem = options.Contains(field.Value ?? string.Empty) ? field.Value : null,
                IsEnabled = !field.IsReadOnly,
                Background = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0xAA, 0x44)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x88, 0x33)),
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string s && !CommitFieldEdit(field, s, sink))
                    combo.SelectedItem = field.Value;
            };
            return combo;
        }

        // Treat any value other than "Off"/null as checked for the MVP.
        // Acrobat stores radio-button states as the option's name (e.g.
        // "/Choice1"), so this works for both checkbox and the simplest
        // single-radio case.
        bool FieldIsOn() => !string.IsNullOrEmpty(field.Value)
            && !string.Equals(field.Value, "Off", StringComparison.OrdinalIgnoreCase);
        var checkBox = new CheckBox
        {
            IsChecked = FieldIsOn(),
            IsEnabled = !field.IsReadOnly,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0xAA, 0x44)),
        };
        checkBox.IsCheckedChanged += (_, _) =>
        {
            // A refusal sets the box back, which raises this again with nothing to store.
            if (checkBox.IsChecked == FieldIsOn()) return;
            if (!CommitFieldEdit(field, checkBox.IsChecked == true ? "Yes" : "Off", sink))
                checkBox.IsChecked = FieldIsOn();
        };
        return checkBox;
    }

    /// <summary>
    /// How much of a field overlay's colour shows at rest. The tint and border colours are
    /// the ones the controls are built with; at rest they are faint so a dense form (an IRS
    /// 1040 has 128 fields on page 1) stays readable, the way Acrobat and Firefox draw a
    /// field. Hovering a field shows its full border, and focus shows the blue one.
    /// </summary>
    private const byte RestingTintAlpha = 0x0A;
    private const byte RestingBorderAlpha = 0x48;

    private static void ApplyFormFieldChrome(Control input, PdfField field, int tabIndex)
    {
        input.TabIndex = tabIndex;
        input.IsEnabled = input.IsEnabled && !field.IsReadOnly;
        // The theme gives TextBox, ComboBox and CheckBox a 32 x 64 minimum size. On a form
        // with 10 pt boxes that drew every overlay three times too tall and pushed its edge
        // over the fields around it, so the overlay must be exactly the field's rectangle.
        input.MinWidth = 0;
        input.MinHeight = 0;
        // #1205: the field's fully-qualified NAME is an identifier the document
        // supplies and the tooltip is where the user reads it. Display only —
        // an edit is written to the field itself, never looked up by name (#1865).
        ToolTip.SetTip(input, UnicodeTextSafety.EscapeForDisplay(field.FullName));

        var (tint, border) = FieldOverlayBrushes(input);
        var focused = false;
        var hovered = false;
        void Apply() => SetFormFieldChrome(input, focused, hovered, tint, border);

        Apply();
        input.GotFocus += (_, _) => { focused = true; Apply(); };
        input.LostFocus += (_, _) => { focused = false; Apply(); };
        input.PointerEntered += (_, _) => { hovered = true; Apply(); };
        input.PointerExited += (_, _) => { hovered = false; Apply(); };
    }

    private static (Color Tint, Color Border) FieldOverlayBrushes(Control input) => input switch
    {
        TextBox t => (ColorOf(t.Background), ColorOf(t.BorderBrush)),
        ComboBox c => (ColorOf(c.Background), ColorOf(c.BorderBrush)),
        CheckBox k => (ColorOf(k.Background), ColorOf(k.BorderBrush)),
        _ => (Colors.Transparent, Colors.Transparent),
    };

    private static Color ColorOf(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color : Colors.Transparent;

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static void SetFormFieldChrome(Control input, bool focused, bool hovered, Color tint, Color border)
    {
        var loud = focused || hovered;
        var tintBrush = new SolidColorBrush(loud ? tint : WithAlpha(tint, Math.Min(tint.A, RestingTintAlpha)));
        var borderBrush = new SolidColorBrush(focused
            ? Color.FromArgb(0xFF, 0x00, 0x5F, 0xCC)
            : hovered ? border : WithAlpha(border, Math.Min(border.A, RestingBorderAlpha)));
        var thickness = new Thickness(focused ? 2 : 1);

        switch (input)
        {
            case TextBox textBox:
                textBox.Background = tintBrush;
                textBox.BorderBrush = borderBrush;
                textBox.BorderThickness = thickness;
                break;
            case ComboBox comboBox:
                comboBox.Background = tintBrush;
                comboBox.BorderBrush = borderBrush;
                comboBox.BorderThickness = thickness;
                break;
            case CheckBox checkBox:
                checkBox.Background = tintBrush;
                checkBox.BorderBrush = borderBrush;
                checkBox.BorderThickness = thickness;
                break;
        }
    }

    /// <returns>
    /// False when the host or the field refused the value (the input should show
    /// the stored value again); true when it was stored or was already the value.
    /// </returns>
    private static bool CommitFieldEdit(PdfField field, string? newValue, IFormFieldEditSink sink)
    {
        // Skip a no-op assignment so we don't fire spurious re-render events.
        if (string.Equals(field.Value, newValue, StringComparison.Ordinal)) return true;
        if (!sink.AdmitEdit()) return false;
        var oldValue = field.Value;
        try
        {
            field.SetValue(newValue);
        }
        catch (InvalidOperationException) { return false; } // read-only / signature
        catch (ArgumentException ex)
        {
            // Choice value not in /Opt, or (#1671) text the field's font cannot
            // represent. Either way the value was not stored: say so.
            sink.EditRejected(field.FullName, ex.Message);
            return false;
        }

        sink.EditStored(field, newValue, oldValue);
        return true;
    }
}
