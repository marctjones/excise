using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Excise.TestSupport;

/// <summary>
/// THE one way a test finds a file or directory that lives in the repository
/// rather than beside the test binary — fixtures, corpora, manifests,
/// baselines, contracts.
///
/// <para><b>Why this exists (#1527, #1525).</b> Thirteen test files had
/// hand-rolled copies of "walk up N directories from the test binary looking
/// for <c>test-pdfs/…</c>". From the main checkout the corpora are 4 levels
/// up; from <c>&lt;repo&gt;/.claude/worktrees/&lt;branch&gt;/&lt;proj&gt;/bin/Debug/net10.0</c>
/// they are 7. Three of those copies stopped at 6, and they were the redaction
/// gates. Measured on one machine at one commit, with nothing differing but
/// the bound: <b>39 rows collected / 2 passing</b> at bound 6 against
/// <b>1110 / 195</b> at bound 12. <c>RedactionCollateralHarness</c> 0 → 101
/// passing, <c>ConservationGateTests</c> 0 → 80,
/// <c>RedactionRemoteCollateralTests</c> 2 → 14. Every worktree run of those
/// gates since 2026-08-12 was vacuous, and reported itself green.</para>
///
/// <para><b>Why a bigger bound is not the fix.</b> A bound of 12 works only
/// because this repo's worktrees happen to be nested INSIDE the main checkout
/// (<c>.claude/worktrees/…</c>), so walking far enough upward re-enters it. A
/// worktree created anywhere else — <c>/tmp</c>, another volume, a sibling
/// directory — is never reached at any bound, and a bound large enough to
/// leave the repo can also match a stray <c>~/test-pdfs</c>. The bound is a
/// stopgap for one directory layout; this is the resolver.</para>
///
/// <para><b>Why anchoring on a repo marker is not the fix either.</b> In a git
/// worktree <c>.git</c> is a FILE at the worktree root, and
/// <c>excise.sln</c> is checked in, so both markers identify the WORKTREE
/// root. The gitignored corpora (<c>test-pdfs/smoke</c>,
/// <c>test-pdfs/federal</c>, <c>pdfjs</c>, <c>pdfium</c>, <c>verapdf-corpus</c>,
/// …) exist only in the main checkout. Anchoring on a marker therefore stops
/// in exactly the wrong place — measured on
/// <c>EncryptedRedactionOutputIntegrityTests</c>, which already walks
/// unbounded to <c>.git</c> and still cannot see a corpus.</para>
///
/// <para><b>What this does instead.</b> It reads git's own worktree plumbing.
/// A worktree's <c>.git</c> file says <c>gitdir: &lt;path&gt;</c>; that
/// directory holds a <c>commondir</c> file pointing at the main repository's
/// <c>.git</c>; that directory's parent is the MAIN CHECKOUT. So there are two
/// roots, searched in this order:</para>
/// <list type="number">
///   <item><see cref="LocalCheckoutRoot"/> — the checkout the running test
///   assembly was built from. Tracked fixtures must come from HERE, because a
///   worktree may sit at a different commit and its own fixtures are the ones
///   its code is written against.</item>
///   <item><see cref="MainCheckoutRoot"/> — where the gitignored corpora live.
///   Equal to the local root outside a worktree.</item>
/// </list>
///
/// <para>⚠️ <b>Upward search is right for read-only repository data and wrong
/// for build output.</b> A test that needs <c>Excise.Cli/bin/**/excise.dll</c>
/// must use <see cref="FindFileInLocalCheckout"/>: the CLI is a build-order
/// <c>ProjectReference</c>, so it exists inside every checkout by
/// construction, and reaching the MAIN checkout's binary from a worktree would
/// silently test the wrong build — a worse failure than not finding it.</para>
///
/// <para>⚠️ <b>Absence must be claimed truthfully.</b> #1172 requires a skip to
/// carry a reason and checks only that a reason EXISTS; "smoke corpus not
/// present" satisfied it while the corpus sat 7 levels up. Use
/// <see cref="AbsenceReason"/>: it embeds the ABSOLUTE paths that were
/// searched, so <c>scripts/check-skip-budget.sh</c> can re-check the claim
/// with <c>test -e</c> and fail a skip that lies.</para>
/// </summary>
internal static class TestRepoLayout
{
    /// <summary>
    /// Marks an absence claim inside a skip/failure message. The paths that
    /// follow are absolute and were searched; a checker re-tests them.
    /// Kept in sync with <c>scripts/check-skip-budget.sh</c>, which parses it.
    /// </summary>
    internal const string SearchedMarker = "excise-searched:";

