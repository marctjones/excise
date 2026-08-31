using System;
using AwesomeAssertions;
using Avalonia;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

public sealed class SinglePageRenderLifetimeTests
{
    [Fact]
    public void BeginRender_CancelsPriorGeneration_AndRejectsStaleCompletion()
    {
        using var lifetime = new SinglePageRenderLifetime<TrackedBitmap>(cacheCapacity: 2);
        using var first = lifetime.BeginRender();

        first.IsCurrent.Should().BeTrue();
        first.Token.IsCancellationRequested.Should().BeFalse();

        using var second = lifetime.BeginRender();

        first.Token.IsCancellationRequested.Should().BeTrue();
        first.IsCurrent.Should().BeFalse("a superseded render must not publish its bitmap or error");
        second.Token.IsCancellationRequested.Should().BeFalse();
        second.IsCurrent.Should().BeTrue();

        first.Dispose();
        second.IsCurrent.Should().BeTrue("completing a stale generation must not release the active one");
    }

    [Fact]
    public void CancelRender_SupersedesGenerationWithoutReplacement()
    {
        using var lifetime = new SinglePageRenderLifetime<TrackedBitmap>(cacheCapacity: 1);
        using var render = lifetime.BeginRender();

        lifetime.CancelRender();

        render.Token.IsCancellationRequested.Should().BeTrue();
        render.IsCurrent.Should().BeFalse("detach, invalidation, and cache hits make prior work stale");
    }

    [Fact]
    public void Cache_TouchUpdatesLru_AndEvictionDisposesOwnedBitmap()
    {
        using var lifetime = new SinglePageRenderLifetime<TrackedBitmap>(cacheCapacity: 2);
        var first = new TrackedBitmap();
        var second = new TrackedBitmap();
        var third = new TrackedBitmap();
        var firstSize = new Size(100, 200);

        lifetime.Add(pageNumber: 1, dpi: 120, first, firstSize);
        lifetime.Add(pageNumber: 2, dpi: 120, second, new Size(200, 300));
        lifetime.TryGet(pageNumber: 1, dpi: 120, out var found, out var foundSize).Should().BeTrue();
        lifetime.Add(pageNumber: 3, dpi: 120, third, new Size(300, 400));

        found.Should().BeSameAs(first);
        foundSize.Should().Be(firstSize);
        first.IsDisposed.Should().BeFalse("the LRU touch made page 1 most recent");
        second.IsDisposed.Should().BeTrue("page 2 became the least-recent entry");
        third.IsDisposed.Should().BeFalse();
        lifetime.TryGet(pageNumber: 2, dpi: 120, out _, out _).Should().BeFalse();

        var diagnostics = lifetime.GetCacheDiagnostics();
        diagnostics.EntryCount.Should().Be(2);
        diagnostics.Capacity.Should().Be(2);
        diagnostics.Hits.Should().Be(1);
        diagnostics.Misses.Should().Be(1);
        diagnostics.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public void Cache_ReplacementAndInvalidationDisposeEveryOwnedBitmap()
    {
        using var lifetime = new SinglePageRenderLifetime<TrackedBitmap>(cacheCapacity: 2);
        var original = new TrackedBitmap();
        var replacement = new TrackedBitmap();
        var other = new TrackedBitmap();

        lifetime.Add(pageNumber: 1, dpi: 120, original, new Size(100, 100));
        lifetime.Add(pageNumber: 1, dpi: 120, replacement, new Size(110, 110));
        lifetime.Add(pageNumber: 2, dpi: 120, other, new Size(200, 200));

        original.IsDisposed.Should().BeTrue("replacing a cache key transfers ownership to the new bitmap");
        replacement.IsDisposed.Should().BeFalse();
        other.IsDisposed.Should().BeFalse();

        lifetime.InvalidateCache();

        replacement.IsDisposed.Should().BeTrue();
        other.IsDisposed.Should().BeTrue();
        lifetime.TryGet(pageNumber: 1, dpi: 120, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Dispose_CancelsActiveRender_DisposesCache_AndRejectsReuse()
    {
        var lifetime = new SinglePageRenderLifetime<TrackedBitmap>(cacheCapacity: 1);
        var bitmap = new TrackedBitmap();
        var render = lifetime.BeginRender();
        lifetime.Add(pageNumber: 1, dpi: 120, bitmap, new Size(100, 100));

        lifetime.Dispose();

        render.Token.IsCancellationRequested.Should().BeTrue();
        render.IsCurrent.Should().BeFalse();
        bitmap.IsDisposed.Should().BeTrue();
        lifetime.Invoking(subject => subject.BeginRender()).Should().Throw<ObjectDisposedException>();
        lifetime.Invoking(subject => subject.TryGet(1, 120, out _, out _)).Should().Throw<ObjectDisposedException>();

        render.Dispose();
        lifetime.Dispose();
    }

    private sealed class TrackedBitmap : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
