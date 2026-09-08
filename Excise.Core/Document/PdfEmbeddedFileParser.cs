using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Parser for document-level embedded files (PDF 2.0 §7.7).
/// Walks the /Catalog/Names/EmbeddedFiles name tree (or legacy /Catalog/Names/AF array
/// and /Catalog/AF) to extract file specifications for portfolio and associated files.
/// </summary>
internal static class PdfEmbeddedFileParser
{
    /// <summary>
    /// Parse all embedded files from the document catalog.
    /// Checks /Catalog/Names/EmbeddedFiles (PDF 2.0 name tree) first,
    /// then falls back to legacy /Catalog/Names/AF and /Catalog/AF arrays.
    /// Returns empty list if no embedded files are found.
    /// </summary>
    public static IReadOnlyList<PdfEmbeddedFile> ParseEmbeddedFiles(PdfDocument doc)
    {
        var result = new List<PdfEmbeddedFile>();

        // Try modern PDF 2.0: /Catalog/Names/EmbeddedFiles name tree
        var namesObj = doc.Catalog.GetOptional("Names");
        var foundCatalogLevel = false;
        if (namesObj != null && doc.Resolve(namesObj) is PdfDictionary namesDictRoot)
        {
            var embeddedFilesObj = namesDictRoot.GetOptional("EmbeddedFiles");
            if (embeddedFilesObj != null && doc.Resolve(embeddedFilesObj) is PdfDictionary embeddedFilesRoot)
            {
                WalkNameTree(doc, embeddedFilesRoot, result);
                foundCatalogLevel = result.Count > 0;
            }
        }

        // Fall back to legacy PDF 1.7: /Catalog/Names/AF array or /Catalog/AF array
        // These are less common but still valid per PDF 2.0 §7.7.4. Only tried when
        // the modern tree found nothing -- an /AF array conventionally just POINTS
        // BACK at entries already reachable from /Names/EmbeddedFiles (§14.13), so
        // walking it too would double-count them.
        if (!foundCatalogLevel)
        {
            var afObj = namesObj != null && doc.Resolve(namesObj) is PdfDictionary namesDict
                            ? namesDict.GetOptional("AF")
                            : null;
            afObj ??= doc.Catalog.GetOptional("AF");

            if (afObj != null && doc.Resolve(afObj) is PdfArray afArray)
            {
                foreach (var fsObj in afArray)
                {
                    if (doc.Resolve(fsObj) is PdfDictionary fsDict)
                    {
                        // For legacy arrays, we don't have explicit names, so use a generated name.
                        var name = $"_AF_{result.Count}";
                        var file = ParseFileSpecification(doc, fsDict, name);
                        if (file != null)
                            result.Add(file);
                    }
                }
            }
        }

        // A /FileAttachment annotation carries its OWN /FS directly (§12.5.6.15) —
        // it is never registered in /Catalog/Names/EmbeddedFiles or /AF, so neither
        // walk above can see it, and (unlike that pair) it is NOT an either/or with
        // catalog-level attachments -- a document can have both, so this always
        // runs, regardless of what was found above. Before this, GetEmbeddedFiles()
        // (and therefore ScrubEmbeddedFiles, HasEmbeddedFiles, and the attachments
        // panel) had no visibility into an annotation-only attachment at all -- its
        // description, filename, and payload bytes all survived redaction undetected.
        WalkAnnotationFileAttachments(doc, result);

        return result;
    }