    /// <summary>Separator between searched paths — <c>,</c> and <c>:</c> can occur in a path.</summary>
    internal const string SearchedSeparator = " | ";

    private static readonly Lazy<Roots> Discovered = new(() => DiscoverRoots(AppContext.BaseDirectory));

    /// <summary>
    /// The checkout holding the running test assembly — a git worktree's own
    /// root when the tests were built in one. Null when no checkout can be
    /// identified at all (no <c>.git</c> and no solution file above the
    /// binary), which is not a situation any gate is expected to run in.
    /// </summary>
    public static string? LocalCheckoutRoot => Discovered.Value.Local;

    /// <summary>
    /// The MAIN checkout — the one whose <c>.git</c> is a real directory, and
    /// the only one that holds the gitignored corpora. Equal to
    /// <see cref="LocalCheckoutRoot"/> outside a worktree.
    /// </summary>
    public static string? MainCheckoutRoot => Discovered.Value.Main;

    /// <summary>
    /// The roots <see cref="FindFile"/> and <see cref="FindDirectory"/> search,
    /// in order, deduplicated. Local first: a worktree's tracked fixtures beat
    /// the main checkout's, which may be at a different commit.
    /// </summary>
    public static IReadOnlyList<string> SearchRoots => Discovered.Value.SearchRoots;

    /// <summary>
    /// Absolute path of a repository FILE, or null. <paramref name="segments"/>
    /// are repo-relative — <c>FindFile("test-pdfs", "smoke", "irs-w4.pdf")</c>
    /// or <c>FindFile("tests/gates.tsv")</c>; forward slashes inside a segment
    /// are accepted so existing relative-path call sites need no rewriting.
    /// </summary>
    public static string? FindFile(params string[] segments) =>
        Find(segments, File.Exists, SearchRoots);

    /// <summary>Absolute path of a repository DIRECTORY, or null.</summary>
    public static string? FindDirectory(params string[] segments) =>
        Find(segments, Directory.Exists, SearchRoots);

    /// <summary>
    /// Absolute path of a file in the LOCAL checkout only — never the main
    /// checkout. Use this and nothing else for build output: see the class
    /// remarks.
    /// </summary>
    public static string? FindFileInLocalCheckout(params string[] segments) =>
        Find(segments, File.Exists, LocalCheckoutRoot == null ? Array.Empty<string>() : new[] { LocalCheckoutRoot });

    /// <summary>
    /// A skip/failure reason whose absence claim is CHECKABLE: it names what
    /// was wanted and the absolute paths that were searched for it.
    /// </summary>
    /// <param name="what">Short description, e.g. "smoke/federal corpus".</param>
    /// <param name="relativePaths">The repo-relative paths that were looked for.</param>
    public static string AbsenceReason(string what, params string[] relativePaths)
    {
        var searched = relativePaths.Length == 0 || SearchRoots.Count == 0
            ? Array.Empty<string>()
            : relativePaths
                .SelectMany(rel => SearchRoots.Select(root => Path.Combine(root, NormalizeRelative(rel))))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        // No root at all is itself the claim: say so rather than emitting an
        // empty searched-list that a checker would read as "nothing to check".
        return searched.Length == 0
            ? $"{what} not present — no repository checkout could be identified above {AppContext.BaseDirectory} " +
              $"[{SearchedMarker} (none)]"
            : $"{what} not present [{SearchedMarker} {string.Join(SearchedSeparator, searched)}]";
    }

