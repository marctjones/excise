using System.Text;
using System.Text.RegularExpressions;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Authoring;

/// <summary>
/// The PDF/A identification a file is RECOGNISED by: <c>pdfaid:part</c>, and the
/// <c>pdfaid:conformance</c> / <c>pdfaid:rev</c> that qualify it. Values are
/// validated on read (see <see cref="PdfAIdentityXmp.TryRead"/>), so an instance
/// only ever holds one of a handful of fixed tokens — never free text from the
/// document.
/// </summary>
internal readonly record struct PdfAIdentity(string Part, string? Conformance, string? Rev);

/// <summary>
/// Reads and re-emits the <c>pdfaid</c> identification of a document's XMP
/// packet (#1507). Sits beside <see cref="PdfAWriter"/> deliberately: that class
/// authors the packet for a document excise is CREATING as PDF/A, this one
/// preserves the identification of a document that already had it. Both must
/// agree on the serialisation — see <c>PdfAWriter.WriteXmp</c>.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <see cref="PdfDocument.ScrubMetadata"/> removes
/// the catalog <c>/Metadata</c> stream outright, which is the right answer for a
/// carrier that can restate a redacted term in any schema — and fatal for a
/// PDF/A file, whose conformance requires that stream to exist
/// (<c>containsMetadata</c>, veraPDF PDFA-1B/2B/4 profiles) and to carry the
/// identification (<c>containsPDFAIdentification</c>). An area redaction has no
/// term, so it could not scrub the packet selectively even in principle; the only
/// move that keeps both guarantees is to strip the packet and re-emit an
/// identity-only one. See
/// <see cref="PdfDocument.ScrubMetadataPreservingPdfAIdentity"/>.</para>
///
/// <para><b>Why this cannot reintroduce redacted text.</b> Nothing is copied
/// from the old packet except three values, each of which must match a fixed
/// pattern (<c>1-4</c>, one of <c>A B U E F</c>, four digits) or the
/// identification is dropped entirely. There is no path by which a name, a
/// title, or a custom schema's contents rides back into the document.</para>
///
/// <para><b>Deliberately NOT preserved.</b>
/// <list type="bullet">
///   <item><c>dc:title</c>, <c>dc:creator</c>, <c>pdf:Keywords</c>,
///     <c>xmp:CreatorTool</c>, dates, and every custom schema — these are the
///     carriers the strip exists to remove. No PDF/A rule requires any of them:
///     the Info-dictionary consistency rules (PDFA-1B, <c>CosInfo</c>) are all of
///     the form "<c>X == null || X == XMP-X</c>", and the strip empties the Info
///     dictionary on the same pass, so both sides are absent and consistent.</item>
///   <item><c>pdfaid:amd</c> and <c>pdfaid:corr</c> (optional amendment /
///     corrigendum identifiers). No profile rule requires them; the profiles only
///     constrain their namespace prefix.</item>
///   <item><c>pdfuaid:part</c> — a PDF/UA claim is NOT preserved, on purpose.
///     PDF/UA-1 requires a non-empty <c>dc:title</c> (and
///     <c>/ViewerPreferences /DisplayDocTitle</c>), and the strip deletes the
///     title; a preserved claim would therefore be a claim the file no longer
///     satisfies. Withdrawing a claim is safe, asserting a false one is not.</item>
/// </list></para>
/// </remarks>
internal static class PdfAIdentityXmp
{
    // One regex per property, covering the two serialisations XMP permits: an
    // element (<pdfaid:part>2</pdfaid:part>) and an attribute
    // (pdfaid:part="2"). The captures are length-bounded and contain no nested
    // quantifier, so these stay linear on hostile input.
    private static readonly Regex PartPattern = PropertyPattern("part");
    private static readonly Regex ConformancePattern = PropertyPattern("conformance");
    private static readonly Regex RevPattern = PropertyPattern("rev");

    private static Regex PropertyPattern(string name) => new(
        $"<pdfaid:{name}>\\s*(?<v>[^<]{{1,16}}?)\\s*</pdfaid:{name}>"
        + $"|\\bpdfaid:{name}\\s*=\\s*\"(?<v>[^\"]{{1,16}})\""
        + $"|\\bpdfaid:{name}\\s*=\\s*'(?<v>[^']{{1,16}})'",
        RegexOptions.CultureInvariant,
        System.TimeSpan.FromSeconds(2));

