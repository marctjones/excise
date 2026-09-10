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
        => TryCollectContentStreamBytes(skipRecoverableContentStreams: true, out data, out warnings, out _);

    private bool TryCollectContentStreamBytes(
        bool skipRecoverableContentStreams,
        out byte[] data,
        out IReadOnlyList<ContentStreamReadWarning> warnings)
        => TryCollectContentStreamBytes(skipRecoverableContentStreams, out data, out warnings, out _);

    /// <summary>
    /// As the three-out overload, plus <paramref name="arrayBoundaries"/>: when
    /// <c>/Contents</c> was a multi-stream array, the offset within
    /// <paramref name="data"/> at which each element after the first began —
    /// see <see cref="Content.ContentStream.SourceArrayBoundaries"/>. Null for
    /// a single-stream (or missing) <c>/Contents</c>, or an array with only one
    /// usable stream.
    /// </summary>
    private bool TryCollectContentStreamBytes(
        bool skipRecoverableContentStreams,
        out byte[] data,
        out IReadOnlyList<ContentStreamReadWarning> warnings,
        out IReadOnlyList<int>? arrayBoundaries)
    {
        var warningList = new List<ContentStreamReadWarning>();
        arrayBoundaries = null;
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
            var boundaries = new List<int>();
            var streamCount = 0;
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

                // A boundary marks the start of every element AFTER the
                // first, so it must be recorded before this element's own
                // bytes (and separator) are written (#1449).
                if (streamCount > 0)
                    boundaries.Add((int)ms.Position);
                streamCount++;

                ms.Write(streamData);
                ms.WriteByte((byte)'\n'); // Separate streams with newline
            }
            data = ms.ToArray();
            warnings = warningList;
            if (complete && boundaries.Count > 0)
                arrayBoundaries = boundaries;
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
        => GetContentStream(trackSourceSpans: false);

    /// <summary>
    /// Parse the page content, optionally recording the source span of every
    /// operator so a later <see cref="SetContentStream"/> can re-emit the
    /// untouched ones VERBATIM instead of re-serializing the whole stream
    /// (#1093). Editing paths pass true; read-only callers pay nothing.
    /// </summary>
    internal Excise.Core.Content.ContentStream GetContentStream(bool trackSourceSpans)
    {
        IReadOnlyList<int>? arrayBoundaries = null;
        byte[] bytes;
        if (trackSourceSpans)
        {
            // skipRecoverableContentStreams: false — matches GetContentStreamBytes()
            // (editing paths must never silently drop undecodable content).
            TryCollectContentStreamBytes(
                skipRecoverableContentStreams: false, out bytes, out _, out arrayBoundaries);
        }
        else
        {
            bytes = GetContentStreamBytes();
        }

        if (bytes.Length == 0)
            return new Excise.Core.Content.ContentStream();

        var parser = new Excise.Core.Content.ContentStreamParser(bytes, this)
        {
            TrackSourceSpans = trackSourceSpans,
            ArrayBoundaries = arrayBoundaries,
        };
        return parser.Parse();
    }

    /// <summary>
    /// Set the content stream from a ContentStream object. When the stream
    /// carries the bytes it was parsed from
    /// (<see cref="Excise.Core.Content.ContentStream.SourceBytes"/>), every
    /// operator that was neither replaced nor mutated keeps its original bytes
    /// rather than being re-serialized (#1093).
    ///
    /// <para>When the source was also a multi-stream <c>/Contents</c> array
    /// (<see cref="Excise.Core.Content.ContentStream.SourceArrayBoundaries"/>),
    /// this tries to write the result back into the SAME number of array
    /// elements at the same seams, instead of collapsing the array to one
    /// stream (#1449). It falls back to the single-stream write whenever a
    /// seam no longer lands at a clean operator boundary — e.g. an edit
    /// touched the operator straddling it — which is exactly today's
    /// behaviour, so the failure mode is "no worse than before".</para>
    /// </summary>
    public void SetContentStream(Excise.Core.Content.ContentStream content)
    {
        var writer = new Excise.Core.Content.ContentStreamWriter();

        if (content.SourceBytes is { } arraySource
            && content.SourceArrayBoundaries is { Count: > 0 } boundaries
            && TrySetSplicedArrayContentStream(writer, content, arraySource, boundaries))
        {
            return;
        }

        var bytes = content.SourceBytes is { } source
            ? writer.Write(content, source)
            : writer.Write(content);
        SetContentStreamBytes(bytes);
    }

    /// <summary>
    /// #1449: try to write <paramref name="content"/> back into the page's
    /// existing multi-element <c>/Contents</c> array, one chunk per original
    /// element, rather than collapsing it to a single stream. Returns false
    /// (having mutated nothing) when there is no honest way to do that —
    /// caller falls back to <see cref="SetContentStreamBytes"/>.
    /// </summary>
    private bool TrySetSplicedArrayContentStream(
        Excise.Core.Content.ContentStreamWriter writer,
        Excise.Core.Content.ContentStream content,
        byte[] source,
        IReadOnlyList<int> boundaries)
    {
        var bytes = writer.Write(content, source, boundaries, out var outputBoundaries);
        if (outputBoundaries == null)
            return false;

        // Re-resolve the CURRENT array rather than trusting stale state — it
        // must still have exactly the stream elements this was read from, in
        // the same order, or there is no honest slot to write chunk k into.
        var currentContentsObj = _pageDict.GetOptional("Contents");
        if (currentContentsObj == null || _document.Resolve(currentContentsObj) is not PdfArray array)
            return false;

        var streamIndices = new List<int>();
        for (int i = 0; i < array.Count; i++)
        {
            if (_document.Resolve(array[i]) is PdfStream)
                streamIndices.Add(i);
        }

        if (streamIndices.Count != outputBoundaries.Length + 1)
            return false;

        // Validate every chunk BEFORE mutating anything — a partial write
        // left half-applied on a late failure would be worse than either
        // whole outcome.
        var chunks = new byte[streamIndices.Count][];
        var start = 0;
        for (int k = 0; k < streamIndices.Count; k++)
        {
            var end = k < outputBoundaries.Length ? outputBoundaries[k] : bytes.Length;
            var len = end - start;
            // Every chunk (including the last) carries the synthetic '\n'
            // TryCollectContentStreamBytes appends after every element on
            // read; strip exactly one here or the next read doubles it.
            if (len < 1 || bytes[end - 1] != (byte)'\n')
                return false;

            chunks[k] = new byte[len - 1];
            Array.Copy(bytes, start, chunks[k], 0, len - 1);
            start = end;
        }

        InvalidateTextExtractionCache();
        for (int k = 0; k < streamIndices.Count; k++)
        {
            if (_document.Resolve(array[streamIndices[k]]) is PdfStream streamObj)
                streamObj.DecodedData = chunks[k];
        }

        return true;
    }
}
