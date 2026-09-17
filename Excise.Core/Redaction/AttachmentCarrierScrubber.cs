using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Finds and removes every embedded file a PDF can carry (#1572), and — when a
/// redaction keeps attachments — redacts or reports the ones it keeps.
/// </summary>
/// <remarks>
/// <para><b>Why one walker.</b> Embedded files reach a reader by more routes
/// than the catalog name tree: the catalog's and each page's <c>/AF</c>
/// arrays (§14.13), a <c>/FileAttachment</c> annotation's own <c>/FS</c>
/// (§12.5.6.15), a RichMedia annotation's assets, a Screen or Movie
/// annotation's media clip, a Sound annotation's sound stream, and any other
/// file specification with an <c>/EF</c> entry (a <c>/GoToE</c> or
/// <c>/Launch</c> action, a form XObject's or structure element's
/// <c>/AF</c>). <c>ScrubEmbeddedFiles</c> used to remove only the first two
/// catalog entries and reported "attachments scrubbed" over a file that still
/// carried the rest.</para>
/// <para><b>Detach AND strip.</b> The writer saves only objects reachable from
/// the catalog, so detaching an attachment drops it — unless something else
/// still points at it (a structure element's <c>/OBJR</c> keeps an annotation
/// reachable after it leaves <c>/Annots</c>). Every file specification found
/// therefore also loses its <c>/EF</c>, <c>/RF</c> and <c>/Desc</c> in place,
/// and a final walk of the reachable graph strips any <c>/EF</c> the named
/// routes did not reach.</para>
/// </remarks>
internal static class AttachmentCarrierScrubber
{
    /// <summary>How deep nested PDF attachments are redacted before excise refuses.</summary>
    internal const int MaxNestedPdfDepth = 3;

    /// <summary>The shortest term cut from a kept attachment (the carrier-scrub floor).</summary>
    private const int MinTermLength = 3;

    private static readonly string[] PayloadKeys = { "UF", "F", "DOS", "Mac", "Unix" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".xml", ".html", ".htm", ".json", ".md",
    };

    // Keys a local walk from an annotation must not follow: they lead back to
    // the page, the page tree or other annotations, i.e. the whole document.
    private static readonly HashSet<string> LocalWalkStopKeys = new(StringComparer.Ordinal)
    {
        "P", "Parent", "Popup", "IRT", "Dest", "Next", "Prev", "First", "Last",
    };

    /// <summary>One embedded file and how it is attached.</summary>
    internal sealed class Found
    {
        internal Found(string name, string location, PdfDictionary? fileSpec, PdfStream? payload)
        {
            Name = name;
            Location = location;
            FileSpec = fileSpec;
            Payload = payload;
        }

        internal string Name { get; }
        internal string Location { get; }
        internal PdfDictionary? FileSpec { get; }
        internal PdfStream? Payload { get; }

        /// <summary>The dictionary to strip <see cref="OwnerKey"/> from (a Sound or RichMedia annotation).</summary>
        internal PdfDictionary? Owner { get; init; }
        internal string? OwnerKey { get; init; }

        /// <summary>The key in the document's <c>/EmbeddedFiles</c> name tree, when listed there (#1582).</summary>
        internal string? NameTreeKey { get; init; }
    }

    // ───────────────────────── enumeration ─────────────────────────

    /// <summary>Every embedded file in <paramref name="document"/>, once each.</summary>
    internal static List<Found> Enumerate(PdfDocument document)
    {
        var found = new List<Found>();
        var specs = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var streams = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);

        void AddSpec(PdfDictionary fs, string? key, string location)
        {
            if (!specs.Add(fs)) return;
            var payload = PayloadOf(document, fs);
            if (payload != null && !streams.Add(payload)) return;
            found.Add(new Found(NameOf(document, fs, key), location, fs, payload) { NameTreeKey = key });
        }

        void AddSpecArray(PdfObject? value, string location)
        {
            if (document.Resolve(value ?? PdfNull.Instance) is not PdfArray array) return;
            foreach (var item in array)
                if (document.Resolve(item) is PdfDictionary fs)
                    AddSpec(fs, null, location);
        }

        var names = ResolveDict(document, document.Catalog.GetOptional("Names"));
        if (ResolveDict(document, names?.GetOptional("EmbeddedFiles")) is { } tree)
            WalkNameTree(document, tree, (key, value) =>
            {
                if (document.Resolve(value) is PdfDictionary fs) AddSpec(fs, key, "document");
            }, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance));
        AddSpecArray(names?.GetOptional("AF"), "document (associated file)");
        AddSpecArray(document.Catalog.GetOptional("AF"), "document (associated file)");

        for (var pageNumber = 1; pageNumber <= document.PageCount; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            AddSpecArray(page.Dictionary.GetOptional("AF"), $"page {pageNumber} (associated file)");

            if (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
                continue;
            foreach (var annotObj in annots)
            {
                if (document.Resolve(annotObj) is not PdfDictionary annot) continue;
                var subtype = annot.GetNameOrNull("Subtype") ?? "unknown";
                var where = $"page {pageNumber} ({subtype} annotation)";

                switch (subtype)
                {
                    case "FileAttachment":
                        if (ResolveDict(document, annot.GetOptional("FS")) is { } attachedSpec)
                            AddSpec(attachedSpec, null, $"page {pageNumber} (file attachment annotation)");
                        break;
                    case "Sound":
                        if (document.Resolve(annot.GetOptional("Sound") ?? PdfNull.Instance) is PdfStream sound
                            && streams.Add(sound))
                        {
                            found.Add(new Found("(sound clip)", where, null, sound)
                            {
                                Owner = annot,
                                OwnerKey = "Sound",
                            });
                        }
                        break;
                }

                if (subtype is "RichMedia" or "Screen" or "Movie" or "FileAttachment")
                {
                    foreach (var mediaSpec in LocalFileSpecs(document, annot))
                        AddSpec(mediaSpec, null, where);
                }

                AddSpecArray(annot.GetOptional("AF"), $"{where}, associated file");
            }
        }

        // Anything the named routes did not reach: actions, form XObject and
        // structure element /AF, and whatever a future producer invents.
        foreach (var fs in ReachableFileSpecs(document))
            AddSpec(fs, null, "elsewhere in the document (an action, form XObject or structure element)");

        return found;
    }

    /// <summary>
    /// True when the annotation plays or carries an embedded file and goes
    /// with it on removal.
    /// </summary>
    private static bool AnnotationCarriesEmbeddedFile(PdfDocument document, PdfDictionary annot)
    {
        switch (annot.GetNameOrNull("Subtype"))
        {
            case "FileAttachment":
            case "RichMedia":
                return true;
            case "Sound":
                return annot.GetOptional("Sound") != null;
            case "Screen":
            case "Movie":
                return LocalFileSpecs(document, annot).Any();
            default:
                return false;
        }
    }

    // ───────────────────────── removal ─────────────────────────

    /// <summary>
    /// Remove every embedded file. <paramref name="undo"/>, when given,
    /// receives one action per mutation; run them in reverse order to restore.
    /// </summary>
    internal static IReadOnlyList<AttachmentRedactionResult> RemoveAll(
        PdfDocument document, List<Action>? undo = null)
    {
        var found = Enumerate(document);

        var names = ResolveDict(document, document.Catalog.GetOptional("Names"));
        if (names != null)
        {
            RemoveKey(names, "EmbeddedFiles", undo);
            RemoveKey(names, "AF", undo);
        }
        RemoveKey(document.Catalog, "AF", undo);

        for (var pageNumber = 1; pageNumber <= document.PageCount; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            RemoveKey(page.Dictionary, "AF", undo);

            if (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
                continue;
            for (var i = annots.Count - 1; i >= 0; i--)
            {
                if (document.Resolve(annots[i]) is not PdfDictionary annot) continue;
                RemoveKey(annot, "AF", undo);
                if (!AnnotationCarriesEmbeddedFile(document, annot)) continue;

                var index = i;
                var removed = annots[i];
                annots.RemoveAt(i);
                undo?.Add(() => annots.Insert(index, removed));

                // A structure element may still reach the annotation through
                // /OBJR, which keeps it in the saved file. Reduce it to a stub
                // so nothing it carried — its note, its media, its file — is
                // written with it.
                foreach (var key in annot.Keys.Select(k => k.Value).ToList())
                {
                    if (key is not ("Type" or "Subtype" or "Rect"))
                        RemoveKey(annot, key, undo);
                }
            }
        }

        foreach (var file in found)
        {
            if (file.FileSpec != null)
            {
                RemoveKey(file.FileSpec, "EF", undo);
                RemoveKey(file.FileSpec, "RF", undo);
                RemoveKey(file.FileSpec, "Desc", undo);
            }
            if (file.Owner != null && file.OwnerKey != null)
                RemoveKey(file.Owner, file.OwnerKey, undo);
        }

        document.InvalidateDerivedState(PdfDocumentDerivedStateScope.Attachments);
        if (undo != null)
            undo.Add(() => document.InvalidateDerivedState(PdfDocumentDerivedStateScope.Attachments));

        return found
            .Select(f => new AttachmentRedactionResult(f.Name, SizeOf(document, f), f.Location, AttachmentDisposition.Removed))
            .ToList();
    }

    /// <summary>
    /// The PDF-portfolio refusal (#1572): a catalog <c>/Collection</c> means
    /// the attachments ARE the document.
    /// </summary>
    internal static void ThrowIfPortfolio(PdfDocument document)
    {
        if (document.Catalog.GetOptional("Collection") != null)
            throw new PdfPortfolioRedactionException();
    }

    // ───────────────────────── kept attachments ─────────────────────────

    /// <summary>
    /// The redaction of attachments a caller chose to KEEP: text files have
    /// the terms cut out, nested PDFs are redacted with the same options, and
    /// anything else is reported as not checked.
    /// </summary>
    /// <param name="redactNested">Redacts one nested document for one term and
    /// returns its report.</param>
    /// <exception cref="AttachmentRedactionRefusedException">A nested PDF could
    /// not be redacted. Thrown before anything in <paramref name="document"/>
    /// is changed.</exception>
    internal static List<(Found File, AttachmentRedactionResult Result)> RedactKept(
        PdfDocument document,
        IReadOnlyList<string> terms,
        bool caseSensitive,
        bool wholeWord,
        int depth,
        Func<PdfDocument, string, RedactionReport> redactNested)
    {
        var results = new List<(Found, AttachmentRedactionResult)>();
        var writes = new List<Action>();

        // The document-carrier scrub's floor (#999): cutting one- and
        // two-character fragments out of a file corrupts it for no security
        // benefit, so such a term is reported rather than applied.
        var allTerms = terms;
        terms = terms.Where(t => t.Length >= MinTermLength).ToList();

        foreach (var file in Enumerate(document))
        {
            // A rewritten file reports the size it is saved with, not the original's.
            long? size = SizeOf(document, file);
            AttachmentRedactionResult Result(AttachmentDisposition disposition, string? detail)
                => new(file.Name, size, file.Location, disposition, detail);

            if (terms.Count == 0)
            {
                results.Add((file, Result(AttachmentDisposition.KeptNotChecked, allTerms.Count == 0
                    ? "there was no term to look for (area redaction); the file may contain the redacted text"
                    : $"the term is shorter than {MinTermLength} characters, so attachments were not searched")));
                continue;
            }

            // #1582: the name-tree key is a second name the file is listed
            // under, and a reader shows it. When it holds the term the file
            // goes, as it does when /F or /UF holds it (#1151).
            if (file.NameTreeKey is { } treeKey && file.FileSpec is { } keyedSpec
                && terms.Any(t => WithoutTerm(treeKey, t, caseSensitive, wholeWord) != treeKey))
            {
                writes.Add(() => RemoveFromEmbeddedFilesTree(document, keyedSpec, terms, caseSensitive, wholeWord));
                results.Add((file, Result(AttachmentDisposition.Removed,
                    "removed: its name in the document's attachment list held the term")));
                continue;
            }

            var bytes = DecodedBytes(file.Payload);
            if (bytes == null)
            {
                results.Add((file, Result(AttachmentDisposition.KeptNotChecked,
                    file.Payload == null ? "the file's content is not embedded" : "the file's content could not be decoded")));
                continue;
            }

            switch (Classify(file, bytes))
            {
                case FileKind.Text:
                {
                    var (encoding, preamble, text) = DecodeText(bytes);
                    var redacted = text;
                    foreach (var term in terms)
                        redacted = WithoutTerm(redacted, term, caseSensitive, wholeWord);

                    if (redacted == text)
                    {
                        results.Add((file, Result(AttachmentDisposition.KeptTermNotFound, null)));
                        break;
                    }

                    var body = encoding.GetBytes(redacted);
                    var newBytes = new byte[preamble.Length + body.Length];
                    preamble.CopyTo(newBytes, 0);
                    body.CopyTo(newBytes, preamble.Length);

                    var stillThere = terms.Any(t => ContainsTerm(newBytes, t, caseSensitive, wholeWord));
                    var payload = file.Payload!;
                    writes.Add(() => ReplacePayload(document, payload, newBytes));
                    size = newBytes.Length;
                    results.Add((file, stillThere
                        ? Result(AttachmentDisposition.KeptNotClean,
                            "the term was cut out but can still be read in another encoding of the file")
                        : Result(AttachmentDisposition.KeptTermRemoved, "the term was cut out of the text")));
                    break;
                }

                case FileKind.Pdf:
                {
                    if (depth >= MaxNestedPdfDepth)
                        throw new AttachmentRedactionRefusedException(file.Name,
                            $"it is a PDF nested more than {MaxNestedPdfDepth} levels deep");

                    PdfDocument nested;
                    try
                    {
                        // An encrypted member opens only if its user password is
                        // empty; anything else throws and is refused below.
                        nested = PdfDocument.Open(bytes);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        throw new AttachmentRedactionRefusedException(file.Name,
                            $"it is a PDF excise could not open, or one protected by a password ({ex.Message})");
                    }

                    using (nested)
                    {
                        var reports = terms.Select(term => redactNested(nested, term)).ToList();
                        using var output = new MemoryStream();
                        // #643: an encrypted member stays encrypted.
                        nested.Save(output, nested.GetReEncryptionOptions(userPassword: null));
                        var newBytes = output.ToArray();
                        var payload = file.Payload!;
                        writes.Add(() => ReplacePayload(document, payload, newBytes));
                        size = newBytes.Length;

                        var unclean = reports.FirstOrDefault(r => !r.IsCleanSuccess);
                        var removed = reports.Sum(r => r.VerifiedRemovals);
                        results.Add((file, unclean != null
                            ? Result(AttachmentDisposition.KeptNotClean,
                                $"nested PDF redacted, but not cleanly: {unclean}")
                            : removed > 0
                                ? Result(AttachmentDisposition.KeptTermRemoved,
                                    $"nested PDF redacted with the same options ({removed} removed)")
                                : Result(AttachmentDisposition.KeptTermNotFound,
                                    "nested PDF redacted with the same options; no occurrence on its pages")));
                    }
                    break;
                }

                default:
                    results.Add((file, Result(AttachmentDisposition.KeptNotChecked,
                        $"excise does not inspect {DescribeType(file)}; it may contain the redacted text")));
                    break;
            }
        }

        // Every refusal has been raised by now, so nothing was half-applied.
        foreach (var write in writes)
            write();
        if (writes.Count > 0)
            document.InvalidateDerivedState(PdfDocumentDerivedStateScope.Attachments);

        return results;
    }

    /// <summary>
    /// Kept results whose file a later pass removed (the term-based
    /// embedded-files carrier scrub drops a file whose name or description
    /// holds the term) are re-labelled as removed.
    /// </summary>
    internal static List<AttachmentRedactionResult> Reconcile(
        PdfDocument document, List<(Found File, AttachmentRedactionResult Result)> kept)
    {
        if (kept.Count == 0) return new List<AttachmentRedactionResult>();

        var still = Enumerate(document);
        var specs = new HashSet<PdfDictionary>(
            still.Where(f => f.FileSpec != null).Select(f => f.FileSpec!), ReferenceEqualityComparer.Instance);
        var streams = new HashSet<PdfStream>(
            still.Where(f => f.Payload != null).Select(f => f.Payload!), ReferenceEqualityComparer.Instance);

        return kept.Select(k =>
        {
            var present = k.File.FileSpec != null
                ? specs.Contains(k.File.FileSpec)
                : k.File.Payload != null && streams.Contains(k.File.Payload);
            return present
                ? k.Result
                : k.Result with
                {
                    Disposition = AttachmentDisposition.Removed,
                    Detail = "removed by the embedded-files carrier scrub: its name, description or content held the term",
                };
        }).ToList();
    }

    private enum FileKind { Text, Pdf, Other }

    private static FileKind Classify(Found file, byte[] bytes)
    {
        var extension = Path.GetExtension(file.Name);
        var mime = file.Payload?.GetNameOrNull("Subtype")?.ToLowerInvariant();

        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)
            || mime == "application/pdf"
            || StartsWithPdfHeader(bytes))
            return FileKind.Pdf;

        if (TextExtensions.Contains(extension)
            || (mime != null && (mime.StartsWith("text/", StringComparison.Ordinal)
                                 || mime is "application/json" or "application/xml")))
            return FileKind.Text;

        return FileKind.Other;
    }

    private static string DescribeType(Found file)
    {
        var extension = Path.GetExtension(file.Name);
        if (extension.Length > 0) return $"{extension} files";
        return file.Payload?.GetNameOrNull("Subtype") is { } mime ? $"{mime} files" : "files of this type";
    }

    private static bool StartsWithPdfHeader(byte[] bytes)
    {
        // §7.5.2 lets the header sit anywhere in the first 1024 bytes.
        var window = Math.Min(bytes.Length, 1024);
        var header = "%PDF-"u8;
        return bytes.AsSpan(0, window).IndexOf(header) >= 0;
    }

    private static (Encoding Encoding, byte[] Preamble, string Text) DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false), bytes[..3], Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(false, false), bytes[..2], Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(true, false), bytes[..2], Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));

        var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        try
        {
            return (new UTF8Encoding(false), Array.Empty<byte>(), strictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            // Latin-1 maps every byte to one char and back, so a file in a
            // legacy single-byte encoding round-trips unchanged outside the cut.
            return (Encoding.Latin1, Array.Empty<byte>(), Encoding.Latin1.GetString(bytes));
        }
    }

    private static bool ContainsTerm(byte[] bytes, string term, bool caseSensitive, bool wholeWord)
    {
        foreach (var encoding in new Encoding[] { Encoding.Latin1, Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var text = encoding.GetString(bytes);
            if (WithoutTerm(text, term, caseSensitive, wholeWord) != text)
                return true;
        }
        return false;
    }

    private static string WithoutTerm(string value, string term, bool caseSensitive, bool wholeWord)
    {
        if (string.IsNullOrEmpty(term)) return value;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        var sb = new StringBuilder(value.Length);
        var from = 0;
        var at = 0;
        var changed = false;
        while (at <= value.Length - term.Length)
        {
            var found = value.IndexOf(term, at, comparison);
            if (found < 0) break;
            var end = found + term.Length;
            if (wholeWord &&
                ((found > 0 && IsWordChar(value[found - 1])) || (end < value.Length && IsWordChar(value[end]))))
            {
                at = found + 1;
                continue;
            }
            sb.Append(value, from, found - from);
            from = end;
            at = end;
            changed = true;
        }
        if (!changed) return value;
        sb.Append(value, from, value.Length - from);
        return sb.ToString();
    }

    private static void ReplacePayload(PdfDocument document, PdfStream payload, byte[] bytes)
    {
        payload.DecodedData = bytes;
        if (ResolveDict(document, payload.GetOptional("Params")) is { } parameters)
        {
            parameters.SetInt("Size", bytes.Length);
            // An MD5 of the ORIGINAL content confirms a guessed value; it has
            // to go with the content it describes.
            parameters.Remove("CheckSum");
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private static byte[]? DecodedBytes(PdfStream? stream)
    {
        if (stream == null) return null;
        try { return stream.DecodedData; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static long? SizeOf(PdfDocument document, Found file)
    {
        if (file.Payload == null) return null;
        if (ResolveDict(document, file.Payload.GetOptional("Params")) is { } parameters
            && document.Resolve(parameters.GetOptional("Size") ?? PdfNull.Instance) is PdfInteger size
            && size.Value >= 0)
            return size.Value;
        return DecodedBytes(file.Payload)?.Length ?? file.Payload.EncodedData.Length;
    }

    private static PdfStream? PayloadOf(PdfDocument document, PdfDictionary fs)
    {
        if (ResolveDict(document, fs.GetOptional("EF")) is not { } ef) return null;
        foreach (var key in PayloadKeys)
            if (document.Resolve(ef.GetOptional(key) ?? PdfNull.Instance) is PdfStream stream)
                return stream;
        return null;
    }

    private static string NameOf(PdfDocument document, PdfDictionary fs, string? key)
    {
        foreach (var k in new[] { "UF", "F" })
            if (document.Resolve(fs.GetOptional(k) ?? PdfNull.Instance) is PdfString s && s.Value.Length > 0)
                return s.Value;
        return string.IsNullOrEmpty(key) ? "(unnamed attachment)" : key;
    }

    private static PdfDictionary? ResolveDict(PdfDocument document, PdfObject? value)
        => value == null ? null : document.Resolve(value) as PdfDictionary;

    private static void RemoveKey(PdfDictionary dictionary, string key, List<Action>? undo)
    {
        if (dictionary.GetOptional(key) is not { } value) return;
        dictionary.Remove(key);
        undo?.Add(() => dictionary.Set(key, value));
    }

    private static void WalkNameTree(
        PdfDocument document, PdfDictionary node, Action<string?, PdfObject> visit, HashSet<PdfDictionary> seen)
    {
        if (!seen.Add(node)) return;
        if (document.Resolve(node.GetOptional("Names") ?? PdfNull.Instance) is PdfArray pairs)
        {
            for (var i = 0; i + 1 < pairs.Count; i += 2)
                visit((document.Resolve(pairs[i]) as PdfString)?.Value, pairs[i + 1]);
        }
        if (document.Resolve(node.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
        {
            foreach (var kid in kids)
                if (document.Resolve(kid) is PdfDictionary child)
                    WalkNameTree(document, child, visit, seen);
        }
    }

    /// <summary>
    /// Drop <paramref name="fileSpec"/>'s entry from the <c>/EmbeddedFiles</c>
    /// name tree, strip its embedded data wherever else it is referenced, and
    /// cut the terms from any <c>/Limits</c> string that repeats the key.
    /// </summary>
    private static void RemoveFromEmbeddedFilesTree(
        PdfDocument document, PdfDictionary fileSpec, IReadOnlyList<string> terms, bool caseSensitive, bool wholeWord)
    {
        var names = ResolveDict(document, document.Catalog.GetOptional("Names"));
        if (ResolveDict(document, names?.GetOptional("EmbeddedFiles")) is not { } root) return;

        var stack = new Stack<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;

            if (document.Resolve(node.GetOptional("Names") ?? PdfNull.Instance) is PdfArray pairs)
            {
                for (var i = pairs.Count - 2; i >= 0; i -= 2)
                {
                    if (ReferenceEquals(document.Resolve(pairs[i + 1]), fileSpec))
                    {
                        pairs.RemoveAt(i + 1);
                        pairs.RemoveAt(i);
                    }
                }
            }

            if (document.Resolve(node.GetOptional("Limits") ?? PdfNull.Instance) is PdfArray limits)
            {
                for (var i = 0; i < limits.Count; i++)
                {
                    if (document.Resolve(limits[i]) is not PdfString limit) continue;
                    var cut = limit.Value;
                    foreach (var term in terms)
                        cut = WithoutTerm(cut, term, caseSensitive, wholeWord);
                    if (cut != limit.Value)
                        limits[i] = new PdfString(cut);
                }
            }

            if (document.Resolve(node.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
                foreach (var kid in kids)
                    if (document.Resolve(kid) is PdfDictionary child)
                        stack.Push(child);
        }

        RemoveKey(fileSpec, "EF", null);
        RemoveKey(fileSpec, "RF", null);
        RemoveKey(fileSpec, "Desc", null);
        document.InvalidateDerivedState(PdfDocumentDerivedStateScope.Attachments);
    }

    /// <summary>File specifications with <c>/EF</c> reachable from an annotation without leaving it.</summary>
    private static IEnumerable<PdfDictionary> LocalFileSpecs(PdfDocument document, PdfDictionary annot)
    {
        var result = new List<PdfDictionary>();
        var visitedObjects = new HashSet<int>();
        var visitedDirect = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Walk(PdfObject obj, int depth)
        {
            if (depth > 12) return;
            if (obj is PdfReference reference)
            {
                if (!visitedObjects.Add(reference.ObjectNum)) return;
                PdfObject resolved;
                try { resolved = document.Resolve(reference); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
                Walk(resolved, depth + 1);
                return;
            }
            if (!visitedDirect.Add(obj)) return;

            switch (obj)
            {
                case PdfDictionary dict:
                    var type = dict.GetNameOrNull("Type");
                    if (type is "Page" or "Pages" or "Catalog") return;
                    if (ResolveDict(document, dict.GetOptional("EF")) != null && !ReferenceEquals(dict, annot))
                        result.Add(dict);
                    foreach (var (key, value) in dict)
                    {
                        if (LocalWalkStopKeys.Contains(key.Value)) continue;
                        Walk(value, depth + 1);
                    }
                    break;
                case PdfArray array:
                    foreach (var item in array) Walk(item, depth + 1);
                    break;
            }
        }

        Walk(annot, 0);
        return result;
    }

    /// <summary>Every file specification with <c>/EF</c> reachable from the trailer.</summary>
    private static List<PdfDictionary> ReachableFileSpecs(PdfDocument document)
    {
        var result = new List<PdfDictionary>();
        HashSet<int> reachable;
        try { reachable = document.ComputeReachableObjects(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return result; }

        var visitedDirect = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(PdfObject obj, int depth)
        {
            // References are separate objects, enumerated by number below.
            if (depth > 64 || obj is PdfReference || !visitedDirect.Add(obj)) return;
            switch (obj)
            {
                case PdfDictionary dict:
                    if (ResolveDict(document, dict.GetOptional("EF")) != null)
                        result.Add(dict);
                    foreach (var (_, value) in dict) Walk(value, depth + 1);
                    break;
                case PdfArray array:
                    foreach (var item in array) Walk(item, depth + 1);
                    break;
            }
        }

        foreach (var objectNumber in reachable.OrderBy(n => n))
        {
            PdfObject obj;
            try { obj = document.GetObject(objectNumber); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            Walk(obj, 0);
        }
        return result;
    }
}
