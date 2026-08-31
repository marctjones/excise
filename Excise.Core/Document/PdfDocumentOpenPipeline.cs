using System.Text;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Excise.Core.Security;

namespace Excise.Core.Document;

/// <summary>
/// The fully assembled inputs for one <see cref="PdfDocument"/>. The result
/// makes transfer of the single object store, and therefore source-stream
/// ownership, explicit at the document-construction boundary.
/// </summary>
internal sealed record PdfDocumentOpenResult(
    PdfDocumentObjectStore ObjectStore,
    PdfDictionary Trailer,
    PdfDictionary Catalog,
    PdfDictionary? Info,
    string Version,
    PdfPermissions Permissions);

/// <summary>
/// Composes the existing PDF header, xref, parser, and Standard security
/// implementations into one document-open lifecycle.
/// </summary>
/// <remarks>
/// This pipeline owns revision traversal, hybrid-xref precedence, trailer and
/// catalog validation, encryption negotiation, construction of the one
/// <see cref="PdfDocumentObjectStore"/>, and cleanup after a failed open. It is
/// orchestration only: <see cref="XRefParser"/>, <see cref="PdfParser"/>, and
/// <see cref="PdfStandardSecurityHandler"/> remain the authoritative
/// algorithms, and no second reader or document graph is introduced.
/// </remarks>
internal static class PdfDocumentOpenPipeline
{
    internal static PdfDocument Open(
        Stream stream,
        bool ownsStream,
        bool allowEncrypted,
        string? userPassword)
    {
        PdfDocumentObjectStore? objectStore = null;
        try
        {
            var version = ReadVersion(stream);
            var (trailer, xref) = AssembleCrossReferences(stream);
            var encryption = NegotiateEncryption(
                stream, trailer, xref, allowEncrypted, userPassword);

            objectStore = new PdfDocumentObjectStore(
                stream, ownsStream, xref, encryption.Handler);
            var result = CompleteResult(
                objectStore, trailer, version, encryption.Permissions);
            return new PdfDocument(result);
        }
        catch
        {
            // Before store construction the pipeline still owns the raw
            // stream. Afterwards disposal flows through the one store owner.
            if (objectStore != null)
                objectStore.Dispose();
            else if (ownsStream)
                stream.Dispose();
            throw;
        }
    }

    private static (PdfDictionary Trailer, Dictionary<int, XRefEntry> XRef)
        AssembleCrossReferences(Stream stream)
    {
        var xrefParser = new XRefParser(stream);
        var (trailer, xref) = xrefParser.ParseRootXRef();
        var fullXRef = new Dictionary<int, XRefEntry>(xref);
        var currentTrailer = trailer;
        var parsedPreviousXRefs = new HashSet<long>();
        var parsedHybridXRefStreams = new HashSet<long>();

        // The root section may itself carry /XRefStm, so merge it before
        // walking /Prev rather than only inspecting older sections (#872).
        MergeHybridXRefStream(
            xrefParser, stream, trailer, fullXRef, parsedHybridXRefStreams);

        while (currentTrailer.GetReferenceOrNull("Prev") != null
               || currentTrailer.ContainsKey("Prev"))
        {
            var prevObj = currentTrailer.GetOptional("Prev");
            if (prevObj == null)
                break;

            // /Prev is a direct integer offset. A damaged link costs the
            // older revisions, not the otherwise readable document (#960).
            if (!prevObj.TryGetNumber(out var prevNumber))
                break;

            var prevXRef = (long)prevNumber;
            if (prevXRef < 0
                || prevXRef >= stream.Length
                || !parsedPreviousXRefs.Add(prevXRef))
            {
                break;
            }

            (PdfDictionary Trailer, Dictionary<int, XRefEntry> XRef) previous;
            try
            {
                previous = xrefParser.ParseDocumentXRef(prevXRef);
            }
            catch (Exception ex) when (IsRecoverableIncrementalXRefException(ex))
            {
                break;
            }

            // Newer entries remain authoritative; older revisions fill gaps.
            MergeMissingEntries(fullXRef, previous.XRef);
            MergeHybridXRefStream(
                xrefParser,
                stream,
                previous.Trailer,
                fullXRef,
                parsedHybridXRefStreams);
            currentTrailer = previous.Trailer;
        }

        RecoverUnreachableRoot(xrefParser, trailer, fullXRef);
        return (trailer, fullXRef);
    }

