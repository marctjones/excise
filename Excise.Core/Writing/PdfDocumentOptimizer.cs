using System.IO.Compression;
using System.Security.Cryptography;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Security;

namespace Excise.Core.Writing;

/// <summary>
/// Reduce File Size (#1550): rewrite a document so it takes fewer bytes.
/// </summary>
/// <remarks>
/// <para><b>What it does.</b> Every preset: drop page thumbnails and other
/// applications' private data, point byte-identical images, form XObjects and
/// font programs at one copy, and re-encode uncompressed or weakly encoded
/// streams with the strongest Flate level. The writer then drops everything
/// unreachable, as it does on every save, and writes for size
/// (<see cref="PdfDocumentWriter.OptimizeForSize"/>): the form-field, font and
/// Info dictionaries an ordinary save keeps greppable go into object streams,
/// object streams are larger, and the cross-reference stream is predicted.
/// The lossy presets also downsample images whose effective resolution at
/// their largest placed size is well above the preset's target.</para>
///
/// <para><b>Why it cannot undo a redaction.</b> The optimizer works on a copy
/// opened from the bytes an ordinary save writes
/// (<see cref="SaveOptimizedCopy"/>), so it never sees the live document's
/// object cache or the source file's unreachable objects. Every pass only
/// re-encodes or merges bytes that are already in that copy; nothing is read
/// back from the original file.</para>
///
/// <para><b>What it does not do.</b> Subset or merge already-embedded fonts,
/// touch inline images, or resample images whose placement it cannot measure
/// (anything drawn from inside a form XObject, pattern or annotation
/// appearance). Those images are left as they are.</para>
/// </remarks>
public static class PdfDocumentOptimizer
{
    private static readonly HashSet<string> GenericFilters = new(StringComparer.Ordinal)
    {
        "FlateDecode", "Fl",
        "LZWDecode", "LZW",
        "ASCIIHexDecode", "AHx",
        "ASCII85Decode", "A85",
        "RunLengthDecode", "RL",
    };

    private static readonly HashSet<string> FontProgramKeys = new(StringComparer.Ordinal)
    {
        "FontFile", "FontFile2", "FontFile3",
    };

