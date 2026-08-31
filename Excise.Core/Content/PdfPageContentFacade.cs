using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Content-engine compatibility members exposed on the document page model.
/// The partial type preserves the established public API while content stream
/// reading, parsing, mutation, and serialization remain in <c>core-content</c>.
/// </summary>
public partial class PdfPage
{
    /// <summary>
    /// Get the raw content stream bytes (decoded).
    /// </summary>
    public byte[] GetContentStreamBytes()
    {
        TryCollectContentStreamBytes(skipRecoverableContentStreams: false, out var data, out _);
        return data;
    }

    /// <summary>
    /// Try to get decoded page content stream bytes, skipping streams whose
    /// filter pipeline could not be decoded. Returns false if any stream was
    /// skipped. Use this for best-effort viewing only; editing and redaction
    /// should keep using <see cref="GetContentStreamBytes"/> so undecodable
    /// content never silently disappears from mutation paths.
    /// </summary>
    internal bool TryGetContentStreamBytes(out byte[] data)
        => TryGetContentStreamBytes(out data, out _);

    internal bool TryGetContentStreamBytes(
        out byte[] data,
        out IReadOnlyList<ContentStreamReadWarning> warnings)
        => TryCollectContentStreamBytes(skipRecoverableContentStreams: true, out data, out warnings);

    private bool TryCollectContentStreamBytes(
        bool skipRecoverableContentStreams,
        out byte[] data,
        out IReadOnlyList<ContentStreamReadWarning> warnings)
    {
        var warningList = new List<ContentStreamReadWarning>();
        var contentsObj = _pageDict.GetOptional("Contents");
        if (contentsObj == null)
        {
            data = Array.Empty<byte>();
            warnings = Array.Empty<ContentStreamReadWarning>();
            return true;
        }

        contentsObj = _document.Resolve(contentsObj);

        if (contentsObj is PdfStream stream)
        {
            var complete = TryGetDecodedContentStreamBytes(
                stream,
                skipRecoverableContentStreams,
                warningList,
                out data);
            warnings = warningList;
            return complete;
        }

        if (contentsObj is PdfArray array)
        {
            // Multiple content streams - concatenate
            var complete = true;
            using var ms = new MemoryStream();
            foreach (var item in array)
            {
                var resolved = _document.Resolve(item);
                if (resolved is not PdfStream contentStream)
                    continue;

                if (!TryGetDecodedContentStreamBytes(
                        contentStream,
                        skipRecoverableContentStreams,
                        warningList,
                        out var streamData))
                {
                    complete = false;
                    continue;
                }

                ms.Write(streamData);
                ms.WriteByte((byte)'\n'); // Separate streams with newline
            }
            data = ms.ToArray();
            warnings = warningList;
            return complete;
        }

        data = Array.Empty<byte>();
        warnings = Array.Empty<ContentStreamReadWarning>();
        return true;
    }

    private static bool TryGetDecodedContentStreamBytes(
        PdfStream stream,
        bool skipRecoverableContentStreams,
        List<ContentStreamReadWarning> warnings,
        out byte[] data)
    {
        data = Array.Empty<byte>();
        if (TryGetImageOnlyContentFilter(stream, out var imageOnlyFilter))
        {
            var warning = ContentStreamReadWarning.ImageOnlyFilter(
                stream.ObjectNumber ?? 0,
                stream.GenerationNumber ?? 0,
                imageOnlyFilter);
            if (skipRecoverableContentStreams)
            {
                warnings.Add(warning);
                return false;
            }

            throw new InvalidDataException(warning.Message);
        }

        if (skipRecoverableContentStreams && stream.IsFiltered && !stream.IsDecoded)
        {
            warnings.Add(ContentStreamReadWarning.UndecodedFilter(
                stream.ObjectNumber ?? 0,
                stream.GenerationNumber ?? 0,
                stream.Filters));
            return false;
        }

        data = stream.DecodedData;
        return true;
    }

    private static bool TryGetImageOnlyContentFilter(PdfStream stream, out string filter)
    {
        foreach (var candidate in stream.Filters)
        {
            if (IsNamedFilter(candidate, "JBIG2Decode"))
            {
                filter = candidate;
                return true;
            }
        }

        filter = "";
        return false;
    }

    private static bool IsNamedFilter(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.Ordinal)
           || (string.Equals(expected, "JBIG2Decode", StringComparison.Ordinal)
               && string.Equals(actual, "JBIG2", StringComparison.Ordinal));

    /// <summary>
    /// Sets the content stream bytes for this page.
    /// </summary>
    public void SetContentStreamBytes(byte[] data)
    {
        // Any cached extraction (Letters/Text/Words from A4) is now stale —
        // the content has changed underneath it. Multi-match redaction relies
        // on the second RedactArea call seeing freshly-extracted letters that
        // reflect the first redaction's deletions.
        InvalidateTextExtractionCache();

        var contentsObj = _pageDict.GetOptional("Contents");

        if (contentsObj == null)
        {
            // Create a new content stream as a proper indirect object —
            // PDF streams are not valid inline in a dictionary.
            var newStream = new PdfStream(data);
            var streamRef = _document.AddIndirectObject(newStream);
            _pageDict["Contents"] = streamRef;
            return;
        }

        contentsObj = _document.Resolve(contentsObj);

        if (contentsObj is PdfStream stream)
        {
            // Update existing stream (also updates encoded data and length)
            stream.DecodedData = data;
        }
        else if (contentsObj is PdfArray array && array.Count > 0)
        {
            // Update first stream in array
            var firstRef = array[0];
            var resolved = _document.Resolve(firstRef);
            if (resolved is PdfStream firstStream)
            {
                // Update first stream (removes filters too)
                firstStream.DecodedData = data;
                // Clear other streams in the array if present
                while (array.Count > 1)
                    array.RemoveAt(array.Count - 1);
            }
        }
    }

    /// <summary>
    /// Get the content stream as a parsed ContentStream object.
    /// </summary>
    public Excise.Core.Content.ContentStream GetContentStream()
    {
        var bytes = GetContentStreamBytes();
        if (bytes.Length == 0)
            return new Excise.Core.Content.ContentStream();

        var parser = new Excise.Core.Content.ContentStreamParser(bytes, this);
        return parser.Parse();
    }

    /// <summary>
    /// Set the content stream from a ContentStream object.
    /// </summary>
    public void SetContentStream(Excise.Core.Content.ContentStream content)
    {
        var writer = new Excise.Core.Content.ContentStreamWriter();
        var bytes = writer.Write(content);
        SetContentStreamBytes(bytes);
    }
}
