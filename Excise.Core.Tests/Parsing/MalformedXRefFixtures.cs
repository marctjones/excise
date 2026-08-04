using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// Hand-built PDFs that reproduce the three #884/#869 recovery mechanisms
/// exactly, without needing a gitignored corpus. Each one is byte-authored so
/// the defect is surgical and the xref offsets are real.
///
/// These are shared by the parser tests (does the document open, and does it
/// open to the RIGHT object) and by the redaction-completeness tests in
/// <c>Text/Segmentation/TolerantParsePathRedactionTests</c> (having opened it,
/// does redaction still reach the text). Both halves are required: a parser
/// change that widens what excise reads is only a gain if what comes back out
/// still redacts completely.
/// </summary>
internal static class MalformedXRefFixtures
{
    internal const string DefaultContent =
        "BT /F1 24 Tf 72 700 Td (SECRET) Tj 0 -30 Td (KEEPME) Tj ET";

    /// <summary>
    /// The /ObjStm slot that the catalog really occupies in
    /// <see cref="BuildNarrowXRefWidthObjStmPdf"/>. Chosen &gt; 255 so that the
    /// one-byte third field of /W [1 3 1] cannot hold it.
    /// </summary>
    internal const int CatalogSlot = 260;

    /// <summary>
    /// The slot the truncated index points at instead — <c>260 &amp; 0xFF</c>.
    /// Occupied by a /Type /Annot /Subtype /Link decoy, mirroring
    /// pdfjs/bug1978317.pdf where the same wrap made a link annotation
    /// masquerade as the document catalog (#869).
    /// </summary>
    internal const int DecoySlot = CatalogSlot & 0xFF;

    /// <summary>
    /// Object number of the decoy annotation, so a test can assert that the
    /// catalog is NOT it.
    /// </summary>
    internal const int DecoyObjectNumber = FirstFillerObject + DecoySlot;

    private const int FirstFillerObject = 6;
    private const int ObjStmObjectNumber = 267;
    private const int XRefObjectNumber = 268;