    private static string? Find(string[] segments, Func<string, bool> exists, IReadOnlyList<string> roots)
    {
        if (segments.Length == 0) return null;
        var rel = NormalizeRelative(Path.Combine(segments));

        foreach (var root in roots)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, rel));
            if (exists(candidate)) return candidate;
        }

        return null;
    }

    private static string NormalizeRelative(string rel) =>
        rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    internal readonly struct Roots
    {
        public Roots(string? local, string? main)
        {
            Local = local;
            Main = main;
            SearchRoots = new[] { local, main }
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => r!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        public string? Local { get; }
        public string? Main { get; }
        public IReadOnlyList<string> SearchRoots { get; }
    }

    /// <summary>
    /// Root discovery, as a pure function of a starting directory so it can be
    /// exercised against a synthetic worktree tree rather than only against the
    /// machine it happens to run on (<c>TestRepoLayoutTests</c>). A depth bound
    /// cannot pass those tests, which is the point.
    /// </summary>
    internal static Roots DiscoverRoots(string startDirectory)
    {
        var local = FindCheckoutRoot(startDirectory);
        if (local == null) return new Roots(null, null);

        var main = FindMainCheckoutRoot(local) ?? local;
        return new Roots(local, main);
    }

    /// <summary>
    /// Nearest ancestor of <paramref name="startDirectory"/> that looks like a
    /// checkout of this repository. <c>.git</c> first (directory OR file — a
    /// worktree's is a file), then <c>excise.sln</c> so a source export with no
    /// git metadata still resolves its tracked fixtures.
    /// <para>The walk is unbounded by design: the 31 locators that already
    /// walked to the filesystem root were the correct ones, and bounding it is
    /// the defect this class replaces.</para>
    /// </summary>
    private static string? FindCheckoutRoot(string startDirectory)
    {
        var dir = SafeDirectory(startDirectory);
        while (dir != null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return dir.FullName;
            dir = dir.Parent;
        }

        dir = SafeDirectory(startDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// The main checkout for a (possibly) linked worktree, read out of git's
    /// own plumbing rather than guessed from the directory layout:
    /// <code>
    /// &lt;worktree&gt;/.git                     → "gitdir: &lt;repo&gt;/.git/worktrees/&lt;name&gt;"
    /// &lt;repo&gt;/.git/worktrees/&lt;name&gt;/commondir → "../.."   (→ &lt;repo&gt;/.git)
    /// parent of that                          → &lt;repo&gt;        ← the main checkout
    /// </code>
    /// Returns null when <paramref name="checkoutRoot"/> is not a linked
    /// worktree, or when the plumbing does not resolve to an existing
    /// directory — a broken or hand-written <c>.git</c> file must not silently
    /// redirect fixture lookups somewhere arbitrary.
    /// </summary>
    private static string? FindMainCheckoutRoot(string checkoutRoot)
    {
        var dotGit = Path.Combine(checkoutRoot, ".git");

        // A real .git DIRECTORY means this already IS the main checkout.
        if (Directory.Exists(dotGit) || !File.Exists(dotGit)) return null;

        string text;
        try { text = File.ReadAllText(dotGit); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var gitDir = ParseGitdirPointer(text, checkoutRoot);
        if (gitDir == null || !Directory.Exists(gitDir)) return null;

        // commondir is git's own answer for "where is the shared .git". It is
        // usually relative ("../..") and resolved against gitDir; absolute is
        // legal too. Absent (an unusual layout) falls back to the documented
        // .git/worktrees/<name> shape.
        var commonDir = ReadCommonDir(gitDir) ?? Path.GetFullPath(Path.Combine(gitDir, "..", ".."));
        if (!Directory.Exists(commonDir)) return null;

        var root = Path.GetDirectoryName(commonDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (root == null || !Directory.Exists(root)) return null;

        return Path.GetFullPath(root);
    }

    private static string? ParseGitdirPointer(string dotGitContents, string relativeTo)
    {
        foreach (var raw in dotGitContents.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) continue;

            var target = line.Substring("gitdir:".Length).Trim();
            if (target.Length == 0) return null;

            return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(relativeTo, target));
        }

        return null;
    }

    private static string? ReadCommonDir(string gitDir)
    {
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile)) return null;

        string target;
        try { target = File.ReadAllText(commonDirFile).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (target.Length == 0) return null;

        return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(gitDir, target));
    }

    private static DirectoryInfo? SafeDirectory(string path)
    {
        try { return new DirectoryInfo(Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
    }
}
