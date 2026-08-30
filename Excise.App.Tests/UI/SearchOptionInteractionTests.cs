using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.App.Tests.UI.InteractionCoverage;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Drives the three search-option checkboxes — Case sensitive, Whole words,
/// Regex (#1086) — with a REAL mouse click, then re-runs the search with real
/// keyboard input and asserts the match set actually CHANGED in the way the
/// option promises. Not "the box toggled": #1000 decided whole-word matching,
/// and until now the checkbox that turns it on was clicked by no test at all,
/// so a broken binding on it would pass every gate.
///
/// <para>The page carries the words <c>alpha</c>, <c>alphabet</c> and
/// <c>ALPHA</c>, chosen so each option flips a specific, observable match:
/// whole-word rejects the <c>alphabet</c> substring; case-sensitive rejects
/// <c>ALPHA</c>; regex makes a metacharacter pattern that matches nothing as a
/// literal start matching.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class SearchOptionInteractionTests
{
    private const string PageText = "alpha alphabet ALPHA";

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task WholeWordsCheckboxClick_StopsMatchingTheSubstringInsideAnotherWord()
    {
        var (window, vm) = await OpenWithSearchBar("wholewords");
        try
        {
            await RunSearch(window, vm, "alpha");
            // Substring finds 'alpha' three times: standalone, inside 'alphabet', and 'ALPHA'.
            var before = vm.SearchMatches.Count;
            before.Should().Be(3, "substring search must find 'alpha' inside 'alphabet' as well as the two whole words");

            await ClickOptionCheckbox(window, "Whole words");
            vm.SearchWholeWords.Should().BeTrue("a real click on the Whole words box must toggle the bound option");

            await RunSearch(window, vm, "alpha");
            vm.SearchMatches.Count.Should().Be(2,
                "whole-word search must drop the 'alpha' embedded in 'alphabet' and keep only the two whole words");
            vm.SearchMatches.Should().OnlyContain(m => m.MatchedText.Equals("alpha", StringComparison.OrdinalIgnoreCase),
                "every whole-word match must be the standalone word, never a fragment of 'alphabet'");

            AssertRecorded("Whole words");
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task CaseSensitiveCheckboxClick_StopsMatchingTheDifferentlyCasedWord()
    {
        var (window, vm) = await OpenWithSearchBar("casesensitive");
        try
        {
            await RunSearch(window, vm, "alpha");
            vm.SearchMatches.Should().Contain(m => m.MatchedText == "ALPHA",
                "case-insensitive search must first find 'ALPHA'");

            await ClickOptionCheckbox(window, "Case sensitive");
            vm.SearchCaseSensitive.Should().BeTrue("a real click on the Case sensitive box must toggle the bound option");

            await RunSearch(window, vm, "alpha");
            vm.SearchMatches.Should().NotContain(m => m.MatchedText == "ALPHA",
                "case-sensitive search for 'alpha' must reject the upper-case 'ALPHA'");

            AssertRecorded("Case sensitive");
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task RegexCheckboxClick_MakesAMetacharacterPatternStartMatching()
    {
        var (window, vm) = await OpenWithSearchBar("regex");
        try
        {
            // "al.ha" as a LITERAL matches nothing (there is no dot in the text).
            await RunSearch(window, vm, "al.ha");
            vm.SearchMatches.Should().BeEmpty("as a literal, 'al.ha' cannot match text that contains no dot");

            await ClickOptionCheckbox(window, "Regex");
            vm.SearchUseRegex.Should().BeTrue("a real click on the Regex box must toggle the bound option");

            await RunSearch(window, vm, "al.ha");
            vm.SearchMatches.Should().NotBeEmpty(
                "as a regex, 'al.ha' must match 'alpha'/'ALPHA' — the option changed the engine, not just the box");

            AssertRecorded("Regex");
        }
        finally { window.Close(); }
    }

    private static void AssertRecorded(string checkboxContent) =>
        GuiInteractionRecorder.ObservedIds.Should().Contain(
            id => id.StartsWith($"MainWindow/CheckBox:{checkboxContent}\t", StringComparison.Ordinal),
            $"the real pointer click on the '{checkboxContent}' checkbox must reach the interaction-coverage " +
            "recorder under the id the inventory gives it");

    private static async Task<(MainWindow window, MainWindowViewModel vm)> OpenWithSearchBar(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-search-opts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{tag}.pdf");
        CreateTextPdf(path);

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        // Real Ctrl+F opens the search bar (and lays out its option checkboxes).
        await window.PressKeyAsync(Key.F, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        vm.IsSearchVisible.Should().BeTrue("Ctrl+F must open the search bar so its checkboxes lay out");
        window.UpdateLayout();

        return (window, vm);
    }

    private static async Task RunSearch(MainWindow window, MainWindowViewModel vm, string term)
    {
        vm.SearchText = term;
        var searchBox = window.FindControl<TextBox>("SearchTextBox");
        searchBox.Should().NotBeNull();
        // Real Enter in the search box runs the search immediately (skips debounce).
        searchBox!.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Route = RoutingStrategies.Bubble,
            Key = Key.Enter,
        });
        await WaitForSearch(vm);
    }

    private static async Task WaitForSearch(MainWindowViewModel vm)
    {
        // The search may not have flipped IsSearching on yet when we arrive, so
        // give it a beat, then wait for it to settle back to idle.
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(50);
        for (var i = 0; i < 80; i++)
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            if (!vm.IsSearching) return;
            await Task.Delay(25);
        }
    }

    private static async Task ClickOptionCheckbox(MainWindow window, string content)
    {
        var checkbox = window.GetLogicalDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(c => (c.Content as string) == content);
        checkbox.Should().NotBeNull($"the search bar must host a '{content}' checkbox");
        window.UpdateLayout();

        var center = new Point(checkbox!.Bounds.Width / 2, checkbox.Bounds.Height / 2);
        var inWindow = checkbox.TranslatePoint(center, window) ?? default;
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static void CreateTextPdf(string path)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using var graphics = page.GetGraphics();
        graphics.DrawString(PageText, PdfFont.Helvetica(24), PdfBrush.Black, 72, 600);
        graphics.Flush();
        doc.Save(path);
    }
}