    /// <summary>
    /// Open <paramref name="savedPdf"/> — the bytes of an ordinary save of the
    /// document — optimize that copy, and write it to
    /// <paramref name="outputPath"/>. The file is written to a temporary name
    /// first and moved into place, so a failure never leaves a partial file.
    /// </summary>
    /// <param name="savedPdf">A plaintext save of the source document
    /// (<c>PdfDocument.SaveToBytes()</c>).</param>
    /// <param name="outputPath">Where the optimized copy goes.</param>
    /// <param name="options">What to do.</param>
    /// <param name="encryptionOptions">Encryption for the written file —
    /// pass the source's <c>GetReEncryptionOptions(...)</c> so an encrypted
    /// input stays encrypted (#643).</param>
    /// <param name="cancellationToken">Cancels between objects.</param>
    public static PdfOptimizationResult SaveOptimizedCopy(
        byte[] savedPdf,
        string outputPath,
        PdfOptimizationOptions options,
        PdfEncryptionOptions? encryptionOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savedPdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        var fullPath = Path.GetFullPath(outputPath);
        using var copy = PdfDocument.Open(savedPdf);
        var result = Optimize(copy, options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory ?? string.Empty,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                new PdfDocumentWriter(copy, encryptionOptions) { OptimizeForSize = true }.Write(file);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        return result with { OutputSizeBytes = new FileInfo(fullPath).Length };
    }

    /// <summary>
    /// Optimize <paramref name="document"/> in place. The caller saves it.
    /// </summary>
    /// <remarks>
    /// Run this on a copy opened from saved bytes, never on a document the
    /// user is editing: it rewrites image streams and repoints references.
    /// <see cref="SaveOptimizedCopy"/> does that for you.
    /// </remarks>
    public static PdfOptimizationResult Optimize(
        PdfDocument document,
        PdfOptimizationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var thumbnails = options.RemoveThumbnails ? RemoveThumbnails(document) : 0;
        var privateData = options.RemovePrivateApplicationData ? RemovePrivateData(document) : 0;
        cancellationToken.ThrowIfCancellationRequested();

        var graph = ReferenceGraph.Build(document, cancellationToken);
        var warnings = new List<string>();
        if (graph.HasSignatureDictionary)
        {
            warnings.Add(
                "The document is digitally signed. The optimized copy is a rewritten file, " +
                "so its signatures will no longer validate.");
        }

        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var downsampled = options.TargetImageDpi is { } targetDpi and > 0
            ? DownsampleImages(document, graph, options, targetDpi, skipped, cancellationToken)
            : 0;

        var deduplicated = options.DeduplicateStreams
            ? DeduplicateStreams(graph, cancellationToken)
            : 0;

        var recompressed = options.RecompressStreams
            ? RecompressStreams(graph, cancellationToken)
            : 0;

        return new PdfOptimizationResult
        {
            Preset = options.Preset,
            StreamsRecompressed = recompressed,
            StreamsDeduplicated = deduplicated,
            ThumbnailsRemoved = thumbnails,
            PrivateDataEntriesRemoved = privateData,
            ImagesDownsampled = downsampled,
            ImagesSkipped = skipped,
            Warnings = warnings,
        };
    }

    // ── Non-content data ────────────────────────────────────────────────────

    private static int RemoveThumbnails(PdfDocument document)
    {
        var removed = 0;
        foreach (var page in document.GetPages())
        {
            if (page.Dictionary.Remove("Thumb"))
                removed++;
        }

        return removed;
    }

    private static int RemovePrivateData(PdfDocument document)
    {
        var removed = 0;
        if (document.Catalog.Remove("PieceInfo"))
            removed++;
        foreach (var page in document.GetPages())
        {
            if (page.Dictionary.Remove("PieceInfo"))
                removed++;
        }

        foreach (var objectNumber in document.ComputeReachableObjects())
        {
            if (TryGetObject(document, objectNumber) is PdfStream { } stream
                && stream.GetNameOrNull("Subtype") == "Form"
                && stream.Remove("PieceInfo"))
            {
                removed++;
            }
        }

        return removed;
    }

    // ── Image downsampling ──────────────────────────────────────────────────

    private static int DownsampleImages(
        PdfDocument document,
        ReferenceGraph graph,
        PdfOptimizationOptions options,
        int targetDpi,
        Dictionary<string, int> skipped,
        CancellationToken cancellationToken)
    {
        var placements = MeasurePlacements(document, graph, cancellationToken, out var pageXObjectDicts, out var unmeasurable);
        var downsampled = 0;

        foreach (var (objectNumber, lowestDpi) in placements.OrderBy(p => p.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lowestDpi <= options.ImageDpiThreshold)
                continue;
            if (graph.Objects.GetValueOrDefault(objectNumber) is not PdfStream image)
                continue;

            var reason = unmeasurable.Contains(objectNumber)
                ? "drawn somewhere its size cannot be measured"
                : !graph.AllReferrersAreIn(objectNumber, pageXObjectDicts)
                    ? "also used outside page resources"
                    : TryDownsample(document, image, lowestDpi, targetDpi, options);
            if (reason == null)
                downsampled++;
            else
                skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
        }

        return downsampled;
    }

    /// <summary>
    /// The lowest effective resolution each page-level image is drawn at, by
    /// object number. Reads the CTM the one content-stream walker records on
    /// every <c>Do</c> (<see cref="Content.ContentOperator.GraphicsTransform"/>);
    /// it does not track graphics state itself.
    /// </summary>
    private static Dictionary<int, double> MeasurePlacements(
        PdfDocument document,
        ReferenceGraph graph,
        CancellationToken cancellationToken,
        out HashSet<PdfDictionary> pageXObjectDicts,
        out HashSet<int> unmeasurable)
    {
        var lowest = new Dictionary<int, double>();
        pageXObjectDicts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        unmeasurable = new HashSet<int>();

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xobjects = page.Resources?.ResolveDictionary(document, "XObject");
            if (xobjects == null)
                continue;
            pageXObjectDicts.Add(xobjects);

            IReadOnlyList<Content.ContentOperator> operators;
            try
            {
                operators = page.GetContentStream().Operators;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                MarkAllImages(xobjects, unmeasurable);
                continue;
            }

            // §7.8.3: a form XObject or Type 3 font with no /Resources of its own
            // may draw from the PAGE's resources, at a size this pass cannot see.
            if (PageResourcesMayBeUsedIndirectly(document, page, xobjects))
                MarkAllImages(xobjects, unmeasurable);

            var userUnit = page.Dictionary.GetOptional("UserUnit") is { } unit
                           && unit.TryGetNumber(out var u) && u > 0 ? u : 1.0;

            foreach (var op in operators)
            {
                if (op.Name != "Do" || op.Operands.Count == 0 || op.Operands[0] is not PdfName name)
                    continue;
                if (xobjects.GetOptional(name.Value) is not PdfReference reference)
                    continue;
                if (graph.Objects.GetValueOrDefault(reference.ObjectNum) is not PdfStream stream
                    || stream.GetNameOrNull("Subtype") != "Image")
                    continue;

                var dpi = op.GraphicsTransform is { } ctm
                    ? EffectiveDpi(stream, ctm, userUnit)
                    : null;
                if (dpi is not { } value)
                {
                    unmeasurable.Add(reference.ObjectNum);
                    value = double.PositiveInfinity;
                }

                lowest[reference.ObjectNum] = lowest.TryGetValue(reference.ObjectNum, out var existing)
                    ? Math.Min(existing, value)
                    : value;
            }
        }

        return lowest;
    }

