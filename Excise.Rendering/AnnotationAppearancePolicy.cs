using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Rendering;

/// <summary>
/// Non-canvas policy for deciding whether an annotation participates in a render
/// and which normal appearance stream, if any, the document selected.
/// </summary>
internal static class AnnotationAppearancePolicy
{
    public static AnnotationVisibilityDecision EvaluateVisibility(
        PdfAnnotation annotation,
        RenderOptions options)
    {
        var isFieldOrLink = annotation.Subtype is
            PdfAnnotationSubtype.Widget or PdfAnnotationSubtype.Link;

        if (isFieldOrLink && !options.ShowFieldAndLinkAnnotations)
            return new(AnnotationVisibilityDisposition.CategoryDisabled, isFieldOrLink);

        if (!isFieldOrLink && !options.ShowCommentAnnotations)
            return new(AnnotationVisibilityDisposition.CategoryDisabled, isFieldOrLink);

        if ((annotation.Flags & (PdfAnnotationFlags.Hidden | PdfAnnotationFlags.NoView)) != 0
            && !options.RevealHiddenAnnotations)
        {
            return new(AnnotationVisibilityDisposition.HiddenByFlags, isFieldOrLink);
        }

        // Invisible is narrower than its name (§12.5.3): it suppresses only
        // non-standard annotations with no handler. Standard subtypes still draw;
        // treating the bit as an unconditional skip blanked conformance fixtures.
        if ((annotation.Flags & PdfAnnotationFlags.Invisible) != 0
            && annotation.Subtype == PdfAnnotationSubtype.Unknown)
        {
            return new(AnnotationVisibilityDisposition.UnsupportedInvisible, isFieldOrLink);
        }

        return new(AnnotationVisibilityDisposition.Render, isFieldOrLink);
    }

    public static PdfStream? ResolveNormalAppearance(
        PdfAnnotation annotation,
        PdfDocument document,
        ICollection<string>? diagnostics)
    {
        var appearanceObject = annotation.RawDictionary.GetOptional("AP");
        if (appearanceObject == null
            || document.Resolve(appearanceObject) is not PdfDictionary appearanceDictionary)
        {
            return null;
        }

        var normalObject = appearanceDictionary.GetOptional("N");
        if (normalObject == null)
            return null;

        var resolvedNormal = document.Resolve(normalObject);
        if (resolvedNormal is PdfStream stream)
            return stream;

        if (resolvedNormal is not PdfDictionary stateDictionary)
            return null;

        var stateName = annotation.RawDictionary.GetNameOrNull("AS");
        if (stateName != null)
        {
            var selectedObject = stateDictionary.GetOptional(stateName);
            return selectedObject != null
                ? document.Resolve(selectedObject) as PdfStream
                : null;
        }

        PdfStream? only = null;
        foreach (var entry in stateDictionary)
        {
            if (document.Resolve(entry.Value) is not PdfStream candidate)
                continue;

            if (only != null)
            {
                diagnostics?.Add(
                    $"Annotation /{annotation.Subtype} has no /AS and /AP /N defines several " +
                    "appearance states; nothing drawn (§12.5.5 makes /AS the selector).");
                return null;
            }

            only = candidate;
        }

        return only;
    }
}

internal readonly record struct AnnotationVisibilityDecision(
    AnnotationVisibilityDisposition Disposition,
    bool IsFieldOrLink)
{
    public bool ShouldRender => Disposition == AnnotationVisibilityDisposition.Render;
}

internal enum AnnotationVisibilityDisposition
{
    Render,
    CategoryDisabled,
    HiddenByFlags,
    UnsupportedInvisible,
}
