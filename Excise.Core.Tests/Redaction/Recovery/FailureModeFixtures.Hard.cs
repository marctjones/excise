using System.Collections.Generic;
using System.Globalization;
using System.Text;
using static Excise.Core.Tests.Redaction.Recovery.RecoveryFixtureBuilder;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1645 — the variants for the four modes that are hardest to synthesise:
/// prior revisions, XFA, the SMask trick and the retained original image.
///
/// <para><b>Each variant differs in a PARAMETER the channel responds to</b>, not
/// in a difficulty label. A label can be wrong and nothing catches it; a
/// parameter — chain depth, XML nesting, how nearly-zero a mask is, whether the
/// replacement matches dimensions — lets a gate assert the ladder is real.</para>
///
/// <para>⚠️ Some variants here are EXPECTED NOT TO BE RECOVERED, and that is the
/// point. The registry calls <c>image-smask-trick</c> and
/// <c>image-original-object-retained</c> partial; a fixture ladder turns that
/// word into a measurement, and the confusion matrix reports the miss as a false
/// negative rather than letting `partial` stand as prose forever.</para>
/// </summary>
internal static partial class FailureModeFixtures
{
    // ── incremental-update-prior-revision ────────────────────────────────────
    // Parameter: CHAIN DEPTH. One /Prev hop is a different walk from three, and
    // a reader that stops after the first hop passes the shallow fixture.

    /// <summary>The secret is in the immediately previous revision. One hop.</summary>
    internal static byte[] PriorRevisionDepth1(string secret)
        => IncrementalUpdate(
            Build($"BT /F1 14 Tf 72 700 Td ({secret}) Tj ET\n"),
            "BT /F1 14 Tf 72 700 Td (REDACTED) Tj ET\n");

    /// <summary>
    /// The secret is THREE revisions back, with two innocuous edits layered over
    /// it. A channel that reads only the immediately previous revision finds
    /// "INTERIM" and reports a clean document.
    /// </summary>
    internal static byte[] PriorRevisionDepth3(string secret)
    {
        var r0 = Build($"BT /F1 14 Tf 72 700 Td ({secret}) Tj ET\n");
        var r1 = IncrementalUpdate(r0, "BT /F1 14 Tf 72 700 Td (INTERIM ONE) Tj ET\n");
        var r2 = IncrementalUpdate(r1, "BT /F1 14 Tf 72 700 Td (INTERIM TWO) Tj ET\n");
        return IncrementalUpdate(r2, "BT /F1 14 Tf 72 700 Td (REDACTED) Tj ET\n");
    }

    /// <summary>
    /// TWO different secrets, removed at DIFFERENT hops: one in the first
    /// update, one in the second. A channel that walks to the oldest revision
    /// and reads only that finds one of them and reports the file handled.
    ///
    /// <para>⚠️ This replaced a variant that kept the same text in every
    /// revision and merely covered it with a box. That fixture was WRONG, not
    /// the code: the channel deliberately ignores text present in both
    /// revisions, because text the current revision still shows is not
    /// something anyone needs recovering
    /// (<c>Text_PresentInBothRevisions_IsNotReported</c>). Recording it here
    /// because the mistake is an easy one to repeat.</para>
    /// </summary>
    internal static byte[] PriorRevisionSecretsAtDifferentHops(string first, string second)
    {
        var r0 = Build($"BT /F1 14 Tf 72 700 Td ({first}) Tj ET\n");
        var r1 = IncrementalUpdate(r0, $"BT /F1 14 Tf 72 700 Td ({second}) Tj ET\n");
        return IncrementalUpdate(r1, "BT /F1 14 Tf 72 700 Td (REDACTED) Tj ET\n");
    }

    // ── leftover-xfa ─────────────────────────────────────────────────────────
    // Parameter: XML NESTING DEPTH and how many fields share the packet. A
    // reader that takes the first text node, or that does not descend, passes
    // the flat one-field case and fails both of these.

    /// <summary>One field at the top of the datasets packet.</summary>
    internal static byte[] XfaFlat(string value) => XfaFormWithValue("ssn", value);

    /// <summary>
    /// The value is four subforms deep. §12.7.8's datasets packet is an
    /// arbitrary tree; a channel that reads only the first level is blind here.
    /// </summary>
    internal static byte[] XfaNestedSubform(string value)
        => XfaPacket(
            "<subform name=\"page1\"><subform name=\"section\">" +
            "<subform name=\"party\"><subform name=\"detail\">" +
            $"<ssn>{value}</ssn>" +
            "</subform></subform></subform></subform>");

    /// <summary>
    /// Five fields, one of which holds the secret. A channel reporting the first
    /// node it finds reports a phone number and calls the document clean.
    /// </summary>
    internal static byte[] XfaAmongSiblings(string value)
        => XfaPacket(
            "<subform name=\"form\">" +
            "<phone>555-0100</phone><city>Springfield</city>" +
            $"<ssn>{value}</ssn>" +
            "<state>IL</state><zip>62704</zip>" +
            "</subform>");

    /// <summary>An XFA document whose datasets packet holds <paramref name="inner"/>.</summary>
    private static byte[] XfaPacket(string inner)
    {
        var xml = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">" +
            "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">" +
            "<xfa:data>" + inner + "</xfa:data></xfa:datasets></xdp:xdp>");

