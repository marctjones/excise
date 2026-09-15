using System;
using System.IO;
using AwesomeAssertions;
using Xunit;

namespace Excise.App.Tests.UI.InteractionCoverage;

/// <summary>
/// Pins where <see cref="GuiInteractionRecorder"/> writes its coverage
/// artifacts. A worktree nests inside the main checkout, so the failure mode is
/// OVERSHOOT: stopping at the outer <c>.git</c> directory instead of the
/// worktree's <c>.git</c> file writes another branch's coverage over the main
/// checkout's and leaves the worktree's gate with nothing to read.
/// </summary>
public class GuiInteractionRecorderRepoRootTests
{
    [Fact]
    public void FindRepoRoot_InAWorktree_StopsAtTheGitFile_NotTheParentCheckout()
    {
        var main = Path.Combine(Path.GetTempPath(), "excise-reporoot-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(main, ".git"));
            var worktree = Path.Combine(main, ".claude", "worktrees", "wt");
            var binary = Path.Combine(worktree, "Excise.App.Tests", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(binary);
            File.WriteAllText(Path.Combine(worktree, ".git"),
                "gitdir: " + Path.Combine(main, ".git", "worktrees", "wt") + "\n");

            GuiInteractionRecorder.FindRepoRoot(binary).Should().Be(worktree);
        }
        finally
        {
            Directory.Delete(main, recursive: true);
        }
    }

    [Fact]
    public void FindRepoRoot_InAPlainCheckout_StopsAtTheGitDirectory()
    {
        var main = Path.Combine(Path.GetTempPath(), "excise-reporoot-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(main, ".git"));
            var binary = Path.Combine(main, "Excise.App.Tests", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(binary);

            GuiInteractionRecorder.FindRepoRoot(binary).Should().Be(main);
        }
        finally
        {
            Directory.Delete(main, recursive: true);
        }
    }
}
