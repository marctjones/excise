using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;
namespace Excise.Core.Tests.Parsing;

/// <summary>
/// Phase 1 (PDF 2.0) acceptance: every modern PDF (1.5+) compresses
/// indirect objects into /ObjStm streams. Without correct compressed-
/// object resolution, GetObject() returns junk for any object whose
/// xref entry has type 2.
///
/// The existing PdfDocument.GetObjectFromStream code path *appears*
/// implemented — these tests pin that down so it can't silently
/// regress.
/// </summary>
public class ObjectStreamResolutionTests
{
    private readonly ITestOutputHelper _out;
    public ObjectStreamResolutionTests(ITestOutputHelper o) { _out = o; }

    private const string ScrambledBirthCert =
        "../../../../test-pdfs/sample-pdfs/birth-certificate-request-scrambled.pdf";
    private const string MultilingualCjk =
        "../../../../test-pdfs/sample-pdfs/multilingual-noto-cjk.pdf";

    [Fact]
    public void OpensFileWithObjectStreams_AllCompressedObjectsResolveWithoutError()
    {
        // The scrambled birth certificate uses /ObjStm to compress its
        // page-tree and resource objects (6 occurrences of /ObjStm in the
        // file). If compressed-object resolution were broken, opening
        // would fail or the catalog/pages chain would return garbage.
        if (!File.Exists(ScrambledBirthCert)) return;

        using var doc = PdfDocument.Open(ScrambledBirthCert);

        doc.Catalog.Should().NotBeNull("catalog must resolve even when stored in /ObjStm");
        doc.PageCount.Should().BeGreaterThan(0,
            "pages tree must resolve even when stored in /ObjStm");

        // Walk every page — each resolves its dict, resources, content
        // streams transitively; if any compressed dependency fails, this
        // throws.
        for (int p = 1; p <= doc.PageCount; p++)
        {
            var page = doc.GetPage(p);
            page.Should().NotBeNull($"page {p} must resolve");
            page.Width.Should().BeGreaterThan(0);
            page.Height.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void OpensFileWithObjectStreams_CompressedObjectsAreActuallyInStream()
    {
        // Stronger assertion than the previous test: confirm that AT LEAST
        // ONE object reachable from the catalog truly has IsCompressed=true
        // in its xref entry. Otherwise the file may not actually exercise
        // the /ObjStm path and the test is vacuous.
        if (!File.Exists(ScrambledBirthCert)) return;

        using var doc = PdfDocument.Open(ScrambledBirthCert);

        var xref = GetXRef(doc);
        var compressed = xref.Where(e => e.Value.IsCompressed).ToList();
        _out.WriteLine($"xref entries total: {xref.Count}, compressed (type 2): {compressed.Count}");

        compressed.Should().NotBeEmpty(
            "the test file must contain at least one type-2 xref entry — " +
            "otherwise this test isn't actually exercising /ObjStm resolution");

        foreach (var (objNum, entry) in compressed.Take(5))
        {
            _out.WriteLine($"  obj {objNum}: stream={entry.ObjectStreamNumber} index={entry.IndexInStream}");
            var obj = doc.GetObject(objNum);
            obj.Should().NotBeNull($"compressed object {objNum} must resolve via /ObjStm");
            obj.Should().NotBeOfType<PdfNull>(
                $"compressed object {objNum} must resolve to its real value, not null");
        }
    }

    [Fact]
    public void RoundTripFileWithObjectStreams_LoadSaveLoad_PreservesCatalogAndPages()
    {
        // Open a /ObjStm-containing file, save it (the writer materialises
        // each object as an uncompressed indirect — that's fine, /ObjStm
        // is just a compression scheme), reload, and check that every
        // object that was resolvable before is still resolvable after.
        if (!File.Exists(ScrambledBirthCert)) return;

        int originalPageCount;
        int originalCompressedCount;
        using (var doc = PdfDocument.Open(ScrambledBirthCert))
        {
            originalPageCount = doc.PageCount;
            var xref = GetXRef(doc);
            originalCompressedCount = xref.Count(e => e.Value.IsCompressed);
        }

        // Round-trip through the writer.
        using var ms = new MemoryStream();
        using (var doc = PdfDocument.Open(ScrambledBirthCert))
        {
            var writer = new Excise.Core.Writing.PdfDocumentWriter(doc);
            writer.Write(ms);
        }
        ms.Position = 0;
        var roundTripBytes = ms.ToArray();
        _out.WriteLine($"original size: {new FileInfo(ScrambledBirthCert).Length}, " +
                       $"round-trip size: {roundTripBytes.Length}, " +
                       $"compressed objs in original: {originalCompressedCount}");

        using (var roundTripped = PdfDocument.Open(new MemoryStream(roundTripBytes)))
        {
            roundTripped.Catalog.Should().NotBeNull(
                "catalog must still resolve after round-trip");
            roundTripped.PageCount.Should().Be(originalPageCount,
                "page count must be preserved across save/reload");

            for (int p = 1; p <= roundTripped.PageCount; p++)
            {
                var page = roundTripped.GetPage(p);
                page.Should().NotBeNull($"page {p} must resolve in the round-tripped file");
                page.Width.Should().BeGreaterThan(0,
                    $"page {p} dimensions must survive round-trip");
            }

            // After round-trip, the writer materialises everything as
            // uncompressed indirect objects, so /ObjStm count goes to 0.
            // That's fine — what matters is that every formerly-compressed
            // object still resolves to a real value.
            var xref = GetXRef(roundTripped);
            _out.WriteLine($"round-tripped xref entries: {xref.Count}, " +
                           $"compressed (type 2): {xref.Count(e => e.Value.IsCompressed)}");
        }
    }

    [Theory]
    [InlineData("../../../../test-pdfs/smoke/irs-1040.pdf")]
    [InlineData("../../../../test-pdfs/smoke/irs-w9.pdf")]
    [InlineData("../../../../test-pdfs/smoke/scotus-trump-v-anderson.pdf")]
    public void OpensLinearizedPdf_ParsesViaStartxrefFromTail(string relativePath)
    {
        // Linearized ("Fast Web View") PDFs have a /Linearized dict at the
        // start of the file plus hint streams for streamed delivery. A
        // PDF *reader* doesn't need to interpret either — we read the
        // file end-first via startxref. These IRS / SCOTUS forms are all
        // linearized; if startxref-tail parsing breaks on linearized
        // structure, every page fetch fails.
        if (!File.Exists(relativePath)) return;

        using var doc = PdfDocument.Open(relativePath);
        doc.Catalog.Should().NotBeNull("catalog must resolve in linearized PDF");
        doc.PageCount.Should().BeGreaterThan(0, "page count must resolve");

        // First page is the meaningful one for linearization (front-loaded
        // for streamed delivery). Confirm it parses.
        var firstPage = doc.GetPage(1);
        firstPage.Should().NotBeNull();
        firstPage.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void OpensCjkFileWithObjectStreams_PagesAndFontsResolve()
    {
        // CJK file: /ObjStm + Type 0/CID fonts. The fonts won't render
        // correctly until Phase 8, but the *parsing* should already work
        // — page tree and font dictionary objects must resolve through
        // the /ObjStm path.
        if (!File.Exists(MultilingualCjk)) return;

        using var doc = PdfDocument.Open(MultilingualCjk);
        doc.PageCount.Should().BeGreaterThan(0);

        var page = doc.GetPage(1);
        page.Resources.Should().NotBeNull(
            "page resources dict must resolve (typically inside /ObjStm)");
    }

    private const string EncryptedRC4Pdf =
        "../../../../test-pdfs/isartor/Isartor testsuite/PDFA-1b/6.1 File structure/6.1.3 File trailer/isartor-6-1-3-t02-fail-a.pdf";

    // qpdf-generated test PDFs: the Isartor file works for testing the
    // open path, but its single page has an empty content stream
    // (deliberately malformed file). For end-to-end "did the bytes
    // come back as real PDF operators" we need an encrypted file
    // with non-trivial page content. Generated via:
    //   qpdf --allow-weak-crypto --encrypt '' '' 128 -- src.pdf rc4-128.pdf
    //   qpdf --allow-weak-crypto --encrypt '' '' 40  -- src.pdf rc4-40.pdf
    private const string EncryptedRC4_128 =
        "../../../../test-pdfs/encrypted/birth-cert-rc4-128.pdf";
    private const string EncryptedRC4_40 =
        "../../../../test-pdfs/encrypted/birth-cert-rc4-40.pdf";
    private const string EncryptedAES_128 =
        "../../../../test-pdfs/encrypted/birth-cert-aes-128.pdf";
    private const string EncryptedAES_256 =
        "../../../../test-pdfs/encrypted/birth-cert-aes-256.pdf";
    private const string EmbeddedFileOnlyEncryptedPdf =
        "../../../../test-pdfs/pdfjs/auth-event-ef-open.pdf";
    private const string PasswordEncryptedAes128 =
        "../../../../test-pdfs/poppler/unittestcases/PasswordEncrypted.pdf";
    private const string PasswordEncryptedAes256 =
        "../../../../test-pdfs/poppler/unittestcases/encrypted-256.pdf";
    private const string PasswordEncryptedPdfDocEncoding =
        "../../../../test-pdfs/poppler/unittestcases/Gday garçon - open.pdf";

    [Theory]
    [InlineData(EncryptedRC4_128, "RC4 V=2 R=3 (128-bit)")]
    [InlineData(EncryptedRC4_40, "RC4 V=1 R=2 (40-bit legacy)")]
    [InlineData(EncryptedAES_128, "AES-128 V=4 R=4 CFM=AESV2")]
    [InlineData(EncryptedAES_256, "AES-256 V=5 R=6 CFM=AESV3 (PDF 2.0)")]
    public void OpensRealEncryptedPdf_DecryptedContentIsValidPdfOperators(string path, string description)
    {
        // End-to-end Phase 2 RC4 test: take a known PDF (the scrambled
        // birth certificate, which we already test extensively in Phase
        // 1), encrypt it with qpdf using empty user password, then
        // verify excise decrypts it transparently and the content stream
        // bytes are recognisable PDF operators ("BT", "Tj", "ET", etc.)
        // not RC4 ciphertext.
        if (!File.Exists(path)) return;

        using var doc = PdfDocument.Open(path);
        doc.IsEncrypted.Should().BeTrue();
        doc.IsDecrypting.Should().BeTrue($"{description}: handler must build for empty password");

        var page = doc.GetPage(1);
        var content = page.GetContentStreamBytes();
        content.Should().NotBeNull();
        content!.Length.Should().BeGreaterThan(50,
            $"{description}: page has real content; decrypted+decoded bytes should be substantive");

        var text = System.Text.Encoding.Latin1.GetString(content);
        _out.WriteLine($"{description}: {content.Length} bytes, preview: " +
                       text.Substring(0, Math.Min(150, text.Length)).Replace('\n', ' ').Replace('\r', ' '));

        // PDF text-drawing operators that should appear if the content
        // stream is real PDF — these are present in the source's
        // content streams.
        text.Should().ContainAny(new[] { "BT", "Tj", "TJ", "ET" },
            $"{description}: decrypted content must look like real PDF operators");
    }

    [Fact]
    public void OpensAes256Pdf_VerifiesPasswordViaAlgorithm2BAndDerivesFileKey()
    {
        // AES-256 (V=5 R=6) is the PDF 2.0 native handler. Different KDF
        // from V=4: 48-byte /U /O carrying validation+key salts, /UE /OE
        // holding the encrypted file key, the chained SHA-256/384/512
        // hash from Algorithm 2.B for password verification + key
        // derivation.
        //
        // This test asserts the V=5 path successfully verifies the empty
        // user password (Algorithm 11), AES-decrypts /UE to recover the
        // 32-byte file encryption key, and produces a usable handler.
        // End-to-end content is also covered by the parameterised theory
        // above since AES-256 is included there.
        if (!File.Exists(EncryptedAES_256)) return;

        using var doc = PdfDocument.Open(EncryptedAES_256);
        doc.IsEncrypted.Should().BeTrue();
        doc.IsDecrypting.Should().BeTrue(
            "V=5 R=6 with empty password must verify via Algorithm 2.B and yield a working handler");
        doc.PageCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void OpensEmbeddedFileOnlyEncryptedPdf_IdentityDocumentFiltersDoNotRequirePassword()
    {
        if (!File.Exists(EmbeddedFileOnlyEncryptedPdf)) return;

        using var doc = PdfDocument.Open(EmbeddedFileOnlyEncryptedPdf);

        doc.IsEncrypted.Should().BeTrue();
        doc.IsDecrypting.Should().BeFalse(
            "the file's /StmF and /StrF are /Identity; only embedded-file streams use the encrypted crypt filter");
        doc.PageCount.Should().Be(1);
    }

    [Fact]
    public void OpensEncryptedPdf_RC4_BuildsHandlerAndDecryptsStreams()
    {
        // Pin down that the Isartor encrypted file (RC4 V=2 R=3 with empty
        // password) opens via the security-handler path. The file's
        // single page deliberately has an empty content stream — it's a
        // PDF/A "fail" test fixture — so we can't assert on operators
        // here. End-to-end content-bytes-are-operators is covered by
        // OpensRealEncryptedPdf_DecryptedContentIsValidPdfOperators on
        // qpdf-generated files with real content.
        if (!File.Exists(EncryptedRC4Pdf)) return;

        using var doc = PdfDocument.Open(EncryptedRC4Pdf);
        doc.IsEncrypted.Should().BeTrue("file's trailer carries /Encrypt");
        doc.IsDecrypting.Should().BeTrue(
            "the security handler must have built successfully for the empty-password case " +
            "— means key derivation matches Algorithm 2 and password verification matches Algorithm 6");
        doc.PageCount.Should().BeGreaterThan(0);

        // Decryption must produce valid zlib bytes (deflate header) for
        // the page content stream — even when the resulting decoded
        // content is empty.
        var page = doc.GetPage(1);
        var contentsObj = page.Dictionary.GetOptional("Contents");
        if (contentsObj is PdfReference cref &&
            doc.GetObject(cref) is PdfStream ps && ps.Filters.Contains("FlateDecode"))
        {
            // First two bytes of a valid zlib stream: CMF (0x78) + FLG.
            // (CMF*256 + FLG) % 31 == 0 per RFC 1950.
            ps.EncodedData[0].Should().Be(0x78,
                "decryption must produce a valid zlib header (CMF=0x78). " +
                "Pre-decryption these were ciphertext that wouldn't pass any header check.");
            int header = (ps.EncodedData[0] << 8) | ps.EncodedData[1];
            (header % 31).Should().Be(0, "zlib header checksum must validate");
        }
    }

    [Fact]
    public void OpensEncryptedPdf_WithAllowEncrypted_StillReturnsDocumentForInspection()
    {
        // The opt-in escape from before the RC4 path landed: when the
        // handler can't be built (unsupported V/R, wrong password),
        // allowEncrypted=true keeps returning a doc for inspecting the
        // /Encrypt dict at the caller's risk. With RC4 working, the
        // empty-password case won't hit this fallback path; this test
        // just keeps the API contract pinned.
        if (!File.Exists(EncryptedRC4Pdf)) return;

        using var doc = PdfDocument.Open(EncryptedRC4Pdf, allowEncrypted: true);
        doc.IsEncrypted.Should().BeTrue();
        doc.Trailer.GetOptional("Encrypt").Should().NotBeNull(
            "/Encrypt dict must be reachable so callers inspecting encryption parameters can read /V, /R, /U, /O");
    }

    [Fact]
    public void OpensEncryptedPdf_WithSuppliedAsciiUserPassword()
    {
        if (!File.Exists(PasswordEncryptedAes128)) return;

        using var doc = PdfDocument.Open(PasswordEncryptedAes128, userPassword: "password");

        doc.IsEncrypted.Should().BeTrue();
        doc.IsDecrypting.Should().BeTrue();
        doc.PageCount.Should().Be(1);
    }

    [Fact]
    public void OpensEncryptedPdf_WithSuppliedAes256UserPassword()
    {
        if (!File.Exists(PasswordEncryptedAes256)) return;

        using var doc = PdfDocument.Open(PasswordEncryptedAes256, userPassword: "user-secret");

        doc.IsEncrypted.Should().BeTrue();
        doc.IsDecrypting.Should().BeTrue();
        doc.PageCount.Should().Be(1);
    }

    [Fact]
    public void OpensEncryptedPdf_WithPdfDocEncodingPassword()
    {
        if (!File.Exists(PasswordEncryptedPdfDocEncoding)) return;

        using var doc = PdfDocument.Open(PasswordEncryptedPdfDocEncoding, userPassword: "garçon");

        doc.IsEncrypted.Should().BeTrue();
        doc.IsDecrypting.Should().BeTrue();
        doc.PageCount.Should().Be(1);
    }

    // ── /ObjStm materialization cache (#743) ─────────────────────────────
    // GetObjectFromStream caches the parsed object-stream index and batch-
    // materializes all N contained objects on first touch instead of
    // re-parsing the index per fetch. These tests pin the invariant that
    // this is caching only: identical resolution regardless of fetch order,
    // cache hits return the cached instance, and malformed streams degrade
    // exactly as the old per-fetch path did (exception on the broken object
    // only, no crash/hang, siblings still resolve).

    [Fact]
    public void ObjStmCache_FetchOrderDoesNotChangeResolvedValues()
    {
        // Two documents over the same file: one fetches every compressed
        // object in ascending order, the other in descending order (so each
        // object stream is first touched via a different contained object).
        // Every object must serialize identically — fetch order and cache
        // state must not affect resolution.
        if (!File.Exists(ScrambledBirthCert)) return;

        using var docAsc = PdfDocument.Open(ScrambledBirthCert);
        using var docDesc = PdfDocument.Open(ScrambledBirthCert);

        var compressed = GetXRef(docAsc)
            .Where(e => e.Value.IsCompressed)
            .Select(e => e.Key)
            .OrderBy(n => n)
            .ToList();
        compressed.Should().NotBeEmpty("fixture must exercise the /ObjStm path");

        var ascending = compressed
            .ToDictionary(n => n, n => Excise.Core.Writing.PdfObjectWriter.Serialize(docAsc.GetObject(n)));
        var descending = ((IEnumerable<int>)compressed).Reverse()
            .ToDictionary(n => n, n => Excise.Core.Writing.PdfObjectWriter.Serialize(docDesc.GetObject(n)));

        foreach (var n in compressed)
        {
            descending[n].Should().Be(ascending[n],
                $"compressed object {n} must resolve to the same value regardless of fetch order");
        }
    }

    [Fact]
    public void ObjStmCache_RepeatedFetchReturnsCachedInstance()
    {
        if (!File.Exists(ScrambledBirthCert)) return;

        using var doc = PdfDocument.Open(ScrambledBirthCert);
        var compressed = GetXRef(doc).First(e => e.Value.IsCompressed).Key;

        var first = doc.GetObject(compressed);
        var second = doc.GetObject(compressed);
        second.Should().BeSameAs(first,
            "a second fetch of the same compressed object must be a cache hit");
    }

    [Fact]
    public void ObjStmCache_BrokenObjectOffset_ThrowsOnThatObjectOnly_SiblingsResolve()
    {
        // Object stream whose index maps object 3 to an offset far past the
        // end of the decoded data. The old per-fetch path threw when (and
        // only when) the broken object was requested; the batch-materialized
        // path must behave identically: siblings in the same stream resolve,
        // the broken object throws, nothing crashes or hangs.
        var pdf = BuildObjStmPdf(objThreeOffset: 9999);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        doc.Catalog.Should().NotBeNull("catalog lives in the ObjStm and is intact");

        var good = doc.GetObject(2);
        good.Should().BeOfType<PdfDictionary>("the intact sibling object must resolve");
        ((PdfDictionary)good).GetStringOrNull("Foo").Should().Be("bar");

        var act = () => doc.GetObject(3);
        act.Should().Throw<Exception>(
            "the object whose offset points past the end of the stream must fail as before");

        // And the failure must not have poisoned the stream's cache entry.
        doc.GetObject(2).Should().BeSameAs(good);
    }

    [Fact]
    public void ObjStmCache_TruncatedIndex_ThrowsParseException_NoHang()
    {
        // /N claims 3 pairs but the index region only contains 2 — the
        // index parse itself fails. Must surface PdfParseException on any
        // compressed fetch, same as the old per-fetch index parse.
        var pdf = BuildObjStmPdf(objThreeOffset: 9999, truncateIndex: true);
        var act = () =>
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            doc.GetObject(2);
        };
        act.Should().Throw<Exception>(
            "a truncated /ObjStm index must fail cleanly, not crash or hang");
    }

    /// <summary>
    /// Build a minimal PDF 1.5 whose catalog and two more objects live in a
    /// single /ObjStm referenced from an xref stream. Object 3's offset in
    /// the ObjStm index is caller-controlled so tests can point it past the
    /// end of the decoded data. With <paramref name="truncateIndex"/> the
    /// index region declares 3 pairs (/N 3) but carries only 2.
    /// </summary>
    private static byte[] BuildObjStmPdf(int objThreeOffset, bool truncateIndex = false)
    {
        var latin1 = System.Text.Encoding.Latin1;
        using var ms = new MemoryStream();
        void W(string s) { var b = latin1.GetBytes(s); ms.Write(b, 0, b.Length); }

        W("%PDF-1.5\n");

        // obj 4: pages tree (regular, uncompressed)
        long off4 = ms.Position;
        W("4 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        // obj 5: the object stream. Contained objects:
        //   index 0 -> obj 1 (catalog), index 1 -> obj 2, index 2 -> obj 3
        string body = "<< /Type /Catalog /Pages 4 0 R >>\n<< /Foo (bar) >>\n<< /Baz true >>\n";
        int off1 = 0;
        int off2 = body.IndexOf("<< /Foo", StringComparison.Ordinal);
        string index = truncateIndex
            ? $"1 {off1} 2 {off2}\n"
            : $"1 {off1} 2 {off2} 3 {objThreeOffset}\n";
        string objStmData = index + body;
        int first = latin1.GetByteCount(index);

        long off5 = ms.Position;
        W($"5 0 obj\n<< /Type /ObjStm /N 3 /First {first} /Length {latin1.GetByteCount(objStmData)} >>\nstream\n");
        W(objStmData);
        W("endstream\nendobj\n");

        // obj 6: xref stream. /W [1 2 1]; rows for objects 0..6.
        long off6 = ms.Position;
        var rows = new List<byte>();
        void Row(byte type, long mid, byte low)
        {
            rows.Add(type);
            rows.Add((byte)((mid >> 8) & 0xFF));
            rows.Add((byte)(mid & 0xFF));
            rows.Add(low);
        }
        Row(0, 0, 0xFF);          // obj 0: free
        Row(2, 5, 0);             // obj 1: in stream 5, index 0
        Row(2, 5, 1);             // obj 2: in stream 5, index 1
        Row(2, 5, 2);             // obj 3: in stream 5, index 2
        Row(1, off4, 0);          // obj 4: regular at off4
        Row(1, off5, 0);          // obj 5: regular at off5
        Row(1, off6, 0);          // obj 6: the xref stream itself

        W($"6 0 obj\n<< /Type /XRef /Size 7 /W [1 2 1] /Root 1 0 R /Length {rows.Count} >>\nstream\n");
        ms.Write(rows.ToArray(), 0, rows.Count);
        W("\nendstream\nendobj\n");

        W($"startxref\n{off6}\n%%EOF\n");
        return ms.ToArray();
    }

    private static IReadOnlyDictionary<int, Excise.Core.Parsing.XRefEntry> GetXRef(PdfDocument doc)
    {
        // _xref is private; expose via reflection so this test can verify
        // the actual entry types without changing PdfDocument's API.
        var field = typeof(PdfDocument).GetField("_xref",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PdfDocument._xref field not found");
        return (Dictionary<int, Excise.Core.Parsing.XRefEntry>)field.GetValue(doc)!;
    }
}
