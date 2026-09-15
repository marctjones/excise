using System;
using System.Runtime.InteropServices;

namespace Excise.App.Services;

/// <summary>
/// macOS <c>malloc_zone_pressure_relief(NULL, 0)</c> (#1481): ask every malloc
/// zone to return the free pages it is holding to the OS.
/// </summary>
/// <remarks>
/// A cache trim frees bitmaps back to the allocator, but the allocator keeps
/// the pages. Measured live in the #1478 T1 run: a Warn trim released 192 MB of
/// tiles, and about 99 MB of it stayed in the process as
/// <c>MALLOC_LARGE (empty)</c> in vmmap. Bound lazily through
/// <see cref="NativeLibrary"/> and a function pointer, like
/// <see cref="MacMemoryPressureSource"/>. Anywhere it cannot be bound (not
/// macOS, a missing export) it does nothing.
/// </remarks>
internal static unsafe class MacMallocPressureRelief
{
    internal const string LibSystemPath = "/usr/lib/libSystem.B.dylib";
    internal const string ExportName = "malloc_zone_pressure_relief";

    private static readonly Lazy<Func<long>?> Bound = new(
        () => Bind(OperatingSystem.IsMacOS(), LibSystemPath, ExportName));

    /// <summary>True when the relief call is bound on this machine.</summary>
    internal static bool IsAvailable => Bound.Value != null;

    /// <summary>Bytes the allocator returned to the OS; 0 when the call is unavailable.</summary>
    internal static long Relieve() => Bound.Value?.Invoke() ?? 0;

    /// <summary>
    /// The relief call, or null when it cannot be bound: not macOS, the library
    /// does not load, or the export is missing.
    /// </summary>
    internal static Func<long>? Bind(bool isMacOS, string libraryPath, string exportName)
    {
        if (!isMacOS)
            return null;
        if (!NativeLibrary.TryLoad(libraryPath, out var library))
            return null;
        if (!NativeLibrary.TryGetExport(library, exportName, out var address) || address == 0)
            return null;

        // size_t malloc_zone_pressure_relief(malloc_zone_t *zone, size_t goal);
        // zone NULL = every zone, goal 0 = as much as possible.
        var relief = (delegate* unmanaged[Cdecl]<nint, nuint, nuint>)address;
        return () => (long)relief(0, 0);
    }
}
