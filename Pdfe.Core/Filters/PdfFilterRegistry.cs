using System.IO;
using Pdfe.Core.Filters.Ccitt;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Filters;

internal sealed class PdfFilterRegistry
{
    private readonly IReadOnlyList<IPdfFilterDecoder> _decoders;

    public PdfFilterRegistry(IEnumerable<IPdfFilterDecoder> decoders)
    {
        _decoders = decoders.ToArray();
    }

    public static PdfFilterRegistry CreateDefault()
        => new(new IPdfFilterDecoder[]
        {
            new FlateFilterDecoder(),
            new AsciiHexFilterDecoder(),
            new Ascii85FilterDecoder(),
            new LzwFilterDecoder(),
            new RunLengthFilterDecoder(),
            new PassThroughFilterDecoder("DCTDecode", "DCT"),
            new JpxFilterDecoder(),
            new CcittFaxFilterDecoder(),
            new Jbig2FilterDecoder(),
            new BrotliFilterDecoder(),
            new PassThroughFilterDecoder("Crypt")
        });

    public byte[] Decode(string filterName, byte[] data, PdfFilterDecodeContext context)
    {
        foreach (var decoder in _decoders)
        {
            if (decoder.CanDecode(filterName))
                return decoder.Decode(data, context);
        }

        throw new NotSupportedException($"Unknown filter: {filterName}");
    }
}

internal abstract class AliasedFilterDecoder : IPdfFilterDecoder
{
    private readonly HashSet<string> _aliases;

    protected AliasedFilterDecoder(params string[] aliases)
    {
        _aliases = new HashSet<string>(aliases, StringComparer.Ordinal);
    }

    public bool CanDecode(string filterName) => _aliases.Contains(filterName);

    public abstract byte[] Decode(byte[] data, PdfFilterDecodeContext context);
}

internal sealed class AsciiHexFilterDecoder : AliasedFilterDecoder
{
    public AsciiHexFilterDecoder()
        : base("ASCIIHexDecode", "AHx")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
        => BasicStreamFilters.DecodeAsciiHex(data);
}

internal sealed class Ascii85FilterDecoder : AliasedFilterDecoder
{
    public Ascii85FilterDecoder()
        : base("ASCII85Decode", "A85")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
        => BasicStreamFilters.DecodeAscii85(data);
}

internal sealed class RunLengthFilterDecoder : AliasedFilterDecoder
{
    public RunLengthFilterDecoder()
        : base("RunLengthDecode", "RL")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
        => BasicStreamFilters.DecodeRunLength(data);
}

internal sealed class BrotliFilterDecoder : AliasedFilterDecoder
{
    public BrotliFilterDecoder()
        : base("BrotliDecode")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
        => BasicStreamFilters.DecodeBrotli(data);
}

internal sealed class PassThroughFilterDecoder : AliasedFilterDecoder
{
    public PassThroughFilterDecoder(params string[] aliases)
        : base(aliases)
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context) => data;
}

internal sealed class JpxFilterDecoder : AliasedFilterDecoder
{
    public JpxFilterDecoder()
        : base("JPXDecode")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
    {
        try
        {
            return Jpx.JpxDecoder.Decode(data).Pixels;
        }
        catch (Exception ex) when (IsExpectedCodecFallback(ex))
        {
            return data;
        }
    }

    private static bool IsExpectedCodecFallback(Exception ex)
        => ex is NotSupportedException
            or ArgumentException
            or InvalidDataException;
}

internal sealed class Jbig2FilterDecoder : AliasedFilterDecoder
{
    public Jbig2FilterDecoder()
        : base("JBIG2Decode")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
    {
        var stream = context.Stream;
        if (stream == null)
            return data;

        int width = stream.GetInt("Width", 0);
        int height = stream.GetInt("Height", 0);
        if (width <= 0 || height <= 0)
            return data;

        try
        {
            return Jbig2.Jbig2Decoder.Decode(data, TryGetGlobals(context.DecodeParms), width, height);
        }
        catch (Exception ex) when (IsExpectedCodecFallback(ex))
        {
            return data;
        }
    }

    private static byte[]? TryGetGlobals(PdfDictionary? decodeParms)
        => decodeParms?.GetOptional("JBIG2Globals") is PdfStream globals
            ? globals.EncodedData
            : null;

    private static bool IsExpectedCodecFallback(Exception ex)
        => ex is NotSupportedException
            or ArgumentException
            or OverflowException
            or InvalidOperationException
            or InvalidDataException;
}
