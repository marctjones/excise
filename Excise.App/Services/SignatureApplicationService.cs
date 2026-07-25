using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Excise.Core.Document;
using Excise.Core.Primitives;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Excise.App.Services;

/// <summary>
/// Options controlling how a signature is applied by
/// <see cref="SignatureApplicationService"/>.
/// </summary>
public sealed class SignatureApplicationOptions
{
    /// <summary>
    /// Name of the signature field to sign. If a field with this name already
    /// exists (authored earlier via <c>AddSignatureField</c>) and is unsigned,
    /// it is signed in place; otherwise a new invisible signature field with
    /// this name is created. Default: <c>Signature1</c>.
    /// </summary>
    public string FieldName { get; init; } = "Signature1";

    /// <summary>Optional signing reason (<c>/Reason</c>).</summary>
    public string? Reason { get; init; }

    /// <summary>Optional signing location (<c>/Location</c>).</summary>
    public string? Location { get; init; }

    /// <summary>Optional contact info (<c>/ContactInfo</c>).</summary>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// Human-readable signer name (<c>/Name</c>). Defaults to the signing
    /// certificate's subject common name.
    /// </summary>
    public string? SignerName { get; init; }

    /// <summary>
    /// Signing time recorded in the signature dictionary (<c>/M</c>). Defaults
    /// to the current UTC time. The CMS signed attributes carry their own
    /// signingTime added by BouncyCastle.
    /// </summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>
    /// Bytes reserved for the DER-encoded CMS signature in <c>/Contents</c>.
    /// The hole is written before the signature exists (two-pass ByteRange),
    /// so it must be large enough for the final CMS structure including all
    /// embedded certificates. Default 8192 bytes is ample for an RSA-2048
    /// self-signed identity.
    /// </summary>
    public int SignatureCapacityBytes { get; init; } = 8192;

    /// <summary>
    /// Additional certificates (e.g. intermediates for a locally-held chain)
    /// to embed in the CMS bundle so verifiers can build the chain offline.
    /// </summary>
    public IReadOnlyList<X509Certificate2>? AdditionalCertificates { get; init; }
}

/// <summary>
/// Applies a PKCS#7/CMS detached digital signature (<c>/Filter /Adobe.PPKLite</c>,
/// <c>/SubFilter /adbe.pkcs7.detached</c>) to a PDF document using a self-signed
/// or locally-held certificate. No CA account, no external API, no network —
/// the deliberate constraint of issue #623.
///
/// <para><b>Two-pass ByteRange strategy.</b> The signature covers the whole
/// file except the <c>/Contents</c> hex string itself, so the file must be
/// fully serialized before the signature can be computed, yet the signature
/// must live inside that same file. The service therefore:</para>
/// <list type="number">
///   <item>writes the signature dictionary with a zero-filled fixed-capacity
///     <c>/Contents</c> hex placeholder and a fixed-width <c>/ByteRange</c>
///     placeholder (<c>[0 1000000000 1000000000 1000000000]</c> — each number
///     exactly 10 digits);</item>
///   <item>saves the document to bytes and locates the placeholder hex hole
///     (offset of <c>&lt;</c> … offset just past <c>&gt;</c>);</item>
///   <item>patches the real ByteRange values in place, zero-padded to 10
///     digits so no byte offset in the file shifts;</item>
///   <item>digests the two signed ranges (everything before <c>&lt;</c> and
///     everything after <c>&gt;</c>), produces the detached CMS SignedData with
///     BouncyCastle, and backfills its hex into the hole (remaining capacity
///     stays <c>0</c>, i.e. trailing zero bytes after the DER — trimmed by
///     verifiers).</item>
/// </list>
///
/// <para>The output verifies through <see cref="SignatureVerificationService"/>
/// (#466), which is an independent implementation of ByteRange + CMS checking
/// and serves as the round-trip oracle in tests. A self-signed signer verifies
/// as cryptographically valid but untrusted — the correct, expected result.</para>
///
/// <para>Signing rewrites the whole file (excise saves are full rewrites, not
/// incremental updates), so it must be the last operation: any existing signed
/// signature would be invalidated, and the service refuses to sign a document
/// that already carries one. Timestamping (RFC3161) and LTV remain out of
/// scope per #623; a visible appearance (<c>/AP /N</c>) is baked onto the
/// widget when its <c>/Rect</c> has non-zero area — see
/// <see cref="SignatureAppearanceAuthoring"/>.</para>
/// </summary>
public class SignatureApplicationService
{
    // Placeholder ByteRange as serialized by PdfObjectWriter: each of the
    // three non-zero entries is exactly 10 digits so the real values can be
    // patched in as zero-padded 10-digit numbers without moving any byte.
    private const long ByteRangePlaceholderValue = 1000000000;
    private const string ByteRangePlaceholderToken =
        "/ByteRange [0 1000000000 1000000000 1000000000]";

