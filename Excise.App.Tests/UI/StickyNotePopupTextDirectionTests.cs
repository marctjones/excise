using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1916 — the sticky-note edit-in-place <c>TextBox</c>
/// (<c>Views/StickyNotePopupView.axaml</c>'s <c>NoteTextBox</c>) had no
/// <c>FlowDirection</c> at all, so typing Arabic/Hebrew into a note showed
/// and edited it in the wrong direction — Avalonia's TextBox defaults to
/// LeftToRight regardless of content.
/// <see cref="StickyNotePopupViewModel.FlowDirection"/> now recomputes from
/// <see cref="StickyNotePopupViewModel.Text"/> by UAX #9's P2/P3
/// first-strong-character rule, bound in the view's XAML — these pin that
/// the bound control actually reflects it, both on open and while typing.
/// <c>TextAlignment</c> is deliberately left unbound: the TextBox's own
/// default (<c>Start</c>) already resolves to Left/Right from
/// <c>FlowDirection</c> (confirmed by the render-capture test below), so
/// binding it separately would only duplicate that for no visible effect.
/// </summary>
[Collection("AvaloniaTests")]
public class StickyNotePopupTextDirectionTests
{
    private static StickyNotePopupViewModel MakeViewModel(string initialText) =>
        new(
            pageNumber: 1,
            rect: new PdfRectangle(20, 20, 220, 170),
            displayRect: new PdfRectangle(20, 20, 220, 170),
            initialText: initialText,
            onCommit: _ => Task.CompletedTask);

    private static (TextBox textBox, Window window) OpenNoteTextBox(StickyNotePopupViewModel vm)
    {
        var view = new StickyNotePopupView { DataContext = vm };
        var window = new Window { Content = view, Width = 300, Height = 300 };
        window.Show();
        window.UpdateLayout();

        var textBox = view.FindControl<TextBox>("NoteTextBox");
        textBox.Should().NotBeNull("StickyNotePopupView.axaml must still name its edit box NoteTextBox");
        return (textBox!, window);
    }

    [FixedAvaloniaFact]
    public void PlainLatinText_IsLeftToRight()
    {
        var (textBox, _) = OpenNoteTextBox(MakeViewModel("Alpha review"));

        textBox.FlowDirection.Should().Be(FlowDirection.LeftToRight);
    }

    [FixedAvaloniaTheory]
    [InlineData("، Latin", false)]
    [InlineData("١٢٣ مرحبا", true)]
    [InlineData("\U00010400 שלום", false)]
    public void NeutralPrefixesAndUnicodeLetters_UseTheSharedEditorPolicy(string text, bool rtl)
    {
        var (textBox, window) = OpenNoteTextBox(MakeViewModel(text));
        try
        {
            textBox.FlowDirection.Should().Be(rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact]
    public void HebrewText_IsRightToLeft()
    {
        var (textBox, _) = OpenNoteTextBox(MakeViewModel("שלום עולם"));

        textBox.FlowDirection.Should().Be(FlowDirection.RightToLeft);
    }

    [FixedAvaloniaFact]
    public void ArabicText_IsRightToLeft()
    {
        var (textBox, _) = OpenNoteTextBox(MakeViewModel("مرحبا بالعالم"));

        textBox.FlowDirection.Should().Be(FlowDirection.RightToLeft);
    }

    /// <summary>
    /// UAX #9 P2/P3 is FIRST-strong, not "contains any RTL character" — a
    /// note that starts in English and only later mentions one RTL word
    /// (e.g. a proper noun) must stay left-to-right.
    /// </summary>
    [FixedAvaloniaFact]
    public void LatinTextContainingOneRtlWord_StaysLeftToRight()
    {
        var (textBox, _) = OpenNoteTextBox(MakeViewModel("Reviewed by Ahmad (أحمد) on Tuesday"));

        textBox.FlowDirection.Should().Be(FlowDirection.LeftToRight);
    }

    [FixedAvaloniaFact]
    public void TypingRtlTextAfterLatin_FlipsDirectionLive()
    {
        var vm = MakeViewModel("hello");
        var (textBox, _) = OpenNoteTextBox(vm);
        textBox.FlowDirection.Should().Be(FlowDirection.LeftToRight);

        vm.Text = "مرحبا";

        textBox.FlowDirection.Should().Be(FlowDirection.RightToLeft);
    }

    /// <summary>
    /// The property assertions above only pin the <c>FlowDirection</c> enum
    /// value; this renders the actual bound control and checks where the
    /// glyphs land, proving the unbound <c>TextAlignment</c> default really
    /// does follow <c>FlowDirection</c> rather than assuming it.
    /// </summary>
    [FixedAvaloniaFact]
    public void HebrewText_RendersInkOnTheRightSideOfTheBox_LatinOnTheLeft()
    {
        var (latinBox, latinWindow) = OpenNoteTextBox(MakeViewModel("hello"));
        var latinInk = InkBounds(Capture(latinBox));
        latinWindow.Close();

        var (hebrewBox, hebrewWindow) = OpenNoteTextBox(MakeViewModel("שלום"));
        var hebrewInk = InkBounds(Capture(hebrewBox));
        hebrewWindow.Close();

        latinInk.Should().NotBeNull("the Latin note must draw visible ink");
        hebrewInk.Should().NotBeNull("the Hebrew note must draw visible ink");

        var width = (int)latinBox.Bounds.Width;
        latinInk!.Value.minX.Should().BeLessThan(width / 2,
            "left-to-right text must start near the left edge");
        hebrewInk!.Value.maxX.Should().BeGreaterThan(width / 2,
            "right-to-left text must end near the right edge, not sit where LTR text would");
    }

    private static SKBitmap Capture(Control control)
    {
        var w = Math.Max(1, (int)control.Bounds.Width);
        var h = Math.Max(1, (int)control.Bounds.Height);
        using var rt = new RenderTargetBitmap(new PixelSize(w, h));
        rt.Render(control);
        using var ms = new MemoryStream();
        rt.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        return SKBitmap.Decode(ms) ?? throw new InvalidOperationException("could not decode capture");
    }

    private static (int minX, int maxX)? InkBounds(SKBitmap bmp)
    {
        int minX = int.MaxValue, maxX = int.MinValue;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Red < 150 && c.Green < 150 && c.Blue < 150)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
            }
        }
        return maxX >= minX ? (minX, maxX) : null;
    }
}
