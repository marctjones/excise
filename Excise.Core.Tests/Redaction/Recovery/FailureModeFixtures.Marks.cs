using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using static Excise.Core.Tests.Redaction.Recovery.RecoveryFixtureBuilder;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1645 batch 2 — the MARK families: a box drawn by an annotation, a box
/// inside a Form XObject, an unapplied /Redact annotation, and the two
/// marked-content/structure carriers.
///
/// <para>The parameter that matters here is <b>where the mark comes from</b> and
/// <b>how much of the glyph it covers</b>. A channel that finds a box in the
/// page content stream does not automatically find the same box arriving via an
/// annotation appearance or a nested form, and a 50% coverage rule behaves
/// differently at 55% than at 95%.</para>
/// </summary>
internal static partial class FailureModeFixtures
{
    // ── box-drawn-by-annotation ─────────────────────────────────────────────
    // Parameter: WHICH annotation subtype paints the box. The mark does not
    // exist in the content stream at all, so a content-only walk sees nothing.

    /// <summary>A /Square annotation with an interior colour over the text.</summary>
    internal static byte[] AnnotationSquareOverText(string secret)
        => TextUnderSquareAnnotation(secret);

    /// <summary>
    /// A /Highlight annotation whose /IC is black. Same shape as the square,
    /// different subtype — a channel keyed on `Subtype /Square` misses it.
    /// </summary>
    internal static byte[] AnnotationHighlightOverText(string secret)
        => TextUnderAnnotation(secret, "/Highlight");

    /// <summary>
    /// A /Stamp annotation. Also the subtype most likely to be a legitimate
    /// graphic rather than a redaction, so it is the one where mark detection
    /// and false positives meet.
    /// </summary>
    internal static byte[] AnnotationStampOverText(string secret)
        => TextUnderAnnotation(secret, "/Stamp");

    private static byte[] TextUnderAnnotation(string text, string subtype, double fontSize = 14)
    {
        const double x = 72, y = 700;
        var width = text.Length * fontSize * 0.78 + 6;
        return Build(
            string.Create(CultureInfo.InvariantCulture,
                $"BT /F1 {fontSize} Tf {x} {y} Td ({text}) Tj ET\n"),
            extraObjects: new List<Obj>
            {
                new(string.Create(CultureInfo.InvariantCulture,
                    $"<< /Type /Annot /Subtype {subtype} " +
                    $"/Rect [{x - 2} {y - 3} {x + width + 2} {y + fontSize}] " +
                    $"/IC [0 0 0] /C [0 0 0] /F 4 /P 3 0 R >>")),
            },
            pageExtra: "/Annots [7 0 R]");
    }

    // ── box-inside-form-xobject ─────────────────────────────────────────────
    // Parameter: NESTING DEPTH. The page holds only a `Do`; the box is one, two
    // or three forms down, each with its own /Matrix to compose.

    /// <summary>The box is one form deep.</summary>
    internal static byte[] BoxInFormDepth1(string secret) => TextUnderBoxInFormXObject(secret);

    /// <summary>The box is two forms deep, each contributing a /Matrix.</summary>
    internal static byte[] BoxInFormDepth2(string secret) => BoxInNestedForms(secret, depth: 2);

    /// <summary>
    /// Three deep. A recursion bound of two passes the shallower fixtures and
    /// silently reports a clean page here.
    /// </summary>
    internal static byte[] BoxInFormDepth3(string secret) => BoxInNestedForms(secret, depth: 3);

    /// <summary>
    /// The child form is named ONLY in its parent form's /Resources, which is
    /// where §8.10.1 says it belongs (#1666). Was an expected miss when written
    /// — excise resolved a nested `Do` against the PAGE's /XObject — and is now
    /// found, because isolating the lookup from the depth made the fix obvious.
    ///
    /// <para>Not a depth limit — MaxFormDepth is 8. The two fixtures above pass
    /// at the same nesting because they ALSO name every form on the page; the
    /// difference between them and this one is the lookup, which is what
    /// isolates the defect.</para>
    /// </summary>
    internal static byte[] BoxInFormChildScopedToTheForm(string secret)
        => BoxInNestedForms(secret, depth: 2, alsoDeclareOnPage: false);

