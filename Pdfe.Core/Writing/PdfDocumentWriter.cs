using System.Text;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Writing;

/// <summary>
/// Writes a PDF document to a stream.
/// </summary>
public class PdfDocumentWriter
{
    private readonly PdfDocument _document;
    private readonly Dictionary<int, long> _objectOffsets = new();

    public PdfDocumentWriter(PdfDocument document)
    {
        _document = document;
    }

    /// <summary>
    /// Write the document to a stream.
    /// </summary>
    public void Write(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);

        // Write header
        WriteHeader(writer);

        // Write all objects
        WriteObjects(writer);

        // Write xref table
        long xrefOffset = WriteXRef(writer);

        // Write trailer
        WriteTrailer(writer, xrefOffset);
    }

    private void WriteHeader(BinaryWriter writer)
    {
        var header = $"%PDF-{_document.Version}\n";
        writer.Write(Encoding.ASCII.GetBytes(header));

        // Write binary marker (PDF spec recommends this for binary files)
        writer.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }); // %âãÏÓ\n
    }

    private void WriteObjects(BinaryWriter writer)
    {
        // Get all objects sorted by object number for consistent output
        var objects = _document.GetAllObjects()
            .OrderBy(o => o.ObjectNumber)
            .ToList();

        foreach (var (objNum, gen, obj) in objects)
        {
            // Skip cross-reference plumbing: object streams (/ObjStm) and
            // cross-reference streams (/XRef). GetAllObjects already yields the
            // ObjStm's members decompressed as standalone objects, and we emit
            // a classic xref table + trailer, so these containers are redundant.
            // Re-emitting an /ObjStm would also be a security leak: an object
            // freed via RemoveObject (e.g. a redacted Form XObject inlined and
            // pruned, #359) would still ship inside the container's bytes. Their
            // object numbers simply become free entries in the xref.
            if (IsCrossReferencePlumbing(obj))
                continue;

            _objectOffsets[objNum] = writer.BaseStream.Position;
            WriteIndirectObject(writer, objNum, gen, obj);
        }
    }

    private static bool IsCrossReferencePlumbing(PdfObject obj)
    {
        if (obj is not PdfStream s) return false;
        var type = s.GetNameOrNull("Type");
        return type == "ObjStm" || type == "XRef";
    }

    private void WriteIndirectObject(BinaryWriter writer, int objNum, int gen, PdfObject obj)
    {
        // Object header: "1 0 obj\n"
        var header = $"{objNum} {gen} obj\n";
        writer.Write(Encoding.ASCII.GetBytes(header));

        // Object content
        if (obj is PdfStream stream)
        {
            WriteStream(writer, stream);
        }
        else
        {
            var content = PdfObjectWriter.Serialize(obj);
            writer.Write(Encoding.Latin1.GetBytes(content));
        }

        // Object footer: "\nendobj\n"
        writer.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
    }

    private void WriteStream(BinaryWriter writer, PdfStream stream)
    {
        // Get stream data (use encoded if available, otherwise decoded)
        var data = stream.EncodedData;

        // Ensure Length is correct
        stream["Length"] = new PdfInteger(data.Length);

        // Write dictionary part using the specialized serializer
        var sb = new StringBuilder();
        PdfObjectWriter.SerializeStreamDictionary(stream, sb);
        writer.Write(Encoding.Latin1.GetBytes(sb.ToString()));

        // Write stream
        writer.Write(Encoding.ASCII.GetBytes("\nstream\n"));
        writer.Write(data);
        writer.Write(Encoding.ASCII.GetBytes("\nendstream"));
    }

    private long WriteXRef(BinaryWriter writer)
    {
        long xrefOffset = writer.BaseStream.Position;

        // Get max object number
        int maxObjNum = _objectOffsets.Count > 0 ? _objectOffsets.Keys.Max() : 0;

        // Write xref header
        writer.Write(Encoding.ASCII.GetBytes("xref\n"));
        writer.Write(Encoding.ASCII.GetBytes($"0 {maxObjNum + 1}\n"));

        // Write entries
        // Entry 0 is always free
        writer.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));

        for (int i = 1; i <= maxObjNum; i++)
        {
            if (_objectOffsets.TryGetValue(i, out var offset))
            {
                // In-use object
                var entry = $"{offset:D10} 00000 n \n";
                writer.Write(Encoding.ASCII.GetBytes(entry));
            }
            else
            {
                // Free object (link to next free, or 0)
                writer.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));
            }
        }

        return xrefOffset;
    }

    private void WriteTrailer(BinaryWriter writer, long xrefOffset)
    {
        int size = (_objectOffsets.Count > 0 ? _objectOffsets.Keys.Max() : 0) + 1;

        // Build trailer dictionary
        var trailer = new PdfDictionary
        {
            ["Size"] = new PdfInteger(size),
            ["Root"] = _document.GetCatalogReference()
        };

        // Copy Info if present
        var infoRef = _document.Trailer.GetReferenceOrNull("Info");
        if (infoRef != null)
        {
            trailer["Info"] = infoRef;
        }

        // /ID — a file identifier array of two byte strings (ISO 32000-1 §14.4).
        // Required by PDF/A and recommended for every file. Preserve an existing
        // one; otherwise generate a fresh pair.
        var existingId = _document.Trailer.ContainsKey("ID") ? _document.Trailer.GetArray("ID") : null;
        if (existingId is { Count: > 0 })
        {
            trailer["ID"] = existingId;
        }
        else
        {
            var id = new PdfString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), isHex: true);
            trailer["ID"] = new PdfArray(id, id);
        }

        // Write trailer
        writer.Write(Encoding.ASCII.GetBytes("trailer\n"));
        var trailerStr = PdfObjectWriter.Serialize(trailer);
        writer.Write(Encoding.Latin1.GetBytes(trailerStr));

        // Write startxref
        writer.Write(Encoding.ASCII.GetBytes($"\nstartxref\n{xrefOffset}\n%%EOF\n"));
    }
}
