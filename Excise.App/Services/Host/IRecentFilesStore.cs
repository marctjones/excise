using System.Collections.Generic;
using System.IO;

namespace Excise.App.Services.Host;

/// <summary>
/// The recent-files list as a dependency (#1500 step 2): the one route to
/// <c>recent.txt</c>.
/// </summary>
/// <remarks>
/// Mechanism only. The MRU rules — at most ten entries, newest first,
/// de-duplicated, and "drop entries whose file no longer exists" (#25) — stay
/// with the view model that owns the list.
/// </remarks>
internal interface IRecentFilesStore
{
    /// <summary>
    /// The stored paths in stored order, or empty when nothing is stored.
    /// Entries are returned as written: existence is not checked here.
    /// </summary>
    IReadOnlyList<string> Load();

    /// <summary>Replaces the stored list with <paramref name="paths"/>.</summary>
    void Save(IEnumerable<string> paths);
}

/// <summary>The production <see cref="IRecentFilesStore"/>: one path per line.</summary>
internal sealed class FileRecentFilesStore : IRecentFilesStore
{
    /// <inheritdoc />
    public IReadOnlyList<string> Load()
    {
        var path = AppPaths.RecentFilesPath;
        return File.Exists(path) ? File.ReadAllLines(path) : System.Array.Empty<string>();
    }

    /// <inheritdoc />
    public void Save(IEnumerable<string> paths)
    {
        // AppPaths.DataDir ensures the directory exists.
        File.WriteAllLines(AppPaths.RecentFilesPath, paths);
    }
}