    /// <summary>
    /// Merge the cross-reference stream named by a classic trailer's
    /// <c>/XRefStm</c> (a hybrid-reference file, ISO 32000-1 §7.5.8.4).
    /// </summary>
    /// <remarks>
    /// The classic table is merged first and the stream fills gaps it cannot
    /// express. The stream's own <c>/Prev</c> is deliberately not followed:
    /// the containing classic trailer owns revision chaining.
    /// </remarks>
    private static void MergeHybridXRefStream(
        XRefParser xrefParser,
        Stream stream,
        PdfDictionary sectionTrailer,
        Dictionary<int, XRefEntry> fullXRef,
        HashSet<long> parsedHybridXRefStreams)
    {
        var xrefStmObj = sectionTrailer.GetOptional("XRefStm");
        if (xrefStmObj == null)
            return;

        long offset;
        try
        {
            offset = xrefStmObj.GetLong();
        }
        catch
        {
            return;
        }

        if (offset <= 0
            || offset >= stream.Length
            || !parsedHybridXRefStreams.Add(offset))
        {
            return;
        }

        Dictionary<int, XRefEntry> entries;
        try
        {
            entries = xrefParser.ParseDocumentXRef(offset).XRef;
        }
        catch (Exception ex) when (IsRecoverableIncrementalXRefException(ex))
        {
            // Best effort: a broken hybrid stream leaves the classic section
            // usable rather than making the entire open fail.
            return;
        }

        MergeMissingEntries(fullXRef, entries);
    }

    private static void RecoverUnreachableRoot(
        XRefParser xrefParser,
        PdfDictionary trailer,
        Dictionary<int, XRefEntry> fullXRef)
    {
        // Reconstruction belongs after the complete /Prev walk. One healthy
        // incremental section may legitimately omit a catalog stored in an
        // older section, so only the assembled table can prove it missing.
        if (RootIsReachable(trailer, fullXRef)
            || !xrefParser.TryReconstructXRef(out var rebuiltTrailer, out var rebuiltXRef))
        {
            return;
        }

        // Real xref entries, including FREE entries, stay authoritative.
        MergeMissingEntries(fullXRef, rebuiltXRef);

        if (!RootIsReachable(trailer, fullXRef)
            && rebuiltTrailer.GetOptional("Root") is { } rebuiltRoot
            && RootIsReachable(rebuiltTrailer, fullXRef))
        {
            trailer["Root"] = rebuiltRoot;
        }
    }

    private static void MergeMissingEntries(
        Dictionary<int, XRefEntry> target,
        Dictionary<int, XRefEntry> olderOrRecovered)
    {
        foreach (var (objectNumber, entry) in olderOrRecovered)
        {
            if (!target.ContainsKey(objectNumber))
                target[objectNumber] = entry;
        }
    }

    private static bool RootIsReachable(
        PdfDictionary trailer,
        Dictionary<int, XRefEntry> xref)
    {
        var rootRef = trailer.GetReferenceOrNull("Root");
        if (rootRef == null)
            return trailer.GetOptional("Root") is PdfDictionary;

        return xref.TryGetValue(rootRef.ObjectNum, out var entry) && entry.InUse;
    }

    private static EncryptionState NegotiateEncryption(
        Stream stream,
        PdfDictionary trailer,
        Dictionary<int, XRefEntry> xref,
        bool allowEncrypted,
        string? userPassword)
    {
        PdfStandardSecurityHandler? handler = null;
        var permissions = PdfPermissions.AllAllowed;
        if (!trailer.ContainsKey("Encrypt"))
            return new EncryptionState(handler, permissions);

        try
        {
            var encryptObj = trailer.GetOptional("Encrypt");
            if (encryptObj is PdfReference encryptRef)
            {
                try
                {
                    encryptObj = ReadIndirectObjectAt(
                        stream, xref[encryptRef.ObjectNum].Offset);
                }
                catch (Exception ex) when (IsRecoverableMalformedEncryptObjectException(ex))
                {
                    encryptObj = null;
                }
            }

            if (encryptObj == null)
                return new EncryptionState(null, permissions);
            if (encryptObj is not PdfDictionary encryptDict)
                throw new PdfParseException("/Encrypt is not a dictionary");

            // /P is plain policy metadata and is available even if a handler
            // cannot be built (#642).
            permissions = ReadPermissions(encryptDict);

            // PDF 2.0 can encrypt embedded files only while document strings
            // and streams explicitly use /Identity (#1167).
            if (!UsesIdentityCryptFiltersForDocumentContent(encryptDict))
            {
                // /ID must be direct because it is needed before a handler
                // exists to decrypt indirect objects.
                var idArray = trailer.GetDirectArrayOrNull("ID");
                if (idArray is null
                    || idArray.Count == 0
                    || idArray[0] is not PdfString firstId)
                {
                    throw new PdfParseException("/ID array missing or empty");
                }

                handler = PdfStandardSecurityHandler.Build(
                    encryptDict, firstId.Bytes, userPassword);
            }
        }
        catch (PdfEncryptionNotSupportedException)
        {
            if (!allowEncrypted)
                throw;

            // Inspection-only fallback: callers explicitly accept ciphertext.
            handler = null;
        }

        return new EncryptionState(handler, permissions);
    }

