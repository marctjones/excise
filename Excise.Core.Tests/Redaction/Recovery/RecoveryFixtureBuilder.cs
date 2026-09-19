using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// #1599/#1625 — TWO spans sharing ONE named property list, the case every
    /// other named-list fixture here misses.
    ///
    /// <para>Every existing fixture emits one <c>/Span /P1 BDC</c> against one
    /// <c>/Properties</c> entry, so the dictionary is EXCLUSIVE to its span.
    /// That leaves the interesting branch untested: when two spans reference
    /// <c>/P1</c> and only one is redacted, the carrier must stay — the
    /// surviving span still needs it — and the recovery channel must still
    /// find it, ONCE rather than once per referencing span.</para>
    ///
    /// <para>⚠️ The first span's text is the redaction target; the second is
    /// the survivor. Both draw real glyphs, so a test can assert the redaction
    /// actually happened rather than passing because nothing ran.</para>
    /// </summary>
    internal static byte[] SharedNamedPropertyList(
        string carrierValue, string firstText, string secondText,
        string carrierKey = "ActualText")
    {
        var content = string.Create(CultureInfo.InvariantCulture,
            $"/Span /P1 BDC\nBT /F1 14 Tf 72 700 Td ({firstText}) Tj ET\nEMC\n" +
            $"/Span /P1 BDC\nBT /F1 14 Tf 72 670 Td ({secondText}) Tj ET\nEMC\n");

        return Build(content,
            extraResources: $"/Properties << /P1 << /{carrierKey} ({carrierValue}) >> >>");
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
    /// #1592 — text drawn with render mode 3 (invisible, §9.3.6). Fully
    /// extractable, never painted: how every OCR layer is written.
    /// </summary>
    internal static byte[] InvisibleText(string text, double x = 72, double y = 700)
        => Build(string.Create(CultureInfo.InvariantCulture,
            $"BT /F1 14 Tf 3 Tr {x} {y} Td ({text}) Tj ET\n"));

    /// <summary>
    /// #1592 — the covering box is a dark /Square ANNOTATION, not page content.
    /// The page content stream holds the text and nothing else.
    /// </summary>
    internal static byte[] TextUnderSquareAnnotation(
        string text, double x = 72, double y = 700, double fontSize = 14)
    {
        var width = text.Length * fontSize * 0.78 + 6;
        var content = string.Create(CultureInfo.InvariantCulture,
            $"BT /F1 {fontSize} Tf {x} {y} Td ({text}) Tj ET\n");
        var extraObjects = new List<Obj>
        {
            new($"<< /Type /Annot /Subtype /Square " +
                $"/Rect [{F(x - 2)} {F(y - 3)} {F(x + width)} {F(y + fontSize)}] " +
                $"/IC [0 0 0] /C [0 0 0] /F 4 /P 3 0 R >>"),
        };
        return Build(content, extraObjects: extraObjects, pageExtra: "/Annots [7 0 R]");
    }

    /// <summary>#1592 — a page carrying a /Thumb pre-render of itself.</summary>
    internal static byte[] PageWithThumbnail(string text = "VISIBLE")
    {
        var pixels = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var content = string.Create(CultureInfo.InvariantCulture,
            $"BT /F1 14 Tf 72 700 Td ({text}) Tj ET\n");
        var extraObjects = new List<Obj>
        {
            new("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
                "/ColorSpace /DeviceGray /BitsPerComponent 8 /Length 4 >>", pixels),
        };
        return Build(content, extraObjects: extraObjects, pageExtra: "/Thumb 7 0 R");
    }

    /// <summary>
    /// #1606 — the covering box lives inside a Form XObject the page invokes
    /// with Do. The page content stream holds the text and the Do, and nothing
    /// else; the box is one level down.
    /// </summary>
    internal static byte[] TextUnderBoxInFormXObject(
        string text, double x = 72, double y = 700, double fontSize = 14)
    {
        var width = text.Length * fontSize * 0.78 + 6;
        // The form draws the box in its OWN space; /Matrix translates it onto
        // the text, so a detector that ignores the matrix lands in the wrong
        // place and finds nothing.
        var formContent = string.Create(CultureInfo.InvariantCulture,
            $"0 0 0 rg 0 0 {F(width)} {F(fontSize + 3)} re f\n");
        var formBytes = Encoding.ASCII.GetBytes(formContent);

        var content = string.Create(CultureInfo.InvariantCulture,
            $"BT /F1 {fontSize} Tf {x} {y} Td ({text}) Tj ET\n" +
            $"q /Fx0 Do Q\n");

        var extraObjects = new List<Obj>
        {
            new($"<< /Type /XObject /Subtype /Form /BBox [0 0 {F(width)} {F(fontSize + 3)}] " +
                $"/Matrix [1 0 0 1 {F(x - 2)} {F(y - 3)}] /Length {formBytes.Length} >>",
                formBytes),
        };
        return Build(content, extraObjects: extraObjects,
            resourcesExtra: "/XObject << /Fx0 7 0 R >>");
    }

    /// <summary>
    /// #1608 — a page drawing a REPLACEMENT image while the same-size original
    /// stays in the file, referenced by nothing. The shape of an editor that
    /// swaps an image rather than removing it.
    /// </summary>
    internal static byte[] OrphanedOriginalImage(int size = 32)
    {
        var original = new byte[size * size];
        for (var i = 0; i < original.Length; i++) original[i] = (byte)(i % 251);
        var replacement = new byte[size * size];   // all-black: the "redacted" one

        var content = string.Create(CultureInfo.InvariantCulture,
            $"q 120 0 0 120 72 600 cm /Im0 Do Q\n");
        var extraObjects = new List<Obj>
        {
            // 7: the orphan — a complete image nothing points at.
            new($"<< /Type /XObject /Subtype /Image /Width {size} /Height {size} " +
                $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Length {original.Length} >>", original),
            // 8: the replacement the page actually draws.
            new($"<< /Type /XObject /Subtype /Image /Width {size} /Height {size} " +
                $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Length {replacement.Length} >>", replacement),
        };
        return Build(content, extraObjects: extraObjects,
            resourcesExtra: "/XObject << /Im0 8 0 R >>");
    }

    /// <summary>
    /// #1608 — an image drawn under a fully transparent /SMask: present,
    /// complete, and invisible.
    /// </summary>
    internal static byte[] FullyMaskedImage(int size = 32)
    {
        var pixels = new byte[size * size];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);
        var mask = new byte[size * size];   // every sample zero = fully transparent

        var content = "q 120 0 0 120 72 600 cm /Im0 Do Q\n";
        var extraObjects = new List<Obj>
        {
            new($"<< /Type /XObject /Subtype /Image /Width {size} /Height {size} " +
                $"/ColorSpace /DeviceGray /BitsPerComponent 8 /SMask 8 0 R " +
                $"/Length {pixels.Length} >>", pixels),
            new($"<< /Type /XObject /Subtype /Image /Width {size} /Height {size} " +
                $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Length {mask.Length} >>", mask),
        };
        return Build(content, extraObjects: extraObjects,
            resourcesExtra: "/XObject << /Im0 7 0 R >>");
    }

    /// <summary>
    /// #1609 — an XFA form whose datasets packet still holds a field value the
    /// page no longer shows.
    /// </summary>
    internal static byte[] XfaFormWithValue(string field = "ssn", string value = "123-45-6789")
    {
        var xml = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">" +
            "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">" +
            "<xfa:data><form1><personal>" +
            $"<{field}>{value}</{field}>" +
            "</personal></form1></xfa:data></xfa:datasets></xdp:xdp>");

        var content = "q 0 0 0 rg 72 700 160 18 re f Q\n";
        var extraObjects = new List<Obj>
        {
            new($"<< /Length {xml.Length} >>", xml),
        };
        return Build(content,
            extraObjects: extraObjects,
            catalogExtra: "/AcroForm << /Fields [] /XFA 7 0 R >>");
    }

    /// <summary>#1592 — a document carrying an embedded file.</summary>
    internal static byte[] PageWithAttachment(string fileName = "notes.txt")
    {
        var payload = Encoding.ASCII.GetBytes("attachment payload");
        var content = "BT /F1 14 Tf 72 700 Td (COVER PAGE) Tj ET\n";
        var extraObjects = new List<Obj>
        {
            new($"<< /Type /EmbeddedFile /Length {payload.Length} >>", payload),
            new($"<< /Type /Filespec /F ({fileName}) /UF ({fileName}) /EF << /F 7 0 R >> >>"),
        };
        return Build(content,
            extraObjects: extraObjects,
            catalogExtra: "/Names << /EmbeddedFiles << /Names [(item) 8 0 R] >> >>");
    }

    /// <summary>
    /// #1592 — an INCREMENTAL UPDATE: <paramref name="original"/> bytes
    /// followed by an appended revision that replaces the page's content
    /// stream. The original revision stays whole at the front of the file, so
    /// truncating at the first <c>%%EOF</c> yields the pre-redaction document.
    /// </summary>
    internal static byte[] IncrementalUpdate(byte[] original, string replacementContent)
    {
        using var ms = new MemoryStream();
        ms.Write(original);
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        // Object 4 is the content stream in every fixture Build() produces.
        var offset = ms.Position;
        var body = Encoding.ASCII.GetBytes(replacementContent);
        Write($"4 0 obj\n<< /Length {body.Length} >>\nstream\n");
        ms.Write(body);
        Write("\nendstream\nendobj\n");

        var xref = ms.Position;
        // A one-entry update section, with /Prev pointing at the original's
        // xref so a reader chains back through the revisions (§7.5.6).
        var priorXref = FindLastStartxref(original);
        Write($"xref\n4 1\n{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size 9 /Root 1 0 R /Prev {priorXref} >>\n" +
              $"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static long FindLastStartxref(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        var idx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (idx < 0) return 0;
        var digits = new string(text[(idx + 9)..]
            .SkipWhile(c => !char.IsDigit(c))
            .TakeWhile(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
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
