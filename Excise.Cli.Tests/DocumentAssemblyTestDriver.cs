using Excise.Cli.Commands;

namespace Excise.Cli.Tests;

internal static class DocumentAssemblyTestDriver
{
    internal static int RunMerge(
        string[] inputPaths,
        string outputPath,
        bool ignorePermissions = false)
        => DocumentAssemblyHandler.Merge(new MergeDocumentsRequest(
            inputPaths,
            outputPath,
            ignorePermissions)).PageCount;

    internal static IReadOnlyList<string> RunSplitToSinglePages(
        string inputPath,
        string outputFolder,
        bool ignorePermissions = false)
        => DocumentAssemblyHandler.Split(new SplitDocumentRequest(
            inputPath,
            outputFolder,
            SplitDocumentMode.Single,
            Every: null,
            Boundaries: [],
            ignorePermissions)).WrittenPaths;
}
