using System.Text;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Pdfe.Core.Security;

namespace Pdfe.Core.Writing;

/// <summary>
/// Writes a PDF document to a stream.
/// </summary>
public class PdfDocumentWriter
{
    private readonly PdfDocument _document;
    private readonly PdfEncryptionOptions? _encryptionOptions;
    private readonly Dictionary<int, long> _objectOffsets = new();

    private PdfStandardSecurityEncryptor? _encryptor;
    private int _encryptObjNum;
    private PdfDictionary? _encryptDict;

    public PdfDocumentWriter(PdfDocument document, PdfEncryptionOptions? encryptionOptions = null)
    {
        _document = document;
        _encryptionOptions = encryptionOptions;
    }

    /// <summary>
    /// Write the document to a stream.
    /// </summary>
    public void Write(Stream stream)
    {
        if (_encryptionOptions != null)
            PrepareEncryption();

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

    /// <summary>
    /// Derive the file encryption key and precompute the /Encrypt
    /// dictionary (O/U/OE/UE/Perms) before any objects are written. Kept
    /// entirely local to this writer instance/call — never persisted onto
    /// <see cref="_document"/> — so repeated Save() calls on the same
    /// PdfDocument don't leak growing numbers of orphaned encrypt-dict
    /// objects (see <see cref="PdfDocument.NextFreeObjectNumber"/>).
    /// </summary>
    private void PrepareEncryption()
    {
        var options = _encryptionOptions!;
        if (options.Algorithm != PdfEncryptionAlgorithm.Aes256)
            throw new NotSupportedException(
                $"Encryption algorithm {options.Algorithm} is not yet implemented. " +
                "Only PdfEncryptionAlgorithm.Aes256 (V=5 R=6, PDF 2.0 native) is supported; " +
                "see issue #640 for AES-128 (V=4 R=4).");

        var userPasswordBytes = Encoding.UTF8.GetBytes(options.UserPassword ?? string.Empty);
        var ownerPasswordBytes = Encoding.UTF8.GetBytes(options.OwnerPassword ?? string.Empty);

        _encryptor = PdfStandardSecurityEncryptor.CreateR6(
            userPasswordBytes, ownerPasswordBytes, options.Permissions, options.EncryptMetadata);

        // Reserve an object number that isn't part of the document graph —
        // the /Encrypt dict is referenced only from the trailer, never from
        // the catalog, so it must never go through AddIndirectObject (which
        // would make it "real" and reachable-adjacent in the document).
        _encryptObjNum = _document.NextFreeObjectNumber;
        _encryptDict = BuildEncryptDictionary(_encryptor, options);
    }

    private static PdfDictionary BuildEncryptDictionary(PdfStandardSecurityEncryptor enc, PdfEncryptionOptions options)
    {
        var stdCf = new PdfDictionary
        {
            ["CFM"] = new PdfName("AESV3"),
            ["AuthEvent"] = new PdfName("DocOpen"),
            ["Length"] = new PdfInteger(32),
        };
        var cf = new PdfDictionary { ["StdCF"] = stdCf };

        return new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(5),
            ["R"] = new PdfInteger(6),
            ["Length"] = new PdfInteger(256),
            ["CF"] = cf,
            ["StmF"] = new PdfName("StdCF"),
            ["StrF"] = new PdfName("StdCF"),
            ["O"] = new PdfString(enc.O, isHex: true),
            ["U"] = new PdfString(enc.U, isHex: true),
            ["OE"] = new PdfString(enc.OE, isHex: true),
            ["UE"] = new PdfString(enc.UE, isHex: true),
            ["P"] = new PdfInteger(options.Permissions),
            ["Perms"] = new PdfString(enc.Perms, isHex: true),
            ["EncryptMetadata"] = PdfBoolean.Get(options.EncryptMetadata),
        };
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
        var reachable = _document.ComputeSaveReachableObjects();

        // Get all objects sorted by object number for consistent output
        var objects = _document.GetAllObjects()
            .Where(o => reachable.Contains(o.ObjectNumber))
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
            WriteIndirectObject(writer, objNum, gen, obj, isEncryptDict: false);
        }