    private static PdfDocumentOpenResult CompleteResult(
        PdfDocumentObjectStore objectStore,
        PdfDictionary trailer,
        string version,
        PdfPermissions permissions)
    {
        // A hostile or truncated trailer fails with a typed parse error, not a
        // raw missing-key/cast exception (#352).
        var catalogReference = trailer.GetReferenceOrNull("Root")
            ?? throw new PdfParseException("Trailer has no valid /Root reference");
        var catalog = objectStore.GetObject(catalogReference) as PdfDictionary
            ?? throw new PdfParseException("Could not load document catalog");

        PdfDictionary? info = null;
        var infoReference = trailer.GetReferenceOrNull("Info");
        if (infoReference != null)
        {
            try
            {
                info = objectStore.GetObject(infoReference) as PdfDictionary;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                info = null;
            }
        }

        return new PdfDocumentOpenResult(
            objectStore, trailer, catalog, info, version, permissions);
    }

    /// <summary>
    /// Decode <c>/P</c> from the encryption dictionary. Malformed or missing
    /// advisory permission metadata fails open to all-allowed.
    /// </summary>
    private static PdfPermissions ReadPermissions(PdfDictionary encryptDict)
    {
        try
        {
            var value = encryptDict.GetOptional("P");
            if (value is PdfInteger or PdfReal)
                return new PdfPermissions(value.GetLong());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fall through to the compatibility default.
        }

        return PdfPermissions.AllAllowed;
    }

    private static bool UsesIdentityCryptFiltersForDocumentContent(PdfDictionary encryptDict)
        => string.Equals(
               encryptDict.GetNameOrNull("StmF"), "Identity", StringComparison.Ordinal)
           && string.Equals(
               encryptDict.GetNameOrNull("StrF"), "Identity", StringComparison.Ordinal);

    private static bool IsRecoverableIncrementalXRefException(Exception ex)
        => ex is PdfParseException or FormatException or OverflowException or KeyNotFoundException;

    private static bool IsRecoverableMalformedEncryptObjectException(Exception ex)
        => ex is PdfParseException { Message: var message }
           && (message.Contains("Unexpected keyword", StringComparison.Ordinal)
               || message.Contains("Expected object number", StringComparison.Ordinal)
               || message.Contains("Expected generation number", StringComparison.Ordinal)
               || message.Contains("Expected 'obj'", StringComparison.Ordinal)
               || message.Contains("Unterminated dictionary", StringComparison.Ordinal));

    /// <summary>
    /// Resolve an indirect encryption dictionary before the document object
    /// store exists. The ordinary <see cref="PdfParser"/> remains the reader.
    /// </summary>
    private static PdfObject ReadIndirectObjectAt(Stream stream, long offset)
    {
        var lexer = new PdfLexer(stream, ownsStream: false);
        lexer.Seek(offset);
        var parser = new PdfParser(lexer);
        return parser.ParseIndirectObject().Value;
    }

    private static string ReadVersion(Stream stream)
    {
        stream.Position = 0;
        var buffer = new byte[Math.Min(1024, Math.Max(20, (int)Math.Min(stream.Length, 1024)))];
        var read = stream.Read(buffer, 0, buffer.Length);

        var header = Encoding.ASCII.GetString(buffer, 0, read);
        var headerStart = header.IndexOf("%PDF-", StringComparison.Ordinal);
        if (headerStart < 0)
            return "0.0";

        var end = headerStart + 5;
        while (end < header.Length && (char.IsDigit(header[end]) || header[end] == '.'))
            end++;

        var version = header.Substring(headerStart + 5, end - (headerStart + 5));
        return IsValidPdfVersion(version) ? version : "0.0";
    }

    private static bool IsValidPdfVersion(string version)
        => version.Length == 3
           && version[1] == '.'
           && char.IsDigit(version[0])
           && char.IsDigit(version[2]);

    private readonly record struct EncryptionState(
        PdfStandardSecurityHandler? Handler,
        PdfPermissions Permissions);
}