        return Build(
            "BT /F1 14 Tf 72 700 Td (Form) Tj ET\n",
            extraObjects: new List<Obj> { new($"<< /Length {xml.Length} >>", xml) },
            catalogExtra: "/AcroForm << /XFA 7 0 R >>");
    }

    // ── image-smask-trick ────────────────────────────────────────────────────
    // Parameter: HOW transparent, and by what mechanism. The registry calls this
    // mode partial; these three say exactly where the edge is.

    /// <summary>Every mask sample zero, 8-bit grey. The detected case.</summary>
    internal static byte[] SMaskAllZero(int size = 32) => FullyMaskedImage(size);

    /// <summary>
    /// ⚠️ EXPECTED NOT RECOVERED. A mask that is all-zero except four samples —
    /// visually identical to fully transparent, structurally not all-zero. The
    /// documented gap in #1608, now measured instead of described.
    /// </summary>
    internal static byte[] SMaskNearlyAllZero(int size = 32)
    {
        var pixels = new byte[size * size];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);
        var mask = new byte[size * size];
        mask[0] = 1; mask[1] = 1; mask[^1] = 1; mask[^2] = 1;

        return Build("q 120 0 0 120 72 600 cm /Im0 Do Q\n",
            extraObjects: new List<Obj>
            {
                new($"<< /Type /XObject /Subtype /Image /Width {size} /Height {size} " +
                    $"/ColorSpace /DeviceGray /BitsPerComponent 8 /SMask 8 0 R " +
                    $"/Length {pixels.Length} >>", pixels),
                new($"<< /Type /XObject /Subtype /Image /Width {size} /Height {size} " +
                    $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Length {mask.Length} >>", mask),
            },
            resourcesExtra: "/XObject << /Im0 7 0 R >>");
    }

    /// <summary>
    /// ⚠️ EXPECTED NOT RECOVERED. No mask at all — the image is drawn under an
    /// /ExtGState with /ca 0, so it is fully transparent by graphics state
    /// rather than by sample data. The other half of #1608's gap: it needs alpha
    /// tracking in the walker, which the box-light-or-low-contrast row wants too.
    /// </summary>
    internal static byte[] ImageDrawnFullyTransparent(int size = 32)
    {
        var pixels = new byte[size * size];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);

        return Build("q /GS0 gs 120 0 0 120 72 600 cm /Im0 Do Q\n",
            extraObjects: new List<Obj>
            {
                new($"<< /Type /XObject /Subtype /Image /Width {size} /Height {size} " +
                    $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Length {pixels.Length} >>", pixels),
            },
            resourcesExtra: "/XObject << /Im0 7 0 R >> /ExtGState << /GS0 << /ca 0 /CA 0 >> >>");
    }

    // ── image-original-object-retained ───────────────────────────────────────
    // Parameter: whether a SAME-DIMENSION replacement is referenced. That gate
    // is what stops every incrementally-updated PDF in existence reading as a
    // leak, and it is the thing most likely to be loosened by accident.

    /// <summary>The orphan and the drawn replacement share dimensions. Detected.</summary>
    internal static byte[] OrphanWithMatchingReplacement(int size = 32) => OrphanedOriginalImage(size);

    /// <summary>Two orphans, both matching the one drawn image.</summary>
    internal static byte[] TwoOrphansWithMatchingReplacement(int size = 32)
    {
        byte[] Pattern(int seed)
        {
            var b = new byte[size * size];
            for (var i = 0; i < b.Length; i++) b[i] = (byte)((i + seed) % 251);
            return b;
        }

        return Build("q 120 0 0 120 72 600 cm /Im0 Do Q\n",
            extraObjects: new List<Obj>
            {
                new(Img(size, Pattern(0).Length), Pattern(0)),     // 7: orphan
                new(Img(size, Pattern(7).Length), Pattern(7)),     // 8: orphan
                new(Img(size, size * size), new byte[size * size]) // 9: drawn
            },
            resourcesExtra: "/XObject << /Im0 9 0 R >>");
    }

    /// <summary>
    /// ⚠️ EXPECTED NOT REPORTED, and this is a NEGATIVE control rather than a
    /// gap. An unreferenced image of DIFFERENT dimensions is ordinary
    /// incremental-update debris, which accumulates benignly in a large fraction
    /// of real PDFs. Reporting it would drown the channel in noise — the same
    /// failure shape as #1624, arrived at from the other direction.
    /// </summary>
    internal static byte[] OrphanWithNoMatchingReplacement(int size = 32)
    {
        var orphan = new byte[size * size];
        for (var i = 0; i < orphan.Length; i++) orphan[i] = (byte)(i % 251);
        var drawn = new byte[(size / 2) * (size / 2)];

        return Build("q 120 0 0 120 72 600 cm /Im0 Do Q\n",
            extraObjects: new List<Obj>
            {
                new(Img(size, orphan.Length), orphan),
                new(Img(size / 2, drawn.Length), drawn),
            },
            resourcesExtra: "/XObject << /Im0 8 0 R >>");
    }

    private static string Img(int side, int length) => string.Create(CultureInfo.InvariantCulture,
        $"<< /Type /XObject /Subtype /Image /Width {side} /Height {side} " +
        $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Length {length} >>");
}
