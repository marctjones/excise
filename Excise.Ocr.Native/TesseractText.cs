using System;
using System.Runtime.InteropServices;

namespace Excise.Ocr.Native;

/// <summary>
/// Owns a UTF-8 <c>char*</c> handed back by <c>TessBaseAPIGetUTF8Text</c> /
/// <c>TessBaseAPIGetTsvText</c>. The C API allocates it and requires it be
/// freed with <c>TessDeleteText</c> — <b>not</b> <c>Marshal.FreeHGlobal</c> or
/// <c>free()</c>. Hiding that behind a <see cref="SafeHandle"/> makes the
/// correct deallocator the only reachable one: read the value with
/// <see cref="ReadString"/>, then let <c>using</c>/finalization free it.
/// </summary>
internal sealed class TesseractText : SafeHandle
{
    public TesseractText() : base(invalidHandleValue: IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// Marshals the unmanaged UTF-8 bytes into a managed string. Returns
    /// <c>null</c> when the API produced no text (null pointer). Does not free
    /// the handle — disposal does that.
    /// </summary>
    public string? ReadString()
        => IsInvalid ? null : Marshal.PtrToStringUTF8(handle);

    protected override bool ReleaseHandle()
    {
        TesseractCApi.TessDeleteText(handle);
        return true;
    }
}
