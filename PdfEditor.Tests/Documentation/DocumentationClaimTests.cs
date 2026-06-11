using AwesomeAssertions;
using Xunit;

namespace PdfEditor.Tests.Documentation;

public class DocumentationClaimTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Theory]
    [InlineData("README.md", "page organization", "PdfEditor/Views/MainWindow.axaml", "Insert Pages _Before Current")]
    [InlineData("README.md", "move current or selected pages earlier/later", "PdfEditor/ViewModels/MainWindowViewModel.Commands.cs", "MoveCurrentPageLaterCommand")]
    [InlineData("README.md", "selected pages", "PdfEditor/ViewModels/MainWindowViewModel.Commands.cs", "ExtractSelectedPagesCommand")]
    [InlineData("README.md", "selected pages", "PdfEditor/ViewModels/MainWindowViewModel.Commands.cs", "MoveSelectedPagesLaterCommand")]
    [InlineData("README.md", "AddTextAnnotation", "Pdfe.Core/Document/PdfAnnotationAuthoring.cs", "AddTextAnnotation")]
    [InlineData("Pdfe.Core/README.md", "AddHighlightAnnotation", "Pdfe.Core/Document/PdfAnnotationAuthoring.cs", "AddHighlightAnnotation")]
    [InlineData("README.md", "OS trust-chain validation limitations", "PdfEditor/Services/SignatureVerificationSummaryFormatter.cs", "trust")]
    [InlineData("README.md", "PublicApiApprovalTests", "Pdfe.Core.Tests/Authoring/PublicApiApprovalTests.cs", "APPROVE_PUBLIC_API")]
    public void DocumentedFeatureClaims_MapToImplementation(
        string documentationPath,
        string documentationClaim,
        string implementationPath,
        string implementationToken)
    {
        Read(documentationPath).Should().Contain(documentationClaim);
        Read(implementationPath).Should().Contain(implementationToken);
    }

    [Fact]
    public void ReleaseChecklist_IncludesDocumentationAndValidationGates()
    {
        var checklist = Read("docs/RELEASE_CHECKLIST.md");

        checklist.Should().Contain("scripts/verify-doc-claims.sh");
        checklist.Should().Contain("dotnet build pdfe.sln --no-restore");
        checklist.Should().Contain("dotnet test pdfe.sln --no-build");
        checklist.Should().Contain("git diff --check");
    }

    private static string Read(string relativePath)
    {
        var fullPath = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).Should().BeTrue($"{relativePath} should exist");
        return File.ReadAllText(fullPath);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pdfe.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
