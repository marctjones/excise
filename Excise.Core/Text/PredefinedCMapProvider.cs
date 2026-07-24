using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;

namespace Excise.Core.Text;

/// <summary>
/// Loads predefined (registered) CJK CMaps embedded into this assembly (#515).
///
/// Two kinds of CMap are shipped, mirroring the two-part model of PDF §9.7.5.2
/// and §9.10.2:
/// <list type="bullet">
/// <item><b>Encoding CMaps</b> (code → CID): registered names a Type0 font can
/// use directly as its <c>/Encoding</c>, e.g. <c>/UniGB-UCS2-H</c> (2-byte
/// UCS-2 codes → Adobe-GB1 CIDs) or <c>/90ms-RKSJ-H</c> (mixed 1/2-byte
/// Shift-JIS codes → Adobe-Japan1 CIDs). Parsed with
/// <see cref="CidCMap"/>; <c>usecmap</c> references (the vertical -V CMaps
/// inherit their -H base) resolve recursively through this provider.</item>
/// <item><b>CID → Unicode CMaps</b> (the <c>Adobe-&lt;Ordering&gt;-UCS2</c>
/// files): map a Registry/Ordering's CIDs to UCS-2, selected via the font's
/// <c>/CIDSystemInfo</c> per §9.10.2 method (b). Their source "codes" are
/// CIDs, so they parse with <see cref="ToUnicodeCMapParser"/>.</item>
/// </list>
/// Resources are unmodified Adobe cmap-resources / mapping-resources-pdf files
/// (BSD-3-Clause; see Resources/CMaps/LICENSE.md), gzipped and embedded.
/// Parsed CMaps are cached per process; misses are cached as null.
/// </summary>
internal static class PredefinedCMapProvider
{
    /// <summary>
    /// The registered encoding CMaps shipped with this build, mapped to the
    /// CIDSystemInfo /Ordering their CIDs belong to. Used both as the
    /// known-name gate and to pick the CID→Unicode companion when the font's
    /// own /CIDSystemInfo is missing or unreadable.
    /// </summary>
    private static readonly Dictionary<string, string> EncodingCMapOrdering = new(StringComparer.Ordinal)
    {
        ["UniGB-UCS2-H"] = "GB1",
        ["UniGB-UCS2-V"] = "GB1",
        ["UniCNS-UCS2-H"] = "CNS1",
        ["UniCNS-UCS2-V"] = "CNS1",
        ["UniJIS-UCS2-H"] = "Japan1",
        ["UniJIS-UCS2-V"] = "Japan1",
        ["UniKS-UCS2-H"] = "Korea1",
        ["UniKS-UCS2-V"] = "Korea1",
        ["90ms-RKSJ-H"] = "Japan1",
        ["90ms-RKSJ-V"] = "Japan1",
    };

    /// <summary>Orderings with an embedded Adobe-&lt;Ordering&gt;-UCS2 CID→Unicode CMap.</summary>
    private static readonly HashSet<string> KnownOrderings = new(StringComparer.Ordinal)
    {
        "GB1", "CNS1", "Japan1", "Korea1", "KR",
    };

    private static readonly ConcurrentDictionary<string, CidCMap?> EncodingCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Dictionary<int, string>?> CidToUnicodeCache = new(StringComparer.Ordinal);

    /// <summary>True when <paramref name="name"/> is a registered encoding CMap this build ships.</summary>
    public static bool IsKnownEncodingCMap(string name) => EncodingCMapOrdering.ContainsKey(name);

    /// <summary>Registered vertical CMaps are the <c>-V</c> variants.</summary>
    public static bool IsVertical(string name) => name.EndsWith("-V", StringComparison.Ordinal);

    /// <summary>
    /// The CIDSystemInfo /Ordering the named encoding CMap's CIDs belong to,
    /// or null for names this build does not ship.
    /// </summary>
    public static string? GetOrderingForEncodingCMap(string name)
        => EncodingCMapOrdering.TryGetValue(name, out var ordering) ? ordering : null;

    /// <summary>
    /// Loads a registered encoding CMap (code → CID) by name, or null when the
    /// name is unknown or the resource fails to parse. Results (including
    /// misses) are cached for the process lifetime.
    /// </summary>
    public static CidCMap? TryGetEncodingCMap(string name)
        => TryGetEncodingCMap(name, visited: null);

    private static CidCMap? TryGetEncodingCMap(string name, HashSet<string>? visited)
    {
        if (!EncodingCMapOrdering.ContainsKey(name))
            return null;

        if (EncodingCache.TryGetValue(name, out var cached))
            return cached;

        // Cycle guard for usecmap chains (defensive; the shipped set is acyclic).
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visited.Add(name))
            return null;

        CidCMap? cmap = null;
        try
        {
            var content = LoadResourceText(name);
            if (content != null)
                cmap = CidCMap.Parse(content, referenced => TryGetEncodingCMap(referenced, visited));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cmap = null;
        }

        EncodingCache[name] = cmap;
        return cmap;
    }

    /// <summary>
    /// Loads the CID → Unicode map for a CIDSystemInfo <paramref name="ordering"/>
    /// (e.g. "GB1" → the Adobe-GB1-UCS2 CMap), or null when no companion CMap is
    /// shipped for that ordering. Results (including misses) are cached.
    /// </summary>
    public static IReadOnlyDictionary<int, string>? TryGetCidToUnicodeMap(string ordering)
    {
        if (!KnownOrderings.Contains(ordering))
            return null;

        return CidToUnicodeCache.GetOrAdd("Adobe-" + ordering + "-UCS2", static resourceName =>
        {
            try
            {
                var content = LoadResourceText(resourceName);
                return content == null ? null : ToUnicodeCMapParser.Parse(content);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        });
    }

    private static string? LoadResourceText(string name)
    {
        using var stream = typeof(PredefinedCMapProvider).GetTypeInfo().Assembly
            .GetManifestResourceStream("CMaps/" + name + ".gz");
        if (stream == null)
            return null;

        using var gunzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gunzip);
        return reader.ReadToEnd();
    }
}
