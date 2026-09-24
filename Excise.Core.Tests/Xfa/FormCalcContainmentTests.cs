using AwesomeAssertions;
using Xunit;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #1570: scripts run when a form is opened and nowhere else. Redaction, save, print-copy creation and
/// the command line must never execute one, and nothing outside the XFA layout may reach the
/// interpreter. This reads the sources, so a new caller fails here and has to be argued for.
/// </summary>
public class FormCalcContainmentTests
{
    private static IEnumerable<(string Path, string Text)> Sources(params string[] roots)
    {
        foreach (var root in roots)
        {
            var dir = TestRepoLayout.FindDirectory(root);
            Assert.SkipWhen(dir == null, TestRepoLayout.AbsenceReason("source tree", root));
            foreach (var file in Directory.EnumerateFiles(dir!, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;
                yield return (file, File.ReadAllText(file));
            }
        }
    }

    [Fact]
    public void OnlyTheXfaScriptRunnerReachesTheInterpreter()
    {
        var offenders = Sources("Excise.Core", "Excise.Cli", "Excise.App", "Excise.Rendering", "Excise.Avalonia", "Excise.Ocr")
            .Where(s => s.Text.Contains("FormCalcInterpreter", StringComparison.Ordinal)
                        && !s.Path.Contains($"{Path.DirectorySeparatorChar}FormCalc{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && Path.GetFileName(s.Path) != "XfaScripts.cs")
            .Select(s => s.Path).ToList();
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void OnlyApplyXfaLayoutRunsScripts()
    {
        var offenders = Sources("Excise.Core", "Excise.Cli", "Excise.App", "Excise.Rendering", "Excise.Avalonia")
            .Where(s => s.Text.Contains("XfaScripts.Run(", StringComparison.Ordinal)
                        && Path.GetFileName(s.Path) is not ("PdfXfaLayout.cs" or "XfaScripts.cs"))
            .Select(s => s.Path).ToList();
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void RedactionSaveAndTheCommandLineDoNotLayOutForms()
    {
        var offenders = Sources("Excise.Core/Redaction", "Excise.Cli")
            .Where(s => s.Text.Contains("ApplyXfaLayout", StringComparison.Ordinal))
            .Select(s => s.Path).ToList();
        offenders.Should().BeEmpty("layout runs scripts, and only opening a document may do that");
    }
}
