using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;
using static Excise.Core.Document.PdfAttachmentGraph;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// What a redaction does with the attachments it keeps (#1572, #1582): text
/// files have the term cut out, nested PDFs are redacted, a file listed under
/// a name holding the term is removed, and anything else is reported as not
/// checked. Finding and removing attachments is
/// <see cref="PdfAttachmentGraph"/>'s job.
/// </summary>
internal static class AttachmentCarrierScrubber
{
    /// <summary>How deep nested PDF attachments are redacted before excise refuses.</summary>
    internal const int MaxNestedPdfDepth = 3;

    /// <summary>The shortest term cut from a kept attachment (the carrier-scrub floor).</summary>
    private const int MinTermLength = 3;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".xml", ".html", ".htm", ".json", ".md",
    };

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
}
