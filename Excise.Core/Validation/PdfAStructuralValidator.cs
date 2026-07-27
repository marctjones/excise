using System;
using System.Collections.Generic;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Validation;

/// <summary>
/// A light <b>structural</b> PDF/A conformance check for the document-level
/// markers excise itself emits (<see cref="PdfDocumentBuilder.PdfA"/>): the XMP
/// <c>pdfaid</c> identifier at the requested part/level, an sRGB OutputIntent,
/// and a trailer <c>/ID</c>. It exists so excise can verify its own emitted
/// PDF/A round-trips with the required structures intact.
///
/// <para><b>Not a full PDF/A validator.</b> It does not verify font embedding,
/// colour spaces, transparency, encryption absence, or the hundreds of other
/// ISO 19005 clauses. <see cref="ValidationReport.UncoveredCheckpoints"/> lists
/// the gaps; use veraPDF for an authoritative verdict.</para>
/// </summary>
public static class PdfAStructuralValidator
{
    private static readonly string[] Uncovered =
    {
        "Font embedding and completeness (all fonts embedded, /CIDSet for subset CID fonts)",
        "Colour-space and OutputIntent ICC-profile correctness (only presence is checked)",
        "Transparency, JavaScript, embedded-file, and encryption prohibitions",
        "Annotation and action restrictions",
        "Full XMP schema and Info/XMP consistency validation (only pdfaid keys are parsed)",
        "All remaining ISO 19005 clauses",
    };

    /// <summary>
    /// Structurally check <paramref name="document"/> for the PDF/A markers of the
    /// given <paramref name="conformance"/> level.
    /// </summary>
    public static ValidationReport Validate(PdfDocument document, PdfAConformance conformance)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        var standard = conformance == PdfAConformance.PdfA1B
            ? ConformanceStandard.PdfA1B
            : ConformanceStandard.PdfA2B;
        int expectedPart = conformance == PdfAConformance.PdfA1B ? 1 : 2;

        var results = new List<ValidationResult>();

        // XMP metadata packet with the pdfaid identifier at the expected part/level.
        string xmp = ReadXmp(document);
        bool hasPart = xmp.Contains($"pdfaid:part>{expectedPart}", StringComparison.Ordinal);
        bool hasLevel = xmp.Contains("pdfaid:conformance>B", StringComparison.Ordinal);
        results.Add(new ValidationResult(
            "A-XmpPdfaId",
            $"XMP metadata declares pdfaid:part {expectedPart} and conformance B.",
            RuleSeverity.Error,
            (hasPart && hasLevel) ? RuleStatus.Pass : RuleStatus.Fail,
            location: "Catalog/Metadata",
            reference: "ISO 19005-1/-2 §6.7.11 (metadata)"));

        // sRGB OutputIntent with a destination profile.
        bool hasOutputIntent = HasOutputIntent(document, out string oiDetail);
        results.Add(new ValidationResult(
            "A-OutputIntent",
            "An OutputIntent (GTS_PDFA1) with a /DestOutputProfile is present.",
            RuleSeverity.Error,
            hasOutputIntent ? RuleStatus.Pass : RuleStatus.Fail,
            location: "Catalog/OutputIntents" + (oiDetail.Length > 0 ? $" ({oiDetail})" : ""),
            reference: "ISO 19005-1/-2 §6.2.2 (output intent)"));

        // Trailer /ID — required for a conformant file.
        bool hasId = document.Trailer.ContainsKey("ID");
        results.Add(new ValidationResult(
            "A-TrailerId",
            "The trailer has a file identifier (/ID).",
            RuleSeverity.Error,
            hasId ? RuleStatus.Pass : RuleStatus.Fail,
            location: "Trailer/ID",
            reference: "ISO 19005-1/-2 §6.1.3 (file structure)"));

        return new ValidationReport(standard, results, Uncovered);
    }

    private static string ReadXmp(PdfDocument doc)
    {
        if (doc.Resolve(doc.Catalog.GetOptional("Metadata") ?? PdfNull.Instance) is not PdfStream s)
            return "";
        try { return s.GetDecodedString(System.Text.Encoding.UTF8); }
        catch { return ""; }
    }

    private static bool HasOutputIntent(PdfDocument doc, out string detail)
    {
        detail = "";
        if (doc.Resolve(doc.Catalog.GetOptional("OutputIntents") ?? PdfNull.Instance) is not PdfArray arr)
            return false;
        foreach (var item in arr)
        {
            if (doc.Resolve(item) is not PdfDictionary oi) continue;
            bool isPdfA = doc.Resolve(oi.GetOptional("S") ?? PdfNull.Instance) is PdfName s && s.Value == "GTS_PDFA1";
            bool hasProfile = oi.ContainsKey("DestOutputProfile");
            if (isPdfA && hasProfile)
            {
                detail = "GTS_PDFA1 + DestOutputProfile";
                return true;
            }
        }
        return false;
    }
}
