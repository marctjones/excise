using System;
using System.Runtime.InteropServices;

namespace Excise.Ocr.Native;

/// <summary>
/// Source-generated P/Invoke surface for the tesseract C API (<c>capi.h</c>),
/// the <c>extern "C"</c> flat ABI built for FFI (#1139). This type is
/// <b>internal</b>: the safe, ownership-managed entry point is
/// <see cref="NativeOcrEngine"/>. Nothing outside this assembly touches a raw
/// <see cref="IntPtr"/> or an unmanaged allocation.
/// </summary>
/// <remarks>
/// Handle ownership is expressed in the types themselves:
/// <list type="bullet">
/// <item><description>the API handle is a <see cref="TesseractApiHandle"/>
/// (<see cref="System.Runtime.InteropServices.SafeHandle"/>) that frees itself
/// via <c>TessBaseAPIDelete</c>;</description></item>
/// <item><description>the UTF-8 result pointer is a <see cref="TesseractText"/>
/// (also a <c>SafeHandle</c>) that frees itself via <c>TessDeleteText</c> — the
/// C API's matching deallocator, never <c>free()</c>.</description></item>
/// </list>
/// Because both return values marshal as <c>SafeHandle</c>s, a caller cannot
/// leak them and cannot free them with the wrong deallocator.
/// </remarks>
internal static partial class TesseractCApi
{
    /// <summary>
    /// Logical library name handed to <c>NativeLibrary</c>; the resolver in
    /// <see cref="NativeLibraryResolver"/> maps it to the platform file.
    /// </summary>
    internal const string Lib = "tesseract";

    [LibraryImport(Lib)]
    internal static partial TesseractApiHandle TessBaseAPICreate();

    [LibraryImport(Lib)]
    internal static partial void TessBaseAPIDelete(IntPtr handle);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int TessBaseAPIInit3(TesseractApiHandle handle, string? datapath, string language);

    [LibraryImport(Lib)]
    internal static partial void TessBaseAPISetPageSegMode(TesseractApiHandle handle, int mode);

    [LibraryImport(Lib)]
    internal static partial void TessBaseAPISetImage(
        TesseractApiHandle handle, IntPtr data, int width, int height, int bytesPerPixel, int bytesPerLine);

    [LibraryImport(Lib)]
    internal static partial void TessBaseAPISetSourceResolution(TesseractApiHandle handle, int ppi);

    [LibraryImport(Lib)]
    internal static partial TesseractText TessBaseAPIGetUTF8Text(TesseractApiHandle handle);

    [LibraryImport(Lib)]
    internal static partial TesseractText TessBaseAPIGetTsvText(TesseractApiHandle handle, int pageNumber);

    [LibraryImport(Lib)]
    internal static partial int TessBaseAPIMeanTextConf(TesseractApiHandle handle);

    [LibraryImport(Lib)]
    internal static partial void TessBaseAPIClear(TesseractApiHandle handle);

    /// <summary>Frees a UTF-8 string returned by the API. Called only by
    /// <see cref="TesseractText.ReleaseHandle"/>.</summary>
    [LibraryImport(Lib)]
    internal static partial void TessDeleteText(IntPtr text);
}

/// <summary>
/// Registers the cross-platform library resolver exactly once, before any
/// binding call runs. libtesseract is installed by the host (Homebrew on
/// macOS, apt on Linux), not bundled, and its directory is frequently off the
/// default loader search path (e.g. <c>/opt/homebrew/lib</c>), which is the
/// gap <c>NativeLibrary.SetDllImportResolver</c> exists to close.
/// </summary>
internal static class NativeLibraryResolver
{
    private static int _registered;

    /// <summary>Idempotent; safe to call from every public entry point.</summary>
    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(TesseractCApi).Assembly, Resolve);
        }
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != TesseractCApi.Lib)
            return IntPtr.Zero;   // not ours — let the default resolver try.

        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }
        return IntPtr.Zero;       // fall back to the default resolver.
    }

    /// <summary>
    /// Ordered probe list. An explicit override wins; then versioned SONAMEs
    /// (Linux ships <c>libtesseract.so.5</c>, not an unversioned <c>.so</c>
    /// unless the -dev package is present); then Homebrew/MacPorts/usr-local
    /// absolute paths; then the bare name so the platform loader gets a turn.
    /// </summary>
    private static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable("EXCISE_TESSERACT_LIB");

        if (OperatingSystem.IsMacOS())
        {
            yield return "libtesseract.5.dylib";
            yield return "libtesseract.dylib";
            yield return "/opt/homebrew/lib/libtesseract.5.dylib";  // Apple-silicon brew
            yield return "/opt/homebrew/lib/libtesseract.dylib";
            yield return "/usr/local/lib/libtesseract.5.dylib";     // Intel brew / MacPorts
            yield return "/usr/local/lib/libtesseract.dylib";
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return "libtesseract-5.dll";
            yield return "tesseract.dll";
            yield return "tesseract55.dll";
        }
        else
        {
            yield return "libtesseract.so.5";
            yield return "libtesseract.so";
            yield return "/usr/lib/x86_64-linux-gnu/libtesseract.so.5";
            yield return "/usr/lib/libtesseract.so.5";
        }

        yield return "tesseract";   // last resort: default loader search.
    }
}
