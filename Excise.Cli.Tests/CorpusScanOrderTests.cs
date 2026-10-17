using System.IO;
using System.Linq;
using AwesomeAssertions;
using Xunit;
using RenderProgram = Excise.RenderTools.Program;

namespace Excise.Cli.Tests;

/// <summary>
/// The corpus scans start their largest documents first (the 2026-10-17 full tier ended with one 160-page book
/// working alone for five minutes because it sorted last). The order only changes when each document is
/// scanned, not what is reported, so these tests pin the order itself: size descending, path ascending on a tie,
/// the same every run.
/// </summary>
public class CorpusScanOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-scanorder-{Guid.NewGuid():N}");

    public CorpusScanOrderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private RenderProgram.CorpusPdf File(string name, int bytes)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return new RenderProgram.CorpusPdf(path, name);
    }

    [Fact]
    public void LargestFileFirst_ThenByPath()
    {
        var items = new[] { File("a-small.pdf", 10), File("z-huge.pdf", 5000), File("m-mid.pdf", 300), File("b-mid.pdf", 300) };

        var ordered = RenderProgram.OrderLargestFirst(items).Select(i => i.RelativePath).ToArray();

        ordered.Should().Equal("z-huge.pdf", "b-mid.pdf", "m-mid.pdf", "a-small.pdf");
    }

    [Fact]
    public void AMissingFile_SortsLast_AndDoesNotThrow()
    {
        var present = File("real.pdf", 100);
        var missing = new RenderProgram.CorpusPdf(Path.Combine(_dir, "gone.pdf"), "gone.pdf");

        var ordered = RenderProgram.OrderLargestFirst(new[] { missing, present }).Select(i => i.RelativePath).ToArray();

        ordered.Should().Equal("real.pdf", "gone.pdf");
    }

    [Fact]
    public void TheOrderIsTheSameWhateverOrderTheItemsArriveIn()
    {
        var items = new[] { File("c.pdf", 40), File("a.pdf", 40), File("b.pdf", 90) };

        var forward = RenderProgram.OrderLargestFirst(items).Select(i => i.RelativePath).ToArray();
        var reversed = RenderProgram.OrderLargestFirst(items.Reverse().ToArray()).Select(i => i.RelativePath).ToArray();

        forward.Should().Equal(reversed);
    }
}
