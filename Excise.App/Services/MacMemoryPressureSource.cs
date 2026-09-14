using Microsoft.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Excise.App.Services;

/// <summary>
/// The macOS memory-pressure signal (#1478): a libdispatch
/// <c>DISPATCH_SOURCE_TYPE_MEMORYPRESSURE</c> source, bound through
/// <see cref="NativeLibrary"/> and function pointers so there is no native
/// shim and nothing for Native AOT to trim.
/// </summary>
/// <remarks>
/// <para>
/// The source type is a DATA symbol: the C macro is
/// <c>(&amp;_dispatch_source_type_memorypressure)</c>, so the address
/// <see cref="NativeLibrary.TryGetExport"/> returns for it is the value to pass.
/// Events are delivered on a libdispatch global queue, never the UI thread; the
/// callback must post before it touches the viewer.
/// </para>
/// <para>
/// The handler context is a <see cref="GCHandle"/> to this instance. It is
/// freed in the cancel handler, which libdispatch runs only after any event
/// handler already in progress has returned, so an event can never read a
/// freed handle.
/// </para>
/// <para>
/// Every failure (a missing export, a null source) logs and returns null: the
/// pressure signal is an optimisation, and the caller falls back to GC
/// sampling.
/// </para>
/// </remarks>
internal sealed unsafe class MacMemoryPressureSource : IDisposable
{
    // <dispatch/source.h>
    internal const nuint DispatchMemoryPressureNormal = 0x01;
    internal const nuint DispatchMemoryPressureWarn = 0x02;
    internal const nuint DispatchMemoryPressureCritical = 0x04;

    private const string LibSystemPath = "/usr/lib/libSystem.B.dylib";

    private static readonly object ApiGate = new();
    private static bool s_apiResolved;
    private static bool s_apiAvailable;
    private static nint s_typeMemoryPressure;
    private static delegate* unmanaged[Cdecl]<nint, nuint, nint> s_getGlobalQueue;
    private static delegate* unmanaged[Cdecl]<nint, nuint, nuint, nint, nint> s_sourceCreate;
    private static delegate* unmanaged[Cdecl]<nint, nint, void> s_setContext;
    private static delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, void> s_setEventHandler;
    private static delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, void> s_setCancelHandler;
    private static delegate* unmanaged[Cdecl]<nint, nuint> s_getData;
    private static delegate* unmanaged[Cdecl]<nint, void> s_resume;
    private static delegate* unmanaged[Cdecl]<nint, void> s_cancel;
    private static delegate* unmanaged[Cdecl]<nint, void> s_release;

    private readonly Action<MemoryPressureLevel> _onPressure;
    private readonly ILogger? _logger;
    private nint _source;
    private bool _disposed;

    private MacMemoryPressureSource(Action<MemoryPressureLevel> onPressure, ILogger? logger)
    {
        _onPressure = onPressure;
        _logger = logger;
    }

    /// <summary>
    /// Install the source, or return null when this is not macOS or libdispatch
    /// cannot be bound. <paramref name="onPressure"/> runs on a libdispatch
    /// queue.
    /// </summary>
    internal static MacMemoryPressureSource? TryStart(Action<MemoryPressureLevel> onPressure, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(onPressure);
        if (!OperatingSystem.IsMacOS())
            return null;

        try
        {
            if (!TryResolveApi())
            {
                logger?.LogWarning("Memory-pressure source unavailable: libdispatch exports not found (#1478)");
                return null;
            }

            var source = new MacMemoryPressureSource(onPressure, logger);
            nint queue = s_getGlobalQueue(0, 0);
            nint handle = s_sourceCreate(
                s_typeMemoryPressure, 0,
                DispatchMemoryPressureNormal | DispatchMemoryPressureWarn | DispatchMemoryPressureCritical,
                queue);
            if (handle == 0)
            {
                logger?.LogWarning("Memory-pressure source unavailable: dispatch_source_create returned null (#1478)");
                return null;
            }

            source._source = handle;
            var self = GCHandle.Alloc(source);
            s_setContext(handle, GCHandle.ToIntPtr(self));
            s_setEventHandler(handle, &OnEvent);
            s_setCancelHandler(handle, &OnCancel);
            s_resume(handle);
            logger?.LogInformation("Memory-pressure source installed (#1478)");
            return source;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Memory-pressure source unavailable (#1478)");
            return null;
        }
    }

    /// <summary>The level a <c>dispatch_source_get_data</c> mask reports; the most severe bit wins.</summary>
    internal static MemoryPressureLevel MapPressureData(nuint data) =>
        (data & DispatchMemoryPressureCritical) != 0 ? MemoryPressureLevel.Critical
        : (data & DispatchMemoryPressureWarn) != 0 ? MemoryPressureLevel.Warn
        : MemoryPressureLevel.Normal;

    private static bool TryResolveApi()
    {
        lock (ApiGate)
        {
            if (s_apiResolved)
                return s_apiAvailable;
            s_apiResolved = true;

            if (!NativeLibrary.TryLoad(LibSystemPath, out var lib))
                return false;

            if (!NativeLibrary.TryGetExport(lib, "_dispatch_source_type_memorypressure", out s_typeMemoryPressure) ||
                !Export(lib, "dispatch_get_global_queue", out var getGlobalQueue) ||
                !Export(lib, "dispatch_source_create", out var sourceCreate) ||
                !Export(lib, "dispatch_set_context", out var setContext) ||
                !Export(lib, "dispatch_source_set_event_handler_f", out var setEventHandler) ||
                !Export(lib, "dispatch_source_set_cancel_handler_f", out var setCancelHandler) ||
                !Export(lib, "dispatch_source_get_data", out var getData) ||
                !Export(lib, "dispatch_resume", out var resume) ||
                !Export(lib, "dispatch_source_cancel", out var cancel) ||
                !Export(lib, "dispatch_release", out var release))
                return false;

            s_getGlobalQueue = (delegate* unmanaged[Cdecl]<nint, nuint, nint>)getGlobalQueue;
            s_sourceCreate = (delegate* unmanaged[Cdecl]<nint, nuint, nuint, nint, nint>)sourceCreate;
            s_setContext = (delegate* unmanaged[Cdecl]<nint, nint, void>)setContext;
            s_setEventHandler = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, void>)setEventHandler;
            s_setCancelHandler = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, void>)setCancelHandler;
            s_getData = (delegate* unmanaged[Cdecl]<nint, nuint>)getData;
            s_resume = (delegate* unmanaged[Cdecl]<nint, void>)resume;
            s_cancel = (delegate* unmanaged[Cdecl]<nint, void>)cancel;
            s_release = (delegate* unmanaged[Cdecl]<nint, void>)release;
            s_apiAvailable = true;
            return true;
        }

        static bool Export(nint lib, string name, out nint address) =>
            NativeLibrary.TryGetExport(lib, name, out address) && address != 0;
    }

    // An exception must never unwind into libdispatch: that terminates the
    // process. Both handlers therefore catch everything.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvent(nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not MacMemoryPressureSource self || self._disposed)
                return;
            self._onPressure(MapPressureData(s_getData(self._source)));
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCancel(nint context)
    {
        try
        {
            GCHandle.FromIntPtr(context).Free();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var handle = _source;
        _source = 0;
        if (handle == 0)
            return;
        try
        {
            // Cancel runs OnCancel (which frees the GCHandle) after any event in
            // progress; the source keeps itself alive until then.
            s_cancel(handle);
            s_release(handle);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Memory-pressure source failed to cancel (#1478)");
        }
    }
}
