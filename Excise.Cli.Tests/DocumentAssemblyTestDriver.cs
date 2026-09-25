using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Security;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary><c>excise merge</c> and <c>excise split --single</c> as the commands run them, typed.</summary>
internal static class DocumentAssemblyTestDriver
{
    internal static MergeDocumentsResult RunMerge(
        string[] inputPaths,
        string outputPath,
        bool ignorePermissions = false)
        => PdfDocumentAssembly.Merge(
            inputPaths, outputPath, Gate(ignorePermissions), TestContext.Current.CancellationToken);

    internal static SplitDocumentResult RunSplitToSinglePages(
        string inputPath,
        string outputFolder,
        bool ignorePermissions = false)
    {
        using var document = PdfDocument.Open(inputPath);
        return PdfDocumentAssembly.Split(
            document,
            userPassword: null,
            new SplitDocumentSpecification(SplitDocumentMode.Single),
            outputFolder,
            Path.GetFileNameWithoutExtension(inputPath),
            Gate(ignorePermissions),
            TestContext.Current.CancellationToken);
    }

    internal static Action<PdfDocument, string> Gate(bool ignorePermissions)
        => (document, operation) => DocumentPermissionGuard.Require(
            document, DocumentAction.AssembleDocument, operation, ignorePermissions);
}
