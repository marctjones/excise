using AwesomeAssertions;
using Pdfe.Core.Automation;
using Xunit;

namespace Pdfe.Core.Tests.Automation;

public class PdfCommandRegistryTests
{
    [Fact]
    public void AllCommands_HaveStableUniqueIdsAndAccessibleMetadata()
    {
        var commands = PdfCommandRegistry.All;

        commands.Should().NotBeEmpty();
        commands.Select(command => command.Id)
            .Should().OnlyHaveUniqueItems("semantic command ids are external automation contracts");

        foreach (var command in commands)
        {
            command.Id.Should().MatchRegex("^[a-z][A-Za-z0-9]*(\\.[a-z][A-Za-z0-9]*)+$");
            command.Label.Should().NotBeNullOrWhiteSpace();
            command.Description.Should().NotBeNullOrWhiteSpace();
            command.Category.Should().NotBeNullOrWhiteSpace();
            command.DisabledReason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Registry_CoversCoreGuiCliAndSecurityCommands()
    {
        var required = new[]
        {
            PdfCommandIds.Open,
            PdfCommandIds.Save,
            PdfCommandIds.RenderPage,
            PdfCommandIds.ExtractText,
            PdfCommandIds.SearchOpen,
            PdfCommandIds.NextPage,
            PdfCommandIds.PreviousPage,
            PdfCommandIds.ToggleRedactionMode,
            PdfCommandIds.ApplyRedaction,
            PdfCommandIds.FillForm,
            PdfCommandIds.SaveFlattenedFormCopy,
            PdfCommandIds.VerifySignatures,
            PdfCommandIds.AuditHiddenText,
        };

        foreach (var id in required)
            PdfCommandRegistry.Get(id).Id.Should().Be(id);

        PdfCommandRegistry.Get(PdfCommandIds.ApplyRedaction).IsSecuritySensitive.Should().BeTrue();
        PdfCommandRegistry.Get(PdfCommandIds.ApplyRedaction).IsDestructive.Should().BeTrue();
        PdfCommandRegistry.Get(PdfCommandIds.FillForm).CliCommand.Should().Be("fill-form");
        PdfCommandRegistry.Get(PdfCommandIds.RenderPage).CliCommand.Should().Be("render");
    }

    [Fact]
    public void ForCliCommand_ReturnsSharedMetadata()
    {
        var render = PdfCommandRegistry.ForCliCommand("render");

        render.Should().ContainSingle();
        render[0].Id.Should().Be(PdfCommandIds.RenderPage);
        render[0].Parameters.Should().Contain(parameter => parameter.Name == "output" && parameter.Required);
    }
}
