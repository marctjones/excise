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
        // Adobe-GB1 (Simplified Chinese) — PDF 32000-1 Table 118.
        ["GB-EUC-H"] = "GB1",
        ["GB-EUC-V"] = "GB1",
        ["GBpc-EUC-H"] = "GB1",
        ["GBpc-EUC-V"] = "GB1",
        ["GBK-EUC-H"] = "GB1",
        ["GBK-EUC-V"] = "GB1",
        ["GBKp-EUC-H"] = "GB1",
        ["GBKp-EUC-V"] = "GB1",
        ["GBK2K-H"] = "GB1",
        ["GBK2K-V"] = "GB1",
        ["UniGB-UCS2-H"] = "GB1",
        ["UniGB-UCS2-V"] = "GB1",
        ["UniGB-UTF16-H"] = "GB1",
        ["UniGB-UTF16-V"] = "GB1",

        // Adobe-CNS1 (Traditional Chinese).
        ["B5pc-H"] = "CNS1",
        ["B5pc-V"] = "CNS1",
        ["HKscs-B5-H"] = "CNS1",
        ["HKscs-B5-V"] = "CNS1",
        ["ETen-B5-H"] = "CNS1",
        ["ETen-B5-V"] = "CNS1",
        ["ETenms-B5-H"] = "CNS1",
        ["ETenms-B5-V"] = "CNS1",
        ["CNS-EUC-H"] = "CNS1",
        ["CNS-EUC-V"] = "CNS1",
        ["UniCNS-UCS2-H"] = "CNS1",
        ["UniCNS-UCS2-V"] = "CNS1",
        ["UniCNS-UTF16-H"] = "CNS1",
        ["UniCNS-UTF16-V"] = "CNS1",

        // Adobe-Japan1. "H"/"V" are the JIS X 0208 ISO-2022 CMaps' registered
        // names — one-letter, but names like any other.
        ["83pv-RKSJ-H"] = "Japan1",
        ["90ms-RKSJ-H"] = "Japan1",
        ["90ms-RKSJ-V"] = "Japan1",
        ["90msp-RKSJ-H"] = "Japan1",
        ["90msp-RKSJ-V"] = "Japan1",
        ["90pv-RKSJ-H"] = "Japan1",
        ["Add-RKSJ-H"] = "Japan1",
        ["Add-RKSJ-V"] = "Japan1",
        ["EUC-H"] = "Japan1",
        ["EUC-V"] = "Japan1",
        ["Ext-RKSJ-H"] = "Japan1",
        ["Ext-RKSJ-V"] = "Japan1",
        ["H"] = "Japan1",
        ["V"] = "Japan1",
        ["UniJIS-UCS2-H"] = "Japan1",
        ["UniJIS-UCS2-V"] = "Japan1",
        ["UniJIS-UCS2-HW-H"] = "Japan1",
        ["UniJIS-UCS2-HW-V"] = "Japan1",
        ["UniJIS-UTF16-H"] = "Japan1",
        ["UniJIS-UTF16-V"] = "Japan1",

        // Adobe-Korea1.
        ["KSC-EUC-H"] = "Korea1",
        ["KSC-EUC-V"] = "Korea1",
        ["KSCms-UHC-H"] = "Korea1",
        ["KSCms-UHC-V"] = "Korea1",
        ["KSCms-UHC-HW-H"] = "Korea1",
        ["KSCms-UHC-HW-V"] = "Korea1",
        ["KSCpc-EUC-H"] = "Korea1",
        ["UniKS-UCS2-H"] = "Korea1",
        ["UniKS-UCS2-V"] = "Korea1",
        ["UniKS-UTF16-H"] = "Korea1",
        ["UniKS-UTF16-V"] = "Korea1",

        // Adobe-KR (PDF 2.0, ISO 32000-2 §9.7.5.2).
        ["UniAKR-UTF16-H"] = "KR",
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

    /// <summary>
    /// Whether the named registered CMap selects vertical writing. Shipped
    /// CMaps answer from their own parsed <c>/WMode</c> (§9.7.5.2) — which is
    /// what makes the one-letter Adobe-Japan1 name <c>V</c> (no "-V" suffix)
    /// vertical. The suffix check remains for names not shipped here, notably
    /// <c>Identity-V</c>.
    /// </summary>
    public static bool IsVertical(string name)
        => IsKnownEncodingCMap(name)
            ? TryGetEncodingCMap(name)?.WMode == 1
            : name.EndsWith("-V", StringComparison.Ordinal);

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
