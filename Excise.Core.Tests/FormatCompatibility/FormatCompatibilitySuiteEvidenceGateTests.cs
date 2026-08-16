using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Excise.Core.Tests.FormatCompatibility;

/// <summary>
/// Drift gate for tests/format-compatibility-suite.json (#957).
///
/// That file is a schema'd design tracker (PDF versions, storage formats,
/// feature workflows, oracles, known gaps) with a per-row implemented /
/// partial / planned / not_applicable status. Before this gate, nothing
/// referenced the file, so a row could be hand-edited to "implemented"
/// without any test ever having been written, and no CI signal would catch
/// it drifting away from reality in either direction.
///
/// This test enforces the "evidencePolicy" documented at the top of the
/// JSON: every row whose status is implemented or partial must carry an
/// "evidence" array, and every entry in that array must resolve to
/// something real:
///   - "&lt;path&gt;.cs"              -&gt; file exists, has &gt;=1 runnable
///                                    [Fact]/[Theory]-family test (not
///                                    Skip-only).
///   - "&lt;path&gt;.cs:MethodName"   -&gt; same file check, AND MethodName is
///                                    found with its own runnable
///                                    attribute directly above it.
///   - "&lt;path&gt;.sh"              -&gt; file exists and is executable.
///   - a directory                 -&gt; exists and recursively contains
///                                    &gt;=1 .cs file with a runnable test.
///
/// Deliberately static/textual rather than reflection-based: most evidence
/// in this file lives in Excise.Core.Tests (this assembly), but some lives
/// in other test projects (Excise.Cli.Tests) or in scripts/ that t0 does
/// not build a reference to. Loading those assemblies here would mean
/// Excise.Core.Tests referencing sibling test projects, which the solution
/// deliberately does not do. Reading source text is enough to prove the
/// evidence EXISTS and contains a runnable test declaration; it does not
/// need to actually RUN the test to serve as a drift gate, and staying
/// textual keeps this gate fast enough for t0 (see GuiWorkflowCoverageMatrixTests
/// in Excise.App.Tests for the reflection-based sibling of this mechanism,
/// which works because every row it checks lives in the same assembly).
/// </summary>
public class FormatCompatibilitySuiteEvidenceGateTests
{
    private const string SuiteRelativePath = "tests/format-compatibility-suite.json";

    private static readonly Regex RunnableAttributeRegex =
        new(@"\[(Fact|Theory|FixedAvaloniaFact|FixedAvaloniaTheory)\b([^\]]*)\]", RegexOptions.Compiled);

