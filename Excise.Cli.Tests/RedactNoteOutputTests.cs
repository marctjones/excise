using System.IO;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>#2057: the redact printer lists survivors first and rolls the per-line width notes up.</summary>
public class RedactNoteOutputTests
{
    private static List<string> Notes()
    {
        var notes = new List<string>();
        for (var i = 0; i < 95; i++)
            notes.Add($"WIDTH NOT CLOSED: page {1 + i % 4}, line at y={i}.0: justified line kept in place beside its marker; its edge moved by 9.4 pt, the removed width less the marker's.");
        for (var i = 0; i < 16; i++)
            notes.Add($"NOT REMOVED (hyphen-wrapped): page {i} reads sec- ret -- still readable.");
        notes.Add("ATTACHMENT REMOVED: a.txt");
        notes.Add("This redaction was NOT clean -- see the notes above.");
        return notes;
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split(writer.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Default_PrintsEverySurvivorBeforeOneRollUpLine()
    {
        var notes = Notes();
        var writer = new StringWriter();

        RedactCommand.PrintNotes(notes, verbose: false, writer);

        var lines = Lines(writer);
        lines.Count(l => l.Contains("NOT REMOVED (hyphen-wrapped)")).Should().Be(16);
        lines.Count(l => l.Contains("WIDTH NOT CLOSED")).Should().Be(1);
        lines.Should().HaveCount(16 + 1 + 1 + 1);
        Array.FindLastIndex(lines, l => l.Contains("NOT REMOVED")).Should()
            .BeLessThan(Array.FindIndex(lines, l => l.Contains("WIDTH NOT CLOSED")));
        lines[^1].Should().Contain("This redaction was NOT clean");
        lines.Single(l => l.Contains("WIDTH NOT CLOSED")).Should().Contain("95 lines on 4 pages").And.Contain("--verbose");
        notes.Should().HaveCount(95 + 16 + 2, "the structured notes are untouched");
    }

    [Fact]
    public void Verbose_PrintsEveryLineAsBefore()
    {
        var notes = Notes();
        var writer = new StringWriter();

        RedactCommand.PrintNotes(notes, verbose: true, writer);

        Lines(writer).Should().Equal(notes.Select(n => $"  note: {n}"));
    }

    [Fact]
    public void VerboseOption_IsInTheHelp()
    {
        RedactCommand.Create().Options.Should().Contain(o => o.Name == "--verbose" &&
            (o.Description ?? "").Contains("WIDTH NOT CLOSED"));
    }
}
