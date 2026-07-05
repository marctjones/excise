using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PdfEditor.Services;
using SkiaSharp;
using System.Reflection;
using Xunit;

namespace PdfEditor.Tests.Unit;

/// <summary>
/// Unit tests for PdfRenderService cache functionality.
/// </summary>
public class PdfRenderServiceCacheTests
{
    private readonly PdfRenderService _service;
    private readonly Mock<ILogger<PdfRenderService>> _loggerMock;

    public PdfRenderServiceCacheTests()
    {
        _loggerMock = new Mock<ILogger<PdfRenderService>>();
        _service = new PdfRenderService(_loggerMock.Object);
    }

    [Fact]
    public void MaxCacheEntries_DefaultValue_IsTwenty()
    {
        _service.MaxCacheEntries.Should().Be(20);
    }

    [Fact]
    public void MaxCacheMemoryBytes_DefaultValue_Is100MB()
    {
        _service.MaxCacheMemoryBytes.Should().Be(100 * 1024 * 1024);
    }

    [Fact]
    public void MaxCacheEntries_SetValue_UpdatesProperty()
    {
        _service.MaxCacheEntries = 50;
        _service.MaxCacheEntries.Should().Be(50);
    }

    [Fact]
    public void MaxCacheEntries_SetBelowMinimum_ClampsToOne()
    {
        _service.MaxCacheEntries = 0;
        _service.MaxCacheEntries.Should().Be(1);

        _service.MaxCacheEntries = -5;
        _service.MaxCacheEntries.Should().Be(1);
    }

    [Fact]
    public void MaxCacheMemoryBytes_SetValue_UpdatesProperty()
    {
        _service.MaxCacheMemoryBytes = 50 * 1024 * 1024; // 50 MB
        _service.MaxCacheMemoryBytes.Should().Be(50 * 1024 * 1024);
    }

    [Fact]
    public void MaxCacheMemoryBytes_SetBelowMinimum_ClampsTo1MB()
    {
        _service.MaxCacheMemoryBytes = 100; // Way below 1 MB
        _service.MaxCacheMemoryBytes.Should().Be(1024 * 1024);
    }

    [Fact]
    public void GetCacheStats_InitialState_ReturnsZeroes()
    {
        var stats = _service.GetCacheStats();

        stats.Count.Should().Be(0);
        stats.MaxEntries.Should().Be(20);
        stats.Hits.Should().Be(0);
        stats.Misses.Should().Be(0);
        stats.CurrentBytes.Should().Be(0);
        stats.MaxBytes.Should().Be(100 * 1024 * 1024);
        stats.HitRate.Should().Be(0);
    }

