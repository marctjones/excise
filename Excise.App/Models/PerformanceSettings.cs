using System;

namespace Excise.App.Models;

/// <summary>
/// A named set of <see cref="PerformanceSettings"/>. <see cref="Custom"/> is
/// what the Preferences dialog shows once any individual value differs from
/// every named preset.
/// </summary>
public enum PerformancePreset
{
    LowMemory,
    Balanced,
    Fast,
    Custom,
}

/// <summary>
/// The viewer's memory/CPU trade-offs the user can change from Preferences.
/// Deliberately excludes everything that protects redaction correctness or
/// guards against OOM (letter cache, thumbnail render gate, pixel and decode
/// limits, DPI caps, GC configuration): those are not preferences.
/// </summary>
/// <param name="TileCacheBudgetMb">Continuous-view tile cache budget, MiB.</param>
/// <param name="SinglePageCachedPages">Single-page LRU capacity, bitmaps.</param>
/// <param name="ThumbnailPrewarm">Render every thumbnail in the background after a document opens.</param>
/// <param name="ThumbnailKeepMargin">Thumbnails kept in memory either side of the visible ones.</param>
/// <param name="SoftCacheTrims">Trim on deactivate/minimize/idle (#1478).</param>
/// <param name="IdleTrimSeconds">Idle delay before a soft trim.</param>
/// <param name="RenderThreads">Concurrent continuous-view band renders.</param>
public sealed record PerformanceSettings(
    int TileCacheBudgetMb,
    int SinglePageCachedPages,
    bool ThumbnailPrewarm,
    int ThumbnailKeepMargin,
    bool SoftCacheTrims,
    int IdleTrimSeconds,
    int RenderThreads)
{
    public const int MinTileCacheBudgetMb = 32;
    public const int MaxTileCacheBudgetMb = 1024;
    public const int MinSinglePageCachedPages = 1;
    public const int MaxSinglePageCachedPages = 24;
    public const int MinIdleTrimSeconds = 10;
    public const int MaxIdleTrimSeconds = 600;
    public const int MinRenderThreads = 1;

    /// <summary>
    /// Below the thumbnail prefetch margin (12) the keep window would evict what
    /// the prefetch pass just loaded, and every sidebar scroll would reload it.
    /// </summary>
    public const int MinThumbnailKeepMargin = 12;
    public const int MaxThumbnailKeepMargin = 500;

    /// <summary>
    /// Render threads may go up to the CPU count, but never below 2 so the
    /// Balanced default (at least 2) is representable on a one-core machine.
    /// </summary>
    public static int MaxRenderThreads => Math.Max(2, Environment.ProcessorCount);

    /// <summary>Today's defaults, exactly as the viewer and thumbnail sidebar build them.</summary>
    public static PerformanceSettings Balanced => new(
        TileCacheBudgetMb: 200,
        SinglePageCachedPages: 6,
        ThumbnailPrewarm: true,
        ThumbnailKeepMargin: 48,
        SoftCacheTrims: true,
        IdleTrimSeconds: 30,
        RenderThreads: Math.Clamp(Environment.ProcessorCount - 1, 2, 6));

    public static PerformanceSettings LowMemory => new(
        TileCacheBudgetMb: 64,
        SinglePageCachedPages: 2,
        ThumbnailPrewarm: false,
        ThumbnailKeepMargin: 16,
        SoftCacheTrims: true,
        IdleTrimSeconds: 15,
        RenderThreads: 2);

    public static PerformanceSettings Fast => new(
        TileCacheBudgetMb: 400,
        SinglePageCachedPages: 12,
        ThumbnailPrewarm: true,
        ThumbnailKeepMargin: 48,
        SoftCacheTrims: false,
        IdleTrimSeconds: 30,
        RenderThreads: Math.Max(MinRenderThreads, Math.Min(Environment.ProcessorCount - 1, 8)));

    /// <summary>The values of a named preset; <see cref="PerformancePreset.Custom"/> has none.</summary>
    public static PerformanceSettings For(PerformancePreset preset) => preset switch
    {
        PerformancePreset.LowMemory => LowMemory,
        PerformancePreset.Balanced => Balanced,
        PerformancePreset.Fast => Fast,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Custom has no fixed values"),
    };

    /// <summary>Every value forced into its documented range.</summary>
    public PerformanceSettings Clamped() => new(
        Math.Clamp(TileCacheBudgetMb, MinTileCacheBudgetMb, MaxTileCacheBudgetMb),
        Math.Clamp(SinglePageCachedPages, MinSinglePageCachedPages, MaxSinglePageCachedPages),
        ThumbnailPrewarm,
        Math.Clamp(ThumbnailKeepMargin, MinThumbnailKeepMargin, MaxThumbnailKeepMargin),
        SoftCacheTrims,
        Math.Clamp(IdleTrimSeconds, MinIdleTrimSeconds, MaxIdleTrimSeconds),
        Math.Clamp(RenderThreads, MinRenderThreads, MaxRenderThreads));

    /// <summary>
    /// The named preset these values equal, or <see cref="PerformancePreset.Custom"/>.
    /// The idle delay is ignored while soft trims are off: it has no effect then.
    /// </summary>
    public PerformancePreset DetectPreset()
    {
        foreach (var preset in new[] { PerformancePreset.Balanced, PerformancePreset.LowMemory, PerformancePreset.Fast })
        {
            if (EquivalentTo(For(preset)))
                return preset;
        }
        return PerformancePreset.Custom;
    }

    private bool EquivalentTo(PerformanceSettings other) =>
        TileCacheBudgetMb == other.TileCacheBudgetMb
        && SinglePageCachedPages == other.SinglePageCachedPages
        && ThumbnailPrewarm == other.ThumbnailPrewarm
        && ThumbnailKeepMargin == other.ThumbnailKeepMargin
        && SoftCacheTrims == other.SoftCacheTrims
        && (!SoftCacheTrims || IdleTrimSeconds == other.IdleTrimSeconds)
        && RenderThreads == other.RenderThreads;

    /// <summary>
    /// Read the persisted values. A named preset in the file reproduces that
    /// preset on THIS machine (its render-thread count follows the CPU count);
    /// otherwise the stored values are used, clamped. A file written before
    /// these settings existed holds only defaults and so reads as Balanced.
    /// </summary>
    public static PerformanceSettings FromWindowSettings(WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Enum.TryParse<PerformancePreset>(settings.PerformancePreset, out var preset)
            && preset != PerformancePreset.Custom)
        {
            return For(preset);
        }

        return new PerformanceSettings(
            settings.TileCacheBudgetMb,
            settings.SinglePageCachedPages,
            settings.ThumbnailPrewarm,
            settings.ThumbnailKeepMargin,
            settings.CacheTrimSoftTriggers,
            settings.CacheTrimIdleSeconds,
            settings.RenderThreads).Clamped();
    }

    /// <summary>Write these values, and the preset they match, into <paramref name="settings"/>.</summary>
    public void WriteTo(WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.PerformancePreset = DetectPreset().ToString();
        settings.TileCacheBudgetMb = TileCacheBudgetMb;
        settings.SinglePageCachedPages = SinglePageCachedPages;
        settings.ThumbnailPrewarm = ThumbnailPrewarm;
        settings.ThumbnailKeepMargin = ThumbnailKeepMargin;
        settings.CacheTrimSoftTriggers = SoftCacheTrims;
        settings.CacheTrimIdleSeconds = IdleTrimSeconds;
        settings.RenderThreads = RenderThreads;
    }
}
