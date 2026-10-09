using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Signatures;

/// <summary>
/// Removes a document's certification on save and records what went (#2024; decisions 12 and
/// 19 of docs/architecture/xfa-rendering.md). Registered by the saves that rewrite an XFA form:
/// every save of a dynamic form excise laid out (<c>PdfXfaLayout.ApplyXfaLayout</c>) and every
/// save of a static XFA form whose datasets a fill changed (<c>XfaStaticDataSync</c>).
/// </summary>
/// <remarks>
/// <para><b>Why.</b> excise has no incremental writer, so a save rewrites every byte a
/// signature's <c>/ByteRange</c> covers and the digest can no longer verify. A certified file
/// whose <c>/Perms</c> still names such a signature claims a certification it does not have.
/// Acrobat DC, given an excise-saved copy of IMM 5257e or the Ohio expense report, ignored the
/// XFA form altogether; with only <c>/Perms</c> removed it rendered its own XFA form again
/// (owner check on #2024, 2026-10-09).</para>
/// <para><b>What goes</b> (ISO 32000-2 §12.8): the <c>/DocMDP</c> and <c>/UR3</c> entries of
/// the catalog's <c>/Perms</c> (Table 263), and <c>/Perms</c> itself when nothing else is left
/// in it; the signature dictionaries they name, which nothing else then reaches (a usage rights
/// signature is never a field value, §12.8.1); the <c>/V</c> of any signature field holding one of
/// them, with the widget's <c>/AP</c>, which is drawn when the field is signed (§12.7.5.5); the
/// catalog's <c>/Legal</c> when a DocMDP signature went, since a legal attestation dictionary
/// accompanies a certification signature (§12.8.2.2.1, §12.8.7); and the AppendOnly bit of
/// <c>/SigFlags</c> (Table 225) when no signed field remains.</para>
/// <para><b>What stays.</b> The signature FIELD stays, unsigned: Annex K.2 asks for an AcroForm
/// field per XFA field, and an unsigned signature field is an ordinary field (§12.7.5.5). It is
/// removed only when another field of the same fully qualified name exists, so exactly one field
/// carries the name (IMM 5257e's layout generates <c>form1[0].SignatureField4[0]</c> next to the
/// certification field of that name). Its <c>/Lock</c> stays: Table 235 defines it as the fields
/// locked when the field is signed, an authoring choice for a future signature, not part of this
/// one. Approval signatures (a signed field <c>/Perms</c> does not name) are untouched; the
/// #1415 warning covers them. A <c>/Perms</c> entry other than DocMDP and UR3, and
/// <c>/DSS</c>, are not certification entries and stay.</para>
/// <para>Edits land on dictionaries (<c>Remove</c>, which marks a parsed object modified), never
/// inside a nested array of a parsed object, so the object store cannot evict an edited object
/// and re-read the original (see <see cref="RemovedPageReferenceScrubber"/>).</para>
/// </remarks>
internal static class CertificationStripper
{
    /// <summary>The <c>/Perms</c> entries that are certification (Table 263).</summary>
    private static readonly string[] CertificationKeys = { "DocMDP", "UR3" };

    /// <summary>Table 225: bit 2, AppendOnly.</summary>
    private const int AppendOnly = 2;

    /// <summary>The one-sentence reason shown with the removals (CLI warning, GUI notice).</summary>
    internal const string Summary =
        "The form's certification was removed: excise rewrites the whole file, so the author's certification "
        + "and the Adobe Reader usage rights it granted can no longer verify, and a copy that kept them would "
        + "claim a certification it does not have.";

    /// <summary>
    /// The user-facing lines for <paramref name="removals"/> (a document's
    /// <see cref="PdfDocument.CertificationRemovals"/>): <see cref="Summary"/>, then one line per
    /// removal. Empty when nothing was removed.
    /// </summary>
    internal static IEnumerable<string> Describe(IReadOnlyList<string>? removals)
    {
        if (removals is not { Count: > 0 })
            yield break;
        yield return Summary;
        foreach (var line in removals)
            yield return "certification removed: " + line;
    }

