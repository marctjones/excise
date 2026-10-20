using System.Security.Cryptography;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Operations;
using Excise.Core.Xfa;

namespace Excise.App.Services;

/// <summary>
/// The thumbnail cache salt for a laid-out dynamic XFA form (#1547, #2023). A laid-out
/// form's pages are not the pages in the file, so they must not share the file's cached
/// thumbnails. They are drawn from the XFA packets (template and datasets), so the salt
/// carries a hash of those packets and the identity of the layout engine: the file part of
/// the cache identity is path, length and mtime only, which a changed datasets value need not
/// move, and the renderer part names Excise.Rendering, not Excise.Core where the layout lives.
/// </summary>
internal static class XfaThumbnailCacheSalt
{
    private const string Version = "xfa-layout-v2";

    private static readonly string EngineIdentity =
        typeof(PdfXfaLayout).Module.ModuleVersionId.ToString("N");

    /// <summary>Null when the document shows no laid-out form (its file thumbnails apply).</summary>
    public static string? For(PdfDocument document, XfaLayoutResult? layout)
    {
        if (layout is not { ShowsForm: true })
            return null;
        return $"{Version};engine={EngineIdentity};xfa={PacketHash(document)}";
    }

    private static string PacketHash(PdfDocument document)
    {
        try
        {
            if (!XfaXmlCarrier.TryGetXfa(document, out _, out var entry))
                return "none";
            var streams = document.Resolve(entry) switch
            {
                PdfStream single => new List<PdfStream> { single },
                PdfArray packets => XfaXmlCarrier.ResolvePacketStreams(document, packets),
                _ => new List<PdfStream>(),
            };
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var stream in streams)
            {
                var data = stream.DecodedData;
                hash.AppendData(BitConverter.GetBytes(data.Length));
                hash.AppendData(data);
            }
            return Convert.ToHexString(hash.GetHashAndReset(), 0, 8).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A packet that cannot be decoded gets a salt no other open shares: a cache miss,
            // never another form's thumbnails.
            return "unreadable-" + Guid.NewGuid().ToString("N");
        }
    }
}
