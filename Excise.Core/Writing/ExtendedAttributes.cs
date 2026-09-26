using System.Runtime.InteropServices;

namespace Excise.Core.Writing;

/// <summary>
/// Copies the extended attributes of the file a save replaces onto the
/// temporary that replaces it (#1802): Finder tags and comments, quarantine,
/// <c>user.*</c>. They live on the inode, and <see cref="AtomicFileReplace"/>'s
/// rename gives the target a new one.
/// </summary>
/// <remarks>
/// Best effort, and it never throws: an attribute that cannot be read or
/// written is skipped, so a save never fails, and never writes different bytes,
/// because of one. Nothing is skipped by name: macOS ignores a write to its own
/// <c>com.apple.provenance</c> (measured on 26.6), and dropping <c>com.apple.quarantine</c> would
/// clear a downloaded file's quarantine on its first save. macOS and Linux
/// only; Windows' <c>ReplaceFile</c> keeps attributes and streams itself.
/// </remarks>
internal static partial class ExtendedAttributes
{
    internal static void CarryOver(string target, string temporary)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            return;
        try
        {
            if (Read(buffer => List(target, buffer)) is not { } names)
                return;
            for (int start = 0, end; start < names.Length; start = end + 1)
            {
                end = Array.IndexOf(names, (byte)0, start);
                if (end < 0)
                    break;
                var name = names[start..(end + 1)]; // NUL-terminated, as libc wants it
                if (Read(buffer => Get(target, name, buffer)) is { } value)
                    Set(temporary, name, value);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    // A size query (null buffer), then the read; null when either fails. The
    // attribute can change in between: a larger one fails (ERANGE), a smaller
    // one returns fewer bytes.
    private static byte[]? Read(Func<byte[]?, nint> call)
    {
        var size = call(null);
        if (size <= 0)
            return size == 0 ? [] : null;
        var buffer = new byte[size];
        var read = call(buffer);
        return read < 0 ? null : read == size ? buffer : buffer[..(int)read];
    }

    private static nint List(string path, byte[]? names) => OperatingSystem.IsMacOS()
        ? MacListXattr(path, names, (nuint)(names?.Length ?? 0), 0)
        : LinuxListXattr(path, names, (nuint)(names?.Length ?? 0));

    private static nint Get(string path, byte[] name, byte[]? value) => OperatingSystem.IsMacOS()
        ? MacGetXattr(path, name, value, (nuint)(value?.Length ?? 0), 0, 0)
        : LinuxGetXattr(path, name, value, (nuint)(value?.Length ?? 0));

    private static void Set(string path, byte[] name, byte[] value) => _ = OperatingSystem.IsMacOS()
        ? MacSetXattr(path, name, value, (nuint)value.Length, 0, 0)
        : LinuxSetXattr(path, name, value, (nuint)value.Length, 0);

    // man 2 getxattr: macOS adds a position (the resource fork offset, 0 for
    // everything else) and an options argument to glibc's signatures.
    [LibraryImport("libc", EntryPoint = "listxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint MacListXattr(string path, [Out] byte[]? names, nuint size, int options);

    [LibraryImport("libc", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint MacGetXattr(string path, byte[] name, [Out] byte[]? value, nuint size, uint position, int options);

    [LibraryImport("libc", EntryPoint = "setxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacSetXattr(string path, byte[] name, byte[] value, nuint size, uint position, int options);

    [LibraryImport("libc", EntryPoint = "listxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint LinuxListXattr(string path, [Out] byte[]? names, nuint size);

    [LibraryImport("libc", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint LinuxGetXattr(string path, byte[] name, [Out] byte[]? value, nuint size);

    [LibraryImport("libc", EntryPoint = "setxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxSetXattr(string path, byte[] name, byte[] value, nuint size, int flags);
}