    /// <summary>True when the catalog's <c>/Perms</c> names a DocMDP or UR3 signature.</summary>
    internal static bool HasCertification(PdfDocument document)
        => document.Resolve(document.Catalog.GetOptional("Perms") ?? PdfNull.Instance) is PdfDictionary perms
           && CertificationKeys.Any(perms.ContainsKey);

    /// <summary>
    /// Strip the certification at the start of every later save of <paramref name="document"/>
    /// (once registered, every save; registering again is a no-op). What a save removes is
    /// recorded in <see cref="PdfDocument.CertificationRemovals"/>, which keeps every line for the
    /// life of the document, so a save the user did not start (a print, an undo snapshot) does not
    /// swallow the report of the next one.
    /// </summary>
    internal static void StripOnEverySave(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.CertificationRemovals != null)
            return;
        var record = new List<string>();
        document.CertificationRemovals = record;
        document.RegisterPreSaveAction(() =>
        {
            foreach (var line in Strip(document))
            {
                if (!record.Contains(line))
                    record.Add(line);
            }
        });
    }

    /// <summary>
    /// Remove the certification now. Returns one line per thing removed; empty when the
    /// catalog's <c>/Perms</c> names no DocMDP or UR3 signature (a second call finds nothing).
    /// </summary>
    internal static IReadOnlyList<string> Strip(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var removed = new List<string>();
        var catalog = document.Catalog;
        var permsEntry = catalog.GetOptional("Perms");
        if (document.Resolve(permsEntry ?? PdfNull.Instance) is not PdfDictionary perms)
            return removed;

        var signatures = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var signatureObjects = new HashSet<int>();
        bool docMdp = false;
        foreach (var key in CertificationKeys)
        {
            if (perms.GetOptional(key) is not { } value)
                continue;
            if (value is PdfReference reference)
                signatureObjects.Add(reference.ObjectNum);
            if (document.Resolve(value) is PdfDictionary signature)
                signatures.Add(signature);
            perms.Remove(key);
            if (permsEntry is not PdfReference)
                catalog["Perms"] = perms;   // a direct /Perms: mark the catalog that holds it modified
            if (key == "DocMDP")
            {
                docMdp = true;
                removed.Add("/Perms /DocMDP: the author's certification signature (ISO 32000-2 §12.8.2.2)");
            }
            else
            {
                removed.Add("/Perms /UR3: the usage rights signature that enables Adobe Reader features (§12.8.2.3)");
            }
        }
        if (removed.Count == 0)
            return removed;

        if (perms.Count == 0)
        {
            catalog.Remove("Perms");
            if (permsEntry is PdfReference permsReference)
                signatureObjects.Add(permsReference.ObjectNum);
        }

        removed.AddRange(UnsignFields(document, signatures, signatureObjects));

        if (docMdp && catalog.Remove("Legal"))
            removed.Add("/Legal: the legal attestation that accompanies a certification signature (§12.8.7)");

        if (document.Resolve(catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary acroForm
            && document.Resolve(acroForm.GetOptional("SigFlags") ?? PdfNull.Instance) is PdfInteger flags
            && ((int)flags.Value & AppendOnly) != 0
            && !SignedFieldDetector.HasSignedField(document))
        {
            acroForm.SetInt("SigFlags", (int)flags.Value & ~AppendOnly);
            if (catalog.GetOptional("AcroForm") is PdfDictionary)
                catalog["AcroForm"] = acroForm;   // a direct /AcroForm: mark the catalog modified
            removed.Add("/SigFlags AppendOnly: no signed field remains (Table 225)");
        }

        // Free what nothing reaches now, so no later walk of the object store finds a signature
        // the file no longer holds; the writer would not have written it either.
        var reachable = document.ComputeReachableObjects();
        foreach (var number in signatureObjects)
        {
            if (!reachable.Contains(number))
                document.RemoveObject(number);
        }
        return removed;
    }

    /// <summary>
    /// Clear the signature fields whose <c>/V</c> is one of the removed signatures; remove the
    /// one that shares its fully qualified name with another field.
    /// </summary>
    private static IEnumerable<string> UnsignFields(
        PdfDocument document, HashSet<PdfDictionary> signatures, HashSet<int> signatureObjects)
    {
        var fields = SignedFieldDetector.Fields(document).ToList();
        var lines = new List<string>();
        foreach (var (field, fullName, parent) in fields)
        {
            if (field.GetOptional("V") is not { } value
                || !SignedFieldDetector.IsSignatureField(document, field)
                || !(value is PdfReference r && signatureObjects.Contains(r.ObjectNum)
                     || document.Resolve(value) is PdfDictionary v && signatures.Contains(v)))
            {
                continue;
            }

            field.Remove("V");
            var widgets = Widgets(document, field);
            foreach (var widget in widgets)
                widget.Remove("AP");

            var namesake = fields.Any(other => !ReferenceEquals(other.Field, field)
                && other.FullName == fullName && IsTerminalField(document, other.Field));
            if (namesake && Detach(document, field, parent, widgets))
            {
                lines.Add($"signature field '{fullName}': removed, another field of that name remains "
                    + "(one AcroForm field per XFA field, ISO 32000-2 Annex K.2)");
            }
            else
            {
                lines.Add($"signature field '{fullName}': kept unsigned (its signature value and signed appearance removed)");
            }
        }
        return lines;
    }

    /// <summary>A field that holds a value: it has <c>/FT</c> (its own or inherited) and no kid that is a field.</summary>
    private static bool IsTerminalField(PdfDocument document, PdfDictionary field)
    {
        if (document.Resolve(field.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids
            && kids.Any(k => document.Resolve(k) is PdfDictionary kid && kid.ContainsKey("T")))
        {
            return false;
        }
        return field.ContainsKey("FT") || field.ContainsKey("Parent");
    }

    /// <summary>The field's widgets: itself when merged (§12.5.6.19), else its kids without <c>/T</c>.</summary>
    private static List<PdfDictionary> Widgets(PdfDocument document, PdfDictionary field)
    {
        var widgets = new List<PdfDictionary>();
        if (field.GetNameOrNull("Subtype") == "Widget")
            widgets.Add(field);
        if (document.Resolve(field.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
        {
            foreach (var kid in kids)
            {
                if (document.Resolve(kid) is PdfDictionary widget && !widget.ContainsKey("T"))
                    widgets.Add(widget);
            }
        }
        return widgets;
    }

    /// <summary>
    /// Take <paramref name="field"/> out of the field tree and its widgets out of every page's
    /// <c>/Annots</c>. Each array edit is written back through its owning dictionary.
    /// </summary>
    private static bool Detach(PdfDocument document, PdfDictionary field, PdfDictionary? parent, List<PdfDictionary> widgets)
    {
        PdfDictionary owner;
        if (parent != null)
        {
            owner = parent;
        }
        else if (document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary acroForm)
        {
            owner = acroForm;
        }
        else
        {
            return false;
        }

        var key = parent != null ? "Kids" : "Fields";
        if (!RemoveFrom(document, owner, key, field))
            return false;
        // A non-terminal parent left with no kids is a field that holds nothing; take it out too,
        // walking up, so no empty namesake of a generated subform field stays in the tree.
        while (parent != null
            && document.Resolve(parent.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray { Count: 0 }
            && !parent.ContainsKey("FT") && !parent.ContainsKey("V"))
        {
            var grandparent = document.Resolve(parent.GetOptional("Parent") ?? PdfNull.Instance) as PdfDictionary;
            var acroForm = document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) as PdfDictionary;
            var holder = grandparent ?? acroForm;
            if (holder == null || !RemoveFrom(document, holder, grandparent != null ? "Kids" : "Fields", parent))
                break;
            parent = grandparent;
        }

        foreach (var page in document.Pages)
        {
            foreach (var widget in widgets)
                RemoveFrom(document, page.Dictionary, "Annots", widget);
        }
        return true;
    }

    private static bool RemoveFrom(PdfDocument document, PdfDictionary owner, string key, PdfDictionary item)
    {
        var entry = owner.GetOptional(key);
        if (document.Resolve(entry ?? PdfNull.Instance) is not PdfArray array)
            return false;
        var copy = new PdfArray();
        bool found = false;
        foreach (var element in array)
        {
            if (ReferenceEquals(document.Resolve(element), item))
                found = true;
            else
                copy.Add(element);
        }
        if (found)
            owner[key] = copy;   // a fresh direct array on the dictionary, which marks it modified
        return found;
    }
}
