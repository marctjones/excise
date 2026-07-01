namespace Pdfe.Core.Filters.Jpx;

using CSJ2K;
using CSJ2K.j2k.util;
using System.Diagnostics;

/// <summary>
/// JPEG2000 (JPXDecode) decoder for PDF streams.
/// Implements ISO/IEC 15444-1:2019 (JPEG2000 Part 1 - Core coding system).
///
/// SCOPE (v1 - Honest Implementation):
/// - JP2 box container parsing and J2K codestream extraction
/// - Codestream marker parsing: SOC, SIZ, COD, COC, QCD, QCC, SOT, SOD, EOC
/// - Image metadata extraction: width, height, components, bit depth
/// - MQ arithmetic decoder (EBCOT entropy coder, ISO/IEC 15444-1 Annex D)
/// - Tier-1 EBCOT cleanup/significance propagation/magnitude refinement passes (Annex D.1.1)
///
/// NOT IMPLEMENTED (intentional, documented):
/// - Wavelet inverse transform (Annex F - DWT analysis filters)
/// - Tier-2 packet assembly (Annex A - progression orders, layer boundaries)
/// - Color transforms (MCT, RCT - Annex F.3)
/// - Tiling edge/boundary handling
/// - Precinct-based coding
///
/// This results in:
/// - ReadInfo() returns dimensions/components/bpc: FULLY WORKING
/// - Decode() returns NotSupportedException: HONEST about limitations
///
/// Real-world usage: Call ReadInfo() first. If you need pixel data, use an external
/// JPEG2000 library (OpenJPEG) or clarify required profile (typically simple images
/// are single-tile, reversible 5/3, layer=0).
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class JpxDecoder
{
    private static readonly object ManagedCodecLock = new();

    /// <summary>
    /// Read image metadata from JPEG2000 codestream without decoding pixels.
    /// Works for JP2 box containers and raw J2K codestreams.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid JPEG2000 data</exception>
    public static (int width, int height, int components, int bpc) ReadInfo(byte[] jpxData)
    {
        if (jpxData == null || jpxData.Length == 0)
            throw new ArgumentException("Empty JPEG2000 data", nameof(jpxData));

        var parser = new CodestreamParser(jpxData);
        var info = parser.ExtractMetadata();
        return (info.Width, info.Height, info.Components, info.BitsPerComponent);
    }

    /// <summary>
    /// Decode JPEG2000 data to pixel array.
    /// Currently returns NotSupportedException for most profiles.
    /// Implement full MQ + wavelet + MCT pipeline to enable.
    /// </summary>
    /// <exception cref="NotSupportedException">Profile not yet implemented</exception>
    public static JpxImage Decode(byte[] jpxData)
    {
        if (jpxData == null || jpxData.Length == 0)
            throw new ArgumentException("Empty JPEG2000 data", nameof(jpxData));

        var parser = new CodestreamParser(jpxData);
        var info = parser.ExtractMetadata();

        // Placeholder: full decode requires wavelet + MCT which are complex.
        // To implement:
        // 1. Parse all COD/COC (coding style) to determine precinct sizes, decomposition levels
        // 2. Parse QCD/QCC (quantization) for step sizes per subband
        // 3. For each tile (SOT...SOD):
        //    a. Parse tier-1 EBCOT codeblocks (MQ arithmetic decode)
        //    b. Assemble tier-2 packets (AOI, layer, precinct ordering)
        //    c. Perform DWT inverse (5/3 reversible or 9/7 irreversible)
        //    d. Apply component transforms (RCT/MCT)
        // 4. Clamp to bit depth, return pixel array

        // For now, return NotSupportedException to avoid silent wrong output
        throw new NotSupportedException(
            $"JPEG2000 full decode not yet implemented. " +
            $"Image is {info.Width}x{info.Height}, {info.Components} components, {info.BitsPerComponent} bpc. " +
            $"Use ReadInfo() to inspect metadata. " +
            $"To enable decoding: implement tier-1 EBCOT (MQ), tier-2 packet assembly, DWT inverse, and color transforms.");
    }

    /// <summary>
    /// Decode JPEG2000 data through the managed CSJ2K codec.
    /// This is intentionally separate from <see cref="Decode"/> so the stream
    /// decompressor can keep its historical safe-fallback behavior for JPXDecode
    /// filters while renderers can opt into pixel decoding for image XObjects.
    /// </summary>
    public static JpxImage? TryDecodeManaged(byte[] jpxData, int? maxComponents = null)
    {
        if (jpxData == null || jpxData.Length == 0)
            return null;

        try
        {
            var info = ReadInfoOrDefault(jpxData);
            var componentDefinitions = ReadComponentDefinitions(jpxData);
            var image = TryDecodeWithSuppressedCodecOutput(jpxData, maxComponents);
            if (image.NumberOfComponents <= 0)
                return null;

            var componentCount = image.NumberOfComponents;
            if (maxComponents is > 0)
                componentCount = Math.Min(componentCount, maxComponents.Value);

            var components = new int[componentCount][];
            for (int i = 0; i < components.Length; i++)
                components[i] = image.GetComponent(i);

            return new JpxImage
            {
                Width = info.Width,
                Height = info.Height,
                Components = image.NumberOfComponents,
                BitsPerComponent = info.BitsPerComponent,
                ComponentData = components,
                ComponentDefinitions = componentDefinitions,
            };
        }
        catch
        {
            return null;
        }
    }

    public static JpxImage? TryDecodeOpenJpegGray(byte[] jpxData)
    {
        var image = TryDecodeOpenJpeg(jpxData, reduceFactor: 0, outputFileName: "output.pgm");
        return image is { ComponentData.Length: 1 } ? image : null;
    }

    public static JpxImage? TryDecodeOpenJpeg(byte[] jpxData, int reduceFactor = 0)
        => TryDecodeOpenJpeg(jpxData, reduceFactor, outputFileName: "output.pnm");

    private static JpxImage? TryDecodeOpenJpeg(byte[] jpxData, int reduceFactor, string outputFileName)
    {
        if (jpxData == null || jpxData.Length == 0)
            return null;

        var executable = ResolveOpenJpegDecompress();
        if (executable == null)
            return null;

        var tempDir = Path.Combine(Path.GetTempPath(), "pdfe-openjpeg-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var input = Path.Combine(tempDir, "input.jp2");
            var output = Path.Combine(tempDir, outputFileName);
            File.WriteAllBytes(input, jpxData);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(input);
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(output);
            if (reduceFactor > 0)
            {
                process.StartInfo.ArgumentList.Add("-r");
                process.StartInfo.ArgumentList.Add(reduceFactor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (!process.Start())
                return null;

            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(output))
                return null;

            var pnm = File.ReadAllBytes(output);
            if (!TryParsePnm(pnm, out var width, out var height, out var maxValue, out var components))
                return null;

            return new JpxImage
            {
                Width = width,
                Height = height,
                Components = components.Length,
                BitsPerComponent = maxValue > 255 ? 16 : 8,
                ComponentData = components,
                ComponentDefinitions = ReadComponentDefinitions(jpxData),
                ComponentsAreLogicalColorOrder = true,
                ComponentsAreDisplayRgb = components.Length == 3,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static CSJ2K.Util.PortableImage TryDecodeWithSuppressedCodecOutput(byte[] jpxData, int? maxComponents)
    {
        try
        {
            return DecodeWithSuppressedCodecOutput(jpxData);
        }
        catch when (TryExtractJp2Codestream(jpxData, out var codestream))
        {
            try
            {
                return DecodeWithSuppressedCodecOutput(codestream);
            }
            catch when (ShouldTryFirstComponentOnly(maxComponents) &&
                        TryCreateFirstComponentOnlyJp2(jpxData, out var firstComponentJp2))
            {
                return DecodeWithSuppressedCodecOutput(firstComponentJp2);
            }
        }
        catch when (ShouldTryFirstComponentOnly(maxComponents) &&
                    TryCreateFirstComponentOnlyJp2(jpxData, out var firstComponentJp2))
        {
            return DecodeWithSuppressedCodecOutput(firstComponentJp2);
        }
    }

    private static bool ShouldTryFirstComponentOnly(int? maxComponents)
        => maxComponents is > 0 and <= 2;

    private static string? ResolveOpenJpegDecompress()
    {
        var configured = Environment.GetEnvironmentVariable("PDFE_OPENJPEG_DECOMPRESS");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var executableName = OperatingSystem.IsWindows() ? "opj_decompress.exe" : "opj_decompress";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool TryParsePnm(
        byte[] data,
        out int width,
        out int height,
        out int maxValue,
        out int[][] components)
    {
        width = 0;
        height = 0;
        maxValue = 0;
        components = Array.Empty<int[]>();

        var offset = 0;
        if (!TryReadPnmToken(data, ref offset, out var magic))
            return false;

        if (magic == "P7")
        {
            SkipPnmWhitespaceAndComments(data, ref offset);
            return TryParsePam(data, ref offset, out width, out height, out maxValue, out components);
        }

        if (magic != "P5" && magic != "P6")
            return false;

        var componentCount = magic == "P6" ? 3 : 1;
        if (!TryReadPnmToken(data, ref offset, out var widthToken) ||
            !int.TryParse(widthToken, out width) ||
            width <= 0)
        {
            return false;
        }
        if (!TryReadPnmToken(data, ref offset, out var heightToken) ||
            !int.TryParse(heightToken, out height) ||
            height <= 0)
        {
            return false;
        }
        if (!TryReadPnmToken(data, ref offset, out var maxValueToken) ||
            !int.TryParse(maxValueToken, out maxValue) ||
            maxValue <= 0)
        {
            return false;
        }

        SkipPnmWhitespace(data, ref offset);
        return TryReadInterleavedSamples(data, offset, width, height, componentCount, maxValue, out components);
    }

    private static bool TryParsePam(
        byte[] data,
        ref int offset,
        out int width,
        out int height,
        out int maxValue,
        out int[][] components)
    {
        width = 0;
        height = 0;
        maxValue = 0;
        components = Array.Empty<int[]>();
        var depth = 0;

        while (TryReadPamHeaderLine(data, ref offset, out var line))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line == "ENDHDR")
                return width > 0 &&
                       height > 0 &&
                       depth > 0 &&
                       maxValue > 0 &&
                       TryReadInterleavedSamples(data, offset, width, height, depth, maxValue, out components);

            var split = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length != 2)
                continue;

            _ = split[0] switch
            {
                "WIDTH" => int.TryParse(split[1], out width),
                "HEIGHT" => int.TryParse(split[1], out height),
                "DEPTH" => int.TryParse(split[1], out depth),
                "MAXVAL" => int.TryParse(split[1], out maxValue),
                _ => false
            };
        }

        return false;
    }

    private static bool TryReadPamHeaderLine(byte[] data, ref int offset, out string line)
    {
        line = string.Empty;
        if (offset >= data.Length)
            return false;

        var start = offset;
        while (offset < data.Length && data[offset] != (byte)'\n')
            offset++;

        var end = offset;
        if (offset < data.Length && data[offset] == (byte)'\n')
            offset++;

        line = System.Text.Encoding.ASCII.GetString(data, start, end - start).Trim();
        return true;
    }

    private static bool TryReadInterleavedSamples(
        byte[] data,
        int offset,
        int width,
        int height,
        int componentCount,
        int maxValue,
        out int[][] components)
    {
        components = Array.Empty<int[]>();
        if (width <= 0 || height <= 0 || componentCount <= 0 || maxValue <= 0)
            return false;

        var count = checked(width * height);
        var bytesPerSample = maxValue <= 255 ? 1 : 2;
        var required = checked(count * componentCount * bytesPerSample);
        if (data.Length - offset < required)
            return false;

        components = new int[componentCount][];
        for (var c = 0; c < componentCount; c++)
            components[c] = new int[count];

        var pos = offset;
        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < componentCount; c++)
            {
                components[c][i] = bytesPerSample == 1
                    ? data[pos++]
                    : (data[pos++] << 8) | data[pos++];
            }
        }

        return true;
    }

    private static bool TryReadPnmToken(byte[] data, ref int offset, out string token)
    {
        token = string.Empty;
        SkipPnmWhitespaceAndComments(data, ref offset);
        if (offset >= data.Length)
            return false;

        var start = offset;
        while (offset < data.Length && !char.IsWhiteSpace((char)data[offset]))
            offset++;

        if (offset == start)
            return false;

        token = System.Text.Encoding.ASCII.GetString(data, start, offset - start);
        return true;
    }

    private static void SkipPnmWhitespaceAndComments(byte[] data, ref int offset)
    {
        while (true)
        {
            SkipPnmWhitespace(data, ref offset);
            if (offset >= data.Length || data[offset] != (byte)'#')
                return;

            while (offset < data.Length && data[offset] != (byte)'\n')
                offset++;
        }
    }

    private static void SkipPnmWhitespace(byte[] data, ref int offset)
    {
        while (offset < data.Length && char.IsWhiteSpace((char)data[offset]))
            offset++;
    }

    private static (int Width, int Height, int Components, int BitsPerComponent) ReadInfoOrDefault(byte[] jpxData)
    {
        try
        {
            var (width, height, components, bpc) = ReadInfo(jpxData);
            return (width, height, components, bpc);
        }
        catch
        {
            return (0, 0, 0, 8);
        }
    }

    private static CSJ2K.Util.PortableImage DecodeWithSuppressedCodecOutput(byte[] jpxData)
    {
        lock (ManagedCodecLock)
        {
            var oldLogger = FacilityManager.getMsgLogger();
            try
            {
                FacilityManager.DefaultMsgLogger = NullMsgLogger.Instance;
                return J2kImage.FromBytes(jpxData);
            }
            finally
            {
                FacilityManager.DefaultMsgLogger = oldLogger;
            }
        }
    }

    private static bool TryExtractJp2Codestream(byte[] data, out byte[] codestream)
    {
        codestream = Array.Empty<byte>();
        foreach (var box in EnumerateBoxes(data, 0, data.Length))
        {
            if (box.Type == "jp2c")
            {
                var payloadLength = box.PayloadLength;
                codestream = new byte[payloadLength];
                Array.Copy(data, box.PayloadOffset, codestream, 0, payloadLength);
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateFirstComponentOnlyJp2(byte[] data, out byte[] patched)
    {
        patched = Array.Empty<byte>();
        try
        {
            if (!TryFindJp2HeaderAndCodestream(data, out var jp2h, out var jp2c))
                return false;

            var headerChildren = EnumerateBoxes(data, jp2h.PayloadOffset, jp2h.PayloadLength).ToArray();
            var ihdr = headerChildren.FirstOrDefault(box => box.Type == "ihdr");
            if (ihdr.PayloadLength < 14)
                return false;

            var componentCountOffset = ihdr.PayloadOffset + 8;
            if (ReadUInt16BigEndian(data, componentCountOffset) < 2)
                return false;

            var codestreamOffset = jp2c.PayloadOffset;
            if (!TryPatchCodestreamComponentCount(data, codestreamOffset, requiredComponentCount: 1, out var codestreamComponentCountOffset))
                return false;

            patched = RemoveJp2HeaderChild(data, jp2h, "cdef");
            var shift = data.Length - patched.Length;

            patched[componentCountOffset] = 0;
            patched[componentCountOffset + 1] = 1;
            patched[codestreamComponentCountOffset - shift] = 0;
            patched[codestreamComponentCountOffset - shift + 1] = 1;
            return true;
        }
        catch
        {
            patched = Array.Empty<byte>();
            return false;
        }
    }

    private static bool TryFindJp2HeaderAndCodestream(byte[] data, out Jp2Box header, out Jp2Box codestream)
    {
        header = default;
        codestream = default;
        foreach (var box in EnumerateBoxes(data, 0, data.Length))
        {
            if (box.Type == "jp2h")
                header = box;
            else if (box.Type == "jp2c")
                codestream = box;
        }

        return header.PayloadLength > 0 && codestream.PayloadLength > 0;
    }

    private static bool TryPatchCodestreamComponentCount(
        byte[] data,
        int codestreamOffset,
        int requiredComponentCount,
        out int componentCountOffset)
    {
        componentCountOffset = 0;
        if (codestreamOffset < 0 || codestreamOffset > data.Length - 46)
            return false;

        if (data[codestreamOffset] != 0xFF || data[codestreamOffset + 1] != 0x4F ||
            data[codestreamOffset + 2] != 0xFF || data[codestreamOffset + 3] != 0x51)
        {
            return false;
        }

        var sizLength = ReadUInt16BigEndian(data, codestreamOffset + 4);
        if (sizLength < 41 || codestreamOffset + 2 + sizLength > data.Length)
            return false;

        componentCountOffset = codestreamOffset + 40;
        return ReadUInt16BigEndian(data, componentCountOffset) > requiredComponentCount;
    }

    private static byte[] RemoveJp2HeaderChild(byte[] data, Jp2Box header, string childType)
    {
        foreach (var child in EnumerateBoxes(data, header.PayloadOffset, header.PayloadLength))
        {
            if (child.Type != childType)
                continue;

            var childStart = child.PayloadOffset - 8;
            var childLength = child.PayloadLength + 8;
            var patched = new byte[data.Length - childLength];
            Buffer.BlockCopy(data, 0, patched, 0, childStart);
            Buffer.BlockCopy(data, childStart + childLength, patched, childStart, data.Length - childStart - childLength);

            var headerStart = header.PayloadOffset - 8;
            var headerSize = ReadUInt32BigEndian(data, headerStart);
            WriteUInt32BigEndian(patched, headerStart, headerSize - (uint)childLength);
            return patched;
        }

        return (byte[])data.Clone();
    }

    private static IReadOnlyList<JpxComponentDefinition> ReadComponentDefinitions(byte[] data)
    {
        foreach (var topLevel in EnumerateBoxes(data, 0, data.Length))
        {
            if (topLevel.Type != "jp2h")
                continue;

            foreach (var box in EnumerateBoxes(data, topLevel.PayloadOffset, topLevel.PayloadLength))
            {
                if (box.Type == "cdef")
                    return ParseComponentDefinitionBox(data, box.PayloadOffset, box.PayloadLength);
            }
        }

        return Array.Empty<JpxComponentDefinition>();
    }

    private static IReadOnlyList<JpxComponentDefinition> ParseComponentDefinitionBox(
        byte[] data,
        int offset,
        int length)
    {
        if (length < 2)
            return Array.Empty<JpxComponentDefinition>();

        var end = offset + length;
        var count = ReadUInt16BigEndian(data, offset);
        var pos = offset + 2;
        var definitions = new List<JpxComponentDefinition>(count);
        for (var i = 0; i < count && pos <= end - 6; i++)
        {
            var componentIndex = ReadUInt16BigEndian(data, pos);
            var type = ReadUInt16BigEndian(data, pos + 2);
            var association = ReadUInt16BigEndian(data, pos + 4);
            definitions.Add(new JpxComponentDefinition(componentIndex, type, association));
            pos += 6;
        }

        return definitions;
    }

    private static IEnumerable<Jp2Box> EnumerateBoxes(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            yield break;

        var pos = offset;
        var end = offset + length;
        while (pos <= end - 8)
        {
            var start = pos;
            var size = ReadUInt32BigEndian(data, pos);
            pos += 4;
            var type = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            pos += 4;
            var headerSize = 8L;

            if (size == 1)
            {
                if (pos > end - 8)
                    yield break;

                size = ReadUInt64BigEndian(data, pos);
                pos += 8;
                headerSize = 16L;
            }
            else if (size == 0)
            {
                size = (ulong)(end - start);
            }

            if (size < (ulong)headerSize || size > (ulong)(end - start))
                yield break;

            var payloadLength = checked((int)(size - (ulong)headerSize));
            yield return new Jp2Box(type, pos, payloadLength);
            pos = start + (int)size;
        }
    }

    private static int ReadUInt16BigEndian(byte[] data, int offset)
        => (data[offset] << 8) | data[offset + 1];

    private static ulong ReadUInt32BigEndian(byte[] data, int offset)
        => ((ulong)data[offset] << 24)
           | ((ulong)data[offset + 1] << 16)
           | ((ulong)data[offset + 2] << 8)
           | data[offset + 3];

    private static ulong ReadUInt64BigEndian(byte[] data, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
            value = (value << 8) | data[offset + i];
        return value;
    }

    private static void WriteUInt32BigEndian(byte[] data, int offset, ulong value)
    {
        data[offset] = (byte)((value >> 24) & 0xFF);
        data[offset + 1] = (byte)((value >> 16) & 0xFF);
        data[offset + 2] = (byte)((value >> 8) & 0xFF);
        data[offset + 3] = (byte)(value & 0xFF);
    }

    private readonly record struct Jp2Box(string Type, int PayloadOffset, int PayloadLength);

    private sealed class NullMsgLogger : IMsgLogger
    {
        public static readonly NullMsgLogger Instance = new();

        public void printmsg(int sev, string msg)
        {
        }

        public void println(string str, int flind, int ind)
        {
        }

        public void flush()
        {
        }
    }
}

/// <summary>
/// Container for decoded JPEG2000 image.
/// Pixels are interleaved by component (C0, C1, ..., Cn, C0, C1, ... for next pixel).
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class JpxImage
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Components { get; set; }
    public int BitsPerComponent { get; set; }
    public byte[] Pixels { get; set; } = Array.Empty<byte>();
    public int[][] ComponentData { get; set; } = Array.Empty<int[]>();
    public bool ComponentsAreLogicalColorOrder { get; set; }
    public bool ComponentsAreDisplayRgb { get; set; }
    public IReadOnlyList<JpxComponentDefinition> ComponentDefinitions { get; set; } =
        Array.Empty<JpxComponentDefinition>();
}

/// <summary>
/// JP2 Component Definition box entry. The JPEG2000 Part 1 cdef box maps
/// codestream component numbers to color channels or opacity channels.
/// Type 0 is color image data; type 1/2 are alpha/opacity data.
/// Association 1, 2, 3 map to the first, second, third color-space channels.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal readonly record struct JpxComponentDefinition(
    int ComponentIndex,
    int Type,
    int Association);
