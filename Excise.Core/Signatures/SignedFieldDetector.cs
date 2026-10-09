using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Signatures;

/// <summary>
/// Cheap in-memory check for "does this document have a signed /Sig field" --
/// shared by <see cref="SignatureApplicationService"/>'s already-signed guard
/// (issue #623) and <see cref="PdfDocumentService.HasSignatures"/> (#1415).
/// This is not cryptographic verification -- <see cref="SignatureVerificationService"/>
/// does that, from a saved file path. This only asks whether editing risks
/// invalidating something, so it stays a plain AcroForm field-tree walk.
/// </summary>
internal static class SignedFieldDetector
{
    /// <summary>Deeper than any real field tree; a cycle or a hostile file stops here.</summary>
    private const int MaxDepth = 64;

    public static bool HasSignedField(PdfDocument document)
        => Fields(document).Any(f => IsSignatureField(document, f.Field) && f.Field.GetOptional("V") != null);

    /// <summary>
    /// Every field dictionary of the AcroForm field tree, depth first, with its
    /// fully qualified name (ISO 32000-2 §12.7.4.2: the partial names joined
    /// by periods; a node without <c>/T</c> adds no segment). The whole tree,
    /// not only the top-level <c>/Fields</c> entries (#2025): a Designer form
    /// files its signature field under a subform (IMM 5257e's
    /// <c>form1[0].SignatureField4[0]</c>). A widget kid that is not a field
    /// (no <c>/T</c>, no field keys) is still yielded; callers that want
    /// fields only check what they need. Each dictionary once.
    /// </summary>
    internal static IEnumerable<(PdfDictionary Field, string FullName, PdfDictionary? Parent)> Fields(PdfDocument document)
    {
        var acroForm = document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) as PdfDictionary;
        if (acroForm == null
            || document.Resolve(acroForm.GetOptional("Fields") ?? PdfNull.Instance) is not PdfArray roots)
            yield break;

        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfObject Node, string Prefix, PdfDictionary? Parent, int Depth)>();
        for (int i = roots.Count - 1; i >= 0; i--)
            stack.Push((roots[i], string.Empty, null, 0));

        while (stack.Count > 0)
        {
            var (node, prefix, parent, depth) = stack.Pop();
            if (depth > MaxDepth || document.Resolve(node) is not PdfDictionary field || !visited.Add(field))
                continue;

            var partial = document.Resolve(field.GetOptional("T") ?? PdfNull.Instance) is PdfString t ? t.Value : null;
            var fullName = partial == null ? prefix : prefix.Length == 0 ? partial : prefix + "." + partial;
            yield return (field, fullName, parent);

            if (document.Resolve(field.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                    stack.Push((kids[i], fullName, field, depth + 1));
            }
        }
    }

    /// <summary>§12.7.4.1: <c>/FT</c> is inheritable, so a kid finds it on an ancestor.</summary>
    internal static bool IsSignatureField(PdfDocument document, PdfDictionary field)
    {
        PdfDictionary? node = field;
        for (var depth = 0; node != null && depth < MaxDepth; depth++)
        {
            if (node.GetNameOrNull("FT") is { } type)
                return type == "Sig";
            node = document.Resolve(node.GetOptional("Parent") ?? PdfNull.Instance) as PdfDictionary;
        }
        return false;
    }
}
