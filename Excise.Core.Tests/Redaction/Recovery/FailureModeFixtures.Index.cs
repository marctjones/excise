using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1645 — which fixtures belong to which mode, how to build each one, and
/// what the GROUND TRUTH is. The coverage gate reads the count from here and
/// the confusion matrix reads the cases from here; neither reads a registry
/// row's `evidence` prose, which is human-maintained and drifts.
///
/// <para>⚠️ The index is an EXPLICIT LIST and that weak seam is deliberate.
/// Reflecting over method names would count helpers as fixtures and silently
/// inflate every mode. A builder nobody lists is invisible to the gate, which
/// is the trade: an explicit list is checkable by eye, a clever one is not.</para>
/// </summary>
internal static partial class FailureModeFixtures
{
    /// <param name="ContainsLeak">
    /// ⚠️ GROUND TRUTH. Whether the document actually hides something — NOT
    /// whether excise finds it. A case whose truth came from a tool's output
    /// would make the false-positive column unable to fail (#1624).
    /// </param>
    /// <param name="ExpectedMiss">
    /// A leak excise is known not to detect, with a recorded reason. It still
    /// scores as a FALSE NEGATIVE — the matrix must not launder a gap — but the
    /// report separates documented misses from surprises, because an
    /// undocumented one is the finding.
    /// </param>
    /// <param name="Build">Null for a mode whose fixtures live in the synthetic corpus.</param>
    internal sealed record Variant(
        string Name, Func<byte[]>? Build, bool ContainsLeak = true,
        bool ExpectedMiss = false, string? MissReason = null);

    private const string Secret = "KILIMNIK";
    private const string Value = "123-45-6789";

