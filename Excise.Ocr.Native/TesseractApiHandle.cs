using System;
using System.Runtime.InteropServices;

namespace Excise.Ocr.Native;

/// <summary>
/// Owns a <c>TessBaseAPI*</c>. The whole reason this repo can bind native code
/// at all after #363/#985 is that ownership is never a raw pointer a caller
/// might forget to free or free twice: the runtime releases this handle via
/// <c>TessBaseAPIDelete</c> on finalization or <see cref="SafeHandle.Dispose()"/>,
/// and the source-generated marshaller keeps it ref-counted for the duration
/// of every P/Invoke that receives it, so it cannot be collected mid-call.
/// </summary>
internal sealed class TesseractApiHandle : SafeHandle
{
    // Parameterless ctor is required for the marshaller to construct the handle
    // on return from TessBaseAPICreate.
    public TesseractApiHandle() : base(invalidHandleValue: IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        TesseractCApi.TessBaseAPIDelete(handle);
        return true;
    }
}
