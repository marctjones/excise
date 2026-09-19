using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1645 — which fixtures belong to which mode. The COUNT the coverage gate
/// reads comes from here, never from a registry row's prose.
///
/// <para>⚠️ A builder method not listed here is invisible to the gate. That is
/// the one weak seam in this design and it is deliberate: the alternative is
/// reflecting over method names, which would count a helper as a fixture and
/// silently inflate every mode. An explicit list is checkable by eye; a clever
/// one is not.</para>
/// </summary>
internal static partial class FailureModeFixtures
{
    /// <summary>Mode id → the variants that exercise it, by name.</summary>
    private static readonly Dictionary<string, string[]> Index = new(StringComparer.Ordinal)
    {
        // Batch 1 — the hardest to synthesise.
        ["incremental-update-prior-revision"] = new[]
            { nameof(PriorRevisionDepth1), nameof(PriorRevisionDepth3),
              nameof(PriorRevisionSecretsAtDifferentHops) },
        ["leftover-xfa"] = new[]
            { nameof(XfaFlat), nameof(XfaNestedSubform), nameof(XfaAmongSiblings) },
        ["image-smask-trick"] = new[]
            { nameof(SMaskAllZero), nameof(SMaskNearlyAllZero), nameof(ImageDrawnFullyTransparent) },
        ["image-original-object-retained"] = new[]
            { nameof(OrphanWithMatchingReplacement), nameof(TwoOrphansWithMatchingReplacement),
              nameof(OrphanWithNoMatchingReplacement) },

        // Batch 2 — the mark families.
        ["box-drawn-by-annotation"] = new[]
            { nameof(AnnotationSquareOverText), nameof(AnnotationHighlightOverText),
              nameof(AnnotationStampOverText) },
        ["box-inside-form-xobject"] = new[]
            { nameof(BoxInFormDepth1), nameof(BoxInFormDepth2), nameof(BoxInFormDepth3),
              nameof(BoxInFormChildScopedToTheForm) },
        ["redact-annotation-unapplied"] = new[]
            { nameof(UnappliedRedactOverAWord), nameof(UnappliedRedactWithOverlayText),
              nameof(UnappliedRedactOverPartOfALine) },
        ["marked-content-carrier"] = new[]
            { nameof(MarkedContentActualText), nameof(MarkedContentAlt),
              nameof(MarkedContentNamedPropertyList) },

        // Batch 3 — the rest.
        ["box-light-or-low-contrast"] = new[]
            { nameof(LowContrastExact), nameof(LowContrastNearMatch), nameof(RedOnBlackIsReadable) },
        ["text-render-mode-3"] = new[]
            { nameof(InvisibleTextPlain), nameof(InvisibleTextClipMode),
              nameof(InvisibleTextRestoredByQ) },
        ["image-covered-only"] = new[]
            { nameof(ImageFullyCovered), nameof(ImagePartlyCovered), nameof(OneOfTwoImagesCovered) },
        ["vector-covered-only"] = new[]
            { nameof(VectorFullyCovered), nameof(FilledVectorCovered), nameof(CurveUnderBox) },
        ["leftover-page-thumbnail"] = new[]
            { nameof(ThumbnailOnTheOnlyPage), nameof(LargeThumbnail), nameof(RgbThumbnail) },
        ["leftover-embedded-file"] = new[]
            { nameof(AttachmentInNameTree), nameof(AttachmentOnPageAssociatedFiles),
              nameof(AttachmentAsAnnotation) },
        ["leftover-form-value"] = new[]
            { nameof(FormTextFieldValue), nameof(FormChoiceFieldValue), nameof(FormDefaultValueOnly) },
        ["structure-tree-carrier"] = new[]
            { nameof(StructureActualText), nameof(StructureAlt), nameof(StructureExpansion) },

        // ⚠️ Covered by the SYNTHETIC CORPUS rather than by hand-written
        // fixtures, so the count comes from its bands. Listed by the cases the
        // generator emits, not by C# methods — see gen-redaction-corpus.py.
        ["ocr-layer-left-in-place"] = new[]
            { "UnredactOcrChannelTests: scanned page", "…: OCR layer over image",
              "…: OCR layer under a box" },
        ["partial-glyph-removal-kerning"] = new[]
            { "residue band B1 (width-preserving)", "residue band Bc (monospace)",
              "residue band Bn (digit runs)" },
    };

    /// <summary>Every mode this file claims fixtures for.</summary>
    public static IReadOnlyCollection<string> Modes => Index.Keys;

    /// <summary>How many variants exercise <paramref name="modeId"/>.</summary>
    public static int CountFor(string modeId) =>
        Index.TryGetValue(modeId, out var v) ? v.Length : 0;

    /// <summary>The variant names, for a report that wants to list them.</summary>
    public static IReadOnlyList<string> VariantsFor(string modeId) =>
        Index.TryGetValue(modeId, out var v) ? v : Array.Empty<string>();
}