    private readonly ILogger<SignatureApplicationService> _logger;

    public SignatureApplicationService(ILogger<SignatureApplicationService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Sign <paramref name="document"/> with <paramref name="certificate"/>
    /// (which must carry an accessible RSA or ECDSA private key) and return
    /// the signed file bytes. The in-memory document is mutated (signature
    /// field + signature dictionary are added), but only the returned bytes
    /// constitute the signed file — re-saving the document produces a fresh
    /// serialization whose placeholder is unsigned.
    /// </summary>
    public byte[] SignDocument(
        PdfDocument document,
        X509Certificate2 certificate,
        SignatureApplicationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(certificate);
        options ??= new SignatureApplicationOptions();

        if (options.SignatureCapacityBytes < 2048)
        {
            throw new ArgumentException(
                "SignatureCapacityBytes must be at least 2048 to hold a CMS SignedData structure.",
                nameof(options));
        }

        // Fail closed on encrypted sources: signing serializes via the plain
        // (plaintext-by-design) save path, which would silently strip the
        // document's protection — the exact loss #638/#641 exist to prevent.
        // Signing while preserving encryption needs writer support for an
        // unencrypted /Contents inside an otherwise-encrypted file (spec
        // §7.6.2) and is a follow-up on #623.
        if (document.IsEncrypted)
        {
            throw new InvalidOperationException(
                "Signing an encrypted document is not supported yet: the signed file would be " +
                "written without its encryption. Remove protection explicitly first if that is " +
                "intended (issue #623).");
        }

        // Convert the identity up front so a certificate without a usable
        // private key fails before the document is mutated.
        var (privateKey, bouncyCertificate, signatureAlgorithm) = ConvertIdentity(certificate);

        GuardNoExistingSignedSignature(document);

        var signingTime = options.SigningTime ?? DateTimeOffset.UtcNow;
        var signerName = ResolveSignerName(options, certificate);

        var fieldDictionary = FindOrCreateSignatureField(document, options.FieldName);
        fieldDictionary["V"] = BuildSignatureDictionary(options, signerName, signingTime);
        SetSigFlags(document);

        // Visible signature appearance (#623, last bullet): a baked /AP /N
        // is only added when the widget's /Rect has non-zero area. A
        // zero-size /Rect (the default for a freshly-created field, see
        // FindOrCreateSignatureField) stays invisible — still a fully valid
        // signature. See SignatureAppearanceAuthoring for the appearance
        // stream itself; it mirrors PdfAnnotationAuthoring's baked-appearance
        // pattern (#626) and does not touch the CMS/ByteRange machinery below.
        SignatureAppearanceAuthoring.ApplyVisibleAppearance(
            document, fieldDictionary, BuildAppearanceLines(options, signerName, signingTime));

        _logger.LogInformation(
            "Signing document as {Subject} into field {Field}",
            certificate.Subject, options.FieldName);

        // Pass 1: serialize with placeholders.
        var fileBytes = document.SaveToBytes();

        // Pass 2: locate the hole, fix the ByteRange in place, digest, sign, backfill.
        var (holeStart, holeEnd) = LocateContentsHole(fileBytes, options.SignatureCapacityBytes);
        PatchByteRange(fileBytes, holeStart, holeEnd);

        var signedContent = ExtractSignedContent(fileBytes, holeStart, holeEnd);
        var cmsSignature = CreateDetachedCmsSignature(
            signedContent, privateKey, bouncyCertificate, signatureAlgorithm,
            options.AdditionalCertificates);
        BackfillContents(fileBytes, holeStart, holeEnd, cmsSignature);

        _logger.LogInformation(
            "Applied {CmsBytes}-byte CMS signature; ByteRange [0 {Gap0} {Gap1} {Tail}] over {FileBytes} bytes",
            cmsSignature.Length, holeStart, holeEnd, fileBytes.Length - holeEnd, fileBytes.Length);

        return fileBytes;
    }

    /// <summary>
    /// Open <paramref name="inputPath"/>, sign it, and write the signed file
    /// to <paramref name="outputPath"/>.
    /// </summary>
    public void SignFile(
        string inputPath,
        string outputPath,
        X509Certificate2 certificate,
        SignatureApplicationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        using var document = PdfDocument.Open(inputPath);
        var signedBytes = SignDocument(document, certificate, options);
        File.WriteAllBytes(outputPath, signedBytes);
    }

    // ── field / dictionary construction ─────────────────────────────────────

    /// <summary>
    /// A full-rewrite save invalidates any signature already in the file, so
    /// signing must be the last operation and a second signature cannot be
    /// added without incremental-update support (see issue #623).
    /// </summary>
    private static void GuardNoExistingSignedSignature(PdfDocument document)
    {
        var acroForm = document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) as PdfDictionary;
        var fields = acroForm != null
            ? document.Resolve(acroForm.GetOptional("Fields") ?? PdfNull.Instance) as PdfArray
            : null;
        if (fields == null)
        {
            return;
        }

        foreach (var item in fields)
        {
            if (document.Resolve(item) is PdfDictionary fieldDict &&
                fieldDict.GetNameOrNull("FT") == "Sig" &&
                fieldDict.GetOptional("V") != null)
            {
                throw new InvalidOperationException(
                    "Document already contains a signed signature field. excise saves are full " +
                    "rewrites, so adding a second signature would invalidate the existing one; " +
                    "multi-signature support requires incremental-update saves (issue #623).");
            }
        }
    }

