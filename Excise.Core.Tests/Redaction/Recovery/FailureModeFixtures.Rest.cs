using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static Excise.Core.Tests.Redaction.Recovery.RecoveryFixtureBuilder;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1645 batch 3 — the remaining modes: contrast and coverage on a covering
/// box, invisible text, covered non-text, the residual artefacts, the form
/// value and the structure-tree carrier.
/// </summary>
internal static partial class FailureModeFixtures
{
    // ── box-light-or-low-contrast ───────────────────────────────────────────
    // Parameter: CONTRAST, measured as RGB distance, against the 0.20 threshold
    // the detector actually uses. A label would say "low contrast"; these say
    // how low, on each side of the line.

    /// <summary>Black on black — distance 0. The unambiguous case.</summary>
    internal static byte[] LowContrastExact(string secret)
        => TextOnFill(secret, textOp: "0 0 0 rg", fillOp: "0 0 0 rg");

    /// <summary>
    /// gray(0.10) text on gray(0.15) — distance ~0.087, comfortably under the
    /// 0.20 threshold and genuinely unreadable on screen.
    /// </summary>
    internal static byte[] LowContrastNearMatch(string secret)
        => TextOnFill(secret, textOp: "0.10 g", fillOp: "0.15 g");

    /// <summary>
    /// ⚠️ A NEGATIVE control on the other side of the same threshold: pure RED
    /// text on BLACK. Luminance contrast is only 0.21 — a luminance test
    /// false-positives here — but the RGB distance is 1.0 and the text is
    /// perfectly readable. This is why the detector uses distance, and the
    /// fixture is what stops someone "simplifying" it back to luminance.
    /// </summary>
    internal static byte[] RedOnBlackIsReadable(string secret)
        => TextOnFill(secret, textOp: "1 0 0 rg", fillOp: "0 0 0 rg");

    private static byte[] TextOnFill(string text, string textOp, string fillOp, double fontSize = 14)
    {
        const double x = 72, y = 700;
        var width = text.Length * fontSize * 0.78 + 6;
        return Build(string.Create(CultureInfo.InvariantCulture,
            $"q {fillOp} {x - 2} {y - 3} {width + 4} {fontSize + 3} re f Q\n" +
            $"BT {textOp} /F1 {fontSize} Tf {x} {y} Td ({text}) Tj ET\n"));
    }

    // ── text-render-mode-3 ──────────────────────────────────────────────────
    // Parameter: WHERE the mode is set and whether q/Q restores it. §9.3.6's Tr
    // is Table 52 text state, so a `q 3 Tr … Q` must leave later text VISIBLE.

    /// <summary>Plain <c>3 Tr</c> — how every OCR layer is written.</summary>
    internal static byte[] InvisibleTextPlain(string secret) => InvisibleText(secret);

    /// <summary>
    /// <c>7 Tr</c> — add-to-clip-path, also invisible per §9.3.6 Table 106. A
    /// detector keyed on the literal 3 misses it.
    /// </summary>
    internal static byte[] InvisibleTextClipMode(string secret)
        => Build(string.Create(CultureInfo.InvariantCulture,
            $"BT /F1 14 Tf 7 Tr 72 700 Td ({secret}) Tj ET\n"));

    /// <summary>
    /// ⚠️ A NEGATIVE control for the Table 52 restore. The invisible run is
    /// inside <c>q … Q</c>; the text AFTER the Q is visible and must not be
    /// reported. A detector that leaks Tr past the restore reports both.
    /// </summary>
    internal static byte[] InvisibleTextRestoredByQ(string secret, string visible = "PUBLIC")
        => Build(string.Create(CultureInfo.InvariantCulture,
            $"q BT /F1 14 Tf 3 Tr 72 700 Td ({secret}) Tj ET Q\n" +
            $"BT /F1 14 Tf 72 660 Td ({visible}) Tj ET\n"));

    // ── image-covered-only / vector-covered-only ────────────────────────────
    // Parameter: how much of the content the box covers, and whether the cover
    // is opaque. `present-only` is the right answer for all of these — the
    // channel establishes that data survives, and does not decode it.

    internal static byte[] ImageFullyCovered() => ImageUnderBox();

