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
    [InlineData("README.md", "highlight selected text", "PdfEditor/ViewModels/MainWindowViewModel.Commands.cs", "AddHighlightAnnotationFromSelectionCommand")]
    [InlineData("README.md", "sticky notes", "PdfEditor/ViewModels/MainWindowViewModel.Commands.cs", "AddStickyNoteAnnotationCommand")]
    [InlineData("README.md", "Annotation review tools", "PdfEditor/Views/MainWindow.axaml", "Add _Highlight From Selection")]
    [InlineData("README.md", "Safe-to-share save path", "PdfEditor/Services/RedactedCopySafetyService.cs", "ScrubMetadata(scrubAttachments: options.ScrubAttachments)")]
    [InlineData("README.md", "without repeating removed text", "PdfEditor/Services/RedactedCopySafetyService.cs", "Removed text is not repeated")]
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

    [Fact]
    public void PackagedGuiSmokeDocs_MapToReleaseSmokeScript()
    {
        var checklist = Read("docs/RELEASE_CHECKLIST.md");
        var packagedSmokeDocs = Read("docs/PACKAGED_GUI_SMOKE.md");
        var releaseSmokeScript = Read("scripts/release-smoke.sh");
        var packagedSmokeScript = Read("scripts/run-packaged-gui-smoke.sh");

        checklist.Should().Contain("--packaged-gui");
        checklist.Should().Contain("--packaged-gui-focus-input");
        checklist.Should().Contain("--packaged-gui-direct-exec");
        packagedSmokeDocs.Should().Contain("#558");
        packagedSmokeDocs.Should().Contain("#571");
        packagedSmokeDocs.Should().Contain("#577");
        packagedSmokeDocs.Should().Contain("app-responsiveness.json");
        packagedSmokeDocs.Should().Contain("process remains alive");
        packagedSmokeDocs.Should().Contain("Timing Budgets");
        packagedSmokeDocs.Should().Contain("Accessibility permission");

        releaseSmokeScript.Should().Contain("run_packaged_gui_gate");
        releaseSmokeScript.Should().Contain("--packaged-gui");
        releaseSmokeScript.Should().Contain("--packaged-gui-focus-input");
        releaseSmokeScript.Should().Contain("--packaged-gui-direct-exec");

        packagedSmokeScript.Should().Contain("packaged-gui-smoke.json");
        packagedSmokeScript.Should().Contain("APP_RESPONSIVENESS_REPORT");
        packagedSmokeScript.Should().Contain("PDFE_RESPONSIVENESS_REPORT");
        packagedSmokeScript.Should().Contain("packaged app stayed alive");
        packagedSmokeScript.Should().Contain("native page navigation latency");
        packagedSmokeScript.Should().Contain("native zoom latency");
        packagedSmokeScript.Should().Contain("native redaction preview latency");
        packagedSmokeScript.Should().Contain("--allow-focus-input");
        packagedSmokeScript.Should().Contain("MANUAL_REQUIRED");
    }

    [Fact]
    public void BenchmarkScript_UsesExistingRenderToolsHotspotCommands()
    {
        var script = Read("scripts/run-benchmarks.sh");

        script.Should().NotContain("Pdfe.Benchmarks/Pdfe.Benchmarks.csproj");
        script.Should().Contain("tools/Pdfe.RenderTools/Pdfe.RenderTools.csproj");
        script.Should().Contain("corpus-hotspots");
        script.Should().Contain("gui-display-hotspots");
    }

    [Fact]
    public void FileAssociationDocs_MapToPackagingAndStartupHandlers()
    {
        var readme = Read("README.md");
        var macosBundleScript = Read("scripts/build-macos-app.sh");
        var windowsInstaller = Read("packaging/windows/pdfe.iss");
        var appStartup = Read("PdfEditor/App.axaml.cs");

        readme.Should().Contain("Using pdfe as a PDF reader on macOS");
        readme.Should().Contain("Using pdfe as a PDF reader on Windows");
        readme.Should().Contain("Associate pdfe with .pdf files");
        readme.Should().Contain("Default apps");

        macosBundleScript.Should().Contain("CFBundleDocumentTypes");
        macosBundleScript.Should().Contain("LSItemContentTypes");
        macosBundleScript.Should().Contain("com.adobe.pdf");

        windowsInstaller.Should().Contain("ChangesAssociations=yes");
        windowsInstaller.Should().Contain("Software\\RegisteredApplications");
        windowsInstaller.Should().Contain("Capabilities\\FileAssociations");
        windowsInstaller.Should().Contain("\"\"\"{app}\\{#MyAppExeName}\"\" \"\"%1\"\"\"");

        appStartup.Should().Contain("desktop.Args");
        appStartup.Should().Contain("FileActivatedEventArgs");
        appStartup.Should().Contain("LoadDocumentAsync(path)");
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
