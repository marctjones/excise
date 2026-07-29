using System;
using AwesomeAssertions;
using Avalonia;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Unit tests for the zoom-aware continuous-render DPI selection (#371 pt1).
/// Pure logic — no rendering — so it runs in the non-flaky viewer-lib project.
/// </summary>
public class ContinuousDpiTests
{
    [Theory]
    // zoom, renderScaling (device-pixel-ratio), expected DPI
    // --- standard display (dpr 1.0): behaviour is unchanged from before #682 ---
    [InlineData(1.0, 1.0, 120)]   // at 100% zoom, render at the base DPI
    [InlineData(1.5, 1.0, 180)]   // scales with zoom -> crisper
    [InlineData(2.0, 1.0, 240)]   // at the cap
    [InlineData(4.0, 1.0, 240)]   // deep zoom is clamped to the cap (bounds memory)
    [InlineData(0.5, 1.0, 120)]   // never below the base DPI
    // --- HiDPI / Retina (dpr 2.0): render scales with the device pixel ratio (#682/#683) ---
    [InlineData(1.0, 2.0, 240)]   // 100% on a 2x display -> 2x pixels = crisp, not upscaled
    [InlineData(1.5, 2.0, 360)]
    [InlineData(2.0, 2.0, 480)]   // the cap scales with dpr, so the same *visual* zoom stays crisp
    [InlineData(4.0, 2.0, 480)]   // clamped to the dpr-scaled cap
    [InlineData(0.5, 2.0, 240)]   // floor also scales with dpr
    public void EffectiveContinuousDpi_ScalesWithZoomAndDpr_AndClamps(double zoom, double dpr, int expected)
        => PdfViewerControl.EffectiveContinuousDpi(120, zoom, PdfViewerControl.MaxContinuousDpi, dpr)
            .Should().Be(expected);

    // The single-sliding-band tile request (TryCreateContinuousTileRequest) was
    // removed in the #848 content-addressed-grid rework. Its coverage / clip
    // self-consistency contract now lives, per grid cell, in
    // ContinuousTileGridTests.
}
