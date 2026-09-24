using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Automation;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// The undo gate (#1813, #1660). The undo stack is fed by hand: each mutating command has to call
/// _history.Push itself, so a command added without one is silently not undoable. Bates numbering, form
/// edits, page insertion and sticky-note edits all shipped that way. This walks PdfCommandRegistry and
/// requires every command to be classified in tests/undo-classification.tsv, so adding one forces the
/// question "does it change the document, and can it be undone?".
/// </summary>
public class UndoClassificationTests
{
    private static readonly HashSet<string> Classes = new(StringComparer.Ordinal)
    {
        "Recorded", "ClearsHistory", "WritesCopy", "OwnUndoSurface", "CliOnly", "NotMutating", "Gap",
    };

    private sealed record Row(string Id, string Class, string Evidence, int Line);

    private static List<Row> ReadClassification()
    {
        var path = TestRepoLayout.FindFile("tests/undo-classification.tsv");
        path.Should().NotBeNull("the classification file must exist: " +
            TestRepoLayout.AbsenceReason("undo classification", "tests/undo-classification.tsv"));

        var rows = new List<Row>();
        var lines = File.ReadAllLines(path!);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var parts = line.Split('\t');
            parts.Length.Should().Be(3, $"line {i + 1} must be id<TAB>class<TAB>evidence");
            rows.Add(new Row(parts[0], parts[1], parts[2], i + 1));
        }
        return rows;
    }

    private static IReadOnlyList<PdfCommandMetadata> Registry() => PdfCommandRegistry.All;

    [Fact]
    public void EveryRegistryCommand_IsClassified_ExactlyOnce()
    {
        var rows = ReadClassification();
        var ids = Registry().Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        rows.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty(
            "a command is classified once");
        ids.Except(rows.Select(r => r.Id)).Should().BeEmpty(
            "every command must be classified in tests/undo-classification.tsv: does it change the open " +
            "document, and can it be undone? (#1813)");
        rows.Select(r => r.Id).Except(ids).Should().BeEmpty(
            "a row for a command that no longer exists is stale");
    }

    [Fact]
    public void EveryClass_IsKnown()
    {
        ReadClassification().Where(r => !Classes.Contains(r.Class)).Select(r => $"{r.Id}: {r.Class}")
            .Should().BeEmpty($"the classes are {string.Join(", ", Classes)}");
    }

    [Fact]
    public void RecordedAndClearsHistory_NameATestThatExercisesUndo()
    {
        var problems = new List<string>();
        foreach (var row in ReadClassification().Where(r => r.Class is "Recorded" or "ClearsHistory"))
        {
            var file = TestRepoLayout.FindFile($"Excise.App.Tests/{row.Evidence}");
            if (file == null)
            {
                problems.Add($"{row.Id}: evidence file Excise.App.Tests/{row.Evidence} does not exist");
                continue;
            }

            var text = File.ReadAllText(file);
            var exercisesUndo = text.Contains("UndoCommand", StringComparison.Ordinal)
                || text.Contains("CanUndo", StringComparison.Ordinal);
            if (!exercisesUndo)
                problems.Add($"{row.Id}: {row.Evidence} never exercises UndoCommand or CanUndo");
        }
        problems.Should().BeEmpty("evidence must be a real test file that touches undo, not a claim");
    }

    [Fact]
    public void AGapMustNameAnOpenIssue()
    {
        ReadClassification().Where(r => r.Class == "Gap" && !System.Text.RegularExpressions.Regex.IsMatch(r.Evidence, @"^#\d+$"))
            .Select(r => r.Id).Should().BeEmpty("a known gap cites the issue that will close it");
    }

    [Fact]
    public void ADestructiveCommand_IsNeverClassifiedNotMutating()
    {
        var byId = ReadClassification().ToDictionary(r => r.Id, StringComparer.Ordinal);
        Registry().Where(c => c.IsDestructive && byId.TryGetValue(c.Id, out var r) && r.Class == "NotMutating")
            .Select(c => c.Id).Should().BeEmpty("a command the registry marks destructive changes something");
    }
}