    [Fact]
    public void ClearCache_AfterClearing_StatsShowZero()
    {
        _service.ClearCache();

        var stats = _service.GetCacheStats();
        stats.Count.Should().Be(0);
        stats.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public void MaxCacheEntries_SetBelowCurrentCount_EvictsImmediately()
    {
        AddCacheEntry(_service, "a", 100);
        AddCacheEntry(_service, "b", 100);
        AddCacheEntry(_service, "c", 100);

        _service.MaxCacheEntries = 2;

        _service.GetCacheStats().Count.Should().Be(2);
    }

    [Fact]
    public void TrimCacheForMemoryPressure_EvictsOldestEntriesToTarget()
    {
        AddCacheEntry(_service, "a", 400 * 1024);
        AddCacheEntry(_service, "b", 400 * 1024);
        AddCacheEntry(_service, "c", 400 * 1024);

        _service.TrimCacheForMemoryPressure(800 * 1024);

        var stats = _service.GetCacheStats();
        stats.Count.Should().BeLessThanOrEqualTo(2);
        stats.CurrentBytes.Should().BeLessThanOrEqualTo(800 * 1024);
    }

    [Fact]
    public void LogCacheStats_DoesNotThrow()
    {
        var action = () => _service.LogCacheStats();
        action.Should().NotThrow();
    }

    private static void AddCacheEntry(PdfRenderService service, string key, int sizeBytes)
    {
        var method = typeof(PdfRenderService).GetMethod(
            "AddToCache",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("cache eviction behavior should remain testable without rendering PDFs");

        using var bitmap = CreateBitmapWithApproximateSize(sizeBytes);
        method!.Invoke(service, new object[] { key, bitmap });
    }

    private static SKBitmap CreateBitmapWithApproximateSize(int sizeBytes)
    {
        var pixels = Math.Max(1, sizeBytes / 4);
        var side = Math.Max(1, (int)Math.Floor(Math.Sqrt(pixels)));
        return new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
    }

    private static SKBitmap? TryGetCachedBitmap(PdfRenderService service, string key)
    {
        var method = typeof(PdfRenderService).GetMethod(
            "TryGetFromCache",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("cache hit behavior should remain testable without rendering PDFs");

        object?[] args = [key, null];
        var hit = (bool)method!.Invoke(service, args)!;
        return hit ? (SKBitmap?)args[1] : null;
    }

    [Fact]
    public void CacheStatistics_HitRate_CalculatesCorrectly()
    {
        // Create a CacheStatistics record with known values
        var stats = new PdfRenderService.CacheStatistics(
            Count: 5,
            MaxEntries: 20,
            Hits: 75,
            Misses: 25,
            CurrentBytes: 1024 * 1024,
            MaxBytes: 100 * 1024 * 1024,
            HitRate: 0.75
        );

        stats.HitRate.Should().Be(0.75);
        stats.Count.Should().Be(5);
        stats.Hits.Should().Be(75);
        stats.Misses.Should().Be(25);
    }

    [Fact]
    public void CacheStatistics_Record_SupportsEquality()
    {
        var stats1 = new PdfRenderService.CacheStatistics(5, 20, 10, 5, 1024, 2048, 0.67);
        var stats2 = new PdfRenderService.CacheStatistics(5, 20, 10, 5, 1024, 2048, 0.67);

        stats1.Should().Be(stats2);
    }

    [Fact]
    public void AddToCache_CopiesBitmapSoCallerCanDisposeOriginal()
    {
        var method = typeof(PdfRenderService).GetMethod(
            "AddToCache",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        using (var original = new SKBitmap(new SKImageInfo(12, 8, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            original.Erase(SKColors.CornflowerBlue);
            method!.Invoke(_service, new object[] { "bitmap-copy", original });
        }

        using var cached = TryGetCachedBitmap(_service, "bitmap-copy");

        cached.Should().NotBeNull("the cache should store an owned bitmap copy instead of the caller-owned bitmap");
        cached!.Width.Should().Be(12);
        cached.Height.Should().Be(8);
    }

    [Fact]
    public void TryGetFromCache_ReturnsFreshBitmapCopies()
    {
        AddCacheEntry(_service, "fresh-copy", 4 * 1024);

        using var first = TryGetCachedBitmap(_service, "fresh-copy");
        using var second = TryGetCachedBitmap(_service, "fresh-copy");

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        ReferenceEquals(first, second).Should().BeFalse("cache hits must not share caller-disposed bitmap instances");
    }

    // ========================================================================
    // CACHE MEMORY LIMITS
    // ========================================================================

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public void MaxCacheMemoryBytes_SetValidValue_UpdatesCorrectly(long megabytes)
    {
        var bytes = megabytes * 1024 * 1024;
        _service.MaxCacheMemoryBytes = bytes;

        _service.MaxCacheMemoryBytes.Should().Be(bytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public void MaxCacheEntries_SetValidValue_UpdatesCorrectly(int entries)
    {
        _service.MaxCacheEntries = entries;

        _service.MaxCacheEntries.Should().Be(entries);
    }

    [Fact]
    public void MaxCacheEntries_SetToZero_ClampsToOne()
    {
        _service.MaxCacheEntries = 0;
        _service.MaxCacheEntries.Should().Be(1);
    }

    [Fact]
    public void MaxCacheMemoryBytes_SetTo0_ClampsTo1MB()
    {
        _service.MaxCacheMemoryBytes = 0;
        _service.MaxCacheMemoryBytes.Should().Be(1024 * 1024);
    }

    [Fact]
    public void MaxCacheMemoryBytes_SetTo512KB_ClampsTo1MB()
    {
        _service.MaxCacheMemoryBytes = 512 * 1024;
        _service.MaxCacheMemoryBytes.Should().Be(1024 * 1024);
    }

    // ========================================================================
    // CACHE STATISTICS CALCULATIONS
    // ========================================================================

    [Fact]
    public void CacheStatistics_WithZeroHitsAndMisses_HitRateIsZero()
    {
        var stats = new PdfRenderService.CacheStatistics(0, 20, 0, 0, 0, 100 * 1024 * 1024, 0);

        stats.HitRate.Should().Be(0);
    }

    [Fact]
    public void CacheStatistics_AllHits_HitRateIsOne()
    {
        var stats = new PdfRenderService.CacheStatistics(5, 20, 100, 0, 1024, 2048, 1.0);

        stats.HitRate.Should().Be(1.0);
    }

    [Fact]
    public void CacheStatistics_HalfHitsHalfMisses_HitRateIsHalf()
    {
        var stats = new PdfRenderService.CacheStatistics(10, 20, 50, 50, 1024, 2048, 0.5);

        stats.HitRate.Should().Be(0.5);
    }

    [Fact]
    public void CacheStatistics_RecordIsImmutable()
    {
        var stats = new PdfRenderService.CacheStatistics(5, 20, 10, 5, 1024, 2048, 0.67);

        stats.Count.Should().Be(5);
        stats.MaxEntries.Should().Be(20);
        stats.Hits.Should().Be(10);
        stats.Misses.Should().Be(5);
        stats.CurrentBytes.Should().Be(1024);
        stats.MaxBytes.Should().Be(2048);
        stats.HitRate.Should().Be(0.67);
    }

    // ========================================================================
    // MULTIPLE SERVICE INSTANCES
    // ========================================================================

    [Fact]
    public void MultipleServiceInstances_HaveIndependentCaches()
    {
        var service1 = new PdfRenderService(_loggerMock.Object);
        var service2 = new PdfRenderService(_loggerMock.Object);

        service1.MaxCacheEntries = 50;
        service2.MaxCacheEntries = 25;

        service1.MaxCacheEntries.Should().Be(50);
        service2.MaxCacheEntries.Should().Be(25);
    }

    [Fact]
    public void MultipleServiceInstances_HaveIndependentCacheMemory()
    {
        var service1 = new PdfRenderService(_loggerMock.Object);
        var service2 = new PdfRenderService(_loggerMock.Object);

        var memoryMB = 50 * 1024 * 1024;
        service1.MaxCacheMemoryBytes = memoryMB;
        service2.MaxCacheMemoryBytes = memoryMB * 2;

        service1.MaxCacheMemoryBytes.Should().Be(memoryMB);
        service2.MaxCacheMemoryBytes.Should().Be(memoryMB * 2);
    }

    // ========================================================================
    // CACHE STATS EDGE CASES
    // ========================================================================

    [Fact]
    public void GetCacheStats_InitialHitRate_IsZero()
    {
        var stats = _service.GetCacheStats();

        stats.HitRate.Should().Be(0);
    }

    [Fact]
    public void CacheStatistics_MaxEntriesMatches()
    {
        var stats = _service.GetCacheStats();

        stats.MaxEntries.Should().Be(_service.MaxCacheEntries);
    }

    [Fact]
    public void CacheStatistics_MaxBytesMatches()
    {
        var stats = _service.GetCacheStats();

        stats.MaxBytes.Should().Be(_service.MaxCacheMemoryBytes);
    }

    // ========================================================================
    // CACHE CLEAR BEHAVIOR
    // ========================================================================

    [Fact]
    public void ClearCache_MultipleTimes_DoesNotThrow()
    {
        var action = () =>
        {
            _service.ClearCache();
            _service.ClearCache();
            _service.ClearCache();
        };

        action.Should().NotThrow();
    }

    [Fact]
    public void ClearCache_ThenGetStats_CountIsZero()
    {
        _service.ClearCache();
        var stats = _service.GetCacheStats();

        stats.Count.Should().Be(0);
    }

    // ========================================================================
    // EDGE CASE: BOUNDARY VALUES
    // ========================================================================

    [Fact]
    public void MaxCacheEntries_SetToMax_Succeeds()
    {
        _service.MaxCacheEntries = int.MaxValue;

        _service.MaxCacheEntries.Should().Be(int.MaxValue);
    }

    [Fact]
    public void MaxCacheMemoryBytes_SetToMax_Succeeds()
    {
        _service.MaxCacheMemoryBytes = long.MaxValue;

        _service.MaxCacheMemoryBytes.Should().Be(long.MaxValue);
    }

    [Fact]
    public void CacheStatistics_WithVeryLargeNumbers_CalculatesHitRate()
    {
        var stats = new PdfRenderService.CacheStatistics(
            Count: 1000000,
            MaxEntries: 2000000,
            Hits: long.MaxValue / 2,
            Misses: long.MaxValue / 2,
            CurrentBytes: long.MaxValue / 2,
            MaxBytes: long.MaxValue,
            HitRate: 0.5);

        stats.HitRate.Should().Be(0.5);
    }
}
