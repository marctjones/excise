using Excise.Core.Parsing;

namespace Excise.Core.Filters;

internal sealed class LzwFilterDecoder : AliasedFilterDecoder
{
    public LzwFilterDecoder()
        : base("LZWDecode", "LZW")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
    {
        // /EarlyChange (§7.4.4.3). The PDF DEFAULT IS 1: the code width grows
        // one code EARLIER than the plain LZW rule. This decoder hard-coded the
        // EarlyChange=0 behaviour and never read the parameter at all, so every
        // conforming LZW stream desynchronised as soon as the table reached a
        // width boundary.
        //
        // Measured on the isartor 6.1.10 fixtures' real 2700-byte stream: the
        // 0 behaviour throws "Invalid LZW code: 704" after emitting 359 of 5805
        // bytes; the 1 behaviour decodes all 5805 cleanly. Two corpus pages
        // rendered blank because of it — the same 215x27 image delivered once
        // as an Image XObject and once as an inline image, which is why they
        // looked like two unrelated clusters (#887).
        //
        // Reach, measured rather than assumed: only 16 of ~3,900 corpus files
        // use LZW at all (PDF/A forbids it), so this is not a corpus-wide win.
        // It matters for older scanned real-world documents, which is exactly
        // the material this tool is pointed at.
        int earlyChange = context.DecodeParms?.GetInt("EarlyChange", 1) ?? 1;
        if (earlyChange is not (0 or 1))
            earlyChange = 1;

        var output = new List<byte>();
        var table = new Dictionary<int, byte[]>();

        for (int i = 0; i < 256; i++)
            table[i] = new[] { (byte)i };

        const int clearCode = 256;
        const int eoiCode = 257;
        int nextCode = 258;
        int codeSize = 9;
        int bitPos = 0;
        int prevCode = -1;

        while (bitPos + codeSize <= data.Length * 8)
        {
            int code = ReadBits(data, bitPos, codeSize);
            bitPos += codeSize;

            if (code == eoiCode)
                break;

            if (code == clearCode)
            {
                table.Clear();
                for (int i = 0; i < 256; i++)
                    table[i] = new[] { (byte)i };

                nextCode = 258;
                codeSize = 9;
                prevCode = -1;
                continue;
            }

            byte[] entry;
            if (table.TryGetValue(code, out var existing))
            {
                entry = existing;
            }
            else if (code == nextCode && prevCode >= 0)
            {
                var prev = table[prevCode];
                entry = new byte[prev.Length + 1];
                Array.Copy(prev, entry, prev.Length);
                entry[prev.Length] = prev[0];
            }
            else
            {
                throw new PdfParseException($"Invalid LZW code: {code}");
            }

            output.AddRange(entry);

            if (prevCode >= 0 && nextCode < 4096)
            {
                var prev = table[prevCode];
                var newEntry = new byte[prev.Length + 1];
                Array.Copy(prev, newEntry, prev.Length);
                newEntry[prev.Length] = entry[0];
                table[nextCode] = newEntry;
                nextCode++;

                if (nextCode + earlyChange >= (1 << codeSize) && codeSize < 12)
                    codeSize++;
            }

            prevCode = code;
        }

        return PdfPredictor.ApplyIfNeeded(output.ToArray(), context.DecodeParms);
    }

    private static int ReadBits(byte[] data, int bitPos, int numBits)
    {
        int result = 0;
        for (int i = 0; i < numBits; i++)
        {
            int byteIdx = (bitPos + i) / 8;
            int bitIdx = 7 - ((bitPos + i) % 8);
            if (byteIdx < data.Length && (data[byteIdx] & (1 << bitIdx)) != 0)
                result |= 1 << (numBits - 1 - i);
        }
        return result;
    }
}
