using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Authoring API for document-level embedded files (PDF 2.0 §7.7). The read
/// side is <see cref="PdfEmbeddedFileParser"/> (via <c>document.GetEmbeddedFiles()</c>);
/// the removal side is <c>document.ScrubEmbeddedFiles()</c>; this is the write
/// side, added for #1413. Builds an <c>/EmbeddedFile</c> stream + <c>/Filespec</c>
/// dictionary and registers it in <c>/Catalog/Names/EmbeddedFiles</c>, mirroring
/// <see cref="PdfOutlineAuthoring"/>'s get-or-create-root shape.
/// </summary>
public static class PdfEmbeddedFileAuthoring
{
    /// <summary>
    /// Attach a new file to the document. Returns the new file specification's
    /// object reference. <paramref name="fileName"/> also becomes the entry's
    /// key in the /EmbeddedFiles name tree, so it must be unique within the
    /// tree -- attaching the same name twice creates two distinct entries with
    /// the same key, which conforming readers may resolve either-or.
    /// </summary>
    public static PdfReference AddEmbeddedFile(this PdfDocument document, string fileName, byte[] data,
        string? mimeType = null, string? description = null)
    {
        var fileDict = new PdfDictionary();
        fileDict.SetName("Type", "EmbeddedFile");
        if (mimeType != null)
            fileDict.SetName("Subtype", mimeType);
        fileDict.SetInt("Length", data.Length);

        var now = PdfDate(DateTimeOffset.UtcNow);
        var paramsDict = new PdfDictionary();
        paramsDict.SetInt("Size", data.Length);
        paramsDict.SetString("CreationDate", now);
        paramsDict.SetString("ModDate", now);
        fileDict["Params"] = paramsDict;

        var fileStreamRef = document.AddIndirectObject(new PdfStream(fileDict, data));

        var ef = new PdfDictionary();
        ef["F"] = fileStreamRef;
        ef["UF"] = fileStreamRef;

        var fileSpec = new PdfDictionary();
        fileSpec.SetName("Type", "Filespec");
        fileSpec.SetString("F", fileName);
        fileSpec.SetString("UF", fileName);
        if (description != null)
            fileSpec.SetString("Desc", description);
        fileSpec["EF"] = ef;

        var fileSpecRef = document.AddIndirectObject(fileSpec);

        var namesDict = GetOrCreateNamesDict(document);
        var (root, _) = GetOrCreateEmbeddedFilesRoot(document, namesDict);
        InsertSorted(document, root, fileName, fileSpecRef);

        document.InvalidateDerivedState(PdfDocumentDerivedStateScope.Attachments);

        return fileSpecRef;
    }

    private static PdfDictionary GetOrCreateNamesDict(PdfDocument document)
    {
        var existingObj = document.Catalog.GetOptional("Names");
        if (existingObj != null && document.Resolve(existingObj) is PdfDictionary existing)
            return existing;

        var namesDict = new PdfDictionary();
        document.Catalog["Names"] = namesDict;
        return namesDict;
    }

    private static (PdfDictionary Root, PdfReference RootRef) GetOrCreateEmbeddedFilesRoot(PdfDocument document, PdfDictionary namesDict)
    {
        var existingObj = namesDict.GetOptional("EmbeddedFiles");
        if (existingObj is PdfReference existingRef && document.Resolve(existingObj) is PdfDictionary existingDict)
            return (existingDict, existingRef);

        var root = new PdfDictionary();
        var rootRef = document.AddIndirectObject(root);
        namesDict["EmbeddedFiles"] = rootRef;
        return (root, rootRef);
    }

    /// <summary>
    /// Insert (name, filespec-ref) into a flat /Names leaf array, keeping
    /// ascending name order per §7.9.6. This codebase has no name-tree
    /// splitting logic anywhere (nothing needed it before #1413), so this
    /// stays a single leaf rather than balancing into /Kids subtrees --
    /// correct for any number of entries, just not what a large third-party
    /// PDF's own name tree would look like internally.
    /// </summary>
    private static void InsertSorted(PdfDocument document, PdfDictionary root, string name, PdfReference fileSpecRef)
    {
        var namesObj = root.GetOptional("Names");
        PdfArray names;
        if (namesObj != null && document.Resolve(namesObj) is PdfArray existing)
            names = existing;
        else
        {
            names = new PdfArray();
            root["Names"] = names;
        }

        int insertAt = names.Count;
        for (int i = 0; i < names.Count; i += 2)
        {
            if (names[i] is PdfString existingName &&
                string.CompareOrdinal(name, existingName.Value) < 0)
            {
                insertAt = i;
                break;
            }
        }
        names.Insert(insertAt, new PdfString(name));
        names.Insert(insertAt + 1, fileSpecRef);
    }

    private static string PdfDate(DateTimeOffset date) => $"D:{date.UtcDateTime:yyyyMMddHHmmss}+00'00'";
}
