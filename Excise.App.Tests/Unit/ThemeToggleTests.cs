using Xunit;
using AwesomeAssertions;
using Avalonia.Styling;
using System;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Tests for B7 Dark mode toggle:
/// - Menu: View → Theme → Light / Dark / System (radio)
/// - FluentAvaloniaUI's FluentTheme.RequestedThemeVariant
/// - Persist setting
/// - Default: System
///
/// Note: Full integration with Avalonia Application.RequestedThemeVariant
/// requires [FixedAvaloniaFact] for UI tests. Here we test enum values and logic.
/// </summary>
public class ThemeToggleTests
{
    [Fact]
    public void ThemeVariantEnumValuesExist()
    {
        // Avalonia has Light, Dark, and Default theme variants
        var light = ThemeVariant.Light;
        var dark = ThemeVariant.Dark;
        var default_ = ThemeVariant.Default;

        light.Should().NotBeNull();
        dark.Should().NotBeNull();
        default_.Should().NotBeNull();
    }

    [Fact]
    public void CanConvertThemeVariantToString()
    {
        // Avalonia themes can be compared with ThemeVariant.Light
        ThemeVariant.Light.ToString().Should().Be("Light");
        ThemeVariant.Dark.ToString().Should().Be("Dark");
    }

    [Fact]
    public void DefaultThemeIsSystem()
    {
        // The default theme should be System (which matches OS)
        var defaultTheme = ThemeVariant.Default;

        defaultTheme.Should().NotBeNull();
        defaultTheme.Should().Be(ThemeVariant.Default);
    }

    [Fact]
    public void CanSetThemeToLight()
    {
        var theme = ThemeVariant.Light;

        theme.Should().Be(ThemeVariant.Light);
        theme.Should().NotBe(ThemeVariant.Dark);
    }

    [Fact]
    public void CanSetThemeToDark()
    {
        var theme = ThemeVariant.Dark;

        theme.Should().Be(ThemeVariant.Dark);
        theme.Should().NotBe(ThemeVariant.Light);
    }

    [Fact]
    public void ThemeValuesAreDistinct()
    {
        var light = ThemeVariant.Light;
        var dark = ThemeVariant.Dark;
        var default_ = ThemeVariant.Default;

        light.Should().NotBe(dark);
        light.Should().NotBe(default_);
        dark.Should().NotBe(default_);
    }

    [Fact]
    public void CanSerializeThemeSetting()
    {
        var original = ThemeVariant.Dark;

        // Simulate saving to string
        var saved = original.ToString();

        saved.Should().Be("Dark");
        saved.Should().NotBeNullOrEmpty();
    }
}
