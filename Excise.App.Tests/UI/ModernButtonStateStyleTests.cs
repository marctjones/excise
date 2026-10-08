using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using FluentAvalonia.Styling;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1801: the modern-button hover/pressed styles must reach what is DRAWN. FluentAvalonia's
/// Button theme sets Background/BorderBrush/Foreground on <c>PART_ContentPresenter</c> in its
/// own :pointerover/:pressed triggers, which beat the presenter's TemplateBinding to the
/// Button, so setters on the Button itself never show. The probe loads the real
/// FluentAvaloniaTheme and the real Controls.axaml, disables the presenter's brush
/// transitions and reads the presenter after a real headless pointer move and press.
/// </summary>
[Collection("AvaloniaTests")]
public class ModernButtonStateStyleTests
{
    private ResourceDictionary? _appResources;

    private static Color ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color ?? default;

    // The palette lives in Styles/Brushes.axaml (#1992); these tests pin that each
    // state's style REACHES the presenter (#1801), so they compare with the token
    // as the window resolves it rather than a copied hex value.
    private static Color Token(Window window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"{key} did not resolve");

    private async Task<(Window window, Button button, ContentPresenter presenter)> ShowAsync(string classes)
    {
        var window = new Window { Width = 300, Height = 200, RequestedThemeVariant = ThemeVariant.Light };
        window.Styles.Add(new FluentAvaloniaTheme());
        // Application level, exactly as App.axaml merges Brushes (its theme dictionaries
        // resolve against the window's ThemeVariant through the application's resource chain).
        var app = new ResourceDictionary();
        app.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Excise.App/"))
            { Source = new Uri("avares://Excise.App/Styles/Brushes.axaml") });
        Application.Current!.Resources.MergedDictionaries.Add(app);
        _appResources = app;
        window.Styles.Add(new StyleInclude(new Uri("avares://Excise.App/"))
            { Source = new Uri("avares://Excise.App/Styles/Controls.axaml") });

        var button = new Button { Content = "Probe", Width = 120, Height = 40, Classes = { } };
        foreach (var c in classes.Split(' ')) button.Classes.Add(c);
        window.Content = new StackPanel { Children = { button } };
        window.Show();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        button.ApplyTemplate();
        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
            .First(p => p.Name == "PART_ContentPresenter");
        presenter.Transitions = null;
        button.Transitions = null;
        return (window, button, presenter);
    }

    private void Close(Window window)
    {
        window.Close();
        Application.Current!.Resources.MergedDictionaries.Remove(_appResources!);
    }

    private static Point Center(Window window, Control c) =>
        c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window)!.Value;

    [FixedAvaloniaFact]
    public async Task ModernButton_Hover_ReachesThePresenter()
    {
        var (window, button, presenter) = await ShowAsync("modern-button");
        try
        {
            window.MouseMove(Center(window, button));
            await KeyboardTestHelpers.FlushDispatcherAsync();

            button.IsPointerOver.Should().BeTrue();
            ColorOf(presenter.Background).Should().Be(ColorOf((IBrush)window.FindResource("HoverBrush")!));
            ColorOf(presenter.BorderBrush).Should().Be(ColorOf((IBrush)window.FindResource("BrandPrimaryBrush")!));
            ColorOf(presenter.Foreground).Should().Be(ColorOf((IBrush)window.FindResource("TextPrimaryBrush")!));
        }
        finally { Close(window); }
    }

    [FixedAvaloniaFact]
    public async Task ModernButton_Pressed_ReachesThePresenter()
    {
        var (window, button, presenter) = await ShowAsync("modern-button");
        try
        {
            var p = Center(window, button);
            window.MouseMove(p);
            window.MouseDown(p, MouseButton.Left);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            button.IsPressed.Should().BeTrue();
            ColorOf(presenter.Background).Should().Be(ColorOf((IBrush)window.FindResource("PressedBrush")!));
            window.MouseUp(p, MouseButton.Left);
        }
        finally { Close(window); }
    }

    [FixedAvaloniaFact]
    public async Task PrimaryButton_Hover_KeepsWhiteTextOnRed()
    {
        var (window, button, presenter) = await ShowAsync("modern-button primary");
        try
        {
            window.MouseMove(Center(window, button));
            await KeyboardTestHelpers.FlushDispatcherAsync();

            ColorOf(presenter.Background).Should().Be(Token(window, "DangerHoverBrush"));
            ColorOf(presenter.Foreground).Should().Be(Colors.White,
                "FluentAvalonia's near-black hover foreground must not win over the primary button's white");
        }
        finally { Close(window); }
    }

    [FixedAvaloniaFact]
    public async Task ActiveToggle_Hover_KeepsTheAccentForeground()
    {
        var (window, button, presenter) = await ShowAsync("modern-button toggle active");
        try
        {
            window.MouseMove(Center(window, button));
            await KeyboardTestHelpers.FlushDispatcherAsync();

            ColorOf(presenter.Background).Should().Be(Token(window, "SelectedHoverBrush"));
            ColorOf(presenter.Foreground).Should().Be(Token(window, "BrandTextBrush"));
        }
        finally { Close(window); }
    }
}
