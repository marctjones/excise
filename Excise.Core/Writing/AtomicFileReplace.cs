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
    /// <summary>
    /// Rename the finished temporary over <paramref name="fullPath"/>.
    /// </summary>
    /// <remarks>
    /// POSIX <c>rename()</c> replaces an open file without complaint, which is
    /// the whole premise of this class. Windows does not: <c>File.Move</c> with
    /// overwrite is <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c>, and it
    /// returned <c>UnauthorizedAccessException</c> on the Windows runner
    /// (#1683) for a destination the GUI still had open — twice on the same
    /// commit, 1 failure of 5,185, so not environmental. The reader in that
    /// test already permits delete sharing
    /// (<c>FileShare.ReadWrite | FileShare.Delete</c>), so the naive
    /// missing-share-flag explanation does not hold.
    ///
    /// <para><c>ReplaceFile</c> (<see cref="File.Replace(string, string, string)"/>)
    /// is the Win32 API written for this case — replacing a file that may be
    /// in use — and it additionally preserves the destination's attributes,
    /// ACLs and alternate data streams, which a rename does not. That is a
    /// bonus here: it narrows the "the target gets a NEW inode" consequence
    /// this class documents.</para>
    ///
    /// <para>It is tried only on Windows and only when the destination already
    /// exists (<c>ReplaceFile</c> requires it), and a failure falls back to the
    /// rename so behaviour can only improve. POSIX keeps the rename untouched.
    /// ⚠️ Whether this actually fixes #1683 is decided by the Windows runner,
    /// not by this comment.</para>
    /// </remarks>
    private static void MoveIntoPlace(string temporary, string fullPath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(fullPath))
        {
            try
            {
                File.Replace(temporary, fullPath, destinationBackupFileName: null);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        File.Move(temporary, fullPath, overwrite: true);
    }

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
            MoveIntoPlace(temporary, fullPath);
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