    private static readonly Regex EvidenceMethodRegex =
        new(@"^(?<path>.+\.cs):(?<member>[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.Compiled);

    [Fact]
    public void ImplementedAndPartialRows_HaveEvidenceThatExistsAndIsRunnable()
    {
        var repoRoot = FindRepoRoot();
        var suitePath = Path.Combine(repoRoot, SuiteRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(suitePath).Should().BeTrue($"{SuiteRelativePath} should exist");

        using var document = JsonDocument.Parse(File.ReadAllText(suitePath));
        var root = document.RootElement;

        var failures = new List<string>();

        CheckSection(root, "testTiers", row => row.GetProperty("id").GetString()!, RowNeedsEvidence, repoRoot, failures);
        CheckSection(root, "versionFormatMatrix", row => row.GetProperty("pdfVersion").GetString()!, RowNeedsEvidence, repoRoot, failures);
        CheckSection(root, "majorSuites", row => row.GetProperty("id").GetString()!, RowNeedsEvidence, repoRoot, failures);
        CheckSection(root, "featureMatrix", row => row.GetProperty("feature").GetString()!, FeatureRowNeedsEvidence, repoRoot, failures);

        failures.Should().BeEmpty(
            "tests/format-compatibility-suite.json's evidencePolicy requires every implemented/partial row " +
            "to name evidence that exists and contains a runnable test — see " +
            $"{nameof(FormatCompatibilitySuiteEvidenceGateTests)} and the JSON's own 'evidencePolicy' field");
    }

    [Fact]
    public void PlannedAndNotApplicableRows_CarryNoEvidence()
    {
        // Rule 2 of #957: planned rows are the visible gap list. A row that
        // is still planned but has picked up an "evidence" array (e.g. from
        // a careless copy/paste) is misleading in the opposite direction —
        // it would read as covered without ever being checked by the gate
        // above, since the gate above only *requires* evidence on
        // implemented/partial rows, it doesn't forbid it elsewhere.
        var repoRoot = FindRepoRoot();
        var suitePath = Path.Combine(repoRoot, SuiteRelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(suitePath));
        var root = document.RootElement;

        var misplaced = new List<string>();

        void CheckNoEvidence(string section, Func<JsonElement, string> rowId, Func<JsonElement, bool> needsEvidence)
        {
            foreach (var row in root.GetProperty(section).EnumerateArray())
            {
                if (needsEvidence(row))
                {
                    // implemented/partial: evidence is required and validated by
                    // ImplementedAndPartialRows_HaveEvidenceThatExistsAndIsRunnable.
                    continue;
                }

                if (row.TryGetProperty("evidence", out var evidence) && evidence.GetArrayLength() > 0)
                {
                    misplaced.Add($"{section}/{rowId(row)}: has an 'evidence' array but is not implemented/partial");
                }
            }
        }

        CheckNoEvidence("testTiers", row => row.GetProperty("id").GetString()!, RowNeedsEvidence);
        CheckNoEvidence("versionFormatMatrix", row => row.GetProperty("pdfVersion").GetString()!, RowNeedsEvidence);
        CheckNoEvidence("majorSuites", row => row.GetProperty("id").GetString()!, RowNeedsEvidence);
        CheckNoEvidence("featureMatrix", row => row.GetProperty("feature").GetString()!, FeatureRowNeedsEvidence);

        misplaced.Should().BeEmpty(
            "planned/not_applicable rows are the visible gap list and should not carry evidence " +
            "that was never actually checked for that status");
    }

    private static void CheckSection(
        JsonElement root,
        string section,
        Func<JsonElement, string> rowId,
        Func<JsonElement, bool> needsEvidence,
        string repoRoot,
        List<string> failures)
    {
        foreach (var row in root.GetProperty(section).EnumerateArray())
        {
            if (!needsEvidence(row))
            {
                continue;
            }

            var id = rowId(row);

            if (!row.TryGetProperty("evidence", out var evidence) || evidence.GetArrayLength() == 0)
            {
                failures.Add($"{section}/{id}: status is implemented/partial but has no 'evidence' entries");
                continue;
            }

            foreach (var entryElement in evidence.EnumerateArray())
            {
                var entry = entryElement.GetString();
                if (string.IsNullOrWhiteSpace(entry))
                {
                    failures.Add($"{section}/{id}: evidence array contains a blank entry");
                    continue;
                }

                var reason = ValidateEvidenceEntry(repoRoot, entry);
                if (reason != null)
                {
                    failures.Add($"{section}/{id}: evidence '{entry}' is invalid — {reason}");
                }
            }
        }
    }

    private static bool RowNeedsEvidence(JsonElement row) =>
        row.GetProperty("status").GetString() is "implemented" or "partial";

    private static bool FeatureRowNeedsEvidence(JsonElement row)
    {
        var classic = row.GetProperty("classicXrefStatus").GetString();
        var compressed = row.GetProperty("compressedXrefStatus").GetString();
        return classic is "implemented" or "partial" || compressed is "implemented" or "partial";
    }

    private static string? ValidateEvidenceEntry(string repoRoot, string entry)
    {
        var match = EvidenceMethodRegex.Match(entry);
        var path = match.Success ? match.Groups["path"].Value : entry;
        var member = match.Success ? match.Groups["member"].Value : null;

        var fullPath = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));

        if (Directory.Exists(fullPath))
        {
            return DirectoryHasRunnableTest(fullPath)
                ? null
                : "directory exists but contains no .cs file with a runnable [Fact]/[Theory] test";
        }

        if (!File.Exists(fullPath))
        {
            return "no such file or directory";
        }

        if (path.EndsWith(".sh", StringComparison.Ordinal))
        {
            return IsExecutable(fullPath) ? null : "script exists but is not executable";
        }

        if (path.EndsWith(".cs", StringComparison.Ordinal))
        {
            var text = File.ReadAllText(fullPath);

            if (member != null)
            {
                return MethodHasRunnableAttribute(text, member)
                    ? null
                    : $"method '{member}' was not found with its own runnable [Fact]/[Theory] attribute";
            }

            return HasRunnableAttribute(text)
                ? null
                : "file exists but has no runnable [Fact]/[Theory] test (only Skip-only or none found)";
        }

        return $"unsupported evidence type — expected a .cs file, a .sh script, or a directory, got '{path}'";
    }

    private static bool DirectoryHasRunnableTest(string directoryPath) =>
        Directory.EnumerateFiles(directoryPath, "*.cs", SearchOption.AllDirectories)
            .Any(file => HasRunnableAttribute(File.ReadAllText(file)));

    private static bool HasRunnableAttribute(string sourceText)
    {
        foreach (Match m in RunnableAttributeRegex.Matches(sourceText))
        {
            if (!m.Groups[2].Value.Contains("Skip", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MethodHasRunnableAttribute(string sourceText, string member)
    {
        var lines = sourceText.Replace("\r\n", "\n").Split('\n');
        var methodRegex = new Regex($@"\b{Regex.Escape(member)}\s*\(");

        for (var i = 0; i < lines.Length; i++)
        {
            if (!methodRegex.IsMatch(lines[i]))
            {
                continue;
            }

            var attributeLines = new List<string>();
            var j = i - 1;
            while (j >= 0)
            {
                var line = lines[j].Trim();
                if (line.Length == 0)
                {
                    j--;
                    continue;
                }

                if (line.StartsWith('['))
                {
                    attributeLines.Add(line);
                    j--;
                    continue;
                }

                break;
            }

            if (attributeLines.Any(IsRunnableAttributeLine))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRunnableAttributeLine(string line)
    {
        if (!Regex.IsMatch(line, @"\[(Fact|Theory|FixedAvaloniaFact|FixedAvaloniaTheory)\b"))
        {
            return false;
        }

        return !line.Contains("Skip", StringComparison.Ordinal);
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // No POSIX execute bit on Windows; existence is already verified above.
            return true;
        }

        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root (no excise.sln above test base directory).");
    }
}