    private static double? EffectiveDpi(PdfStream image, Content.ContentTransform ctm, double userUnit)
    {
        var width = image.GetInt("Width", 0);
        var height = image.GetInt("Height", 0);
        // The unit square's x axis maps to (A, B) and its y axis to (C, D), in
        // user-space units of 1/72 inch × /UserUnit.
        var placedWidth = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B) * userUnit;
        var placedHeight = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D) * userUnit;
        if (width <= 0 || height <= 0 || placedWidth < 1e-6 || placedHeight < 1e-6
            || !double.IsFinite(placedWidth) || !double.IsFinite(placedHeight))
            return null;

        return Math.Min(width * 72.0 / placedWidth, height * 72.0 / placedHeight);
    }

    private static bool PageResourcesMayBeUsedIndirectly(PdfDocument document, PdfPage page, PdfDictionary xobjects)
    {
        foreach (var (_, value) in xobjects)
        {
            if (document.Resolve(value) is PdfStream form
                && form.GetNameOrNull("Subtype") == "Form"
                && !form.ContainsKey("Resources"))
                return true;
        }

        var fonts = page.Resources?.ResolveDictionary(document, "Font");
        if (fonts == null)
            return false;
        foreach (var (_, value) in fonts)
        {
            if (document.Resolve(value) is PdfDictionary font
                && font.GetNameOrNull("Subtype") == "Type3"
                && !font.ContainsKey("Resources"))
                return true;
        }

        return false;
    }

    private static void MarkAllImages(PdfDictionary xobjects, HashSet<int> unmeasurable)
    {
        foreach (var (_, value) in xobjects)
        {
            if (value is PdfReference reference)
                unmeasurable.Add(reference.ObjectNum);
        }
    }

    /// <summary>Downsample one image in place. Returns null on success, else why it was left alone.</summary>
    private static string? TryDownsample(
        PdfDocument document,
        PdfStream image,
        double lowestDpi,
        int targetDpi,
        PdfOptimizationOptions options)
    {
        var width = image.GetInt("Width", 0);
        var height = image.GetInt("Height", 0);
        if (width <= 0 || height <= 0)
            return "invalid dimensions";
        if (image.GetBool("ImageMask"))
            return "stencil mask";
        if (image.GetInt("BitsPerComponent", 0) != 8)
            return "not 8 bits per component";
        if (image.ContainsKey("SMask") || image.ContainsKey("Mask") || image.ContainsKey("SMaskInData"))
            return "has a transparency mask";
        if (image.ContainsKey("Decode"))
            return "custom /Decode array";
        if (image.ContainsKey("F"))
            return "external stream data";

        var componentCount = ComponentCount(document, image.GetOptional("ColorSpace"));
        if (componentCount is not (1 or 3))
            return "unsupported color space";
        var components = componentCount.Value;

        var filters = image.Filters;
        var isJpeg = filters.Count == 1 && filters[0] is "DCTDecode" or "DCT";
        if (!isJpeg && !filters.All(GenericFilters.Contains))
            return $"{string.Join("+", filters)} image";
        if (isJpeg && options.JpegCodec == null)
            return "no JPEG codec available";

        var scale = targetDpi / lowestDpi;
        var newWidth = Math.Max(1, (int)Math.Round(width * scale));
        var newHeight = Math.Max(1, (int)Math.Round(height * scale));
        if (newWidth >= width && newHeight >= height)
            return "already at target resolution";

        byte[]? samples;
        if (isJpeg)
        {
            samples = options.JpegCodec!.Decode(
                image.EncodedData, width, height, components, ReadColorTransform(document, image));
        }
        else if (filters.Count == 0)
        {
            samples = image.EncodedData;
        }
        else
        {
            samples = image.TryEnsureDecoded() ? image.DecodedData : null;
        }

        var expected = (long)width * height * components;
        if (samples == null || samples.LongLength != expected)
            return "image data could not be decoded";

        var resized = ImageResampler.AreaAverage(samples, width, height, components, newWidth, newHeight);
        var quality = Math.Clamp(options.JpegQuality, 1, 100);

        byte[] encoded;
        byte[] decoded;
        string filter;
        var jpeg = options.JpegCodec?.Encode(resized, newWidth, newHeight, components, quality);
        if (isJpeg && jpeg == null)
            return "JPEG encoding failed";

        // A losslessly stored image is often line art or a screenshot, where
        // JPEG ringing is ugly and Flate is already competitive, so for those
        // JPEG must win by at least half to be chosen.
        var flate = isJpeg ? null : Deflate(resized);
        if (jpeg != null && (flate == null || jpeg.LongLength * 2 < flate.LongLength))
        {
            encoded = jpeg;
            decoded = jpeg;
            filter = "DCTDecode";
        }
        else if (flate != null)
        {
            encoded = flate;
            decoded = resized;
            filter = "FlateDecode";
        }
        else
        {
            return "JPEG encoding failed";
        }

        if (encoded.LongLength >= image.EncodedData.LongLength)
            return "re-encoding would not be smaller";

        image.SetInt("Width", newWidth);
        image.SetInt("Height", newHeight);
        image.ReplaceEncoding(encoded, decoded, filter);
        return null;
    }

    private static int? ReadColorTransform(PdfDocument document, PdfStream image)
    {
        var parms = image.GetOptional("DecodeParms") is { } value ? document.Resolve(value) : null;
        if (parms is PdfArray array && array.Count > 0)
            parms = document.Resolve(array[0]);
        return parms is PdfDictionary dict && dict.GetOptional("ColorTransform") is { } ct
               && ct.TryGetNumber(out var number)
            ? (int)number
            : null;
    }

    private static int? ComponentCount(PdfDocument document, PdfObject? colorSpace)
    {
        var resolved = colorSpace == null ? null : document.Resolve(colorSpace);
        switch (resolved)
        {
            case PdfName name:
                return name.Value switch
                {
                    "DeviceGray" or "G" => 1,
                    "DeviceRGB" or "RGB" => 3,
                    _ => null,
                };
            case PdfArray { Count: > 0 } array when array[0] is PdfName family:
                switch (family.Value)
                {
                    case "CalGray":
                        return 1;
                    case "CalRGB":
                        return 3;
                    case "ICCBased" when array.Count > 1
                                         && document.Resolve(array[1]) is PdfDictionary profile
                                         && profile.GetOptional("N") is { } n
                                         && n.TryGetNumber(out var count):
                        return count is 1 or 3 ? (int)count : null;
                    default:
                        return null;
                }
            default:
                return null;
        }
    }

    // ── Deduplication ───────────────────────────────────────────────────────

    private static int DeduplicateStreams(ReferenceGraph graph, CancellationToken cancellationToken)
    {
        var canonical = new Dictionary<string, (int ObjectNumber, string Dictionary, PdfStream Stream)>(StringComparer.Ordinal);
        var merged = 0;

        foreach (var (objectNumber, obj) in graph.Objects.OrderBy(o => o.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj is not PdfStream stream || !IsDeduplicationCandidate(graph, objectNumber, stream))
                continue;

            var dictionary = SerializeDictionaryWithoutLength(stream);
            var key = HashKey(dictionary, stream.EncodedData);
            if (!canonical.TryGetValue(key, out var existing))
            {
                canonical[key] = (objectNumber, dictionary, stream);
                continue;
            }

            // A hash collision must never merge two different streams.
            if (!string.Equals(existing.Dictionary, dictionary, StringComparison.Ordinal)
                || !existing.Stream.EncodedData.AsSpan().SequenceEqual(stream.EncodedData))
                continue;

            if (graph.Repoint(objectNumber, existing.ObjectNumber) > 0)
                merged++;
        }

        return merged;
    }

    private static bool IsDeduplicationCandidate(ReferenceGraph graph, int objectNumber, PdfStream stream)
    {
        if (IsPlumbingOrMetadata(stream) || stream.ContainsKey("F"))
            return false;

        switch (stream.GetNameOrNull("Subtype"))
        {
            case "Image":
                return true;
            case "Form":
                // Only forms drawn through a resource dictionary. An annotation's
                // appearance stream is also a form, and is what a later form fill
                // rewrites; two widgets must not end up sharing one.
                return graph.AllReferrersAreIn(objectNumber, graph.XObjectDictionaries);
            default:
                return graph.AllReferrerKeysAreIn(objectNumber, FontProgramKeys);
        }
    }

    private static string SerializeDictionaryWithoutLength(PdfStream stream)
    {
        var copy = new PdfDictionary();
        foreach (var key in stream.Keys.Select(k => k.Value).Where(k => k != "Length").OrderBy(k => k, StringComparer.Ordinal))
            copy[key] = stream[key];
        return PdfObjectWriter.Serialize(copy);
    }

    private static string HashKey(string dictionary, byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(dictionary));
        hash.AppendData(data);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // ── Stream recompression ────────────────────────────────────────────────

    private static int RecompressStreams(ReferenceGraph graph, CancellationToken cancellationToken)
    {
        var recompressed = 0;
        foreach (var (_, obj) in graph.Objects.OrderBy(o => o.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj is PdfStream stream && TryRecompress(stream))
                recompressed++;
        }

        return recompressed;
    }

    private static bool TryRecompress(PdfStream stream)
    {
        // XMP stays uncompressed so metadata tools can read it (§14.3.2), the
        // same rule the #1549 DecodedData setter follows.
        if (IsPlumbingOrMetadata(stream) || stream.ContainsKey("F"))
            return false;

        var filters = stream.Filters;
        if (!filters.All(GenericFilters.Contains))
            return false;

        var usesFlate = filters.Any(f => f is "FlateDecode" or "Fl");
        if (usesFlate)
        {
            // A Flate image, or Flate with a predictor, is already stored the
            // way its producer chose; re-encoding without the predictor can
            // only compete, and decoding every image costs memory for little.
            if (stream.GetNameOrNull("Subtype") == "Image" || stream.ContainsKey("DecodeParms"))
                return false;
        }

        byte[] decoded;
        if (filters.Count == 0)
        {
            decoded = stream.EncodedData;
        }
        else
        {
            if (!stream.TryEnsureDecoded())
                return false;
            decoded = stream.DecodedData;
        }

        if (decoded.Length == 0)
            return false;

        var encoded = Deflate(decoded);
        if (encoded.LongLength >= stream.EncodedData.LongLength)
            return false;

        stream.ReplaceEncoding(encoded, decoded, "FlateDecode");
        return true;
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    private static bool IsPlumbingOrMetadata(PdfStream stream)
        => stream.GetNameOrNull("Type") is "ObjStm" or "XRef" or "Metadata";

    internal static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static PdfObject? TryGetObject(PdfDocument document, int objectNumber)
    {
        try
        {
            return document.GetObject(objectNumber);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static PdfObject? TryResolve(PdfDocument document, PdfObject value)
    {
        try
        {
            return document.Resolve(value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Every reachable indirect object, and every place each one is referenced
    /// from — the container (a dictionary or array, possibly a direct one
    /// nested inside another object) and the key it sits under.
    /// </summary>
    private sealed class ReferenceGraph
    {
        private readonly Dictionary<int, List<(PdfObject Container, string? Key)>> _referrers = new();

        private ReferenceGraph()
        {
        }

        public Dictionary<int, PdfObject> Objects { get; } = new();

        /// <summary>Every dictionary that is the value of an <c>/XObject</c> key.</summary>
        public HashSet<PdfDictionary> XObjectDictionaries { get; } = new(ReferenceEqualityComparer.Instance);

        public bool HasSignatureDictionary { get; private set; }

        public static ReferenceGraph Build(PdfDocument document, CancellationToken cancellationToken)
        {
            var graph = new ReferenceGraph();
            foreach (var objectNumber in document.ComputeReachableObjects())
            {
                if (TryGetObject(document, objectNumber) is { } obj)
                    graph.Objects[objectNumber] = obj;
            }

            foreach (var (_, obj) in graph.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                graph.Walk(document, obj);
            }

            return graph;
        }

        private void Walk(PdfDocument document, PdfObject root)
        {
            var stack = new Stack<PdfObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                switch (stack.Pop())
                {
                    case PdfDictionary dictionary:
                        if (dictionary.ContainsKey("ByteRange"))
                            HasSignatureDictionary = true;
                        foreach (var (name, value) in dictionary)
                        {
                            if (name.Value == "XObject"
                                && TryResolve(document, value) is PdfDictionary xobjects
                                && xobjects is not PdfStream)
                            {
                                XObjectDictionaries.Add(xobjects);
                            }

                            Visit(stack, dictionary, name.Value, value);
                        }
                        break;
                    case PdfArray array:
                        foreach (var item in array)
                            Visit(stack, array, null, item);
                        break;
                }
            }
        }

        private void Visit(Stack<PdfObject> stack, PdfObject container, string? key, PdfObject value)
        {
            if (value is PdfReference reference)
            {
                if (!_referrers.TryGetValue(reference.ObjectNum, out var list))
                    _referrers[reference.ObjectNum] = list = new List<(PdfObject, string?)>();
                list.Add((container, key));
            }
            else if (value is PdfDictionary or PdfArray)
            {
                stack.Push(value);
            }
        }

        public bool AllReferrersAreIn(int objectNumber, HashSet<PdfDictionary> containers)
            => _referrers.TryGetValue(objectNumber, out var list)
               && list.Count > 0
               && list.All(r => r.Container is PdfDictionary dictionary && containers.Contains(dictionary));

        public bool AllReferrerKeysAreIn(int objectNumber, HashSet<string> keys)
            => _referrers.TryGetValue(objectNumber, out var list)
               && list.Count > 0
               && list.All(r => r.Key != null && keys.Contains(r.Key));

        /// <summary>
        /// Replace every reference to <paramref name="from"/> with one to
        /// <paramref name="to"/>. The writer's reachability pass then drops
        /// <paramref name="from"/>. Returns the number of references changed.
        /// </summary>
        public int Repoint(int from, int to)
        {
            if (!_referrers.TryGetValue(from, out var list) || list.Count == 0)
                return 0;

            var target = new PdfReference(to);
            var changed = 0;
            foreach (var (container, key) in list)
            {
                switch (container)
                {
                    case PdfDictionary dictionary when key != null
                                                       && dictionary.GetOptional(key) is PdfReference current
                                                       && current.ObjectNum == from:
                        dictionary[key] = target;
                        changed++;
                        break;
                    case PdfArray array:
                        for (var i = 0; i < array.Count; i++)
                        {
                            if (array[i] is PdfReference item && item.ObjectNum == from)
                            {
                                array[i] = target;
                                changed++;
                            }
                        }
                        break;
                }
            }

            if (!_referrers.TryGetValue(to, out var targetList))
                _referrers[to] = targetList = new List<(PdfObject, string?)>();
            targetList.AddRange(list);
            _referrers.Remove(from);
            return changed;
        }
    }
}