    private static void WalkAnnotationFileAttachments(PdfDocument doc, List<PdfEmbeddedFile> result)
    {
        for (var pageIndex = 1; pageIndex <= doc.PageCount; pageIndex++)
        {
            var page = doc.GetPage(pageIndex);
            if (doc.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
                continue;

            foreach (var annotObj in annots)
            {
                if (doc.Resolve(annotObj) is not PdfDictionary annot)
                    continue;
                if (annot.GetNameOrNull("Subtype") != "FileAttachment")
                    continue;
                if (doc.Resolve(annot.GetOptional("FS") ?? PdfNull.Instance) is not PdfDictionary fsDict)
                    continue;

                var name = $"_Annotation_{pageIndex}_{result.Count}";
                var file = ParseFileSpecification(doc, fsDict, name);
                if (file != null)
                    result.Add(file);
            }
        }
    }

    /// <summary>
    /// Walk a PDF name tree recursively (§7.9.6).
    /// Leaves have /Names array: [name value name value ...]
    /// Branches have /Kids array pointing to subtrees.
    /// </summary>
    private static void WalkNameTree(
        PdfDocument doc,
        PdfDictionary node,
        List<PdfEmbeddedFile> result)
    {
        // Leaf: /Names array
        var namesObj = node.GetOptional("Names");
        if (namesObj != null && doc.Resolve(namesObj) is PdfArray namesArr)
        {
            for (int i = 0; i + 1 < namesArr.Count; i += 2)
            {
                // First element is the key (file name as string)
                var nameObj = namesArr[i];
                string? name = nameObj switch
                {
                    PdfString s => s.Value,
                    _ => null
                };
                if (name == null) continue;

                // Second element is the value (file specification dictionary)
                if (doc.Resolve(namesArr[i + 1]) is PdfDictionary fsDict)
                {
                    var file = ParseFileSpecification(doc, fsDict, name);
                    if (file != null)
                        result.Add(file);
                }
            }
        }

        // Branch: /Kids array of subtrees
        var kidsObj = node.GetOptional("Kids");
        if (kidsObj != null && doc.Resolve(kidsObj) is PdfArray kidsArr)
        {
            foreach (var kidObj in kidsArr)
            {
                var kidDict = doc.Resolve(kidObj) as PdfDictionary;
                if (kidDict != null)
                    WalkNameTree(doc, kidDict, result);
            }
        }
    }

    /// <summary>
    /// Parse a single file specification dictionary (§7.11.2 / §7.7).
    /// The FS dict may contain /UF (Unicode filename), /F (legacy filename), /EF (embedded file stream),
    /// /Desc (description), and other metadata.
    /// Returns null if the file specification cannot be parsed.
    /// </summary>
    private static PdfEmbeddedFile? ParseFileSpecification(
        PdfDocument doc,
        PdfDictionary fsDict,
        string name)
    {
        // /UF preferred (Unicode), /F is legacy 7-bit/PDFDocEncoded.
        var fileName = fsDict.GetStringOrNull("UF") ?? fsDict.GetStringOrNull("F");

        // /Desc (description)
        var description = fsDict.GetStringOrNull("Desc");

        // Try to extract the embedded file stream from /EF
        byte[]? bytes = null;
        string? mimeType = null;
        DateTimeOffset? creationDate = null;
        DateTimeOffset? modDate = null;

        if (fsDict.GetOptional("EF") is { } efObj && doc.Resolve(efObj) is PdfDictionary ef)
        {
            // /EF /F (legacy) or /UF (PDF 2.0) → embedded-file stream
            var fileStreamObj = ef.GetOptional("F") ?? ef.GetOptional("UF");
            if (fileStreamObj != null && doc.Resolve(fileStreamObj) is PdfStream stream)
            {
                try { bytes = stream.DecodedData; }
                catch (Exception __ex) when (__ex is not OutOfMemoryException) { bytes = null; }

                mimeType = stream.GetNameOrNull("Subtype");

                // Extract creation/mod dates from /Params
                if (stream.GetOptional("Params") is { } paramsObj && doc.Resolve(paramsObj) is PdfDictionary paramsDict)
                {
                    creationDate = ParseDate(paramsDict.GetStringOrNull("CreationDate"));
                    modDate = ParseDate(paramsDict.GetStringOrNull("ModDate"));
                }
            }
        }

        return new PdfEmbeddedFile(
            name: name,
            fileName: fileName,
            description: description,
            bytes: bytes,
            mimeType: mimeType,
            creationDate: creationDate,
            modDate: modDate,
            rawDictionary: fsDict);
    }

    /// <summary>
    /// Parse a PDF date string (D:YYYYMMDDHHmmSSOHH'mm' format) into a DateTimeOffset.
    /// Returns null if the string is null or cannot be parsed.
    /// </summary>
    private static DateTimeOffset? ParseDate(string? dateStr)
    {
        if (dateStr == null || dateStr.Length < 4)
            return null;

        // Remove leading 'D:' if present
        if (dateStr.StartsWith("D:"))
            dateStr = dateStr.Substring(2);

        // Try to parse YYYYMMDDHHMMSS format
        if (dateStr.Length < 14)
            return null;

        try
        {
            int year = int.Parse(dateStr.Substring(0, 4));
            int month = int.Parse(dateStr.Substring(4, 2));
            int day = int.Parse(dateStr.Substring(6, 2));
            int hour = int.Parse(dateStr.Substring(8, 2));
            int minute = int.Parse(dateStr.Substring(10, 2));
            int second = int.Parse(dateStr.Substring(12, 2));

            // Parse timezone if present (±HH'mm' format after position 14)
            TimeSpan offset = TimeSpan.Zero;
            if (dateStr.Length > 14)
            {
                char offsetSign = dateStr[14];
                if (offsetSign == '+' || offsetSign == '-')
                {
                    if (dateStr.Length >= 21) // ±HH'mm'
                    {
                        int offsetHour = int.Parse(dateStr.Substring(15, 2));
                        int offsetMinute = int.Parse(dateStr.Substring(18, 2));
                        offset = new TimeSpan(offsetHour, offsetMinute, 0);
                        if (offsetSign == '-')
                            offset = offset.Negate();
                    }
                }
            }

            var dt = new DateTime(year, month, day, hour, minute, second);
            return new DateTimeOffset(dt, offset);
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
