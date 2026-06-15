using AwesomeAssertions;
using Pdfe.Core.Parsing;
using Xunit;

namespace Pdfe.Cli.Tests;

public class CorpusScanClassificationTests
{
    [Fact]
    public void ClassifyCorpusFailure_OpenPhase_ReturnsParseError()
    {
        Program.ClassifyCorpusFailure(
                new InvalidDataException("bad xref"),
                Program.CorpusFailurePhase.Open)
            .Should().Be("PARSE_ERROR");
    }

    [Fact]
    public void ClassifyCorpusFailure_RenderDecodeFailure_ReturnsDecodeError()
    {
        Program.ClassifyCorpusFailure(
                new PdfParseException("Invalid hex digit in ASCIIHexDecode"),
                Program.CorpusFailurePhase.Render)
            .Should().Be("DECODE_ERROR");
    }

    [Fact]
    public void ClassifyCorpusFailure_RenderFilterFailure_ReturnsDecodeError()
    {
        Program.ClassifyCorpusFailure(
                new NotSupportedException("Unknown filter: BogusDecode"),
                Program.CorpusFailurePhase.Render)
            .Should().Be("DECODE_ERROR");
    }

    [Fact]
    public void ClassifyCorpusFailure_RenderNonDecodeFailure_ReturnsRenderError()
    {
        Program.ClassifyCorpusFailure(
                new InvalidOperationException("renderer state failed"),
                Program.CorpusFailurePhase.Render)
            .Should().Be("RENDER_ERROR");
    }
}
