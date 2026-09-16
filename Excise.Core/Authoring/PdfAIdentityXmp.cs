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
///
/// <para><b>This is the ONE pdfaid parser (#1526).</b> Every consumer in the
/// codebase reads the identification through this class, and nobody writes a
/// substring match of their own. There were FOUR readers before #1524, each a
/// one-line <c>Contains</c> in a different project, and they disagreed about
/// the same file: <c>PdfDocumentWriter.IsPdfA1</c> matched only
/// <c>&lt;pdfaid:part&gt;1&lt;/pdfaid:part&gt;</c>, so a valid PDF/A-1 file
/// using the equally-legal ATTRIBUTE serialisation (<c>pdfaid:part="1"</c>) read
/// as "not PDF/A-1" and the writer put object streams — which ISO 19005-1
/// forbids (veraPDF PDFA-1B 6.1.4#3, <c>containsXRefStream == false</c>) — into
/// an archival file. Same failure shape as "one walk, many sinks": N copies of
/// one rule, drifting, none authoritative.</para>
///
/// <para><b>Three questions, three entry points.</b> The distinction is the
/// reason this is not simply "everyone calls <see cref="TryRead"/>":
/// <list type="table">
///   <item><term><see cref="DeclaresAnyIdentification"/></term><description>
///     PRESENCE — "does this file CLAIM PDF/A at all". Used to decide NOT to
///     emit something PDF/A forbids (<c>/NeedAppearances</c>, #1499), where a
///     claim excise cannot validate must still count as a claim: being
///     conservative costs nothing, and reading <c>pdfaid:part&gt;9</c> as "not
///     PDF/A" would put the forbidden construct back into a file that claims
///     conformance.</description></item>
///   <item><term><see cref="ReadDeclaredPart"/> /
///     <see cref="ReadDeclaredConformance"/></term><description>
///     The DECLARED TOKEN, exactly as the file spells it, unvalidated — "which
///     part does this file claim". Used where the answer gates a
///     conformance-preserving decision about the part in hand (the writer's
///     object-stream suppression, the structural self-check). Deliberately not
///     routed through <see cref="TryParse"/>: that returns null when a
///     QUALIFIER fails validation, so a file declaring
///     <c>part 1</c> with <c>conformance b</c> (lower case — non-conforming,
///     but unambiguously claiming PDF/A-1) would read as "not part 1" and get
///     object streams anyway. A claim is honoured on the strength of the part
///     alone.</description></item>
///   <item><term><see cref="TryRead"/> / <see cref="TryParse"/></term><description>
///     The VALIDATED identity — every value checked against a closed token set.
///     The only read permitted to feed <see cref="Write"/>, because re-emitting
///     a claim is a stronger act than respecting one.</description></item>
/// </list></para></summary>
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
    //
    // The element alternative tolerates attributes on the start tag
    // (<pdfaid:part rdf:datatype="...">2</pdfaid:part>) — a strict superset of
    // #1507's pattern, added with #1524 because the same "one exact spelling"
    // assumption that hid the attribute form hides this one too. The optional
    // group requires whitespace before any attribute, so <pdfaid:parts> does
    // not match "part".
    private static readonly Regex PartPattern = PropertyPattern("part");
    private static readonly Regex ConformancePattern = PropertyPattern("conformance");
    private static readonly Regex RevPattern = PropertyPattern("rev");

    private static Regex PropertyPattern(string name) => new(
        $"<pdfaid:{name}(?:\\s[^>]{{0,256}})?>\\s*(?<v>[^<]{{1,16}}?)\\s*</pdfaid:{name}>"
        + $"|\\bpdfaid:{name}\\s*=\\s*\"(?<v>[^\"]{{1,16}})\""
        + $"|\\bpdfaid:{name}\\s*=\\s*'(?<v>[^']{{1,16}})'",
        RegexOptions.CultureInvariant,
        System.TimeSpan.FromSeconds(2));

    /// <summary>
    /// Whether <paramref name="xmp"/> declares a PDF/A identification at all,
    /// without judging its value — the PRESENCE question (see the class
    /// remarks). Matches the <c>pdfaid:part</c> property name, which covers both
    /// serialisations, and deliberately stays a bare substring match so that a
    /// malformed or unknown value still counts as a claim.
    /// </summary>
    internal static bool DeclaresAnyIdentification(string? xmp)
        => xmp != null && xmp.Contains("pdfaid:part", StringComparison.Ordinal);

    /// <summary>
    /// The <c>pdfaid:part</c> token exactly as <paramref name="xmp"/> spells it
    /// (trimmed), from either serialisation, or null when there is none.
    /// UNVALIDATED: the closed-set guarantee belongs to <see cref="TryParse"/>,
    /// so a value from here must never be written back into a document.
    /// </summary>
    internal static string? ReadDeclaredPart(string? xmp)
        => xmp is null ? null : Read(xmp, PartPattern);

    /// <summary>
    /// The <c>pdfaid:conformance</c> token exactly as <paramref name="xmp"/>
    /// spells it (trimmed), from either serialisation, or null when there is
    /// none. UNVALIDATED — see <see cref="ReadDeclaredPart"/>.
    /// </summary>
    internal static string? ReadDeclaredConformance(string? xmp)
        => xmp is null ? null : Read(xmp, ConformancePattern);

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

        return TryParse(text);
    }

    /// <summary>
    /// The validated PDF/A identification in an XMP packet's <b>text</b>, or
    /// null when there is none or any value present fails validation. The core
    /// <see cref="TryRead(PdfDocument)"/> delegates to, exposed separately
    /// because a caller does not always hold a <see cref="PdfDocument"/> whose
    /// catalog is the right source — the writer reads the packet through its
    /// save session (#1524), which is the view actually being serialised.
    /// </summary>
    internal static PdfAIdentity? TryParse(string? xmp)
    {
        if (string.IsNullOrEmpty(xmp)) return null;
        string text = xmp;

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
    /// forbidden: veraPDF <c>XMPPackage</c> rules), and the ELEMENT
    /// serialisation of <c>pdfaid:part</c> because that is what
    /// <c>PdfAWriter.WriteXmp</c> emits, so excise's own PDF/A output has one
    /// shape.
    ///
    /// <para>This used to say the element form was required because
    /// <c>PdfDocumentWriter.IsPdfA1</c> grepped for
    /// <c>&lt;pdfaid:part&gt;1&lt;/pdfaid:part&gt;</c>. That grep was #1524's
    /// defect and is gone: every consumer now reads through
    /// <see cref="ReadDeclaredPart"/>, which accepts either serialisation, so
    /// nothing downstream depends on which one is written here.</para>
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