    /// <summary>
    /// The box covers a 60% middle band; the image's edges show. Above the 50%
    /// majority rule CoversMajority applies — at 40% it is correctly NOT
    /// reported, which is the rule working, not a gap.
    /// </summary>
    internal static byte[] ImagePartlyCovered(double size = 120)
    {
        const double x = 72, y = 600;
        var pixels = new byte[] { 0x00, 0x40, 0x80, 0xFF };
        return Build(string.Create(CultureInfo.InvariantCulture,
                $"q {size} 0 0 {size} {x} {y} cm /Im0 Do Q\n" +
                $"q 0 0 0 rg {x} {y + size * 0.2} {size} {size * 0.6} re f Q\n"),
            extraObjects: new List<Obj>
            {
                new("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
                    "/ColorSpace /DeviceGray /BitsPerComponent 8 /Length 4 >>", pixels),
            },
            resourcesExtra: "/XObject << /Im0 7 0 R >>");
    }

    /// <summary>Two images, one covered and one not — only the covered one is a leak.</summary>
    internal static byte[] OneOfTwoImagesCovered(double size = 100)
    {
        var pixels = new byte[] { 0x00, 0x40, 0x80, 0xFF };
        return Build(string.Create(CultureInfo.InvariantCulture,
                $"q {size} 0 0 {size} 72 600 cm /Im0 Do Q\n" +
                $"q {size} 0 0 {size} 320 600 cm /Im0 Do Q\n" +
                $"q 0 0 0 rg 72 600 {size} {size} re f Q\n"),
            extraObjects: new List<Obj>
            {
                new("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
                    "/ColorSpace /DeviceGray /BitsPerComponent 8 /Length 4 >>", pixels),
            },
            resourcesExtra: "/XObject << /Im0 7 0 R >>");
    }

    internal static byte[] VectorFullyCovered() => VectorUnderBox();

    /// <summary>A FILLED vector shape rather than strokes, fully covered.</summary>
    internal static byte[] FilledVectorCovered(double size = 120)
        => Build(string.Create(CultureInfo.InvariantCulture,
            $"q 0 0 1 rg 80 610 {size - 20} {size - 20} re f Q\n" +
            $"q 0 0 0 rg 72 600 {size} {size} re f Q\n"));

    /// <summary>A curve (c operator) under the box — bounds come from control points.</summary>
    internal static byte[] CurveUnderBox(double size = 120)
        => Build(string.Create(CultureInfo.InvariantCulture,
            $"q 2 w 0 0 1 RG 80 610 m 100 700 140 620 180 700 c S Q\n" +
            $"q 0 0 0 rg 72 600 {size} {size} re f Q\n"));

    // ── leftover-page-thumbnail ─────────────────────────────────────────────
    // Parameter: WHICH page carries it, since /Thumb is per-page and a report
    // that says "the document has a thumbnail" is not actionable.

    internal static byte[] ThumbnailOnTheOnlyPage() => PageWithThumbnail();

    /// <summary>A larger thumbnail — big enough to read the page from.</summary>
    internal static byte[] LargeThumbnail(int side = 64)
    {
        var pixels = new byte[side * side];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);
        return Build("BT /F1 14 Tf 72 700 Td (VISIBLE) Tj ET\n",
            extraObjects: new List<Obj>
            {
                new($"<< /Type /XObject /Subtype /Image /Width {side} /Height {side} " +
                    $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Length {pixels.Length} >>", pixels),
            },
            pageExtra: "/Thumb 7 0 R");
    }

