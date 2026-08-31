using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Rendering;

/// <summary>
/// Non-canvas policy for deciding whether an annotation participates in a render
/// and which normal appearance stream, if any, the document selected.
/// </summary>
internal static class AnnotationAppearancePolicy
{
    public static IReadOnlyList<AnnotationRenderSelection> SelectVisibleAnnotations(
        IReadOnlyList<PdfAnnotation> annotations,
        RenderOptions options,
        ICollection<string>? diagnostics)
    {
        if (annotations.Count == 0)
            return [];

        var selected = new List<AnnotationRenderSelection>(annotations.Count);
        foreach (var annotation in annotations)
        {
            var visibility = EvaluateVisibility(annotation, options);
            if (visibility.ShouldRender)
            {
                selected.Add(new AnnotationRenderSelection(annotation, visibility.IsFieldOrLink));
                continue;
            }

            if (visibility.Disposition == AnnotationVisibilityDisposition.UnsupportedInvisible)
            {
                diagnostics?.Add(
                    "Annotation of a non-standard subtype has the Invisible flag and no " +
                    "handler; not drawn (§12.5.3).");
            }
        }

        return selected;
    }

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

    public static AnnotationSynthesisKind SelectSynthesis(PdfAnnotationSubtype subtype)
        => subtype switch
        {
            PdfAnnotationSubtype.Widget => AnnotationSynthesisKind.Widget,
            PdfAnnotationSubtype.Link => AnnotationSynthesisKind.Link,
            PdfAnnotationSubtype.Text => AnnotationSynthesisKind.StickyNote,
            PdfAnnotationSubtype.Square => AnnotationSynthesisKind.Square,
            PdfAnnotationSubtype.Circle => AnnotationSynthesisKind.Circle,
            PdfAnnotationSubtype.FreeText => AnnotationSynthesisKind.FreeText,
            PdfAnnotationSubtype.Highlight or
            PdfAnnotationSubtype.Underline or
            PdfAnnotationSubtype.Squiggly or
            PdfAnnotationSubtype.StrikeOut => AnnotationSynthesisKind.TextMarkup,
            PdfAnnotationSubtype.Line => AnnotationSynthesisKind.Line,
            PdfAnnotationSubtype.Polygon => AnnotationSynthesisKind.Polygon,
            PdfAnnotationSubtype.PolyLine => AnnotationSynthesisKind.PolyLine,
            PdfAnnotationSubtype.Ink => AnnotationSynthesisKind.Ink,
            _ => AnnotationSynthesisKind.None,
        };
}

internal readonly record struct AnnotationRenderSelection(
    PdfAnnotation Annotation,
    bool IsFieldOrLink);

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

internal enum AnnotationSynthesisKind
{
    None,
    Widget,
    Link,
    StickyNote,
    Square,
    Circle,
    FreeText,
    TextMarkup,
    Line,
    Polygon,
    PolyLine,
    Ink,
}