    /// <summary>
    /// The PDF/A identification in <paramref name="document"/>'s catalog XMP
    /// packet, or null when there is none, when it cannot be read, or when any
    /// value present fails validation.
    /// </summary>
    /// <remarks>
    /// Returning null is the FAIL-SECURE answer, and the only one: the caller
    /// then leaves the document without an identification, which is exactly what
    /// happened before #1507. A claim excise could not verify is never emitted —
    /// this preserves an identification, it does not repair one.
    ///
    /// <para>Known limitation, shared with <c>PdfDocument.TargetsPdfA</c>: the
    /// namespace PREFIX is assumed to be <c>pdfaid</c>. XML permits any prefix
    /// bound to <c>http://www.aiim.org/pdfa/ns/id/</c>, and a file using another
    /// one reads here as "not PDF/A" — the veraPDF profiles require the
    /// <c>pdfaid</c> prefix for <c>part</c>/<c>conformance</c>/<c>rev</c>
    /// anyway (<c>partPrefix == null || partPrefix == "pdfaid"</c>), so such a
    /// file is not conforming to begin with.</para>
    /// </remarks>
    internal static PdfAIdentity? TryRead(PdfDocument document)
    {
        if (document == null) return null;

        byte[]? xmp;
        try { xmp = document.GetXmpMetadata(); }
        catch (System.Exception ex) when (ex is not System.OutOfMemoryException) { return null; }
        if (xmp is not { Length: > 0 }) return null;

        string text;
        try { text = Encoding.UTF8.GetString(xmp); }
        catch (System.Exception ex) when (ex is not System.OutOfMemoryException) { return null; }

        var part = Read(text, PartPattern);
        // Part is the identification; without it there is nothing to preserve.
        // 1-4 are the parts of ISO 19005 that exist (veraPDF: part == 1|2|3|4).
        if (part is null || part is not ("1" or "2" or "3" or "4")) return null;

        var conformance = Read(text, ConformancePattern);
        // A/B/U for parts 1-3, E/F for PDF/A-4e and -4f. PDF/A-4 itself requires
        // conformance to be ABSENT (veraPDF PDFA-4: conformance == null), which is
        // why the value is copied exactly as found and never invented.
        if (conformance != null && conformance is not ("A" or "B" or "U" or "E" or "F"))
            return null;

        var rev = Read(text, RevPattern);
        // PDF/A-4 requires pdfaid:rev == "2020". Accept any four-digit revision so
        // a future edition round-trips, but nothing else.
        if (rev != null && (rev.Length != 4 || !rev.All(char.IsAsciiDigit)))
            return null;

        return new PdfAIdentity(part, conformance, rev);
    }

    private static string? Read(string xmp, Regex pattern)
    {
        Match match;
        try { match = pattern.Match(xmp); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!match.Success) return null;
        var value = match.Groups["v"].Value.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Replace <paramref name="document"/>'s catalog <c>/Metadata</c> with a
    /// packet that carries <paramref name="identity"/> and nothing else.
    /// </summary>
    internal static void Write(PdfDocument document, PdfAIdentity identity)
    {
        var bytes = Encoding.UTF8.GetBytes(BuildPacket(identity));

        var dict = new PdfDictionary();
        dict.SetName("Type", "Metadata");
        dict.SetName("Subtype", "XML");
        dict.SetInt("Length", bytes.Length);

        // No /Filter, deliberately: PDF/A-1 forbids one on the catalog metadata
        // stream (veraPDF PDFA-1B, PDMetadata: "isCatalogMetadata == false ||
        // Filter == null"), and §14.3.2 wants XMP readable without decompression.
        // PdfStream(dict, bytes) stores these bytes as the ENCODED data, which is
        // what the writer serialises.
        document.Catalog["Metadata"] = document.AddIndirectObject(new PdfStream(dict, bytes));
        document.InvalidateDerivedState(PdfDocumentDerivedStateScope.Metadata);
    }

    /// <summary>
    /// The identity-only XMP packet. Same shape as <c>PdfAWriter.WriteXmp</c> —
    /// UTF-8, no <c>bytes</c>/<c>encoding</c> attributes on the header (both
    /// forbidden: veraPDF <c>XMPPackage</c> rules), the ELEMENT serialisation of
    /// <c>pdfaid:part</c> because <c>PdfDocumentWriter.IsPdfA1</c> greps for
    /// <c>&lt;pdfaid:part&gt;1&lt;/pdfaid:part&gt;</c> to suppress the object
    /// streams PDF/A-1 forbids.
    /// </summary>
    /// <remarks>
    /// The three interpolated values need no XML escaping: each is one token from
    /// a closed set, enforced by <see cref="TryRead"/>. That is a property of the
    /// validation, so do not widen the patterns there without adding escaping
    /// here.
    /// </remarks>
    private static string BuildPacket(PdfAIdentity identity)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        sb.Append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
        sb.Append($"   <pdfaid:part>{identity.Part}</pdfaid:part>\n");
        if (identity.Conformance != null)
            sb.Append($"   <pdfaid:conformance>{identity.Conformance}</pdfaid:conformance>\n");
        if (identity.Rev != null)
            sb.Append($"   <pdfaid:rev>{identity.Rev}</pdfaid:rev>\n");
        sb.Append("  </rdf:Description>\n");
        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        sb.Append("   <dc:format>application/pdf</dc:format>\n");
        sb.Append("  </rdf:Description>\n");
        sb.Append(" </rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return sb.ToString();
    }
}
