using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Excise.App.Services;

/// <summary>
/// Turns an embedded file's declared name into a name that is safe to write
/// (#1563).
/// </summary>
/// <remarks>
/// <para>
/// The name inside a PDF is whatever its producer wrote. Used as-is for
/// "Save All", <c>../../.zshrc</c> or <c>/Users/x/Library/LaunchAgents/a.plist</c>
/// would write outside the folder the user chose, and <c>a/b</c> would fail on
/// every platform. So the name is cut to its last path segment, stripped of
/// characters any of the three desktop platforms rejects plus invisible control
/// and format characters (a bidi override in a file name is a spoofing tool,
/// not a name), and given a neutral fallback when nothing is left.
/// </para>
/// <para>
/// This does not decide whether the content is safe. excise never opens or
/// runs an attachment; it only writes the bytes where the user asked.
/// </para>
/// </remarks>
internal static class AttachmentFileNames
{
    private const int MaxLength = 180;

    // Rejected on Windows, and therefore unsafe for a file that may travel
    // there, even though macOS and Linux accept most of them.
    private const string PortableInvalidChars = "<>:\"/\\|?*";

    private static readonly string[] WindowsReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// A single, portable file name for <paramref name="declaredName"/>;
    /// <c>attachment-{ordinal}</c> when the declared name has nothing usable.
    /// </summary>
    public static string ToSafeFileName(string? declaredName, int ordinal)
    {
        var fallback = string.Create(CultureInfo.InvariantCulture, $"attachment-{ordinal}");
        if (string.IsNullOrWhiteSpace(declaredName))
            return fallback;

        // Last path segment under BOTH separators: a name written on Windows
        // uses '\', and Path.GetFileName on macOS would not split on it.
        var name = declaredName.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];

        var builder = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value < 0x20 || rune.Value == 0x7F)
                continue;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
                continue;
            if (rune.IsBmp && (PortableInvalidChars.Contains((char)rune.Value)
                               || Array.IndexOf(Path.GetInvalidFileNameChars(), (char)rune.Value) >= 0))
            {
                builder.Append('_');
                continue;
            }
            builder.Append(rune.ToString());
        }

        // Windows drops trailing dots and spaces silently, and a leading dot
        // makes a hidden file on macOS/Linux — neither is what "save" means.
        name = builder.ToString().Trim().TrimEnd('.').TrimStart('.').Trim();
        if (name.Length == 0)
            return fallback;

        var stem = Path.GetFileNameWithoutExtension(name);
        if (Array.Exists(WindowsReservedNames, r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase)))
            name = "_" + name;

        if (name.Length > MaxLength)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 16)
                extension = string.Empty;
            name = name[..(MaxLength - extension.Length)] + extension;
        }

        return name;
    }

    /// <summary>
    /// The first of <c>name</c>, <c>name (2)</c>, <c>name (3)</c>… that does not
    /// exist in <paramref name="folder"/>, as a full path proven to sit directly
    /// inside it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The combined path escaped <paramref name="folder"/> — unreachable given
    /// <see cref="ToSafeFileName"/>, and checked anyway because this is the line
    /// between "save into the folder you picked" and "write anywhere".
    /// </exception>
    public static string UniquePathIn(string folder, string safeFileName)
    {
        var root = Path.GetFullPath(folder);
        var stem = Path.GetFileNameWithoutExtension(safeFileName);
        var extension = Path.GetExtension(safeFileName);

        for (var n = 1; ; n++)
        {
            var candidateName = n == 1
                ? safeFileName
                : string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){extension}");
            var candidate = Path.GetFullPath(Path.Combine(root, candidateName));

            if (!string.Equals(Path.GetDirectoryName(candidate), root.TrimEnd(Path.DirectorySeparatorChar),
                    OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to write attachment outside {root}.");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }
}
