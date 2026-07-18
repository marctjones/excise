using Excise.Core.Filters;
using Excise.Core.Primitives;

namespace Excise.Core.Parsing;

/// <summary>
/// Decompresses PDF streams by applying the stream's /Filter pipeline.
/// ISO 32000-2:2020 Section 7.4.
/// </summary>
public class StreamDecompressor
{
    private readonly PdfFilterRegistry _filters;

    public StreamDecompressor()
        : this(PdfFilterRegistry.CreateDefault())
    {
    }

    internal StreamDecompressor(PdfFilterRegistry filters)
    {
        _filters = filters;
    }

    /// <summary>
    /// Decompress a stream in place, setting its DecodedData property.
    /// </summary>
    public void Decompress(PdfStream stream)
    {
        var data = stream.EncodedData;
        var filters = stream.Filters;
        var parms = stream.DecodeParams;

        for (int i = 0; i < filters.Count; i++)
        {
            var filterParms = i < parms.Count ? parms[i] : null;
            data = _filters.Decode(filters[i], data, new PdfFilterDecodeContext(filterParms, stream));
        }

        stream.SetDecodedData(data);
    }

    /// <summary>
    /// Apply a single filter to data.
    /// </summary>
    public byte[] ApplyFilter(string filterName, byte[] data, PdfDictionary? parms)
        => _filters.Decode(filterName, data, new PdfFilterDecodeContext(parms, Stream: null));
}
