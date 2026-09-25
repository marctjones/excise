using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Excise.Native;

/// <summary>C ABI entry points. See include/excise.h for the contract.</summary>
public static unsafe class NativeExports
{
    private const int AbiVersion = 1;
    private static readonly nint VersionString = AllocUtf8("excise-native 1");

    [ThreadStatic] private static nint t_lastError;

    private static nint AllocUtf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        p[bytes.Length] = 0;
        return (nint)p;
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_abi_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int AbiVersionExport() => AbiVersion;

    [UnmanagedCallersOnly(EntryPoint = "excise_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static byte* VersionExport() => (byte*)VersionString;

    [UnmanagedCallersOnly(EntryPoint = "excise_last_error", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static byte* LastErrorExport()
    {
        if (t_lastError != 0) NativeMemory.Free((void*)t_lastError);
        t_lastError = AllocUtf8("");
        return (byte*)t_lastError;
    }
}