        // The /Encrypt dictionary itself is written as a normal indirect
        // object (so a plain xref entry points at it, findable before the
        // reader has a key) but is never encrypted — see WriteIndirectObject's
        // isEncryptDict guard.
        if (_encryptionOptions != null)
        {
            _objectOffsets[_encryptObjNum] = writer.BaseStream.Position;
            WriteIndirectObject(writer, _encryptObjNum, 0, _encryptDict!, isEncryptDict: true);
        }
    }

    private static bool IsCrossReferencePlumbing(PdfObject obj)
    {
        if (obj is not PdfStream s) return false;
        var type = s.GetNameOrNull("Type");
        return type == "ObjStm" || type == "XRef";
    }

    private void WriteIndirectObject(BinaryWriter writer, int objNum, int gen, PdfObject obj, bool isEncryptDict)
    {
        // Object header: "1 0 obj\n"
        var header = $"{objNum} {gen} obj\n";
        writer.Write(Encoding.ASCII.GetBytes(header));

        // Object content
        if (obj is PdfStream stream)
        {
            WriteStream(writer, stream, isEncryptDict);
        }
        else
        {
            // The /Encrypt dictionary's own strings (O/U/OE/UE/Perms) are
            // already ciphertext — never route it through the encrypting
            // serializer, or it would be double-encrypted and unreadable.
            Func<byte[], byte[]>? encryptFn = (_encryptionOptions != null && !isEncryptDict) ? _encryptor!.EncryptBytes : null;
            var content = PdfObjectWriter.Serialize(obj, encryptFn);
            writer.Write(Encoding.Latin1.GetBytes(content));
        }

        // Object footer: "\nendobj\n"
        writer.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
    }

    private void WriteStream(BinaryWriter writer, PdfStream stream, bool isEncryptDict)
    {
        // Get stream data (use encoded if available, otherwise decoded)
        var data = stream.EncodedData;

        bool encrypting = _encryptionOptions != null && !isEncryptDict;
        if (encrypting)
        {
            // Honor /EncryptMetadata: when false, the XMP metadata stream
            // itself must stay plaintext even though every other stream is
            // encrypted (ISO 32000-2 §7.6.1) — readers key off /EncryptMetadata
            // to know not to attempt decrypting it.
            bool isMetadataStream = stream.GetNameOrNull("Type") == "Metadata";
            bool skipThisStream = isMetadataStream && !_encryptionOptions!.EncryptMetadata;
            if (!skipThisStream)
                data = _encryptor!.EncryptBytes(data);
        }

        // Ensure Length is correct (post-encryption size, if encrypted)
        stream["Length"] = new PdfInteger(data.Length);

        // Write dictionary part using the specialized serializer. Strings
        // inside a stream's own dictionary (e.g. an image's /Name) are
        // encrypted the same way as any other object's strings.
        Func<byte[], byte[]>? encryptFn = encrypting ? _encryptor!.EncryptBytes : null;
        var sb = new StringBuilder();
        PdfObjectWriter.SerializeStreamDictionary(stream, sb, encryptFn);
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
        var existingId = _document.Trailer.TryGetArray("ID", out var trailerId) ? trailerId : null;
        if (existingId is { Count: > 0 })
        {
            trailer["ID"] = existingId;
        }
        else
        {
            var id = new PdfString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), isHex: true);
            trailer["ID"] = new PdfArray(id, id);
        }

        // /Encrypt — a reference to the plaintext /Encrypt dictionary
        // written in WriteObjects. Never encrypted itself: a reader must be
        // able to find /Filter /V /R /O /U /OE /UE /Perms before it has a
        // key. The trailer as a whole (including /ID) is always written via
        // the plain, non-encrypting Serialize(trailer) call below.
        if (_encryptionOptions != null)
        {
            trailer["Encrypt"] = new PdfReference(_encryptObjNum, 0);
        }

        // Write trailer
        writer.Write(Encoding.ASCII.GetBytes("trailer\n"));
        var trailerStr = PdfObjectWriter.Serialize(trailer);
        writer.Write(Encoding.Latin1.GetBytes(trailerStr));

        // Write startxref
        writer.Write(Encoding.ASCII.GetBytes($"\nstartxref\n{xrefOffset}\n%%EOF\n"));
    }
}
