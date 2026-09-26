using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace Excise.App.Tests.PublicApi;

/// <summary>
/// <c>Excise.App</c> is an executable, so it exports nothing (#1836). The gate is the
/// INVERSE of a snapshot, like <c>Excise.Cli.Tests/PublicApi</c>: tests reach the
/// internals through <c>InternalsVisibleTo</c>, and there is no approved baseline to
/// maintain.
/// </summary>
public class PublicApiApprovalTests
{
    [Fact]
    public void ExciseApp_ExportsNoPublicTypes()
    {
        // The Avalonia XAML compiler emits CompiledAvaloniaXaml.!XamlLoader and
        // !AvaloniaResources public and has no visibility switch; they are not our types.
        var publics = typeof(Excise.App.Services.PdfDocumentService).Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace != "CompiledAvaloniaXaml")
            .Select(t => t.FullName);
        publics.Should().BeEmpty(
            "Excise.App is internal-only by design: public surface on an executable is API " +
            "nobody can consume, and a public type would need an approved baseline again");
    }
}
