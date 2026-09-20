namespace Excise.Core.Writing;

/// <summary>
/// Writes a file by way of a sibling temporary and a rename over the target, so
/// the target is either the old file or the complete new one — never truncated
/// and never half-written.
/// </summary>
/// <remarks>
/// Two things depend on this. A crash or an exception mid-write leaves the old
/// file where it was. And a document that is being READ from the target path —
/// the GUI keeps its current document on a shared-read <c>FileStream</c> rather
/// than a whole-file copy (#1567) — keeps reading the old inode after the
/// rename, so a save back onto the open file cannot corrupt the very bytes the
/// writer is still copying from it. <c>FileMode.Create</c> on the target would
/// truncate it under the reader first.
///
/// <para>Consequences a caller accepts: the target gets a NEW inode, so Finder
/// tags, extended attributes and the old mode bits are not carried over (Reduce
/// File Size has behaved this way since #1550); saving onto a symlink replaces
/// the link with a regular file; and the save needs write permission on the
/// DIRECTORY, not only on the file.</para>
/// </remarks>
internal static class AtomicFileReplace
{
    /// <param name="path">The target file.</param>
    /// <param name="write">Writes the whole file to the stream it is given.</param>
    /// <param name="createDirectory">Create the target's directory when it is
    /// missing. Save(path) does not (a missing directory is a wrong path, and
    /// the GUI's close-with-unsaved-changes flow relies on that save FAILING);
    /// the file-size optimizer always has.</param>
    public static void Write(string path, Action<Stream> write, bool createDirectory = false)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (createDirectory && !string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory ?? string.Empty,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                write(file);
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
