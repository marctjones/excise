using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Excise.Core.Document;

namespace Excise.Core.Authoring;

/// <summary>
/// The PDF/UA identification a file is RECOGNISED by: <c>pdfuaid:part</c> (and
/// the <c>pdfuaid:rev</c> that qualifies PDF/UA-2). Validated on read, so an
/// instance only ever holds one of a handful of fixed tokens — never free text
/// from the document.
/// </summary>
internal readonly record struct PdfUaIdentity(string Part, string? Rev);

/// <summary>
/// Reads the <c>pdfuaid</c> identification of a document's XMP packet so the
/// #1507 metadata strip can preserve it (#1586). Sibling of
/// <see cref="PdfAIdentityXmp"/>, which does the same for <c>pdfaid</c>; the two
/// are written into ONE packet by
/// <see cref="PdfAIdentityXmp.Write(PdfDocument, PdfAIdentity?, PdfUaIdentity?)"/>.
/// </summary>
/// <remarks>
/// <para><b>Why this exists, and what it supersedes.</b> Until #1586,
/// <see cref="PdfAIdentityXmp"/> documented a PDF/UA claim as
/// <i>deliberately not preserved</i>, on the ground that PDF/UA-1 requires a
/// non-empty <c>dc:title</c> which the strip deletes — so a preserved claim
/// would assert something the file no longer satisfies. That reasoning was
/// sound and its premise is now false: #1586 makes the redacted output keep a
/// <b>synthesised</b> title (<see cref="PlaceholderTitle"/>), which is not
/// document-derived and therefore cannot carry a redacted term back in.</para>
///
/// <para><b>Measured, not assumed</b> (2026-09-17, veraPDF 1.28 <c>-f ua1</c>,
/// on <c>test-pdfs/pdfua/7.1-t01-pass-a.pdf</c> which passes as shipped):</para>
/// <list type="bullet">
///   <item>catalog <c>/Metadata</c> removed → FAIL, clause 7.1 test 8 ("the
///     Catalog dictionary shall contain the Metadata key");</item>
///   <item>packet rebuilt with <c>pdfuaid:part</c> ONLY → FAIL, clause 7.1
///     test 9 (<c>dc:title</c> required);</item>
///   <item>packet rebuilt with <c>pdfuaid:part</c> AND a synthesised
///     <c>dc:title</c> → <b>PASS</b>.</item>
/// </list>
/// <para>So both halves are load-bearing: the claim alone is not enough, and
/// preserving the original title is not necessary. A placeholder is the only
/// option that keeps conformance without carrying text across the strip.</para>
///
/// <para><b>Deliberately NOT preserved:</b> the document's own
/// <c>dc:title</c>, and <c>pdfuaid:conformance</c> — PDF/UA-1 has no
/// conformance level, and inventing one would be a claim excise cannot check.
/// <c>/ViewerPreferences /DisplayDocTitle</c> is not touched by the strip at
/// all, so a file that had it keeps it; a file that did not was never
/// PDF/UA-conformant.</para>
/// </remarks>
internal static class PdfUaIdentityXmp
{
    /// <summary>
    /// The title written in place of the document's own. Not document-derived,
    /// so it cannot restate a redacted term; non-empty, so PDF/UA-1 clause 7.1
    /// is satisfied. ⚠️ A caller that reports "XMP metadata removed" must say
    /// this was retained instead — overstating a scrub is the same class of
    /// problem as a carrier that silently keeps a term (#1188).
    /// </summary>
    internal const string PlaceholderTitle = "Redacted document";

    private static readonly Regex PartPattern = PropertyPattern("part");
    private static readonly Regex RevPattern = PropertyPattern("rev");

    // Same two serialisations XMP permits as PdfAIdentityXmp: an element
    // (<pdfuaid:part>1</pdfuaid:part>) and an attribute (pdfuaid:part="1").
    // Length-bounded captures with no nested quantifier — linear on hostile
    // input.
    private static Regex PropertyPattern(string name) => new(
        $"<pdfuaid:{name}(?:\\s[^>]{{0,256}})?>\\s*(?<v>[^<]{{1,16}}?)\\s*</pdfuaid:{name}>"
        + $"|\\bpdfuaid:{name}\\s*=\\s*\"(?<v>[^\"]{{1,16}})\""
        + $"|\\bpdfuaid:{name}\\s*=\\s*'(?<v>[^']{{1,16}})'",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// Whether <paramref name="xmp"/> declares a PDF/UA identification at all,
    /// without judging its value — the PRESENCE question. A bare substring
    /// match, so a malformed or unknown value still counts as a claim.
    /// </summary>
    internal static bool DeclaresAnyIdentification(string? xmp)
        => xmp != null && xmp.Contains("pdfuaid:part", StringComparison.Ordinal);

    /// <summary>
    /// The validated PDF/UA identification in <paramref name="document"/>'s
    /// catalog XMP packet, or null when there is none or a value present fails
    /// validation. Returning null is the FAIL-SECURE answer: the caller then
    /// leaves the document without the claim, which is what happened before
    /// #1586. This preserves an identification; it never repairs one.
    /// </summary>
    internal static PdfUaIdentity? TryRead(PdfDocument document)
    {
        if (document == null) return null;

        byte[]? xmp;
        try { xmp = document.GetXmpMetadata(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        if (xmp is not { Length: > 0 }) return null;

        try { return TryParse(Encoding.UTF8.GetString(xmp)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// The validated identification in an XMP packet's text, or null.
    /// </summary>
    internal static PdfUaIdentity? TryParse(string? xmp)
    {
        if (string.IsNullOrEmpty(xmp)) return null;

        var part = Read(xmp, PartPattern);
        // 1 and 2 are the parts of ISO 14289 that exist.
        if (part is not ("1" or "2")) return null;

        var rev = Read(xmp, RevPattern);
        // PDF/UA-2 carries pdfuaid:rev. Accept any four-digit revision so a
        // future edition round-trips, and nothing else.
        if (rev != null && (rev.Length != 4 || !rev.All(char.IsAsciiDigit)))
            return null;

        return new PdfUaIdentity(part, rev);
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
}
