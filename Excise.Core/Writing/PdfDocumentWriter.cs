using System.IO.Compression;
using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Security;

namespace Excise.Core.Writing;

/// <summary>
/// Writes a PDF document to a stream.
/// </summary>
public class PdfDocumentWriter
{
    private const int MaxObjectsPerObjectStream = 50;

    private readonly PdfDocument _document;
    private readonly PdfEncryptionOptions? _encryptionOptions;
    private readonly Dictionary<int, long> _objectOffsets = new();
    private readonly Dictionary<int, (int ObjectStreamNumber, int Index)> _compressedObjectEntries = new();

    private PdfDocumentSaveSession? _saveSession;
    private PdfStandardSecurityEncryptor? _encryptor;
    private int _encryptObjNum;
    private PdfDictionary? _encryptDict;
    private PdfArray? _idArray;

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
        _saveSession = _document.BeginSaveSession();

        if (_encryptionOptions != null)
            PrepareEncryption();

        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);

        // Write header
        WriteHeader(writer);

        var useCompressedObjects = ShouldUseCompressedObjects();

        if (useCompressedObjects)
        {
            var wroteCompressed = WriteObjectsCompressed(writer);
            if (wroteCompressed)
            {
                WriteXRefStream(writer);
                return;
            }
        }

        WriteObjects(writer);
        long xrefOffset = WriteXRef(writer);
        WriteTrailer(writer, xrefOffset);
    }

    private bool ShouldUseCompressedObjects()
        => _encryptionOptions == null
           && VersionAtLeast(SaveSession.Version, 1, 5)
           && !IsPdfA1();

    private bool IsPdfA1()
    {
        if (SaveSession.Catalog.GetOptional("Metadata") is not { } metadataRef)
            return false;
        if (SaveSession.Resolve(metadataRef) is not PdfStream metadata)
            return false;

        var xmp = Encoding.UTF8.GetString(metadata.DecodedData);
        return xmp.Contains("<pdfaid:part>1</pdfaid:part>", StringComparison.Ordinal);
    }

    private static bool VersionAtLeast(string version, int major, int minor)
    {
        var parts = version.Split('.', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var actualMajor)
            || !int.TryParse(parts[1], out var actualMinor))
        {
            return false;
        }

        return actualMajor > major || (actualMajor == major && actualMinor >= minor);
    }

    /// <summary>
    /// Derive the file encryption key and precompute the /Encrypt
    /// dictionary (O/U/[OE/UE/Perms for R=6]) before any objects are
    /// written. Kept entirely local to this writer instance/call — never
    /// persisted onto <see cref="_document"/> — so repeated Save() calls on
    /// the same PdfDocument don't leak growing numbers of orphaned
    /// encrypt-dict objects. The save session supplies a non-mutating next
    /// object number from the single document store.
    ///
    /// Must run before <see cref="WriteTrailer"/> would otherwise generate a
    /// fresh <c>/ID</c> — R=4's Algorithms 2/3/5 all hash <c>/ID[0]</c> into
    /// the key/derived values, so the ID has to be settled (via
    /// <see cref="GetOrCreateIdArray"/>) before those algorithms run, not
    /// after. R=6 doesn't consume <c>/ID</c> at all, which is why this
    /// ordering requirement was invisible until R=4 (#640) was added.
    /// </summary>
    private void PrepareEncryption()
    {
        var options = _encryptionOptions!;
        var userPasswordBytes = EncodeEncryptionPassword(options.UserPassword, options.Algorithm);
        var ownerPasswordBytes = EncodeEncryptionPassword(options.OwnerPassword, options.Algorithm);

        _encryptor = options.Algorithm switch
        {
            PdfEncryptionAlgorithm.Aes256 => PdfStandardSecurityEncryptor.CreateR6(
                userPasswordBytes, ownerPasswordBytes, options.Permissions, options.EncryptMetadata),

            PdfEncryptionAlgorithm.Aes128 => PdfStandardSecurityEncryptor.CreateR4(
                userPasswordBytes, ownerPasswordBytes, options.Permissions, options.EncryptMetadata,
                GetFirstIdBytes()),

            _ => throw new NotSupportedException(
                $"Encryption algorithm {options.Algorithm} is not supported.")
        };

        // Reserve an object number that isn't part of the document graph —
        // the /Encrypt dict is referenced only from the trailer, never from
        // the catalog, so it must never go through AddIndirectObject (which
        // would make it "real" and reachable-adjacent in the document).
        _encryptObjNum = SaveSession.NextFreeObjectNumber;
        _encryptDict = BuildEncryptDictionary(_encryptor, options);
    }

    /// <summary>
    /// Password bytes for the Standard Security Handler, encoding-matched
    /// to what <see cref="PdfStandardSecurityHandler"/>'s decrypt path tries
    /// first for the same revision (<c>EncodeUserPasswordCandidates</c>):
    /// R=6 (V=5) prefers UTF-8; R&lt;=4 prefers PDFDocEncoding, falling back
    /// to UTF-8 only when the password can't be represented in it. A file
    /// excise writes must be openable by excise's own decrypt path (and by
    /// qpdf/mutool/Ghostscript, which follow the same spec precedence).
    /// </summary>
    private static byte[] EncodeEncryptionPassword(string? password, PdfEncryptionAlgorithm algorithm)
    {
        var text = password ?? string.Empty;
        if (text.Length == 0) return Array.Empty<byte>();

        if (algorithm == PdfEncryptionAlgorithm.Aes128)
        {
            if (PdfString.TryEncodePdfDocEncoding(text, out var pdfDocBytes))
                return pdfDocBytes;
            return Encoding.UTF8.GetBytes(text);
        }

        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// The trailer's /ID[0] bytes, settling (and caching) the /ID array if
    /// it hasn't been determined yet this Write() call. See
    /// <see cref="PrepareEncryption"/>'s remarks for why R=4 needs this
    /// decided before key derivation, not just before the trailer is
    /// serialized.
    /// </summary>
    private byte[] GetFirstIdBytes()
    {
        var idArray = GetOrCreateIdArray();
        return ((PdfString)idArray[0]).Bytes;
    }

    /// <summary>
    /// Returns the trailer's /ID array (ISO 32000-1 §14.4): the existing
    /// one if the source document already had one, otherwise a freshly
    /// generated random pair — computed at most once per Write() call and
    /// reused by both <see cref="PrepareEncryption"/> (R=4 key derivation)
    /// and <see cref="WriteTrailer"/> (the actual trailer bytes), so they
    /// never disagree about what /ID[0] is.
    /// </summary>
    private PdfArray GetOrCreateIdArray()
    {
        if (_idArray != null) return _idArray;

        var existingId = SaveSession.ExistingIdArray;
        if (existingId is { Count: > 0 })
        {
            _idArray = existingId;
        }
        else
        {
            var id = new PdfString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), isHex: true);
            _idArray = new PdfArray(id, id);
        }
        return _idArray;
    }

    private static PdfDictionary BuildEncryptDictionary(PdfStandardSecurityEncryptor enc, PdfEncryptionOptions options)
    {
        return options.Algorithm switch
        {
            PdfEncryptionAlgorithm.Aes256 => BuildR6EncryptDictionary(enc, options),
            PdfEncryptionAlgorithm.Aes128 => BuildR4EncryptDictionary(enc, options),
            _ => throw new NotSupportedException(
                $"Encryption algorithm {options.Algorithm} is not supported.")
        };
    }

    private static PdfDictionary BuildR6EncryptDictionary(PdfStandardSecurityEncryptor enc, PdfEncryptionOptions options)
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
            ["OE"] = new PdfString(enc.OE!, isHex: true),
            ["UE"] = new PdfString(enc.UE!, isHex: true),
            ["P"] = new PdfInteger(options.Permissions),
            ["Perms"] = new PdfString(enc.Perms!, isHex: true),
            ["EncryptMetadata"] = PdfBoolean.Get(options.EncryptMetadata),
        };
    }

    /// <summary>
    /// V=4 R=4 (CFM=AESV2) /Encrypt dict shape — deliberately narrower than
    /// R=6's: no /OE, /UE, or /Perms (those fields don't exist before V=5),
    /// and /CF's crypt filter /Length is in BYTES (16) unlike the outer
    /// /Length which stays in BITS (128), matching qpdf's own R=4 output.
    /// </summary>
    private static PdfDictionary BuildR4EncryptDictionary(PdfStandardSecurityEncryptor enc, PdfEncryptionOptions options)
    {
        var stdCf = new PdfDictionary
        {
            ["CFM"] = new PdfName("AESV2"),
            ["AuthEvent"] = new PdfName("DocOpen"),
            ["Length"] = new PdfInteger(16),
        };
        var cf = new PdfDictionary { ["StdCF"] = stdCf };

        return new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(4),
            ["R"] = new PdfInteger(4),
            ["Length"] = new PdfInteger(128),
            ["CF"] = cf,
            ["StmF"] = new PdfName("StdCF"),
            ["StrF"] = new PdfName("StdCF"),
            ["O"] = new PdfString(enc.O, isHex: true),
            ["U"] = new PdfString(enc.U, isHex: true),
            ["P"] = new PdfInteger(options.Permissions),
            ["EncryptMetadata"] = PdfBoolean.Get(options.EncryptMetadata),
        };
    }

    /// <summary>
    /// Encrypt one object's plaintext bytes, dispatching on the active
    /// algorithm: R=6 uses the file key directly (no per-object
    /// derivation); R=4 derives a fresh key per object (Algorithm 1) from
    /// <paramref name="objNum"/>/<paramref name="gen"/> — see
    /// <see cref="PdfStandardSecurityEncryptor"/>'s class remarks. Callers
    /// must always route through this method rather than calling
    /// <c>_encryptor.EncryptBytes</c> directly, or R=4 output would be
    /// encrypted under the wrong (file-level) key for every object.
    /// </summary>
    private byte[] EncryptForObject(int objNum, int gen, byte[] plaintext)
    {
        return _encryptionOptions!.Algorithm == PdfEncryptionAlgorithm.Aes128
            ? _encryptor!.EncryptObjectBytes(objNum, gen, plaintext)
            : _encryptor!.EncryptBytes(plaintext);
    }

    private void WriteHeader(BinaryWriter writer)
    {
        var header = $"%PDF-{SaveSession.Version}\n";
        writer.Write(Encoding.ASCII.GetBytes(header));

        // Write binary marker (PDF spec recommends this for binary files)
        writer.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }); // %âãÏÓ\n
    }

    private void WriteObjects(BinaryWriter writer)
    {
        _objectOffsets.Clear();
        _compressedObjectEntries.Clear();
        foreach (var (objNum, gen, obj) in SaveSession.Objects)
        {
            // Skip cross-reference plumbing: object streams (/ObjStm) and
            // cross-reference streams (/XRef). The save session already yields
            // the ObjStm's members decompressed as standalone objects, and we
            // emit a classic xref table + trailer, so these containers are redundant.
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

    private bool WriteObjectsCompressed(BinaryWriter writer)
    {
        _objectOffsets.Clear();
        _compressedObjectEntries.Clear();

        var allObjects = SaveSession.Objects
            .Where(o => !IsCrossReferencePlumbing(o.Object))
            .ToList();

        var rootRef = SaveSession.CatalogReference;
        var packable = allObjects
            .Where(o => CanPackIntoObjectStream(o, rootRef))
            .ToList();
        if (packable.Count == 0)
            return false;

        var topLevel = allObjects
            .Where(o => !CanPackIntoObjectStream(o, rootRef))
            .ToList();

        foreach (var (objNum, gen, obj) in topLevel)
        {
            _objectOffsets[objNum] = writer.BaseStream.Position;
            WriteIndirectObject(writer, objNum, gen, obj, isEncryptDict: false);
        }

        var maxExisting = allObjects.Count == 0 ? 0 : allObjects.Max(o => o.ObjectNumber);
        var objectStreamNumber = maxExisting + 1;
        foreach (var chunk in packable.Chunk(MaxObjectsPerObjectStream))
        {
            var objectStream = BuildObjectStream(chunk, objectStreamNumber);
            _objectOffsets[objectStreamNumber] = writer.BaseStream.Position;
            WriteIndirectObject(writer, objectStreamNumber, 0, objectStream, isEncryptDict: false);
            objectStreamNumber++;
        }

        // Reserve the xref stream number now; WriteXRefStream fills the real
        // offset after the final byte position is known.
        _objectOffsets[objectStreamNumber] = -1;
        return true;
    }

    private static bool CanPackIntoObjectStream(
        (int ObjectNumber, int Generation, PdfObject Object) item,
        PdfReference rootRef)
    {
        if (item.Generation != 0) return false;
        if (item.Object is PdfStream) return false;
        if (item.ObjectNumber == rootRef.ObjectNum) return false;
        if (item.Object is PdfDictionary dict
            && (ContainsDocumentCarrierText(dict) || ContainsSignatureData(dict))) return false;
        return true;
    }

    private static bool ContainsDocumentCarrierText(PdfDictionary dict)
        => dict.GetStringOrNull("Title") != null
           || dict.GetStringOrNull("Author") != null
           || dict.GetStringOrNull("Subject") != null
           || dict.GetStringOrNull("Keywords") != null
           || dict.GetStringOrNull("Creator") != null
           || dict.GetStringOrNull("Producer") != null
           || dict.GetStringOrNull("Contents") != null;

    private static bool ContainsSignatureData(PdfDictionary dict)
        => dict.GetOptional("ByteRange") != null
           || dict.GetOptional("Contents") != null
           || dict.GetOptional("Type") is PdfName type && type.Value == "Sig"
           || dict.GetOptional("FT") is PdfName fieldType && fieldType.Value == "Sig";

    private PdfStream BuildObjectStream(
        IReadOnlyList<(int ObjectNumber, int Generation, PdfObject Object)> objects,
        int objectStreamNumber)
    {
        var index = new StringBuilder();
        var body = new StringBuilder();
        for (var i = 0; i < objects.Count; i++)
        {
            var (objNum, _, obj) = objects[i];
            index.Append(objNum).Append(' ').Append(body.Length).Append(' ');
            body.Append(PdfObjectWriter.Serialize(obj)).Append('\n');
            _compressedObjectEntries[objNum] = (objectStreamNumber, i);
        }

        var indexBytes = Encoding.Latin1.GetBytes(index.ToString());
        var bodyBytes = Encoding.Latin1.GetBytes(body.ToString());
        var plain = new byte[indexBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(indexBytes, 0, plain, 0, indexBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, plain, indexBytes.Length, bodyBytes.Length);
        var compressed = FlateCompress(plain);

        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("ObjStm"),
            ["N"] = new PdfInteger(objects.Count),
            ["First"] = new PdfInteger(indexBytes.Length),
            ["Filter"] = new PdfName("FlateDecode"),
            ["Length"] = new PdfInteger(compressed.Length),
        };
        return new PdfStream(dict, compressed);
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
            WriteStream(writer, stream, objNum, gen, isEncryptDict);
        }
        else
        {
            // The /Encrypt dictionary's own strings (O/U/[OE/UE/Perms]) are
            // already ciphertext — never route it through the encrypting
            // serializer, or it would be double-encrypted and unreadable.
            Func<byte[], byte[]>? encryptFn = (_encryptionOptions != null && !isEncryptDict)
                ? (plaintext) => EncryptForObject(objNum, gen, plaintext)
                : null;
            var content = PdfObjectWriter.Serialize(obj, encryptFn);
            writer.Write(Encoding.Latin1.GetBytes(content));
        }

        // Object footer: "\nendobj\n"
        writer.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
    }

    private void WriteStream(BinaryWriter writer, PdfStream stream, int objNum, int gen, bool isEncryptDict)
    {
        // Get stream data (use encoded if available, otherwise decoded)
        var data = stream.EncodedData;

        bool encrypting = _encryptionOptions != null && !isEncryptDict;
        if (encrypting)
        {
            // Honor /EncryptMetadata: when false, the XMP metadata stream
            // itself must stay plaintext even though every other stream is
            // encrypted (ISO 32000-2 §7.6.1) — readers key off /EncryptMetadata
            // to know not to attempt decrypting it. This applies identically
            // under R=4 and R=6; the only difference is which key/algorithm
            // EncryptForObject dispatches to for the streams that ARE encrypted.
            bool isMetadataStream = stream.GetNameOrNull("Type") == "Metadata";
            bool skipThisStream = isMetadataStream && !_encryptionOptions!.EncryptMetadata;
            if (!skipThisStream)
                data = EncryptForObject(objNum, gen, data);
        }

        // Ensure Length is correct (post-encryption size, if encrypted)
        stream["Length"] = new PdfInteger(data.Length);

        // Write dictionary part using the specialized serializer. Strings
        // inside a stream's own dictionary (e.g. an image's /Name) are
        // encrypted the same way as any other object's strings.
        Func<byte[], byte[]>? encryptFn = encrypting ? (plaintext) => EncryptForObject(objNum, gen, plaintext) : null;
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

    private long WriteXRefStream(BinaryWriter writer)
    {
        var xrefObjNum = _objectOffsets.Single(kvp => kvp.Value < 0).Key;
        long xrefOffset = writer.BaseStream.Position;
        _objectOffsets[xrefObjNum] = xrefOffset;

        int size = Math.Max(
            _objectOffsets.Count > 0 ? _objectOffsets.Keys.Max() : 0,
            _compressedObjectEntries.Count > 0 ? _compressedObjectEntries.Keys.Max() : 0) + 1;

        const int w1 = 1;
        const int w2 = 8;
        const int w3 = 4;
        using var raw = new MemoryStream(size * (w1 + w2 + w3));
        WriteXRefRow(raw, 0, 0, 65535, w2, w3);
        for (var objNum = 1; objNum < size; objNum++)
        {
            if (_compressedObjectEntries.TryGetValue(objNum, out var compressed))
                WriteXRefRow(raw, 2, compressed.ObjectStreamNumber, compressed.Index, w2, w3);
            else if (_objectOffsets.TryGetValue(objNum, out var offset))
                WriteXRefRow(raw, 1, offset, 0, w2, w3);
            else
                WriteXRefRow(raw, 0, 0, 65535, w2, w3);
        }

        var xrefData = FlateCompress(raw.ToArray());
        var trailer = BuildTrailerDictionary(size);
        trailer["Type"] = new PdfName("XRef");
        trailer["W"] = new PdfArray(new PdfInteger(w1), new PdfInteger(w2), new PdfInteger(w3));
        trailer["Filter"] = new PdfName("FlateDecode");
        trailer["Length"] = new PdfInteger(xrefData.Length);
        var stream = new PdfStream(trailer, xrefData);

        WriteIndirectObject(writer, xrefObjNum, 0, stream, isEncryptDict: false);
        writer.Write(Encoding.ASCII.GetBytes($"startxref\n{xrefOffset}\n%%EOF\n"));
        return xrefOffset;
    }

    private static void WriteXRefRow(Stream stream, int type, long field2, int field3, int w2, int w3)
    {
        stream.WriteByte((byte)type);
        WriteBigEndian(stream, field2, w2);
        WriteBigEndian(stream, field3, w3);
    }

    private static void WriteBigEndian(Stream stream, long value, int width)
    {
        for (var shift = (width - 1) * 8; shift >= 0; shift -= 8)
            stream.WriteByte((byte)((value >> shift) & 0xFF));
    }

    private static byte[] FlateCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private void WriteTrailer(BinaryWriter writer, long xrefOffset)
    {
        int size = (_objectOffsets.Count > 0 ? _objectOffsets.Keys.Max() : 0) + 1;

        var trailer = BuildTrailerDictionary(size);

        // Write trailer
        writer.Write(Encoding.ASCII.GetBytes("trailer\n"));
        var trailerStr = PdfObjectWriter.Serialize(trailer);
        writer.Write(Encoding.Latin1.GetBytes(trailerStr));

        // Write startxref
        writer.Write(Encoding.ASCII.GetBytes($"\nstartxref\n{xrefOffset}\n%%EOF\n"));
    }

    private PdfDictionary BuildTrailerDictionary(int size)
    {
        var trailer = new PdfDictionary
        {
            ["Size"] = new PdfInteger(size),
            ["Root"] = SaveSession.CatalogReference
        };

        var infoRef = SaveSession.InfoReference;
        if (infoRef != null)
            trailer["Info"] = infoRef;

        trailer["ID"] = GetOrCreateIdArray();

        if (_encryptionOptions != null)
            trailer["Encrypt"] = new PdfReference(_encryptObjNum, 0);

        return trailer;
    }

    private PdfDocumentSaveSession SaveSession
        => _saveSession
           ?? throw new InvalidOperationException(
               "A save session is available only while Write is running.");
}
