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
/// <para>A symlinked target is resolved to its final target first, so the save
/// writes THROUGH the link and the link survives (#1683: before this, saving
/// onto a link replaced it with a regular file and the file it pointed at
/// never received the save).</para>
///
/// <para>Consequences a caller accepts: the target gets a NEW inode, so Finder
/// tags, comments and other extended attributes and the old mode bits are not
/// carried over (measured on macOS for #1683: <c>user.*</c> and
/// <c>kMDItemFinderComment</c> are gone after a save; Reduce File Size has
/// behaved this way since #1550), and the save needs write permission on the
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

    /// <param name="path">The target file. A symlink is followed to its final
    /// target, which is what gets replaced.</param>
    /// <param name="write">Writes the whole file to the stream it is given.</param>
    /// <param name="createDirectory">Create the target's directory when it is
    /// missing. Save(path) does not (a missing directory is a wrong path, and
    /// the GUI's close-with-unsaved-changes flow relies on that save FAILING);
    /// the file-size optimizer always has.</param>
    /// <param name="expected">What the caller last read from disk. When it
    /// names the same file as the target and that file has since changed,
    /// the save is refused with <see cref="FileChangedOnDiskException"/>
    /// instead of overwriting the newer version (#1683).</param>
    /// <returns>The target's on-disk state after the replace.</returns>
    public static OnDiskFileState Write(
        string path, Action<Stream> write, bool createDirectory = false,
        OnDiskFileState? expected = null)
    {
        var fullPath = ResolveFinalPath(path);
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
            // Checked after the (possibly long) write, immediately before the
            // commit, so the window in which a replacement can slip past is
            // the rename itself.
            if (expected is { } known && known.IsSameFileAs(fullPath))
                known.ThrowIfChangedOnDisk();
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

        return OnDiskFileState.Capture(fullPath);
    }

    /// <summary>
    /// The full path with a final-component symlink chain resolved, so a save
    /// replaces the file the link points at rather than the link. A dangling
    /// link resolves to the path it names, and the save creates that file.
    /// </summary>
    internal static string ResolveFinalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            var target = new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? fullPath : Path.GetFullPath(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A link loop or an unreadable link: leave the path as given and
            // let the write report whatever is wrong with it.
            return fullPath;
        }
    }
}

/// <summary>
/// A file's length and last-write time as excise last saw it — at open, or
/// after its own save. #1683: the GUI keeps its document on an open
/// <c>FileStream</c> for the document's life, so if a sync client
/// (Dropbox, iCloud, OneDrive) atomically replaces the file underneath, the
/// stream keeps reading the OLD inode and a plain save would overwrite the
/// newer version with a stale one. Comparing against this snapshot refuses
/// that save instead.
/// </summary>
internal readonly record struct OnDiskFileState(string FullPath, long Length, DateTime LastWriteTimeUtc)
{
    /// <summary>The current on-disk state of <paramref name="path"/>
    /// (symlinks followed).</summary>
    internal static OnDiskFileState Capture(string path)
    {
        var fullPath = AtomicFileReplace.ResolveFinalPath(path);
        var info = new FileInfo(fullPath);
        return new OnDiskFileState(fullPath, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// The state of the file a document is being read from, or null when the
    /// stream is not a file. The length is the OPEN handle's (the inode the
    /// document actually reads) and the time is read right after, by path: a
    /// replacement between the open and this read shows up as a length the
    /// path does not have, which refuses a later save — the safe direction.
    /// </summary>
    internal static OnDiskFileState? FromSource(Stream stream)
    {
        if (stream is not FileStream { CanSeek: true } file)
            return null;
        try
        {
            var fullPath = AtomicFileReplace.ResolveFinalPath(file.Name);
            return new OnDiskFileState(fullPath, file.Length, File.GetLastWriteTimeUtc(fullPath));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal bool IsSameFileAs(string resolvedFullPath)
        => string.Equals(FullPath, resolvedFullPath,
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Refuse when the file now on disk is not the one last seen. A file that
    /// has disappeared is not refused: there is no newer version to lose.
    /// </summary>
    internal void ThrowIfChangedOnDisk()
    {
        var now = new FileInfo(FullPath);
        if (!now.Exists)
            return;
        if (now.Length != Length || now.LastWriteTimeUtc != LastWriteTimeUtc)
            throw new FileChangedOnDiskException(FullPath);
    }
}

/// <summary>
/// A save was refused because the target file changed on disk since excise
/// last read it (#1683). The message is shown to the user as-is.
/// </summary>
internal sealed class FileChangedOnDiskException(string path) : IOException(
    $"\"{Path.GetFileName(path)}\" was changed on disk by another program since it was opened, " +
    "so saving would overwrite the newer version. Nothing was saved. " +
    "Reopen the file to pick up the change, or use Save As to keep both.")
{
    /// <summary>The file that changed.</summary>
    public string FilePath { get; } = path;
}
