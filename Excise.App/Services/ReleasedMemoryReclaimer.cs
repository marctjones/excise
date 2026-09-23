using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Threading;

namespace Excise.App.Services;

/// <summary>What asked for a managed-heap reclaim (#1481); the <c>trigger</c> tag on <c>excise.app.heap_reclaim.*</c>.</summary>
internal enum HeapReclaimTrigger
{
    DocumentClosed,
    DocumentReplaced,
    OsPressure,
    GcMemoryLoad,
    /// <summary>
    /// The idle soft trim, once per idle period (#1496). Unconditional since
    /// #1713: gating it on reported heap fragmentation missed most of what a
    /// reclaim actually freed and made identical sessions land in two very
    /// different memory states depending on run-to-run measurement noise.
    /// </summary>
    Idle,
}

/// <summary>
/// Runs ONE compacting, blocking gen2 collection after the app has released a
/// large amount of managed memory (#1481): a document closed or replaced, an
/// OS-pressure cache trim at Warn or Critical, or an idle trim, once per idle
/// period (#1496, #1713).
/// </summary>
/// <remarks>
/// <para>Why: the .NET GC does not run while the app is idle, so a replaced
/// document's garbage stays resident. Measured live on 2026-09-14: Altona
/// scrolled 17 pages, replaced by irs-w9 and left idle for 2 minutes held
/// 1756 MB RSS / 1552 MB footprint; one forced GC took it to 719 / 515 MB with an
/// 18 MB live heap.</para>
/// <para>Why <see cref="GCCollectionMode.Aggressive"/>: it compacts the large
/// object heap (where a document's file bytes live) and decommits the freed
/// segments. A <see cref="GCCollectionMode.Forced"/> compacting collection does
/// not compact the LOH unless <c>GCSettings.LargeObjectHeapCompactionMode</c> is
/// set. Measured in a scratch probe (400 x 1 MB LOH arrays, half freed):
/// Forced+compacting left 187 MB of LOH fragmentation and 512 MB committed;
/// Aggressive left 0 MB and 322 MB, identical to Forced+CompactOnce.</para>
/// <para>Requests are POSTED to the dispatcher at
/// <see cref="DispatcherPriority.Background"/> so the collection runs after the
/// synchronous close/replace code has dropped the view model's, viewer's,
/// thumbnail session's and text index's references. Back-to-back requests
/// (a replace straight after an open, Warn then Critical) coalesce into the one
/// collection already queued. There is no timer and nothing periodic: each
/// collection answers exactly one burst of requests, so the #1462 idle
/// quiescence is untouched.</para>
/// </remarks>
internal sealed class ReleasedMemoryReclaimer
{
    private readonly Action<HeapReclaimTrigger> _collect;
    private readonly Action<Action> _post;
    private int _pending;
    private HeapReclaimTrigger _pendingTrigger;

    /// <param name="collect">The collection; defaults to <see cref="CollectReleasedMemory"/>. Tests record here.</param>
    /// <param name="post">How the collection is deferred; defaults to a Background-priority UI post.</param>
    /// <remarks>
    /// No native-allocator relief follows the collection. <c>malloc_zone_pressure_relief(NULL, 0)</c>
    /// was measured on Darwin 25.6 on 2026-09-14: it returned 0 bytes after 512 MB was freed, and the
    /// footprint did not move. So it was not kept.
    /// </remarks>
    internal ReleasedMemoryReclaimer(
        Action<HeapReclaimTrigger>? collect = null,
        Action<Action>? post = null)
    {
        _collect = collect ?? CollectReleasedMemory;
        _post = post ?? PostAtBackgroundPriority;
    }

    /// <summary>
    /// Any thread. Queue a collection unless one is already queued; the queued
    /// one keeps the trigger that queued it.
    /// </summary>
    internal void Request(HeapReclaimTrigger trigger)
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
            return;
        _pendingTrigger = trigger;
        _post(Run);
    }

    private void Run()
    {
        var trigger = _pendingTrigger;
        // Cleared first: memory released by a request that arrives after this
        // point is not covered by the collection below, so it may queue another.
        Volatile.Write(ref _pending, 0);

        bool record = AppMetrics.HeapReclaimDuration.Enabled || AppMetrics.HeapReclaimHeapSize.Enabled;
        long before = record ? GC.GetTotalMemory(forceFullCollection: false) : 0;
        long started = Stopwatch.GetTimestamp();
        _collect(trigger);
        long collectMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long after = record ? GC.GetTotalMemory(forceFullCollection: false) : 0;

        if (record)
            AppMetrics.RecordHeapReclaim(trigger, collectMs, before, after);
    }

    /// <summary>The production collection: compacting, blocking, gen2, LOH included.</summary>
    internal static void CollectReleasedMemory(HeapReclaimTrigger trigger) =>
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    private static void PostAtBackgroundPriority(Action action) =>
        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
}
