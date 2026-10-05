using Excise.Rendering.Fonts;
using SkiaSharp;

namespace Excise.Rendering;

internal partial class RenderContext
{
    // Keep detailed byte lengths only where the old path required them; no new hot-path tuple allocation (#1964).
    private static int[] DecodeCidRun(byte[] bytes, ResolvedRenderFont currentFont,
        out IReadOnlyList<(int Code, int Cid, int ByteLength)>? decoded)
    {
        // DecodeDetailed keeps the per-code byte length, which vertical
        // spacing needs: word spacing fires only on the SINGLE-byte code 32
        // (§9.3.3), never on a 2-byte <0020>. The horizontal identity path
        // keeps the flat int[] decode — it's the hot path for all CJK text
        // and the detailed tuples measurably regress per-page allocations.
        decoded = null;
        int[] cids;
        if (currentFont.CidEncodingCMap != null)
        {
            decoded = currentFont.CidEncodingCMap.DecodeDetailed(bytes);
            cids = new int[decoded.Count];
            for (int i = 0; i < decoded.Count; i++)
                cids[i] = decoded[i].Cid;
        }
        else if (currentFont.CidIsVertical)
        {
            decoded = DecodeIdentityCidBytesDetailed(bytes);
            cids = new int[decoded.Count];
            for (int i = 0; i < decoded.Count; i++)
                cids[i] = decoded[i].Cid;
        }
        else
        {
            cids = DecodeIdentityCidBytes(bytes);
        }
        return cids;
    }

    // Existing precedence, not a new glyph-selection policy. Explicit GID 0 and map fallbacks are retained.
    private static ushort ResolveCidGlyphId(int cid, ResolvedRenderFont currentFont, SKFont font)
    {
        ushort gid;
        if (currentFont.CidToGidMap != null && cid >= 0 && cid < currentFont.CidToGidMap.Length)
        {
            // In-range entries of a /CIDToGIDMap stream define the
            // mapping (§9.7.4.2) — including explicit 0 (.notdef).
            // A CID BEYOND the stream's extent falls through to the
            // identity fallback below: that is the unanimous reference
            // behavior (mutool, poppler AND Ghostscript all render an
            // out-of-range CID as GID == CID — verified empirically on
            // the CidGlyphSelectionMatrixTests truncated-map fixture,
            // where all three draw the real glyph, not .notdef). #515
            gid = currentFont.CidToGidMap[cid];
        }
        else if (currentFont.CffCidToGlyph != null)
        {
            // CID-keyed CFF: the embedded charset DEFINES the mapping.
            // A CID absent from the charset has no glyph — .notdef, as
            // FreeType-based references resolve it. Identity fall-through
            // would index the CFF's glyph order with a CID from an
            // unrelated space and draw an arbitrary wrong glyph. #515
            gid = currentFont.CffCidToGlyph.TryGetValue(cid, out var cffGid)
                ? (ushort)cffGid
                : (ushort)0;
        }
        else if (currentFont.CidUseUnicodeCmap)
            gid = (ushort)(font.GetGlyph(cid) is var unicodeGid && unicodeGid != 0 ? unicodeGid : cid);
        else
            gid = ToGlyphId(cid);
        return gid;
    }
}
