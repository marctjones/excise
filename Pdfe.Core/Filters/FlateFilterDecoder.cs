using System.IO.Compression;

namespace Pdfe.Core.Filters;

internal sealed class FlateFilterDecoder : AliasedFilterDecoder
{
    public FlateFilterDecoder()
        : base("FlateDecode", "Fl")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
    {
        int offset = 0;
        if (data.Length >= 2)
        {
            int cmf = data[0];
            int flg = data[1];
            if ((cmf * 256 + flg) % 31 == 0)
            {
                int cm = cmf & 0x0F;
                if (cm == 8)
                {
                    offset = 2;
                    if ((flg & 0x20) != 0)
                        offset += 4;
                }
            }
        }

        using var input = new MemoryStream(data, offset, data.Length - offset);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        deflate.CopyTo(output);
        return PdfPredictor.ApplyIfNeeded(output.ToArray(), context.DecodeParms);
    }
}
