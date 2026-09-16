using System;
using System.IO;
using System.Linq;
using Excise.TestSupport;
using AwesomeAssertions;
using Xunit;

namespace Excise.Core.Tests.TestSupport;

/// <summary>
/// Pins <see cref="TestRepoLayout"/> against SYNTHETIC checkout trees, not
/// against the machine it happens to run on.
///
/// <para>The central case (#1527) is a linked git worktree placed somewhere
/// UNRELATED to the main checkout, with the gitignored corpora present only in
/// main. <b>No depth bound can pass that test</b>, and neither can anchoring on
/// <c>.git</c> or <c>excise.sln</c> — both mark the worktree root. That is the
/// whole reason this class exists rather than a larger number.</para>
/// </summary>
public class TestRepoLayoutTests
{
    // ---------------------------------------------------------------------
    // Synthetic trees
    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds a main checkout and, optionally, a linked worktree of it, laid
    /// out the way git actually lays them out.
    /// </summary>
    private sealed class Sandbox : IDisposable
    {
        public string Base { get; }
        public string MainRoot { get; }

        public Sandbox()
        {
            Base = Path.Combine(Path.GetTempPath(), "excise-layout-" + Guid.NewGuid().ToString("N"));
            MainRoot = Path.Combine(Base, "checkouts", "pdfe");
            Directory.CreateDirectory(Path.Combine(MainRoot, ".git"));
            File.WriteAllText(Path.Combine(MainRoot, "excise.sln"), "");
            // Gitignored corpus: exists in MAIN ONLY. This is the asymmetry
            // every previous locator got wrong.
            Directory.CreateDirectory(Path.Combine(MainRoot, "test-pdfs", "smoke"));
            File.WriteAllText(Path.Combine(MainRoot, "test-pdfs", "smoke", "irs-w4.pdf"), "%PDF-1.7");
            // Tracked file: exists in EVERY checkout.
            Directory.CreateDirectory(Path.Combine(MainRoot, "tests"));
            File.WriteAllText(Path.Combine(MainRoot, "tests", "gates.tsv"), "#\n");
        }

        /// <summary>
        /// A linked worktree. <paramref name="nested"/> false puts it in a
        /// directory that is NOT under the main checkout — the layout an
        /// upward walk can never reach, at any bound.
        /// </summary>
        /// <param name="commonDir">
        /// What to write into <c>commondir</c>: relative (git's normal
        /// <c>../..</c>), an absolute path, or null to omit the file entirely.
        /// </param>
        public string AddWorktree(string name, bool nested, string? commonDir = "../..")
        {
            var root = nested
                ? Path.Combine(MainRoot, ".claude", "worktrees", name)
                : Path.Combine(Base, "elsewhere", "wt-" + name);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "excise.sln"), "");
            Directory.CreateDirectory(Path.Combine(root, "tests"));
            File.WriteAllText(Path.Combine(root, "tests", "gates.tsv"), "#\n");

            var gitDir = Path.Combine(MainRoot, ".git", "worktrees", name);
            Directory.CreateDirectory(gitDir);
            if (commonDir != null)
            {
                var value = commonDir == "@abs" ? Path.Combine(MainRoot, ".git") : commonDir;
                File.WriteAllText(Path.Combine(gitDir, "commondir"), value + "\n");
            }

