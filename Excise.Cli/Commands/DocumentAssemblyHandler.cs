using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Security;

namespace Excise.Cli.Commands;

internal static class DocumentAssemblyHandler
{
    internal static MergeDocumentsResult Merge(
        MergeDocumentsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.InputPaths.Count == 0)
            throw new ArgumentException("At least one input PDF is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var outputPath = Path.GetFullPath(request.OutputPath);
        var opened = new List<PdfDocument>();
        try
        {
            var sources = new List<(PdfDocument Document, IReadOnlyList<int> PageIndices)>();
            foreach (var sourcePath in request.InputPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = new FileInfo(sourcePath);
                if (!input.Exists)
                    throw new FileNotFoundException("A merge input PDF does not exist.", input.FullName);

                // If the output aliases a source, detach that source before the
                // final save so Windows does not retain a read handle over it.
                var document = PdfDocumentLifetime.OpenInputForOutput(input.FullName, outputPath);
                opened.Add(document);
                DocumentPermissionGuard.Require(
                    document,
                    DocumentAction.AssembleDocument,
                    $"merging pages from '{input.Name}'",
                    request.IgnorePermissions);
                sources.Add((document, Enumerable.Range(0, document.PageCount).ToArray()));
            }

            var outputEncryption = ResolveMergeOutputEncryption(opened);
            var droppedCatalogEntries = PdfDocumentMerger
                .CatalogEntriesNotConserved(opened[0])
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            using var merged = PdfDocumentMerger.Merge(sources);
            merged.Save(outputPath, outputEncryption.Options);
            return new MergeDocumentsResult(
                request.InputPaths.Select(Path.GetFullPath).ToArray(),
                outputPath,
                merged.PageCount,
                droppedCatalogEntries,
                outputEncryption.Policy);
        }
        finally
        {
            foreach (var document in opened)
                document.Dispose();
        }
    }

    internal static SplitDocumentResult Split(
        SplitDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputFolder);
        ValidateSplitPolicy(request);
        cancellationToken.ThrowIfCancellationRequested();

        var input = new FileInfo(request.InputPath);
        if (!input.Exists)
            throw new FileNotFoundException("The split input PDF does not exist.", input.FullName);
        var outputFolder = Path.GetFullPath(request.OutputFolder);

        using var document = PdfDocument.Open(input.FullName);
        DocumentPermissionGuard.Require(
            document,
            DocumentAction.AssembleDocument,
            "splitting this document",
            request.IgnorePermissions);
        var outputEncryption = GetOutputEncryption(document);
        var droppedCatalogEntries = PdfDocumentMerger
            .CatalogEntriesNotConserved(document)
            .ToArray();

        Directory.CreateDirectory(outputFolder);
        var fragments = CreateFragments(document, request);
        var baseName = Path.GetFileNameWithoutExtension(input.Name);
        var digits = fragments.Count.ToString().Length;
        var paths = new List<string>();
        try
        {
            for (var index = 0; index < fragments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var suffix = (index + 1).ToString().PadLeft(digits, '0');
                var path = Path.Combine(outputFolder, $"{baseName}_{suffix}.pdf");
                fragments[index].Save(path, outputEncryption.Options);
                paths.Add(path);
            }
        }
        finally
        {
            foreach (var fragment in fragments)
                fragment.Dispose();
        }

        return new SplitDocumentResult(
            input.FullName,
            outputFolder,
            paths,
            droppedCatalogEntries,
            outputEncryption.Policy);
    }

    private static IReadOnlyList<PdfDocument> CreateFragments(
        PdfDocument document,
        SplitDocumentRequest request)
        => request.Mode switch
        {
            SplitDocumentMode.Every => PdfDocumentSplitter.SplitEveryNPages(
                document,
                request.Every!.Value),
            SplitDocumentMode.Single => PdfDocumentSplitter.SplitToSinglePages(document),
            SplitDocumentMode.Bookmarks => PdfDocumentSplitter.SplitAtBookmarks(document),
            SplitDocumentMode.Boundaries => PdfDocumentSplitter.SplitAtPageBoundaries(
                document,
                request.Boundaries),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Mode)),
        };

    private static void ValidateSplitPolicy(SplitDocumentRequest request)
    {
        if (request.Mode == SplitDocumentMode.Every && request.Every is null or < 1)
            throw new ArgumentException("--every must be at least 1.");
        if (request.Mode == SplitDocumentMode.Boundaries && request.Boundaries.Count == 0)
            throw new ArgumentException("At least one split boundary is required.");
    }

    private static AssemblyOutputEncryption ResolveMergeOutputEncryption(
        IReadOnlyList<PdfDocument> sources)
    {
        var first = GetOutputEncryption(sources[0]);
        for (var index = 1; index < sources.Count; index++)
        {
            var next = GetOutputEncryption(sources[index]);
            if (first.Policy != next.Policy || !Equivalent(first.Options, next.Options))
            {
                throw new DocumentAssemblyEncryptionPolicyException(
                    "Cannot merge inputs with conflicting encryption policies. " +
                    "Use `excise decrypt` to make the output policy explicit before merging.");
            }
        }

        return first;
    }

    private static AssemblyOutputEncryption GetOutputEncryption(PdfDocument document)
    {
        var options = document.GetReEncryptionOptions(userPassword: null);
        return options is null
            ? new AssemblyOutputEncryption(DocumentAssemblyEncryptionPolicy.Unencrypted, null)
            : new AssemblyOutputEncryption(DocumentAssemblyEncryptionPolicy.Preserved, options);
    }

    private static bool Equivalent(PdfEncryptionOptions? left, PdfEncryptionOptions? right)
        => left is null || right is null
            ? left is null && right is null
            : left.Algorithm == right.Algorithm
              && left.Permissions == right.Permissions
              && left.EncryptMetadata == right.EncryptMetadata;
}

/// <summary>
/// Raised before an assembly save when merge inputs have no single safe output
/// encryption policy. Callers must explicitly decrypt or normalize the sources.
/// </summary>
internal sealed class DocumentAssemblyEncryptionPolicyException(string message)
    : InvalidOperationException(message);

internal enum DocumentAssemblyEncryptionPolicy
{
    Unencrypted,
    Preserved,
}

internal readonly record struct AssemblyOutputEncryption(
    DocumentAssemblyEncryptionPolicy Policy,
    PdfEncryptionOptions? Options);

internal readonly record struct MergeDocumentsRequest(
    IReadOnlyList<string> InputPaths,
    string OutputPath,
    bool IgnorePermissions);

internal sealed record MergeDocumentsResult(
    IReadOnlyList<string> InputPaths,
    string OutputPath,
    int PageCount,
    IReadOnlyList<string> DroppedCatalogEntries,
    DocumentAssemblyEncryptionPolicy OutputEncryptionPolicy);

internal enum SplitDocumentMode
{
    Every,
    Single,
    Bookmarks,
    Boundaries,
}

internal readonly record struct SplitDocumentRequest(
    string InputPath,
    string OutputFolder,
    SplitDocumentMode Mode,
    int? Every,
    IReadOnlyList<int> Boundaries,
    bool IgnorePermissions);

internal sealed record SplitDocumentResult(
    string InputPath,
    string OutputFolder,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> DroppedCatalogEntries,
    DocumentAssemblyEncryptionPolicy OutputEncryptionPolicy);