    private static PdfDictionary FindOrCreateSignatureField(PdfDocument document, string fieldName)
    {
        var existing = document.GetAcroForm()?.FindField(fieldName);
        if (existing != null)
        {
            if (existing.FieldType != PdfFieldType.Signature)
            {
                throw new InvalidOperationException(
                    $"Field '{fieldName}' exists but is a {existing.FieldType} field, not a signature field.");
            }

            if (existing.RawDictionary.GetOptional("V") != null)
            {
                throw new InvalidOperationException(
                    $"Signature field '{fieldName}' is already signed.");
            }

            return existing.RawDictionary;
        }

        // No such field: author a new invisible signature field on page 1.
        var field = document.AddSignatureField(
            pageNumber: 1,
            rect: new PdfRectangle(0, 0, 0, 0),
            fieldName: fieldName);
        return field.RawDictionary;
    }

    private static PdfDictionary BuildSignatureDictionary(
        SignatureApplicationOptions options,
        string? signerName,
        DateTimeOffset signingTime)
    {
        var signature = new PdfDictionary();
        signature.SetName("Type", "Sig");
        signature.SetName("Filter", "Adobe.PPKLite");
        signature.SetName("SubFilter", "adbe.pkcs7.detached");

        // Fixed-width placeholders — see the class remarks for why the exact
        // serialized width matters.
        var byteRange = new PdfArray();
        byteRange.Add((PdfObject)new PdfInteger(0));
        byteRange.Add((PdfObject)new PdfInteger(ByteRangePlaceholderValue));
        byteRange.Add((PdfObject)new PdfInteger(ByteRangePlaceholderValue));
        byteRange.Add((PdfObject)new PdfInteger(ByteRangePlaceholderValue));
        signature["ByteRange"] = byteRange;
        signature["Contents"] = new PdfString(new byte[options.SignatureCapacityBytes], isHex: true);

        if (!string.IsNullOrEmpty(signerName))
        {
            signature.SetString("Name", signerName);
        }

        if (!string.IsNullOrEmpty(options.Reason))
        {
            signature.SetString("Reason", options.Reason);
        }

        if (!string.IsNullOrEmpty(options.Location))
        {
            signature.SetString("Location", options.Location);
        }

        if (!string.IsNullOrEmpty(options.ContactInfo))
        {
            signature.SetString("ContactInfo", options.ContactInfo);
        }

        signature.SetString("M", FormatPdfDate(signingTime));
        return signature;
    }

    /// <summary>
    /// Resolve the human-readable signer name used both in the signature
    /// dictionary's <c>/Name</c> and in the visible appearance text.
    /// </summary>
    private static string? ResolveSignerName(SignatureApplicationOptions options, X509Certificate2 certificate) =>
        options.SignerName ?? certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