            File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {gitDir}\n");
            return root;
        }

        /// <summary>The directory a test host actually starts from: the assembly output dir.</summary>
        public static string BinDir(string checkoutRoot)
        {
            var d = Path.Combine(checkoutRoot, "Excise.Rendering.Tests", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(d);
            return d;
        }

        public void Dispose()
        {
            try { Directory.Delete(Base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------------
    // The #1527 case
    // ---------------------------------------------------------------------

    [Fact]
    public void AWorktreeOutsideTheMainCheckout_StillResolvesTheGitignoredCorpus()
    {
        using var sb = new Sandbox();
        var worktree = sb.AddWorktree("fix-1527", nested: false);
        var start = Sandbox.BinDir(worktree);

        var roots = TestRepoLayout.DiscoverRoots(start);

        roots.Local.Should().Be(worktree,
            "the local root is the checkout the test binary was built from");
        roots.Main.Should().Be(sb.MainRoot,
            "the main checkout is read out of the worktree's .git file (gitdir:) and its commondir — " +
            "not guessed from the directory layout. This worktree is NOT under the main checkout, " +
            "so no upward walk at any depth bound could have found it (#1527).");

        roots.SearchRoots.Should().Equal(new[] { worktree, sb.MainRoot },
            "local first: a worktree's own tracked fixtures beat the main checkout's, " +
            "which may sit at a different commit");
    }

    [Fact]
    public void ANestedWorktree_ResolvesTheMainCheckoutTheSameWay()
    {
        // The layout this repo actually uses (.claude/worktrees/<branch>) and the
        // only one a raised bound ever handled. It must keep working, and it must
        // work for the same reason as the non-nested case, not by luck of nesting.
        using var sb = new Sandbox();
        var worktree = sb.AddWorktree("fix-1527", nested: true);

        var roots = TestRepoLayout.DiscoverRoots(Sandbox.BinDir(worktree));

        roots.Local.Should().Be(worktree);
        roots.Main.Should().Be(sb.MainRoot);
    }

    [Fact]
    public void AMainCheckout_HasOneRoot()
    {
        using var sb = new Sandbox();

        var roots = TestRepoLayout.DiscoverRoots(Sandbox.BinDir(sb.MainRoot));

        roots.Local.Should().Be(sb.MainRoot);
        roots.Main.Should().Be(sb.MainRoot, ".git is a real directory here, so this IS the main checkout");
        roots.SearchRoots.Should().HaveCount(1, "the two roots coincide and must not be searched twice");
    }

    [Theory]
    [InlineData("../..", "git's normal relative commondir")]
    [InlineData("@abs", "an absolute commondir is legal too")]
    [InlineData(null, "no commondir file: fall back to the documented .git/worktrees/<name> shape")]
    public void TheCommonDirPointer_IsHandledInEveryLegalForm(string? commonDir, string why)
    {
        using var sb = new Sandbox();
        var worktree = sb.AddWorktree("wt", nested: false, commonDir: commonDir);

        var roots = TestRepoLayout.DiscoverRoots(Sandbox.BinDir(worktree));

        roots.Main.Should().Be(sb.MainRoot, why);
    }

    [Fact]
    public void ATrailingNewlineInTheGitPointers_IsTrimmed()
    {
        using var sb = new Sandbox();
        var worktree = sb.AddWorktree("wt", nested: false);
        var gitDir = Path.Combine(sb.MainRoot, ".git", "worktrees", "wt");

        // git writes both files with a trailing newline; an untrimmed read
        // produces a path that exists nowhere and a silent fallback to local.
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");

        TestRepoLayout.DiscoverRoots(Sandbox.BinDir(worktree)).Main.Should().Be(sb.MainRoot);
    }

    [Fact]
    public void ABrokenGitPointer_FallsBackToTheLocalCheckout_RatherThanSomewhereArbitrary()
    {
        using var sb = new Sandbox();
        var worktree = sb.AddWorktree("wt", nested: false);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /nonexistent/does/not/exist\n");

        var roots = TestRepoLayout.DiscoverRoots(Sandbox.BinDir(worktree));

        roots.Local.Should().Be(worktree);
        roots.Main.Should().Be(worktree,
            "a hand-written or stale .git file must not redirect fixture lookups to an arbitrary " +
            "directory; the honest answer is 'only this checkout'");
    }

    [Fact]
    public void ACheckoutWithNoGitMetadata_StillResolvesTrackedFixturesViaTheSolutionFile()
    {
        using var sb = new Sandbox();
        Directory.Delete(Path.Combine(sb.MainRoot, ".git"), recursive: true);

        var roots = TestRepoLayout.DiscoverRoots(Sandbox.BinDir(sb.MainRoot));

        roots.Local.Should().Be(sb.MainRoot, "a source export with no git metadata still has excise.sln");
        roots.Main.Should().Be(sb.MainRoot);
    }

    [Fact]
    public void NoCheckoutAnywhereAbove_ReportsNothingRatherThanGuessing()
    {
        var orphan = Path.Combine(Path.GetTempPath(), "excise-orphan-" + Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(orphan);
        try
        {
            // TMPDIR is normally outside any checkout, but it need not be: say
            // so rather than failing for a reason that is not the subject.
            Assert.SkipWhen(HasCheckoutAbove(orphan),
                $"TMPDIR is inside a checkout ({Path.GetTempPath()}), so 'no checkout above' is not testable here");

            var roots = TestRepoLayout.DiscoverRoots(orphan);
            roots.Local.Should().BeNull();
            roots.Main.Should().BeNull();
            roots.SearchRoots.Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(orphan)!, recursive: true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------------
    // Absence claims must be checkable (the #1172 blind spot)
    // ---------------------------------------------------------------------

    [Fact]
    public void AnAbsenceReason_NamesTheAbsolutePathsItSearched()
    {
        var reason = TestRepoLayout.AbsenceReason("smoke/federal corpus", "test-pdfs/smoke", "test-pdfs/federal");

        reason.Should().Contain(TestRepoLayout.SearchedMarker,
            "scripts/check-skip-budget.sh parses this marker and re-tests every path it names — " +
            "that is what turns 'corpus not present' from an unfalsifiable sentence into a claim (#1527)");

        var searched = ParseSearched(reason);
        searched.Should().NotBeEmpty();
        searched.Should().OnlyContain(p => Path.IsPathRooted(p),
            "a relative path would have to be re-resolved by the checker, which is how the " +
            "checker would inherit the very blindness it is checking for");
        searched.Should().Contain(p => p.EndsWith(Path.Combine("test-pdfs", "smoke"), StringComparison.Ordinal));
        searched.Should().Contain(p => p.EndsWith(Path.Combine("test-pdfs", "federal"), StringComparison.Ordinal));
    }

    [Fact]
    public void AnAbsenceReason_CoversEverySearchRoot_SoAWorktreeClaimIsNotHalfTrue()
    {
        TestRepoLayout.SearchRoots.Should().NotBeEmpty("the tests run from a checkout");

        var searched = ParseSearched(TestRepoLayout.AbsenceReason("corpus", "test-pdfs/smoke"));

        searched.Should().HaveCount(TestRepoLayout.SearchRoots.Count,
            "claiming absence while having searched only one of two roots is the half-truth " +
            "that read as green in a worktree");
    }

    private static bool HasCheckoutAbove(string start)
    {
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))
                || File.Exists(Path.Combine(d.FullName, ".git"))
                || File.Exists(Path.Combine(d.FullName, "excise.sln")))
                return true;
        return false;
    }

    private static string[] ParseSearched(string reason)
    {
        var at = reason.IndexOf(TestRepoLayout.SearchedMarker, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0);
        var tail = reason.Substring(at + TestRepoLayout.SearchedMarker.Length).TrimEnd(']', ' ');
        return tail.Split(TestRepoLayout.SearchedSeparator, StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => s.Trim())
                   .Where(s => s != "(none)")
                   .ToArray();
    }

    // ---------------------------------------------------------------------
    // And on this machine, right now
    // ---------------------------------------------------------------------

    [Fact]
    public void OnThisMachine_TheLayoutResolvesATrackedFile()
    {
        TestRepoLayout.LocalCheckoutRoot.Should().NotBeNull();
        TestRepoLayout.FindFile("tests/gates.tsv").Should().NotBeNull(
            "tests/gates.tsv is tracked, so it is present in every checkout including a worktree");
        TestRepoLayout.FindDirectory("Excise.Core").Should().NotBeNull();
    }

    [Fact]
    public void OnThisMachine_BuildOutputIsSoughtInTheLocalCheckoutOnly()
    {
        // Not an assertion about whether the CLI is built — an assertion about
        // WHERE it may be looked for. A worktree must never resolve a build
        // artifact out of the main checkout: it would test the wrong binary and
        // pass (#1525's two deliberately-unswept FindCliAssembly locators).
        var found = TestRepoLayout.FindFileInLocalCheckout("Excise.Cli", "bin", "Debug", "net10.0", "excise.dll");
        if (found != null)
            found.Should().StartWith(TestRepoLayout.LocalCheckoutRoot!);
    }
}