    private static readonly Dictionary<string, Variant[]> Index = new(StringComparer.Ordinal)
    {
        // ── Batch 1: the hardest to synthesise ──────────────────────────────
        ["incremental-update-prior-revision"] = new[]
        {
            new Variant(nameof(PriorRevisionDepth1), () => PriorRevisionDepth1(Secret)),
            new Variant(nameof(PriorRevisionDepth3), () => PriorRevisionDepth3(Secret)),
            new Variant(nameof(PriorRevisionSecretsAtDifferentHops),
                () => PriorRevisionSecretsAtDifferentHops(Secret, "MADRID")),
        },
        ["leftover-xfa"] = new[]
        {
            new Variant(nameof(XfaFlat), () => XfaFlat(Value)),
            new Variant(nameof(XfaNestedSubform), () => XfaNestedSubform(Value)),
            new Variant(nameof(XfaAmongSiblings), () => XfaAmongSiblings(Value)),
        },
        ["image-smask-trick"] = new[]
        {
            new Variant(nameof(SMaskAllZero), () => SMaskAllZero()),
            new Variant(nameof(SMaskNearlyAllZero), () => SMaskNearlyAllZero(),
                ExpectedMiss: true,
                MissReason: "#1608: only an EXACTLY all-zero 8-bit grey mask is detected"),
            new Variant(nameof(ImageDrawnFullyTransparent), () => ImageDrawnFullyTransparent(),
                ExpectedMiss: true,
                MissReason: "#1608: /ca is not tracked; transparency by graphics state is invisible"),
        },
        ["image-original-object-retained"] = new[]
        {
            new Variant(nameof(OrphanWithMatchingReplacement), () => OrphanWithMatchingReplacement()),
            new Variant(nameof(TwoOrphansWithMatchingReplacement), () => TwoOrphansWithMatchingReplacement()),
            // A NEGATIVE: update debris, not a swap. Reporting it would drown
            // the channel — #1624 from the other direction.
            new Variant(nameof(OrphanWithNoMatchingReplacement), () => OrphanWithNoMatchingReplacement(),
                ContainsLeak: false),
        },

        // ── Batch 2: the mark families ──────────────────────────────────────
        ["box-drawn-by-annotation"] = new[]
        {
            new Variant(nameof(AnnotationSquareOverText), () => AnnotationSquareOverText(Secret)),
            new Variant(nameof(AnnotationHighlightOverText), () => AnnotationHighlightOverText(Secret)),
            new Variant(nameof(AnnotationStampOverText), () => AnnotationStampOverText(Secret),
                ExpectedMiss: true,
                MissReason: "a dark /Stamp is overwhelmingly a legitimate graphic (FILED, an " +
                            "exhibit sticker); accepted subtypes are Square/Circle/Polygon/Highlight"),
        },
        ["box-inside-form-xobject"] = new[]
        {
            new Variant(nameof(BoxInFormDepth1), () => BoxInFormDepth1(Secret)),
            new Variant(nameof(BoxInFormDepth2), () => BoxInFormDepth2(Secret)),
            new Variant(nameof(BoxInFormDepth3), () => BoxInFormDepth3(Secret)),
            new Variant(nameof(BoxInFormChildScopedToTheForm), () => BoxInFormChildScopedToTheForm(Secret)),
        },
        ["redact-annotation-unapplied"] = new[]
        {
            new Variant(nameof(UnappliedRedactOverAWord), () => UnappliedRedactOverAWord(Secret)),
            new Variant(nameof(UnappliedRedactWithOverlayText), () => UnappliedRedactWithOverlayText(Secret)),
            new Variant(nameof(UnappliedRedactOverPartOfALine), () => UnappliedRedactOverPartOfALine(Secret)),
        },
        ["marked-content-carrier"] = new[]
        {
            new Variant(nameof(MarkedContentActualText), () => MarkedContentActualText(Secret)),
            new Variant(nameof(MarkedContentAlt), () => MarkedContentAlt(Secret)),
            new Variant(nameof(MarkedContentNamedPropertyList), () => MarkedContentNamedPropertyList(Secret)),
        },

        // ── Batch 3: the rest ───────────────────────────────────────────────
        ["box-light-or-low-contrast"] = new[]
        {
            new Variant(nameof(LowContrastExact), () => LowContrastExact(Secret)),
            new Variant(nameof(LowContrastNearMatch), () => LowContrastNearMatch(Secret)),
            // Readable red on black: a LEAK all the same — #1180 calls it a
            // visible failed redaction, and an audit must surface it.
            new Variant(nameof(RedOnBlackIsReadable), () => RedOnBlackIsReadable(Secret)),
        },
        ["text-render-mode-3"] = new[]
        {
            new Variant(nameof(InvisibleTextPlain), () => InvisibleTextPlain(Secret)),
            new Variant(nameof(InvisibleTextClipMode), () => InvisibleTextClipMode(Secret)),
            new Variant(nameof(InvisibleTextRestoredByQ), () => InvisibleTextRestoredByQ(Secret)),
        },
        ["image-covered-only"] = new[]
        {
            new Variant(nameof(ImageFullyCovered), () => ImageFullyCovered()),
            new Variant(nameof(ImagePartlyCovered), () => ImagePartlyCovered()),
            new Variant(nameof(OneOfTwoImagesCovered), () => OneOfTwoImagesCovered()),
        },
        ["vector-covered-only"] = new[]
        {
            new Variant(nameof(VectorFullyCovered), () => VectorFullyCovered()),
            new Variant(nameof(FilledVectorCovered), () => FilledVectorCovered()),
            new Variant(nameof(CurveUnderBox), () => CurveUnderBox()),
        },
        ["leftover-page-thumbnail"] = new[]
        {
            new Variant(nameof(ThumbnailOnTheOnlyPage), () => ThumbnailOnTheOnlyPage()),
            new Variant(nameof(LargeThumbnail), () => LargeThumbnail()),
            new Variant(nameof(RgbThumbnail), () => RgbThumbnail()),
        },
        ["leftover-embedded-file"] = new[]
        {
            new Variant(nameof(AttachmentInNameTree), () => AttachmentInNameTree()),
            new Variant(nameof(AttachmentOnPageAssociatedFiles), () => AttachmentOnPageAssociatedFiles(),
                ExpectedMiss: true,
                MissReason: "#1667: the audit's embedded-file walk is catalog-only while the " +
                            "SCRUBBER walks page /AF — excise removes what it cannot see"),
            new Variant(nameof(AttachmentAsAnnotation), () => AttachmentAsAnnotation()),
        },
        ["leftover-form-value"] = new[]
        {
            new Variant(nameof(FormTextFieldValue), () => FormTextFieldValue(Value)),
            new Variant(nameof(FormChoiceFieldValue), () => FormChoiceFieldValue(Value)),
            new Variant(nameof(FormDefaultValueOnly), () => FormDefaultValueOnly(Value)),
        },
        ["structure-tree-carrier"] = new[]
        {
            new Variant(nameof(StructureActualText), () => StructureActualText(Secret)),
            new Variant(nameof(StructureAlt), () => StructureAlt(Secret)),
            new Variant(nameof(StructureExpansion), () => StructureExpansion(Secret)),
        },

        // ⚠️ Covered by the SYNTHETIC CORPUS, not by C# builders. Listed with a
        // null Build so the count is honest and the matrix reports them as NOT
        // MEASURED rather than silently scoring zero — see
        // gen-redaction-corpus.py and ResidueRecoveryRecallTests.
        ["ocr-layer-left-in-place"] = new[]
        {
            new Variant("UnredactOcrChannelTests: scanned page", null),
            new Variant("…: OCR layer over an image", null),
            new Variant("…: OCR layer under a box", null),
        },
        ["partial-glyph-removal-kerning"] = new[]
        {
            new Variant("residue band B1 (width-preserving)", null),
            new Variant("residue band Bc (monospace)", null),
            new Variant("residue band Bn (digit runs)", null),
        },
    };

    /// <summary>The text planted in a text-bearing fixture.</summary>
    public const string PlantedSecret = Secret;

    /// <summary>The value planted in a form/XFA fixture.</summary>
    public const string PlantedValue = Value;

    public static IReadOnlyCollection<string> Modes => Index.Keys;

    public static int CountFor(string modeId) =>
        Index.TryGetValue(modeId, out var v) ? v.Length : 0;

    public static IReadOnlyList<Variant> VariantsFor(string modeId) =>
        Index.TryGetValue(modeId, out var v) ? v : Array.Empty<Variant>();

    /// <summary>Every variant this assembly can actually build, with its mode.</summary>
    public static IEnumerable<(string ModeId, Variant Variant)> Buildable() =>
        Index.SelectMany(kv => kv.Value.Where(v => v.Build != null).Select(v => (kv.Key, v)));

    /// <summary>Modes whose fixtures are NOT buildable here, so a report can say so.</summary>
    public static IReadOnlyList<string> CorpusBackedModes() =>
        Index.Where(kv => kv.Value.All(v => v.Build == null)).Select(kv => kv.Key).ToList();
}