    /// <summary>
    /// Build the text lines drawn into the visible signature appearance
    /// (see <see cref="SignatureAppearanceAuthoring"/>): signer identity,
    /// date, and any optional reason/location the caller supplied.
    /// </summary>
    private static IReadOnlyList<string> BuildAppearanceLines(
        SignatureApplicationOptions options, string? signerName, DateTimeOffset signingTime)
    {
        var lines = new List<string>
        {
            string.IsNullOrEmpty(signerName)
                ? "Digitally signed"
                : $"Digitally signed by {signerName}",
            $"Date: {signingTime:yyyy-MM-dd HH:mm:ss zzz}"
        };

        if (!string.IsNullOrEmpty(options.Reason))
        {
            lines.Add($"Reason: {options.Reason}");
        }

        if (!string.IsNullOrEmpty(options.Location))
        {
            lines.Add($"Location: {options.Location}");
        }

        return lines;
    }

    private static void SetSigFlags(PdfDocument document)
    {
        // /SigFlags 3 = SignaturesExist | AppendOnly (PDF spec §12.7.2).
        if (document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary acroForm)
        {
            acroForm.SetInt("SigFlags", 3);
        }
    }

    /// <summary>PDF date string (spec §7.9.4), e.g. <c>D:20260725093000+00'00'</c>.</summary>
    private static string FormatPdfDate(DateTimeOffset time)
    {
        var offset = time.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{time:yyyyMMddHHmmss}{sign}{magnitude.Hours:D2}'{magnitude.Minutes:D2}'");
    }

    // ── two-pass byte patching ──────────────────────────────────────────────

    /// <summary>
    /// Locate the zero-filled <c>/Contents</c> placeholder in the serialized
    /// file. Returns the offset of <c>&lt;</c> and the offset just past
    /// <c>&gt;</c> — exactly the excluded gap the ByteRange must describe.
    /// </summary>
    private static (int HoleStart, int HoleEnd) LocateContentsHole(byte[] fileBytes, int capacityBytes)
    {
        var pattern = BuildContentsPlaceholderPattern(capacityBytes);
        var first = IndexOf(fileBytes, pattern, 0);
        if (first < 0)
        {
            throw new InvalidOperationException(
                "Serialized document does not contain the signature /Contents placeholder. " +
                "The writer's dictionary serialization may have changed shape.");
        }

        if (IndexOf(fileBytes, pattern, first + 1) >= 0)
        {
            throw new InvalidOperationException(
                "Serialized document contains more than one signature /Contents placeholder; " +
                "cannot determine which signature dictionary to sign.");
        }

        var holeStart = first + "/Contents ".Length;      // offset of '<'
        var holeEnd = holeStart + capacityBytes * 2 + 2;  // offset just past '>'
        return (holeStart, holeEnd);
    }