    /// <summary>
    /// A PDF whose xref stream declares /W [1 3 1], so the type-2 entries'
    /// index-in-stream field holds one byte. The catalog sits at slot 260 of the
    /// object stream and is therefore recorded as slot 4 — which holds a link
    /// annotation.
    ///
    /// Resolving the type-2 entry POSITIONALLY returns that annotation as the
    /// catalog, silently, and the document dies later with "Document has no
    /// Pages dictionary". Resolving it BY OBJECT NUMBER against the /ObjStm's
    /// own N-pair index returns the catalog. That is #869 in miniature: same
    /// mechanism, 261 objects instead of 65,564.
    /// </summary>
    internal static byte[] BuildNarrowXRefWidthObjStmPdf(string content = DefaultContent)
    {
        var latin1 = Encoding.Latin1;
        using var ms = new MemoryStream();
        void W(string s) { var b = latin1.GetBytes(s); ms.Write(b, 0, b.Length); }

        W("%PDF-1.5\n");

        var offsets = new Dictionary<int, long>();

        offsets[2] = ms.Position;
        W("2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] /MediaBox [0 0 612 792] >>\nendobj\n");

        offsets[3] = ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        offsets[4] = ms.Position;
        W($"4 0 obj\n<< /Length {latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        W("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // ── the object stream ───────────────────────────────────────────────
        // Slots 0..259 hold filler objects 6..265; slot 4 is the decoy link
        // annotation. Slot 260 holds object 1, the real catalog.
        var bodies = new List<(int ObjNum, string Text)>();
        for (int slot = 0; slot < CatalogSlot; slot++)
        {
            int objNum = FirstFillerObject + slot;
            bodies.Add((objNum, slot == DecoySlot
                ? "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] >>"
                : $"<< /Filler {slot} >>"));
        }
        bodies.Add((1, "<< /Type /Catalog /Pages 2 0 R >>"));

        var bodyBuilder = new StringBuilder();
        var pairs = new StringBuilder();
        foreach (var (objNum, text) in bodies)
        {
            pairs.Append(objNum).Append(' ').Append(bodyBuilder.Length).Append(' ');
            bodyBuilder.Append(text).Append('\n');
        }
        pairs.Append('\n');

        string index = pairs.ToString();
        int first = latin1.GetByteCount(index);
        string objStmData = index + bodyBuilder;

        offsets[ObjStmObjectNumber] = ms.Position;
        W($"{ObjStmObjectNumber} 0 obj\n<< /Type /ObjStm /N {bodies.Count} /First {first} "
          + $"/Length {latin1.GetByteCount(objStmData)} >>\nstream\n");
        W(objStmData);
        W("\nendstream\nendobj\n");

        // ── the xref stream, /W [1 3 1] ─────────────────────────────────────
        long xrefOffset = ms.Position;
        offsets[XRefObjectNumber] = xrefOffset;

        var rows = new List<byte>();
        void Row(byte type, long field2, int field3)
        {
            rows.Add(type);
            rows.Add((byte)((field2 >> 16) & 0xFF));
            rows.Add((byte)((field2 >> 8) & 0xFF));
            rows.Add((byte)(field2 & 0xFF));
            rows.Add((byte)(field3 & 0xFF));   // one byte: this is the truncation
        }

        Row(0, 0, 0);                                        // 0: free
        Row(2, ObjStmObjectNumber, CatalogSlot);             // 1: catalog, index wraps 260 -> 4
        for (int objNum = 2; objNum <= 5; objNum++)
            Row(1, offsets[objNum], 0);
        for (int slot = 0; slot < CatalogSlot; slot++)
            Row(2, ObjStmObjectNumber, slot);                // 6..265: fillers
        // 266: a type-2 entry pointing INTO the object stream, for an object
        // number the /ObjStm's own index never names. Positional resolution
        // hands back slot 0's filler; there is no correct answer but "some other
        // object" is the wrong one.
        Row(2, ObjStmObjectNumber, 0);
        Row(1, offsets[ObjStmObjectNumber], 0);
        Row(1, offsets[XRefObjectNumber], 0);

        W($"{XRefObjectNumber} 0 obj\n<< /Type /XRef /Size {XRefObjectNumber + 1} /W [1 3 1] "
          + $"/Root 1 0 R /Length {rows.Count} >>\nstream\n");
        ms.Write(rows.ToArray(), 0, rows.Count);
        W("\nendstream\nendobj\n");

        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// A PDF truncated the way pdfium/embedded_images.pdf is: the objects are
    /// all intact, but every xref pointer in the tail — <c>startxref</c> and the
    /// trailer's /Prev — points past EOF, and the only cross-reference section
    /// actually present is a terminal, EMPTY <c>xref 0 0</c>.
    ///
    /// The repair path parses that empty section happily and "succeeds" with
    /// zero entries plus a healthy-looking /Root, so nothing downstream ever
    /// asks for reconstruction — which is the bug, because reconstruction works
    /// on this file.
    /// </summary>
    internal static byte[] BuildTruncatedTerminalXRefPdf(string content = DefaultContent)
    {
        var latin1 = Encoding.Latin1;
        var sb = new StringBuilder();
        sb.Append("%PDF-1.5\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] /MediaBox [0 0 612 792] >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                  + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        sb.Append($"4 0 obj\n<< /Length {latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream\nendobj\n");
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // The tail of a file whose real xref was cut off. Both offsets are far
        // past EOF, exactly as in the corpus file.
        sb.Append("xref\n0 0\ntrailer\n");
        sb.Append("<< /Size 6 /Root 1 0 R /Prev 900000 >>\n");
        sb.Append("startxref\n950000\n%%EOF\n");

        return latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The only text that appears AFTER the unterminated stream in
    /// <see cref="BuildUnterminatedHugeLengthStreamPdf"/>. Recovering that
    /// stream must not fold this into it.
    /// </summary>
    internal const string TailMarker = "TAILMARKER";

    /// <summary>
    /// The unterminated stream's real body — everything the recovery is entitled
    /// to keep, and nothing after it.
    /// </summary>
    internal const string UnterminatedStreamBody = "% no end-of-stream keyword here";

    /// <summary>
    /// A PDF carrying one stream that declares a gigantic /Length and then never
    /// writes <c>endstream</c> at all — pdfium/bug_452455.pdf's shape
    /// (/Length 536870911 in a 1 KB file, no terminator anywhere).
    ///
    /// Three defects meet on this one object:
    ///  * the declared length was allocated up front (512 MB from 1 KB of input);
    ///  * the marker-scan recovery threw at EOF instead of returning the bytes it
    ///    had, condemning the whole document; and
    ///  * returning everything to EOF would fold the objects that FOLLOW into
    ///    this stream — hence <see cref="TailMarker"/>, which sits in object 7,
    ///    after the offender and before the xref.
    ///
    /// The stream is referenced from the page's resources so the writer keeps it
    /// on save; an unreachable object would be garbage-collected and could not
    /// leak anything.
    /// </summary>
    internal static byte[] BuildUnterminatedHugeLengthStreamPdf(
        string content = DefaultContent,
        int declaredLength = 536870911)
    {
        var latin1 = Encoding.Latin1;
        var sb = new StringBuilder();
        var offsets = new Dictionary<int, int>();

        sb.Append("%PDF-1.7\n");

        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] /MediaBox [0 0 612 792] >>\nendobj\n");
        offsets[3] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                  + "/Resources << /Font << /F1 5 0 R >> /XObject << /X6 6 0 R >> >> >>\nendobj\n");
        offsets[4] = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets[5] = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // The offender. No 'endstream' follows it anywhere in the file.
        offsets[6] = sb.Length;
        sb.Append($"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
                  + $"/Length {declaredLength} >>\nstream\n");
        sb.Append(UnterminatedStreamBody).Append('\n');

        // Sits inside the region an unbounded scan-to-EOF would swallow.
        offsets[7] = sb.Length;
        sb.Append($"7 0 obj\n<< /Marker ({TailMarker}) >>\nendobj\n");

        int xrefPos = sb.Length;
        sb.Append("xref\n0 8\n");
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xrefPos).Append("\n%%EOF\n");

        return latin1.GetBytes(sb.ToString());
    }
}
