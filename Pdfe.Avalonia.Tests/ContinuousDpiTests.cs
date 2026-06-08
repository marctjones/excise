using AwesomeAssertions;
using Pdfe.Avalonia.Controls;
using Xunit;

namespace Pdfe.Avalonia.Tests;

/// <summary>
/// Unit tests for the zoom-aware continuous-render DPI selection (#371 pt1).
/// Pure logic — no rendering — so it runs in the non-flaky viewer-lib project.
/// </summary>
public class ContinuousDpiTests
{
    [Theory]
    [InlineData(1.0, 120)]   // at 100% zoom, render at the base DPI
    [InlineData(1.5, 180)]   // scales with zoom -> crisper
    [InlineData(2.0, 240)]   // at the cap
    [InlineData(4.0, 240)]   // deep zoom is clamped to the cap (bounds memory)
    [InlineData(0.5, 120)]   // never below the base DPI
    public void EffectiveContinuousDpi_ScalesWithZoom_AndClamps(double zoom, int expected)
        => PdfViewerControl.EffectiveContinuousDpi(120, zoom, PdfViewerControl.MaxContinuousDpi)
            .Should().Be(expected);
}