    /// <summary>An RGB thumbnail rather than greyscale — a different decode path.</summary>
    internal static byte[] RgbThumbnail(int side = 8)
    {
        var pixels = new byte[side * side * 3];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);
        return Build("BT /F1 14 Tf 72 700 Td (VISIBLE) Tj ET\n",
            extraObjects: new List<Obj>
            {
                new($"<< /Type /XObject /Subtype /Image /Width {side} /Height {side} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} >>", pixels),
            },
            pageExtra: "/Thumb 7 0 R");
    }

    // ── leftover-embedded-file ──────────────────────────────────────────────
    // Parameter: WHERE the file specification hangs. The catalog name tree is
    // the obvious one; /AF and a FileAttachment annotation are the two that get
    // missed, and #1572 is the record of exactly that.

    internal static byte[] AttachmentInNameTree() => PageWithAttachment();

    /// <summary>A file attached to the PAGE via /AF rather than the name tree.</summary>
    internal static byte[] AttachmentOnPageAssociatedFiles(string fileName = "notes.txt")
        => AttachmentFixture(fileName, pageExtra: "/AF [8 0 R]");

    /// <summary>A /FileAttachment annotation — a third place entirely.</summary>
    internal static byte[] AttachmentAsAnnotation(string fileName = "notes.txt")
        => AttachmentFixture(fileName,
            pageExtra: "/Annots [9 0 R]",
            extraAnnot: "<< /Type /Annot /Subtype /FileAttachment /Rect [72 700 92 720] " +
                        "/FS 8 0 R /P 3 0 R >>");

    private static byte[] AttachmentFixture(string fileName, string pageExtra, string? extraAnnot = null)
    {
        var data = Encoding.ASCII.GetBytes("the quick brown fox");
        var objects = new List<Obj>
        {
            new($"<< /Length {data.Length} >>", data),                                  // 7: the bytes
            new($"<< /Type /Filespec /F ({fileName}) /EF << /F 7 0 R >> >>"),            // 8: the spec
        };
        if (extraAnnot != null) objects.Add(new(extraAnnot));                            // 9: the annot
        return Build("BT /F1 14 Tf 72 700 Td (VISIBLE) Tj ET\n",
            extraObjects: objects, pageExtra: pageExtra);
    }

    // ── leftover-form-value ─────────────────────────────────────────────────
    // Parameter: the FIELD TYPE, because /V means something different in each
    // and a text-only reader misses the other two.

    internal static byte[] FormTextFieldValue(string value) => FormFieldValueUnderBox("ssn", value);

    /// <summary>A /Ch choice field — /V is the selected option.</summary>
    internal static byte[] FormChoiceFieldValue(string value)
        => FormFieldFixture("/FT /Ch", $"/V ({value}) /Opt [({value}) (Other)]", "choice");

    /// <summary>
    /// A field whose value is in /DV (the DEFAULT) with /V absent — a viewer
    /// resets to it, so it leaks exactly as /V does.
    /// </summary>
    internal static byte[] FormDefaultValueOnly(string value)
        => FormFieldFixture("/FT /Tx", $"/DV ({value})", "defaulted");

    private static byte[] FormFieldFixture(string fieldType, string valueEntry, string name)
        => Build("q 0 0 0 rg 72 700 160 18 re f Q\n",
            extraObjects: new List<Obj>
            {
                new($"<< /Type /Annot /Subtype /Widget {fieldType} /T ({name}) {valueEntry} " +
                    $"/Rect [72 700 232 718] /F 4 /P 3 0 R >>"),
            },
            pageExtra: "/Annots [7 0 R]",
            catalogExtra: "/AcroForm << /Fields [7 0 R] /NeedAppearances true >>");

    // ── structure-tree-carrier ──────────────────────────────────────────────
    // Parameter: WHICH key on the structure element. §14.9.4 gives three, and a
    // scrubber or reader that knows one knows a third of the carrier.

    internal static byte[] StructureActualText(string secret) => StructureCarrier(secret, "ActualText");
    internal static byte[] StructureAlt(string secret) => StructureCarrier(secret, "Alt");

    /// <summary>
    /// <c>/E</c>, the expansion of an abbreviation. The least-known of the
    /// three and the one most likely to be forgotten by a scrubber.
    /// </summary>
    internal static byte[] StructureExpansion(string secret) => StructureCarrier(secret, "E");

    private static byte[] StructureCarrier(string secret, string key)
        => Build("q 0 0 0 rg 70 697 160 20 re f Q\n",
            extraObjects: new List<Obj>
            {
                new($"<< /Type /StructTreeRoot /K 8 0 R >>"),
                new($"<< /Type /StructElem /S /Span /P 7 0 R /Pg 3 0 R /{key} ({secret}) >>"),
            },
            catalogExtra: "/StructTreeRoot 7 0 R");
}
