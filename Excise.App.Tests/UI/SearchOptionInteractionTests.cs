using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
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
/// Drives the search-option checkboxes — Case sensitive, Whole words,
/// Regex (#1086), Hyphen = space and No space too — with a REAL mouse click, then re-runs the search with real
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

    /// <summary>The hyphenated and the closed spelling of "open source", nothing else.</summary>
    private const string SeparatorPageText = "an open-source and an opensource tool";

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

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task SeparatorOptions_DefaultToHyphenAsSpaceOn_AndClosedCompoundsOff()
    {
        var (window, vm) = await OpenWithSearchBar("separator-defaults", SeparatorPageText);
        try
        {
            vm.SearchHyphenAsSpace.Should().BeTrue("a hyphen and a space match each other by default, as in redaction");
            vm.SearchMatchClosedCompounds.Should().BeFalse("matching no separator is opt-in");
            OptionCheckbox(window, "Hyphen = space").IsChecked.Should().Be(true);
            OptionCheckbox(window, "No space too").IsChecked.Should().Be(false);
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task HyphenAsSpaceCheckboxClick_ReRunsTheSearch_AndDropsTheHyphenatedSpelling()
    {
        var (window, vm) = await OpenWithSearchBar("hyphenasspace", SeparatorPageText);
        try
        {
            await RunSearch(window, vm, "open source");
            vm.SearchMatches.Select(m => m.MatchedText).Should().BeEquivalentTo(["open-source"],
                "by default 'open source' finds the hyphenated spelling and not the closed one");

            await ClickOptionCheckbox(window, "Hyphen = space");
            vm.SearchHyphenAsSpace.Should().BeFalse("a real click must toggle the bound option");
            // No Enter: toggling an option re-runs the current search, like the others.
            await WaitForResults(vm, count: 0);
            vm.SearchMatches.Should().BeEmpty("with the option off a space matches only a space");

            AssertRecorded("Hyphen = space");
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task NoSpaceTooCheckboxClick_ReRunsTheSearch_AndAddsTheClosedSpelling()
    {
        var (window, vm) = await OpenWithSearchBar("closedcompounds", SeparatorPageText);
        try
        {
            await RunSearch(window, vm, "open source");
            vm.SearchMatches.Should().HaveCount(1);

            await ClickOptionCheckbox(window, "No space too");
            vm.SearchMatchClosedCompounds.Should().BeTrue("a real click must toggle the bound option");
            await WaitForResults(vm, count: 2);
            vm.SearchMatches.Select(m => m.MatchedText).Should().BeEquivalentTo(["open-source", "opensource"]);

            AssertRecorded("No space too");
        }
        finally { window.Close(); }
    }

    /// <summary>The accented and the plain spelling of "cafe", nothing else.</summary>
    private const string AccentPageText = "un café et un cafe";

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task IgnoreAccents_DefaultsOn_AndAClickReRunsTheSearch_DroppingTheAccentedSpelling()
    {
        var (window, vm) = await OpenWithSearchBar("ignoreaccents", AccentPageText);
        try
        {
            vm.SearchIgnoreAccents.Should().BeTrue("accents are ignored by default, as in redaction");
            OptionCheckbox(window, "Ignore accents").IsChecked.Should().Be(true);

            await RunSearch(window, vm, "cafe");
            vm.SearchMatches.Select(m => m.MatchedText).Should().BeEquivalentTo(["café", "cafe"],
                "by default 'cafe' finds both spellings, each shown as on the page");

            await ClickOptionCheckbox(window, "Ignore accents");
            vm.SearchIgnoreAccents.Should().BeFalse("a real click must toggle the bound option");
            await WaitForResults(vm, count: 1);
            vm.SearchMatches.Select(m => m.MatchedText).Should().BeEquivalentTo(["cafe"],
                "with the option off an accented letter matches only itself");

            AssertRecorded("Ignore accents");
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaTheory(Timeout = 60000)]
    [InlineData(1600)]
    [InlineData(1024)]
    [InlineData(700)]
    [InlineData(500)]
    public async Task EverySearchBarControl_StaysWhollyOnTheWindow_AtNarrowWidths(double width)
    {
        // Owner rule: a bar never scrolls and never clips a control out of reach.
        // The options row wraps instead.
        var (window, vm) = await OpenWithSearchBar($"layout-{width}", SeparatorPageText);
        try
        {
            window.Width = width;
            window.UpdateLayout();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            window.UpdateLayout();

            var searchBox = window.FindControl<TextBox>("SearchTextBox")!;
            var bar = searchBox.GetLogicalAncestors().OfType<Border>().First();
            var controls = bar.GetLogicalDescendants().OfType<Control>()
                .Where(c => c is CheckBox or Button or TextBox).ToList();
            controls.Should().HaveCountGreaterThanOrEqualTo(11, "six options, the box and four buttons");
            bar.GetLogicalDescendants().OfType<ScrollViewer>().Should().BeEmpty("the search bar never scrolls");

            foreach (var control in controls)
            {
                control.IsEffectivelyVisible.Should().BeTrue($"{Describe(control)} must stay reachable at {width}px");
                var topLeft = control.TranslatePoint(new Point(0, 0), window)!.Value;
                var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value;
                topLeft.X.Should().BeGreaterThanOrEqualTo(-0.5, $"{Describe(control)} at {width}px");
                bottomRight.X.Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5,
                    $"{Describe(control)} must end on the {width}px window, not past it");
                control.Bounds.Width.Should().BeGreaterThan(0, $"{Describe(control)} must not be squeezed to nothing");
            }

            foreach (var option in new[] { "Case sensitive", "Whole words", "Hyphen = space", "No space too", "Ignore accents", "Regex" })
                AutomationProperties.GetName(OptionCheckbox(window, option)).Should().NotBeNullOrWhiteSpace(
                    $"'{option}' needs an accessible name");
        }
        finally { window.Close(); }
    }

    private static string Describe(Control control) =>
        $"{control.GetType().Name} '{AutomationProperties.GetName(control)}'";

    private static CheckBox OptionCheckbox(MainWindow window, string content) =>
        window.GetLogicalDescendants().OfType<CheckBox>().Single(c => (c.Content as string) == content);

    private static async Task WaitForResults(MainWindowViewModel vm, int count)
    {
        // The re-run is debounced; wait for it to land with the expected count.
        for (var i = 0; i < 120; i++)
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            if (!vm.IsSearching && vm.SearchMatches.Count == count) return;
            await Task.Delay(25);
        }
    }

    private static void AssertRecorded(string checkboxContent) =>
        GuiInteractionRecorder.ObservedIds.Should().Contain(
            id => id.StartsWith($"MainWindow/CheckBox:{checkboxContent}\t", StringComparison.Ordinal),
            $"the real pointer click on the '{checkboxContent}' checkbox must reach the interaction-coverage " +
            "recorder under the id the inventory gives it");

    private static async Task<(MainWindow window, MainWindowViewModel vm)> OpenWithSearchBar(
        string tag, string pageText = PageText)
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-search-opts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{tag}.pdf");
        CreateTextPdf(path, pageText);

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

    private static void CreateTextPdf(string path, string pageText)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using var graphics = page.GetGraphics();
        graphics.DrawString(pageText, PdfFont.Helvetica(24), PdfBrush.Black, 72, 600);
        graphics.Flush();
        doc.Save(path);
    }
}
