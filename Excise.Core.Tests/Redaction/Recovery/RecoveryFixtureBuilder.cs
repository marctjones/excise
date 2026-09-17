using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1587 — hand-writes single-page PDFs with a redaction mark and a chosen leak
/// carrier, so each recovery channel can be tested against KNOWN ground truth.
///
/// <para>Raw bytes rather than excise's own writer, on purpose. A fixture built
/// by <c>PdfDocumentBuilder</c> would be excise describing a leak to itself:
/// the writer and the recovery channels would share any misunderstanding of the
/// carrier's shape, and the test could pass over a document no other tool reads
/// the way we do. These bytes are checkable by qpdf and readable by mutool, and
/// the tests do check (<see cref="RecoveryOracleTests"/>).</para>
/// </summary>
internal static class RecoveryFixtureBuilder
{
    /// <summary>A PDF object: its body, written verbatim between "N 0 obj" and "endobj".</summary>
    internal sealed record Obj(string Body, byte[]? StreamData = null);

    /// <summary>
    /// Text at (x, y), with an optional black box over it. The box is written
    /// AFTER the text, which is the classic failed redaction.
    /// </summary>
    internal static byte[] TextUnderBox(
        string text, double x = 72, double y = 700, double fontSize = 14,
        bool drawBox = true, string boxColourOp = "0 0 0 rg")
    {
        // Helvetica caps run ~0.72 em, so a 0.6 factor leaves the last glyph
        // outside the box -- and the detector correctly reports only the run it
        // covers. Sized to cover the whole word, with padding, because these
        // fixtures test the CHANNEL, not the box arithmetic.
        var width = text.Length * fontSize * 0.78 + 6;
        var content = new StringBuilder();
        content.Append(CultureInfo.InvariantCulture,
            $"BT /F1 {fontSize} Tf {x} {y} Td ({text}) Tj ET\n");
        if (drawBox)
            content.Append(CultureInfo.InvariantCulture,
                $"q {boxColourOp} {x - 2} {y - 3} {width + 4} {fontSize + 3} re f Q\n");
        return Build(content.ToString());
    }

    /// <summary>
    /// A page whose visible glyphs were REMOVED, leaving a black box, with the
    /// original word still present in an inline marked-content carrier.
    /// </summary>
    internal static byte[] MarkedContentCarrierUnderBox(
        string carrierValue, string carrierKey = "ActualText",
        double x = 72, double y = 700, bool namedPropertyList = false)
    {
        var width = carrierValue.Length * 8.4;
        var props = namedPropertyList ? "/P1" : $"<</{carrierKey} ({carrierValue})>>";
        var content = string.Create(CultureInfo.InvariantCulture,
            $"/Span {props} BDC\nBT /F1 14 Tf {x} {y} Td (  ) Tj ET\nEMC\n" +
            $"q 0 0 0 rg {x - 2} {y - 3} {width + 4} 17 re f Q\n");

        var extraResources = namedPropertyList
            ? $"/Properties << /P1 << /{carrierKey} ({carrierValue}) >> >>"
            : "";
        return Build(content, extraResources: extraResources);
    }

    /// <summary>A page with a form field whose /V survives behind a black box.</summary>
    internal static byte[] FormFieldValueUnderBox(
        string fieldName, string value, double x = 72, double y = 700)
    {
        // The widget's appearance is empty — the "redaction" blanked it — while
        // /V still holds the value, which any viewer honouring /NeedAppearances
        // paints straight back.
        var content = string.Create(CultureInfo.InvariantCulture,
            $"q 0 0 0 rg {x} {y} 160 18 re f Q\n");
        var extraObjects = new List<Obj>
        {
            new($"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({fieldName}) /V ({value}) " +
                $"/Rect [{F(x)} {F(y)} {F(x + 160)} {F(y + 18)}] /F 4 /P 3 0 R >>"),
        };
        return Build(content,
            extraObjects: extraObjects,
            pageExtra: "/Annots [7 0 R]",
            catalogExtra: "/AcroForm << /Fields [7 0 R] /NeedAppearances true >>");
    }

    /// <summary>A page with a <c>/Redact</c> annotation that was never applied.</summary>
    internal static byte[] UnappliedRedactAnnotation(
        string text, double x = 72, double y = 700, double fontSize = 14)
    {
        var width = text.Length * fontSize * 0.78 + 6;
        var content = string.Create(CultureInfo.InvariantCulture,
            $"BT /F1 {fontSize} Tf {x} {y} Td ({text}) Tj ET\n");
        var extraObjects = new List<Obj>
        {
            new($"<< /Type /Annot /Subtype /Redact " +
                $"/Rect [{F(x - 2)} {F(y - 3)} {F(x + width + 2)} {F(y + fontSize)}] " +
                $"/IC [0 0 0] /P 3 0 R >>"),
        };
        return Build(content, extraObjects: extraObjects, pageExtra: "/Annots [7 0 R]");
    }

    /// <summary>An image XObject painted first, then covered by a black box.</summary>
    internal static byte[] ImageUnderBox(double x = 72, double y = 600, double size = 120)
    {
        // A 2x2 8-bit greyscale image: smallest thing that is unambiguously an
        // image XObject to every parser.
        var pixels = new byte[] { 0x00, 0x40, 0x80, 0xFF };
        var content = string.Create(CultureInfo.InvariantCulture,
            $"q {F(size)} 0 0 {F(size)} {F(x)} {F(y)} cm /Im0 Do Q\n" +
            $"q 0 0 0 rg {F(x)} {F(y)} {F(size)} {F(size)} re f Q\n");
        var extraObjects = new List<Obj>
        {
            new("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
                "/ColorSpace /DeviceGray /BitsPerComponent 8 /Length 4 >>", pixels),
        };
        return Build(content, extraObjects: extraObjects,
            resourcesExtra: "/XObject << /Im0 7 0 R >>");
    }

    /// <summary>A vector drawing (strokes) painted first, then covered by a black box.</summary>
    internal static byte[] VectorUnderBox(double x = 72, double y = 600, double size = 120)
    {
        var content = string.Create(CultureInfo.InvariantCulture,
            $"q 2 w 0 0 1 RG {F(x + 5)} {F(y + 5)} m {F(x + size - 5)} {F(y + size - 5)} l " +
            $"{F(x + 5)} {F(y + size - 5)} l S Q\n" +
            $"q 0 0 0 rg {F(x)} {F(y)} {F(size)} {F(size)} re f Q\n");
        return Build(content);
    }

    /// <summary>
    /// Assemble a one-page PDF. Object numbers are fixed: 1 catalog, 2 pages,
    /// 3 page, 4 contents, 5 font, 6 unused, 7+ extras.
    /// </summary>
    internal static byte[] Build(
        string content,
        IReadOnlyList<Obj>? extraObjects = null,
        string pageExtra = "",
        string catalogExtra = "",
        string resourcesExtra = "",
        string extraResources = "")
    {
        var objects = new List<Obj>
        {
            new($"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>"),
            new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                $"/Resources << /Font << /F1 5 0 R >> {resourcesExtra} {extraResources} >> {pageExtra} >>"),
            new($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>",
                Encoding.ASCII.GetBytes(content)),
            new("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            new("<< >>"),
        };
        if (extraObjects != null) objects.AddRange(extraObjects);

        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{objects[i].Body}\n");
            if (objects[i].StreamData is { } data)
            {
                Write("stream\n");
                ms.Write(data);
                Write("\nendstream\n");
            }
            Write("endobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
