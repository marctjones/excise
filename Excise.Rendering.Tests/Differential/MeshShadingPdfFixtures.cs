namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Hand-built minimal PDFs exercising ShadingType 5 (lattice-form triangle
/// mesh) and ShadingType 7 (tensor-product patch mesh) as PatternType 2
/// shading-pattern fills — the exact dispatch path #633 found unwired
/// (SkiaRenderer.Patterns.cs' RenderFillPattern routed 1-4 and 6, never 5 or
/// 7, even though the direct `sh`-operator path already handled all seven).
///
/// Bit layouts use BitsPerCoordinate=8/BitsPerComponent=8(/BitsPerFlag=8 for
/// type 7) throughout so every field lands on a byte boundary — deliberately
/// avoids exercising MeshBitReader's sub-byte bit-packing, since that's
/// already covered elsewhere; this fixture exists to prove the dispatch
/// wiring, not the bit reader.
/// </summary>
internal static class MeshShadingPdfFixtures
{
    /// <summary>
    /// A 100x100 page filled via `/CS0 cs /P0 scn 0 0 100 100 re f` where P0
    /// is a PatternType 2 pattern whose Shading is the given dictionary/
    /// stream body. <paramref name="shadingDictExtra"/> is the shading
    /// dictionary's inner entries (no surrounding &lt;&lt; &gt;&gt;);
    /// <paramref name="shadingStreamBytes"/> is the raw (already-packed)
    /// stream body, or null for a plain (non-stream) shading dictionary.
    /// </summary>
    public static byte[] CreateShadingPatternFillPdf(string shadingDictExtra, byte[]? shadingStreamBytes)
    {
        using var ms = new MemoryStream();
        var offsets = new long[7];

        void WriteAscii(string value)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }
        void WriteLine(string value) => WriteAscii(value + "\n");

        WriteLine("%PDF-1.4");

        offsets[1] = ms.Position;
        WriteLine("1 0 obj");
        WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        WriteLine("endobj");

        offsets[2] = ms.Position;
        WriteLine("2 0 obj");
        WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteLine("endobj");

        offsets[3] = ms.Position;
        WriteLine("3 0 obj");
        WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R "
            + "/Resources << /Pattern << /P0 5 0 R >> /ColorSpace << /CS0 [/Pattern /DeviceRGB] >> >> >>");
        WriteLine("endobj");

        const string content = "/CS0 cs /P0 scn 0 0 100 100 re f";
        offsets[4] = ms.Position;
        WriteLine("4 0 obj");
        WriteLine($"<< /Length {content.Length} >>");
        WriteLine("stream");
        WriteLine(content);
        WriteLine("endstream");
        WriteLine("endobj");

        offsets[5] = ms.Position;
        WriteLine("5 0 obj");
        WriteLine("<< /Type /Pattern /PatternType 2 /Shading 6 0 R >>");
        WriteLine("endobj");

        offsets[6] = ms.Position;
        WriteLine("6 0 obj");
        if (shadingStreamBytes == null)
        {
            WriteLine($"<< {shadingDictExtra} >>");
        }
        else
        {
            WriteLine($"<< {shadingDictExtra} /Length {shadingStreamBytes.Length} >>");
            WriteLine("stream");
            ms.Write(shadingStreamBytes, 0, shadingStreamBytes.Length);
            WriteLine("");
            WriteLine("endstream");
        }
        WriteLine("endobj");

        var xrefPos = ms.Position;
        WriteLine("xref");
        WriteLine("0 7");
        WriteLine("0000000000 65535 f ");
        for (var i = 1; i <= 6; i++)
            WriteLine($"{offsets[i]:D10} 00000 n ");

        WriteLine("trailer");
        WriteLine("<< /Root 1 0 R /Size 7 >>");
        WriteLine("startxref");
        WriteLine(xrefPos.ToString());
        WriteLine("%%EOF");

        return ms.ToArray();
    }

    /// <summary>
    /// A 100x100 page that paints directly via `/Sh1 sh` (no pattern, no
    /// fill path) against a /Shading resource — used for the `sh`-operator
    /// unsupported-type diagnostic test.
    /// </summary>
    public static byte[] CreateDirectShadingPdf(string shadingDictExtra)
    {
        using var ms = new MemoryStream();
        var offsets = new long[6];

        void WriteAscii(string value)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }
        void WriteLine(string value) => WriteAscii(value + "\n");

        WriteLine("%PDF-1.4");

        offsets[1] = ms.Position;
        WriteLine("1 0 obj");
        WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        WriteLine("endobj");

        offsets[2] = ms.Position;
        WriteLine("2 0 obj");
        WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteLine("endobj");

        offsets[3] = ms.Position;
        WriteLine("3 0 obj");
        WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R "
            + "/Resources << /Shading << /Sh1 5 0 R >> >> >>");
        WriteLine("endobj");

        const string content = "/Sh1 sh";
        offsets[4] = ms.Position;
        WriteLine("4 0 obj");
        WriteLine($"<< /Length {content.Length} >>");
        WriteLine("stream");
        WriteLine(content);
        WriteLine("endstream");
        WriteLine("endobj");

        offsets[5] = ms.Position;
        WriteLine("5 0 obj");
        WriteLine($"<< {shadingDictExtra} >>");
        WriteLine("endobj");

        var xrefPos = ms.Position;
        WriteLine("xref");
        WriteLine("0 6");
        WriteLine("0000000000 65535 f ");
        for (var i = 1; i <= 5; i++)
            WriteLine($"{offsets[i]:D10} 00000 n ");

        WriteLine("trailer");
        WriteLine("<< /Root 1 0 R /Size 6 >>");
        WriteLine("startxref");
        WriteLine(xrefPos.ToString());
        WriteLine("%%EOF");

        return ms.ToArray();
    }

    /// <summary>ShadingType 5 dictionary entries (excluding the enclosing &lt;&lt; &gt;&gt;).</summary>
    public const string Type5ShadingDictExtra =
        "/ShadingType 5 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 "
        + "/Decode [0 100 0 100 0 1 0 1 0 1] /VerticesPerRow 2";

    /// <summary>
    /// 2x2 lattice mesh (VerticesPerRow=2) covering the full [0,100]x[0,100]
    /// page with red/green/blue/yellow corners. All-byte-aligned: 5 bytes
    /// per vertex (x, y, r, g, b), 4 vertices, 20 bytes total.
    /// </summary>
    public static byte[] BuildType5MeshBytes() => new byte[]
    {
        // V00 (0,0) red
        0, 0, 255, 0, 0,
        // V01 (100,0) green
        255, 0, 0, 255, 0,
        // V10 (0,100) blue
        0, 255, 0, 0, 255,
        // V11 (100,100) yellow
        255, 255, 255, 255, 0,
    };

    /// <summary>ShadingType 7 dictionary entries (excluding the enclosing &lt;&lt; &gt;&gt;).</summary>
    public const string Type7ShadingDictExtra =
        "/ShadingType 7 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 "
        + "/BitsPerFlag 8 /Decode [0 100 0 100 0 1 0 1 0 1]";

    /// <summary>
    /// A single flag=0 tensor patch: 1 flag byte + 16 points (32 bytes) +
    /// 4 colors (12 bytes) = 45 bytes, all byte-aligned. Points form a 4x4
    /// grid spanning [0,100]x[0,100] — DecodeType6MeshPatches' canonical
    /// reordering permutes these 16 finite values, so the exact PDF-spec
    /// point-traversal order doesn't matter for proving "ink gets drawn":
    /// any permutation of 16 in-bounds points still yields a bounded,
    /// finite tensor-product Bezier patch.
    /// </summary>
    public static byte[] BuildType7TensorPatchBytes()
    {
        var bytes = new List<byte> { 0 }; // flag = 0

        byte[] coords = { 0, 85, 170, 255 };
        foreach (var y in coords)
        foreach (var x in coords)
        {
            bytes.Add(x);
            bytes.Add(y);
        }

        // 4 corner colors: red, green, blue, yellow.
        byte[][] colors =
        {
            new byte[] { 255, 0, 0 },
            new byte[] { 0, 255, 0 },
            new byte[] { 0, 0, 255 },
            new byte[] { 255, 255, 0 },
        };
        foreach (var c in colors)
            bytes.AddRange(c);

        return bytes.ToArray();
    }
}
