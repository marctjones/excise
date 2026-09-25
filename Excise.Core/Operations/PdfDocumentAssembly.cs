using Excise.Core.Document;
using Excise.Core.Security;

namespace Excise.Core.Operations;

/// <summary>How <see cref="PdfDocumentAssembly.Split"/> groups pages into output files.</summary>
public enum SplitDocumentMode
{
    /// <summary>Chunks of <see cref="SplitDocumentSpecification.PagesPerChunk"/> pages; the last may be smaller.</summary>
    Every,

    /// <summary>One output file per page.</summary>
    Single,

    /// <summary>A new file at each root-level bookmark destination.</summary>
    Bookmarks,

    /// <summary>A new file at each 0-based page index in <see cref="SplitDocumentSpecification.Boundaries"/>.</summary>
    Boundaries,
}

public sealed record SplitDocumentSpecification(
    SplitDocumentMode Mode,
    int PagesPerChunk = 1,
    IReadOnlyList<int>? Boundaries = null);

public sealed record MergeDocumentsResult(
    string OutputPath,
    int PageCount,
    IReadOnlyList<string> DroppedCatalogEntries,
    bool EncryptionPreserved);

public sealed record SplitDocumentResult(
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> DroppedCatalogEntries,
    bool EncryptionPreserved);

/// <summary>
/// Merge and split to files: the one path the CLI and the app share (#1829). An encrypted source
/// saves encrypted (#1343), and every source passes the caller's /P page-assembly gate before
/// anything is written. <c>requireAssemble</c> receives the source and a phrase naming the
/// operation, and throws to refuse.
/// </summary>
public static class PdfDocumentAssembly
{
    public static MergeDocumentsResult Merge(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        Action<PdfDocument, string> requireAssemble,
        CancellationToken cancellationToken = default)
    {
        if (inputPaths.Count == 0)
            throw new ArgumentException("At least one input PDF is required.", nameof(inputPaths));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        outputPath = Path.GetFullPath(outputPath);
        var opened = new List<PdfDocument>();
        try
        {
            var sources = new List<(PdfDocument Document, IReadOnlyList<int> PageIndices)>();
            foreach (var inputPath in inputPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // If the output aliases a source, detach that source before the
                // final save so Windows does not retain a read handle over it.
                var document = PdfDocumentLifetime.OpenInputForOutput(inputPath, outputPath);
                opened.Add(document);
                requireAssemble(document, $"merging pages from '{Path.GetFileName(inputPath)}'");
                sources.Add((document, Enumerable.Range(0, document.PageCount).ToArray()));
            }

            var encryption = ResolveMergeOutputEncryption(opened);
            var droppedCatalogEntries = PdfDocumentMerger.CatalogEntriesNotConserved(opened[0]);
            cancellationToken.ThrowIfCancellationRequested();
            using var merged = PdfDocumentMerger.Merge(sources);
            merged.Save(outputPath, encryption);
            return new MergeDocumentsResult(outputPath, merged.PageCount, droppedCatalogEntries, encryption != null);
        }
        finally
        {
            foreach (var document in opened)
                document.Dispose();
        }
    }

    /// <param name="userPassword">The password <paramref name="source"/> was opened with; an encrypted
    /// source's fragments re-encrypt with it.</param>
    /// <param name="baseName">File-name stem: fragments are written as <c>{baseName}_{n}.pdf</c>.</param>
    public static SplitDocumentResult Split(
        PdfDocument source,
        string? userPassword,
        SplitDocumentSpecification specification,
        string outputFolder,
        string baseName,
        Action<PdfDocument, string> requireAssemble,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        cancellationToken.ThrowIfCancellationRequested();
        requireAssemble(source, "splitting this document");

        outputFolder = Path.GetFullPath(outputFolder);
        var encryption = source.GetReEncryptionOptions(userPassword);
        var droppedCatalogEntries = PdfDocumentMerger.CatalogEntriesNotConserved(source);
        var fragments = specification.Mode switch
        {
            SplitDocumentMode.Every => PdfDocumentSplitter.SplitEveryNPages(source, specification.PagesPerChunk),
            SplitDocumentMode.Single => PdfDocumentSplitter.SplitToSinglePages(source),
            SplitDocumentMode.Bookmarks => PdfDocumentSplitter.SplitAtBookmarks(source),
            SplitDocumentMode.Boundaries => PdfDocumentSplitter.SplitAtPageBoundaries(source, specification.Boundaries ?? []),
            _ => throw new ArgumentOutOfRangeException(nameof(specification)),
        };

        var digits = fragments.Count.ToString().Length;
        var paths = new List<string>();
        try
        {
            Directory.CreateDirectory(outputFolder);
            for (var index = 0; index < fragments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var suffix = (index + 1).ToString().PadLeft(digits, '0');
                var path = Path.Combine(outputFolder, $"{baseName}_{suffix}.pdf");
                fragments[index].Save(path, encryption);
                paths.Add(path);
            }
        }
        finally
        {
            foreach (var fragment in fragments)
                fragment.Dispose();
        }

        return new SplitDocumentResult(paths, droppedCatalogEntries, encryption != null);
    }

    private static PdfEncryptionOptions? ResolveMergeOutputEncryption(IReadOnlyList<PdfDocument> sources)
    {
        var first = sources[0].GetReEncryptionOptions(userPassword: null);
        foreach (var source in sources.Skip(1))
        {
            if (!Equivalent(first, source.GetReEncryptionOptions(userPassword: null)))
            {
                throw new InvalidOperationException(
                    "Cannot merge inputs with conflicting encryption policies. " +
                    "Use `excise decrypt` to make the output policy explicit before merging.");
            }
        }

        return first;
    }

    private static bool Equivalent(PdfEncryptionOptions? left, PdfEncryptionOptions? right)
        => left is null || right is null
            ? left is null && right is null
            : left.Algorithm == right.Algorithm
              && left.Permissions == right.Permissions
              && left.EncryptMetadata == right.EncryptMetadata;
}
