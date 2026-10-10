using AwesomeAssertions;
using Excise.Core.Redaction;
using Xunit;

namespace Excise.Core.Tests.Redaction;

/// <summary>#2057: the human reading of a note list condenses layout notes and nothing else.</summary>
public class RedactionNoteSummaryTests
{
    private const string Survivor = "NOT REMOVED (hyphen-wrapped): page 3 reads sec- ret -- still readable";
    private const string Closing = "This redaction was NOT clean -- see the notes above. Review the output.";

    private static bool IsWidth(string note) => note.StartsWith("WIDTH NOT CLOSED", StringComparison.Ordinal);

    private static List<string> Fixture(int widthNotes = 95, int survivors = 16)
    {
        var notes = new List<string>();
        for (var i = 0; i < widthNotes; i++)
        {
            notes.Add(i % 2 == 0
                ? $"WIDTH NOT CLOSED: page {2 + i % 7}, line at y={400 + i}.0: justified line kept in place beside its marker; its edge moved by {1 + i % 10}.4 pt, the removed width less the marker's."
                : $"WIDTH NOT CLOSED: page {2 + i % 7}, line at y={400 + i}.0: justified, but none of its word spaces is set by an operator of its own, so it was not re-justified and ends 0.{i % 9 + 1} pt short of its right margin.");
            if (i % 6 == 0 && survivors > 0)
            {
                survivors--;
                notes.Add($"{Survivor} {i}");
            }
        }

        for (; survivors > 0; survivors--) notes.Add($"{Survivor} tail");
        notes.Add("NOT SCRUBBED: Carrier outline -- refused");
        notes.Add("ATTACHMENT REMOVED: a.txt");
        notes.Add(Closing);
        return notes;
    }

    [Fact]
    public void Condense_KeepsEverySurvivorInOrder_AndPutsOneSummaryBeforeTheVerdict()
    {
        var notes = Fixture();
        var before = notes.ToList();

        var shown = RedactionNoteSummary.Condense(notes);

        notes.Should().Equal(before, "the structured list is never changed");
        shown.Count(IsWidth).Should().Be(1);
        shown.Where(n => !IsWidth(n)).Should().Equal(notes.Where(n => !IsWidth(n)),
            "every non-width note survives, unmodified, in its original order");
        var summary = shown.Single(IsWidth);
        var summaryAt = shown.ToList().IndexOf(summary);
        shown.Select((n, i) => (n, i)).Where(t => t.n.StartsWith("NOT REMOVED", StringComparison.Ordinal))
            .Should().HaveCount(16).And.OnlyContain(t => t.i < summaryAt, "survivor lines come before the roll-up");
        shown[^1].Should().Be(Closing);
        shown[^2].Should().Be(summary);
        summary.Should().Contain("95 lines on 7 pages").And.Contain("up to 9.4 pt");
    }

    [Fact]
    public void Condense_Expanded_ReturnsTheListUnchanged()
    {
        var notes = Fixture();
        RedactionNoteSummary.Condense(notes, expandWidthNotes: true).Should().Equal(notes);
    }

    [Fact]
    public void Condense_WithoutWidthNotes_ReturnsTheListUnchanged()
    {
        var notes = new List<string> { Survivor, Closing };
        RedactionNoteSummary.Condense(notes).Should().Equal(notes);
    }

    [Fact]
    public void Condense_AWidthNoteThatStatesAShiftInTheFile_IsNeverRolledUp()
    {
        const string tj = "WIDTH NOT CLOSED: page 1, line at y=10.0: a run with no positioning operator of its own was moved by a TJ number, which states the shift in the file.";
        var notes = new List<string>
        {
            tj,
            "WIDTH NOT CLOSED: page 1, line at y=20.0: justified line kept in place beside its marker; its edge moved by 2.0 pt, the removed width less the marker's.",
        };

        var shown = RedactionNoteSummary.Condense(notes);

        shown.Should().Contain(tj);
        shown.Should().HaveCount(2);
    }

    [Fact]
    public void Condense_DetailHintIsAppended()
    {
        RedactionNoteSummary.Condense(Fixture(2, 0), false, "Run with --verbose.")
            .Single(IsWidth).Should().EndWith("Run with --verbose.");
    }
}