    private static byte[] BoxInNestedForms(string secret, int depth, bool alsoDeclareOnPage = true)
    {
        const double x = 72, y = 700, fontSize = 14;
        var width = secret.Length * fontSize * 0.78 + 6;

        // Innermost form paints the covering box; each wrapper just draws the
        // one below it, so the CTM has to compose all the way down.
        var objects = new List<Obj>();
        var innermost = string.Create(CultureInfo.InvariantCulture,
            $"q 0 0 0 rg {x - 2} {y - 3} {width + 4} {fontSize + 3} re f Q\n");
        objects.Add(FormObj(innermost));

        for (var level = 1; level < depth; level++)
        {
            var childName = $"/Fx{level - 1}";
            objects.Add(FormObj($"{childName} Do\n", $"/XObject << {childName} {7 + level - 1} 0 R >>"));
        }

        var outerName = $"/Fx{depth - 1}";
        // Naming every form on the page is NOT spec-required — §8.10.1 scopes a
        // nested child to its parent form. It is done here so these two fixtures
        // isolate DEPTH; BoxInFormChildScopedToTheForm turns it off and isolates
        // the lookup instead (#1666).
        var allNames = alsoDeclareOnPage
            ? string.Join(" ", Enumerable.Range(0, depth).Select(i => $"/Fx{i} {7 + i} 0 R"))
            : $"{outerName} {7 + depth - 1} 0 R";
        return Build(
            string.Create(CultureInfo.InvariantCulture,
                $"BT /F1 {fontSize} Tf {x} {y} Td ({secret}) Tj ET\n{outerName} Do\n"),
            extraObjects: objects,
            resourcesExtra: $"/XObject << {allNames} >>");
    }

    private static Obj FormObj(string content, string resources = "")
    {
        var body = System.Text.Encoding.ASCII.GetBytes(content);
        return new(
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Matrix [1 0 0 1 0 0] " +
            $"/Resources << {resources} >> /Length {body.Length} >>", body);
    }

    // ── redact-annotation-unapplied ─────────────────────────────────────────
    // Parameter: whether the annotation carries overlay text, and whether the
    // text under it is one word or a run. §12.5.6.23's /Redact is a REQUEST;
    // until a processor applies it the content is untouched.

    /// <summary>A bare /Redact over a single word.</summary>
    internal static byte[] UnappliedRedactOverAWord(string secret)
        => UnappliedRedactAnnotation(secret);

    /// <summary>
    /// A /Redact carrying /OverlayText — the annotation says what should replace
    /// the content, which means the file states both the cover story and the
    /// original.
    /// </summary>
    internal static byte[] UnappliedRedactWithOverlayText(string secret, string overlay = "[REDACTED]")
    {
        const double x = 72, y = 700, fontSize = 14;
        var width = secret.Length * fontSize * 0.78 + 6;
        return Build(
            string.Create(CultureInfo.InvariantCulture,
                $"BT /F1 {fontSize} Tf {x} {y} Td ({secret}) Tj ET\n"),
            extraObjects: new List<Obj>
            {
                new(string.Create(CultureInfo.InvariantCulture,
                    $"<< /Type /Annot /Subtype /Redact " +
                    $"/Rect [{x - 2} {y - 3} {x + width + 2} {y + fontSize}] " +
                    $"/OverlayText ({overlay}) /IC [0 0 0] /P 3 0 R >>")),
            },
            pageExtra: "/Annots [7 0 R]");
    }

    /// <summary>
    /// A /Redact whose /Rect covers only the MIDDLE of a longer line — the
    /// common real shape, where one name inside a sentence is marked and the
    /// surrounding words are meant to stay.
    /// </summary>
    internal static byte[] UnappliedRedactOverPartOfALine(string secret)
    {
        const double x = 72, y = 700, fontSize = 14;
        var prefix = "Witness ";
        var startX = x + prefix.Length * fontSize * 0.5;
        var width = secret.Length * fontSize * 0.78 + 6;
        return Build(
            string.Create(CultureInfo.InvariantCulture,
                $"BT /F1 {fontSize} Tf {x} {y} Td ({prefix}{secret} testified) Tj ET\n"),
            extraObjects: new List<Obj>
            {
                new(string.Create(CultureInfo.InvariantCulture,
                    $"<< /Type /Annot /Subtype /Redact " +
                    $"/Rect [{startX - 2} {y - 3} {startX + width + 2} {y + fontSize}] " +
                    $"/IC [0 0 0] /P 3 0 R >>")),
            },
            pageExtra: "/Annots [7 0 R]");
    }

    // ── marked-content-carrier ──────────────────────────────────────────────
    // Parameter: WHICH key holds the text, and whether the property list is
    // inline or NAMED. The named form is #1599's still-open scrub gap, so the
    // recovery side must keep reading it.

    /// <summary>Inline <c>/ActualText</c> — the common form.</summary>
    internal static byte[] MarkedContentActualText(string secret)
        => MarkedContentCarrierUnderBox(secret);

    /// <summary>Inline <c>/Alt</c> — the accessibility sibling, same shape.</summary>
    internal static byte[] MarkedContentAlt(string secret)
        => MarkedContentCarrierUnderBox(secret, carrierKey: "Alt");

    /// <summary>
    /// A NAMED property list in <c>/Resources /Properties</c> rather than an
    /// inline dictionary. ⚠️ #1599: the SCRUB side misses this, so recovery
    /// reading it is what keeps that gap measurable rather than theoretical.
    /// </summary>
    internal static byte[] MarkedContentNamedPropertyList(string secret)
        => MarkedContentCarrierUnderBox(secret, namedPropertyList: true);
}