    private static byte[] BuildContentsPlaceholderPattern(int capacityBytes)
    {
        // PdfObjectWriter emits hex strings as a contiguous "<" + hex + ">"
        // with exactly one space between the /Contents name and the value.
        var sb = new StringBuilder("/Contents <");
        sb.Append('0', capacityBytes * 2);
        sb.Append('>');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Overwrite the placeholder ByteRange with the real offsets, zero-padded
    /// to the same 10-digit width so the file length and every offset in it
    /// are unchanged (leading zeros are valid PDF integer syntax).
    /// </summary>
    private static void PatchByteRange(byte[] fileBytes, int holeStart, int holeEnd)
    {
        var placeholder = Encoding.ASCII.GetBytes(ByteRangePlaceholderToken);
        var first = IndexOf(fileBytes, placeholder, 0);
        if (first < 0)
        {
            throw new InvalidOperationException(
                "Serialized document does not contain the signature /ByteRange placeholder.");
        }

        if (IndexOf(fileBytes, placeholder, first + 1) >= 0)
        {
            throw new InvalidOperationException(
                "Serialized document contains more than one signature /ByteRange placeholder.");
        }

        var tailLength = fileBytes.Length - holeEnd;
        var patched = string.Create(CultureInfo.InvariantCulture,
            $"/ByteRange [0 {holeStart:D10} {holeEnd:D10} {tailLength:D10}]");
        var patchedBytes = Encoding.ASCII.GetBytes(patched);
        if (patchedBytes.Length != placeholder.Length)
        {
            throw new InvalidOperationException(
                "ByteRange values exceed the reserved 10-digit width; file too large to sign in place.");
        }

        patchedBytes.CopyTo(fileBytes, first);
    }

    private static byte[] ExtractSignedContent(byte[] fileBytes, int holeStart, int holeEnd)
    {
        var signedContent = new byte[holeStart + fileBytes.Length - holeEnd];
        Buffer.BlockCopy(fileBytes, 0, signedContent, 0, holeStart);
        Buffer.BlockCopy(fileBytes, holeEnd, signedContent, holeStart, fileBytes.Length - holeEnd);
        return signedContent;
    }

    private static void BackfillContents(byte[] fileBytes, int holeStart, int holeEnd, byte[] cmsSignature)
    {
        var capacityHexChars = holeEnd - holeStart - 2;
        var signatureHex = Convert.ToHexString(cmsSignature);
        if (signatureHex.Length > capacityHexChars)
        {
            throw new InvalidOperationException(
                $"CMS signature ({cmsSignature.Length} bytes) exceeds the reserved /Contents capacity " +
                $"({capacityHexChars / 2} bytes). Increase SignatureApplicationOptions.SignatureCapacityBytes.");
        }

        // The rest of the hole stays '0' — trailing zero bytes after the DER
        // structure, which verifiers trim (the reserved capacity is fixed, so
        // the hex string keeps its exact serialized width).
        Encoding.ASCII.GetBytes(signatureHex).CopyTo(fileBytes, holeStart + 1);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
    {
        var index = haystack.AsSpan(Math.Max(0, startIndex)).IndexOf(needle);
        return index < 0 ? -1 : index + Math.Max(0, startIndex);
    }

    // ── CMS / identity conversion ───────────────────────────────────────────

    private static byte[] CreateDetachedCmsSignature(
        byte[] signedContent,
        AsymmetricKeyParameter privateKey,
        Org.BouncyCastle.X509.X509Certificate signerCertificate,
        string signatureAlgorithm,
        IReadOnlyList<X509Certificate2>? additionalCertificates)
    {
        var signerInfoGenerator = new SignerInfoGeneratorBuilder()
            .Build(new Asn1SignatureFactory(signatureAlgorithm, privateKey), signerCertificate);

        var generator = new CmsSignedDataGenerator();
        generator.AddSignerInfoGenerator(signerInfoGenerator);
        generator.AddCertificate(signerCertificate);

        if (additionalCertificates != null)
        {
            var parser = new Org.BouncyCastle.X509.X509CertificateParser();
            foreach (var extra in additionalCertificates)
            {
                generator.AddCertificate(parser.ReadCertificate(extra.RawData));
            }
        }

        var cms = generator.Generate(new CmsProcessableByteArray(signedContent), encapsulate: false);
        return cms.GetEncoded();
    }

    private static (AsymmetricKeyParameter PrivateKey, Org.BouncyCastle.X509.X509Certificate Certificate, string Algorithm)
        ConvertIdentity(X509Certificate2 certificate)
    {
        var bouncyCertificate = new Org.BouncyCastle.X509.X509CertificateParser()
            .ReadCertificate(certificate.RawData);

        AsymmetricAlgorithm? algorithm = certificate.GetRSAPrivateKey();
        algorithm ??= certificate.GetECDsaPrivateKey();
        if (algorithm == null)
        {
            throw new ArgumentException(
                "Signing certificate has no accessible RSA or ECDSA private key. Load the PKCS#12 " +
                "with an exportable key (SigningCertificateFactory.LoadFromPkcs12 does this).",
                nameof(certificate));
        }

        using (algorithm)
        {
            var privateKey = ExportPrivateKey(algorithm);
            var signatureAlgorithm = privateKey switch
            {
                RsaPrivateCrtKeyParameters or RsaKeyParameters => "SHA256WITHRSA",
                ECPrivateKeyParameters => "SHA256WITHECDSA",
                _ => throw new ArgumentException(
                    $"Unsupported private key type: {privateKey.GetType().Name}", nameof(certificate))
            };
            return (privateKey, bouncyCertificate, signatureAlgorithm);
        }
    }

    private static AsymmetricKeyParameter ExportPrivateKey(AsymmetricAlgorithm algorithm)
    {
        try
        {
            return PrivateKeyFactory.CreateKey(algorithm.ExportPkcs8PrivateKey());
        }
        catch (CryptographicException)
        {
            // Some key stores refuse plaintext export but allow encrypted
            // export — round-trip through an encrypted PKCS#8 blob with a
            // transient in-process password.
            const string transientPassword = "excise-transient-export";
            var encrypted = algorithm.ExportEncryptedPkcs8PrivateKey(
                transientPassword,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
            return PrivateKeyFactory.DecryptKey(transientPassword.ToCharArray(), encrypted);
        }
    }
}
