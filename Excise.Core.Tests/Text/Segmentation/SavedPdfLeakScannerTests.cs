using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1049 — proves the instrument, once, so the migration that converts ~20
/// files of leak assertions does not have to be argued file by file.
///
/// <para>The claim under test is exactly the one that failed in production:
/// <b>a term hidden inside a /FlateDecode stream is invisible to the raw
/// ASCII + UTF-16BE scan, and visible to this one.</b> CLAUDE.md prescribed
/// the raw form as the carrier-agnostic backstop — the thing that catches what
/// the extractor misses — and on #1040's leaking output it caught nothing:
/// 0 ASCII hits, 0 UTF-16BE hits, while mutool read the name straight out of
/// the file. excise compresses on save, so that blindness applies to every
/// assertion made over a saved document.</para>
/// </summary>
public class SavedPdfLeakScannerTests
{
    private const string Secret = "Farrar";

    /// <summary>
    /// A minimal PDF-shaped byte sequence with the term ONLY inside a
    /// compressed stream. Not a real document on purpose: the scanner is a byte
    /// tool and must not need a parseable file to work — on a corrupted or
    /// half-written output it is the last instrument still standing.
    /// </summary>
    private static byte[] BytesWithTermInsideACompressedStream()
    {
        var body = Encoding.Latin1.GetBytes(
            $"BT /F1 12 Tf 20 700 Td (Louise Anne {Secret}) Tj ET\n");

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(body, 0, body.Length);
        var deflated = compressed.ToArray();

        using var file = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); file.Write(b, 0, b.Length); }

        Ascii("%PDF-1.7\n4 0 obj\n<< /Length " + deflated.Length + " /Filter /FlateDecode >>\nstream\n");
        file.Write(deflated, 0, deflated.Length);
        Ascii("\nendstream\nendobj\n%%EOF\n");
        return file.ToArray();
    }

    [Fact]
    public void TheRawScanCLAUDEmdPrescribed_IsBlindToACompressedStream()
    {
        var saved = BytesWithTermInsideACompressedStream();

        // The exact snippet CLAUDE.md gave as assertion option 1.
        var raw = Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved);

        raw.Should().NotContain(Secret,
            "this is the POINT: the prescribed scan reports clean on a file that " +
            "demonstrably contains the term. If this ever starts finding it, the " +
            "fixture stopped compressing and every assertion below proves nothing");
    }

    [Fact]
    public void TheDecompressingScanner_FindsIt()
    {
        var hits = SavedPdfLeakScanner.FindTerm(BytesWithTermInsideACompressedStream(), Secret);

        hits.Should().NotBeEmpty(
            "the term is in the file; an instrument that cannot see it is not a leak scan");
        hits.Should().Contain(h => h.Contains("inflated stream"),
            "and it must SAY WHERE — the location is what makes a red triageable " +
            "rather than a mystery");
    }

    [Fact]
    public void ACleanFile_ScansClean()
    {
        // Negative control. Without it a scanner that returned a hit for every
        // input would pass the test above.
        var clean = Encoding.Latin1.GetBytes("%PDF-1.7\n% nothing to see\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(clean, Secret).Should().BeEmpty(
            "a scanner that always finds something is not an instrument");
    }

    /// <summary>
    /// The case that makes this migration matter on REAL excise output rather
    /// than a hand-built fixture — and it is narrower than it first looks.
    ///
    /// <para>excise's writer already REFUSES to pack a dictionary carrying
    /// <c>/Title</c>, <c>/Author</c>, <c>/Subject</c>, <c>/Keywords</c>,
    /// <c>/Creator</c>, <c>/Producer</c> or <c>/Contents</c> into a compressed
    /// <c>/ObjStm</c> (<c>ContainsDocumentCarrierText</c>). That is a
    /// deliberate anti-leak measure: those carriers stay greppable in the raw
    /// bytes. Measured, an annotation's <c>/Contents</c> is written
    /// uncompressed, so the raw scan CAN see it.</para>
    ///
    /// <para><b>But that list is not the same list the scrubber handles.</b>
    /// <c>/ActualText</c> and <c>/Alt</c> — the structure-tree carriers #636
    /// was filed for — are absent from it, so a structure element holding one
    /// is packed and Flate-compressed like any other dictionary, and the raw
    /// scan cannot see it.</para>
    /// </summary>
    [Fact]
    public void AStructureTreeActualText_IsInvisibleToTheRawScan_AndVisibleHere()
    {
        var pdf = Encoding.Latin1.GetBytes(
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n" +
            "5 0 obj\n<< /Type /StructTreeRoot /K [6 0 R] >>\nendobj\n" +
            $"6 0 obj\n<< /Type /StructElem /S /P /ActualText ({Secret}) >>\nendobj\n" +
            "trailer\n<< /Size 7 /Root 1 0 R >>\n%%EOF\n");

        using var doc = Excise.Core.Document.PdfDocument.Open(pdf);
        using var ms = new MemoryStream();
        doc.Save(ms);
        var saved = ms.ToArray();

        var rawScan = Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved);

        // If this flips, the writer's carrier list grew to cover /ActualText —
        // a GOOD change, and one that should delete this test rather than
        // weaken it. Do not relax the assertion to keep it passing.
        rawScan.Should().NotContain(Secret,
            "/ActualText is not in ContainsDocumentCarrierText, so its structure element " +
            "is packed into a Flate-compressed /ObjStm — invisible to the scan CLAUDE.md " +
            "prescribed, and #636 is exactly a leak through this carrier");

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().NotBeEmpty(
            "the term is in the file; an instrument that reports clean here is the one " +
            "that shipped #1040");
    }

    [Fact]
    public void ItFindsAUtf16BeTermInsideACompressedStream()
    {
        // /Info, /Contents and outline titles routinely carry UTF-16BE, and a
        // compressed object stream can hold any of them. Both dimensions —
        // compression AND encoding — have to be handled at once.
        var utf16 = Encoding.BigEndianUnicode.GetBytes(Secret);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(utf16, 0, utf16.Length);
        var deflated = compressed.ToArray();

        using var file = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); file.Write(b, 0, b.Length); }
        Ascii("%PDF-1.7\n5 0 obj\n<< /Length " + deflated.Length + " /Filter /FlateDecode >>\nstream\n");
        file.Write(deflated, 0, deflated.Length);
        Ascii("\nendstream\nendobj\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(file.ToArray(), Secret)
            .Should().Contain(h => h.Contains("UTF-16BE"),
                "a term that is both compressed and UTF-16BE encoded is still a leak");
    }

    // ── #1295: the trailer /ID is not a text carrier ─────────────────────────
    //
    // /ID (§14.4) is a random 16-byte file identifier written as uppercase
    // hex. Scanning it makes every SHORT-needle absence assertion
    // intermittently red — observed at least four times on
    // FullwidthFormsRedactionTests with a provably clean redacted page.
    // The fixtures below force the collision instead of waiting for it, so
    // the exclusion is pinned deterministically in BOTH directions.

    /// <summary>
    /// A file whose ONLY occurrence of the needles is the trailer /ID.
    /// <c>0x12 0x3A 0xBC</c> serialises as the hex digits <c>123ABC</c>,
    /// reproducing both historically observed collisions ("123" and "ABC") in
    /// one deterministic fixture.
    /// </summary>
    private static byte[] BytesWhoseOnlyHitIsTheTrailerId()
        => Encoding.Latin1.GetBytes(
            "%PDF-1.7\n"
            + "1 0 obj\n<< /Type /Catalog >>\nendobj\n"
            + "trailer\n<< /Size 2 /Root 1 0 R /ID [<123ABC0000000000000000000000DEAD>"
            + "<123ABC0000000000000000000000DEAD>] >>\n%%EOF\n");

    [Theory]
    [InlineData("123")]
    [InlineData("ABC")]
    public void AShortTermOccurringOnlyInTheTrailerId_IsNotReportedAsALeak(string needle)
    {
        var saved = BytesWhoseOnlyHitIsTheTrailerId();

        // Anti-vacuity: the collision is REAL in the bytes. Without this the
        // test would pass on a fixture that never contained the needle at all.
        Encoding.Latin1.GetString(saved).Should().Contain(needle,
            "sanity: the fixture must actually contain the needle in its /ID, " +
            "or the exclusion below is proving nothing");

        SavedPdfLeakScanner.FindTerm(saved, needle).Should().BeEmpty(
            "the /ID is a random, content-INDEPENDENT file identifier — no page " +
            "text can leak into it, so a hit there is a false positive that makes " +
            "short-needle redaction assertions flaky (#1295/#771/#800)");

        SavedPdfLeakScanner.AllCarriersText(saved).Should().NotContain(needle,
            "both scanner entry points must apply the same exclusion — #1049's " +
            "migration fixed one view and left the other, which is how the flake " +
            "came back");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("ABC")]
    public void TheSameTermInARealCarrier_IsStillFound(string needle)
    {
        // The counter-test, and the one that matters: narrowing the scanner
        // must not have blinded it. Same needles, same file shape, but now the
        // term is also genuine page text inside a /FlateDecode stream — the
        // carrier that defeated the raw scan in #1040.
        var body = Encoding.Latin1.GetBytes($"BT /F1 12 Tf 20 700 Td (Account {needle}) Tj ET\n");
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(body, 0, body.Length);
        var deflated = compressed.ToArray();

        using var file = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); file.Write(b, 0, b.Length); }
        Ascii("%PDF-1.7\n4 0 obj\n<< /Length " + deflated.Length + " /Filter /FlateDecode >>\nstream\n");
        file.Write(deflated, 0, deflated.Length);
        Ascii("\nendstream\nendobj\n"
            + "trailer\n<< /Size 2 /ID [<123ABC0000000000000000000000DEAD>"
            + "<123ABC0000000000000000000000DEAD>] >>\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(file.ToArray(), needle)
            .Should().Contain(h => h.Contains("inflated stream"),
                "excluding the /ID must narrow the scan to that array ONLY — a term " +
                "in a compressed content stream is a REAL leak and must still be " +
                "reported, and located by carrier");
    }

    [Fact]
    public void AHexStringOutsideTheIdArray_IsStillScanned()
    {
        // Scope guard. The exclusion keys on the /ID ARRAY, not on hex-string
        // syntax: a <...> in a content stream is how glyph codes are written,
        // so blanking hex strings generally would blind the scanner to exactly
        // the leaks it exists to catch.
        var saved = Encoding.Latin1.GetBytes(
            "%PDF-1.7\n4 0 obj\n<< /Length 30 >>\nstream\n"
            + "BT <123ABC> Tj ET\nendstream\nendobj\n"
            + "trailer\n<< /ID [<DEADBEEF0000000000000000DEADBEEF>] >>\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(saved, "123ABC").Should().NotBeEmpty(
            "a hex string in a CONTENT STREAM is a real text carrier; only the " +
            "/ID array's own strings are content-independent");
    }

    // ── #1846: text strings in the form excise writes them ───────────────────
    //
    // excise never writes a UTF-16BE text string as raw bytes: a <FEFF…> hex
    // string stays hex, and a literal one is written with octal escapes
    // (\376\377\006\063…). A byte search for the term's UTF-16BE encoding
    // finds neither, so FindTerm(...).Should().BeEmpty() passed on any file
    // whose leak sat in an outline title, a comment, a form value, /Info or
    // /ActualText with non-Latin-1 text.

    private const string TitleTerm = "سلام";
    private const string CommentTerm = "مرحبا";
    private const string LatinInUtf16 = "KESTREL";
    private const string ActualTextTerm = "شكرا";

    private static string Utf16Hex(string text) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    private static string Utf16Octal(string text) =>
        @"(\376\377" + string.Concat(Encoding.BigEndianUnicode.GetBytes(text)
            .Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'))) + ")";

    /// <summary>
    /// An outline /Title as a hex UTF-16BE string, a comment /Contents as a
    /// literal one with octal escapes (Latin and Arabic in one string), and a
    /// structure element /ActualText, which excise packs into a compressed
    /// object stream. Opened and saved by excise; nothing is redacted.
    /// </summary>
    private static byte[] SavedTextStringFixture()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R /StructTreeRoot 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [6 0 R] >>",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            $"<< /Title {Utf16Hex(TitleTerm + " chapter")} /Parent 4 0 R >>",
            $"<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents {Utf16Octal($"{LatinInUtf16} and {CommentTerm}")} >>",
            "<< /Type /StructTreeRoot /K [8 0 R] >>",
            $"<< /Type /StructElem /S /P /P 7 0 R /ActualText {Utf16Hex(ActualTextTerm)} >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        using var doc = Excise.Core.Document.PdfDocument.Open(Encoding.Latin1.GetBytes(sb.ToString()));
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void AnOutlineTitle_WrittenAsUtf16BeHex_IsFound()
    {
        var saved = SavedTextStringFixture();
        Encoding.Latin1.GetString(saved).Should().Contain(Utf16Hex(TitleTerm + " chapter"),
            "anti-vacuity: the saved file carries the title as a hex UTF-16BE string");

        SavedPdfLeakScanner.FindTerm(saved, TitleTerm).Should().NotBeEmpty(
            "the outline title is in the file; a scanner that reports it clean " +
            "lets a bookmark leak pass every absence assertion");
    }

    [Fact]
    public void ACommentWrittenWithOctalEscapes_IsFound_IncludingItsLatinText()
    {
        var saved = SavedTextStringFixture();
        var raw = Encoding.Latin1.GetString(saved);
        raw.Should().Contain(@"\000K\000E\000S\000T\000R\000E\000L",
            "anti-vacuity: the saved file carries the comment as an octal-escaped UTF-16BE literal");
        raw.Should().NotContain(LatinInUtf16,
            "which is why a byte search for the Latin term never matched it");

        SavedPdfLeakScanner.FindTerm(saved, CommentTerm).Should().NotBeEmpty(
            "the comment's Arabic text is in the file");
        SavedPdfLeakScanner.FindTerm(saved, LatinInUtf16).Should().NotBeEmpty(
            "a Latin term that shares a UTF-16BE string with non-Latin text is in the file too");
    }

    [Fact]
    public void AnActualTextInsideACompressedObjectStream_IsFound()
    {
        var saved = SavedTextStringFixture();
        SavedPdfLeakScanner.StreamBodies(saved).Should().Contain(b => b.Contains(Utf16Hex(ActualTextTerm)),
            "anti-vacuity: excise packs the structure element into a compressed object stream");

        SavedPdfLeakScanner.FindTerm(saved, ActualTextTerm).Should().Contain(h => h.Contains("inflated stream"),
            "a UTF-16BE string that is both compressed and hex-encoded is still in the file");
    }

    [Fact]
    public void AllCarriersText_DecodesTheSameTextStrings()
    {
        var text = SavedPdfLeakScanner.AllCarriersText(SavedTextStringFixture());

        text.Should().Contain(TitleTerm).And.Contain(CommentTerm).And.Contain(LatinInUtf16)
            .And.Contain(ActualTextTerm, "AllCarriersText backs NotContain assertions and must see what FindTerm sees");
    }

    /// <summary>A page whose only content is <paramref name="content"/>, uncompressed.</summary>
    private static byte[] BytesWithContent(string content) => Encoding.Latin1.GetBytes(
        "%PDF-1.7\n4 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content +
        "\nendstream\nendobj\n%%EOF\n");

    private static byte[] Deflate(byte[] body)
    {
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(body, 0, body.Length);
        return compressed.ToArray();
    }

    private static byte[] BytesWithCompressedContent(string content)
    {
        var deflated = Deflate(Encoding.Latin1.GetBytes(content));

        using var file = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); file.Write(b, 0, b.Length); }
        Ascii("%PDF-1.7\n4 0 obj\n<< /Length " + deflated.Length + " /Filter /FlateDecode >>\nstream\n");
        file.Write(deflated, 0, deflated.Length);
        Ascii("\nendstream\nendobj\n%%EOF\n");
        return file.ToArray();
    }

    [Fact]
    public void AHexGlyphStringInACompressedContentStream_IsFound()
    {
        SavedPdfLeakScanner.FindTerm(BytesWithCompressedContent("BT /F1 12 Tf <534543524554> Tj ET"), "SECRET")
            .Should().Contain(h => h.Contains("inflated stream"),
                "a hex string is how a content stream often writes its glyph codes");
    }

    [Theory]
    [InlineData(@"BT (\123ECRET) Tj ET")]
    [InlineData("BT (SEC\\\nRET) Tj ET")]
    [InlineData("BT (SEC\\\r\nRET) Tj ET")]
    [InlineData(@"BT (S\ECRET) Tj ET")]
    [InlineData("BT [(SECR\\105T)] TJ ET")]
    public void AnEscapedLiteral_IsFoundAfterItsEscapesAreResolved(string content)
    {
        // §7.3.4.2: octal codes, backslash-EOL line continuation, and a
        // backslash before any other character is ignored.
        SavedPdfLeakScanner.FindTerm(BytesWithContent(content), "SECRET").Should().NotBeEmpty(
            "the string's value is SECRET whatever escapes spell it");
    }

    [Theory]
    [InlineData("<FEFF0053004500430052004500540020>", "SECRET", "UTF-16BE")]
    [InlineData(@"(\377\376S\000E\000C\000R\000E\000T\000)", "SECRET", "UTF-16LE")]
    [InlineData(@"(\357\273\277\320\241\320\225\320\232\320\240\320\225\320\242)", "СЕКРЕТ", "UTF-8")]
    public void EveryMarkedTextStringEncoding_IsDecoded(string text, string term, string encoding)
    {
        var saved = Encoding.Latin1.GetBytes($"%PDF-1.7\n1 0 obj\n<< /Title {text} >>\nendobj\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(saved, term).Should().Contain(h => h.Contains(encoding),
            $"a text string whose byte order mark says {encoding} is read as {encoding}");
    }

    [Fact]
    public void AnUnmarkedTextString_IsReadAsPdfDocEncoding()
    {
        // 0x84 is an em dash in PDFDocEncoding (Annex D, Table D.2) and a C1
        // control in Latin-1.
        var saved = Encoding.Latin1.GetBytes("%PDF-1.7\n1 0 obj\n<< /Title (Q3\\204report) >>\nendobj\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(saved, "Q3—report").Should().NotBeEmpty(
            "an unmarked text string is PDFDocEncoding, not Latin-1");
    }

    [Fact]
    public void AParenthesisInsideAStreamBody_DoesNotSwallowTheStringsAfterIt()
    {
        // Binary stream data is not PDF syntax: a stray '(' in it would open a
        // literal string that runs until some later ')' and hide every real
        // string in between.
        var saved = Encoding.Latin1.GetBytes(
            "%PDF-1.7\n% a comment is not a string either: (\n" +
            "4 0 obj\n<< /Length 9 >>\nstream\nÿ(Øÿà\u0000\u0010ÿ\nendstream\nendobj\n" +
            $"5 0 obj\n<< /Title {Utf16Hex(TitleTerm)} >>\nendobj\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(saved, TitleTerm).Should().NotBeEmpty(
            "the title after the stream is a real text string");
    }

    [Fact]
    public void ATextStringHoldingSomethingElse_ScansClean()
    {
        // Negative control for the decoding: a decoder that reported every
        // UTF-16BE string would pass every positive test above.
        var saved = Encoding.Latin1.GetBytes(
            $"%PDF-1.7\n1 0 obj\n<< /Title {Utf16Hex(CommentTerm)} /Subject {Utf16Octal("KESTRAL")} >>\nendobj\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(saved, TitleTerm).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, LatinInUtf16).Should().BeEmpty();
    }

    // ── #1855: the word "stream" is not always the stream keyword ────────────
    //
    // Stream bodies were found by a byte search for "stream". The word in a
    // string, comment or name before the real keyword opened a body there that
    // ran to the real stream's endstream, so the real body was never inflated
    // or tokenized and a term in it was invisible.

    private const string Hidden = "SECRETNAME";
    private const string ByteSearchFallback = "byte search";

    /// <summary>
    /// <paramref name="before"/>, then a content stream showing <see cref="Hidden"/>:
    /// a literal string, Flate-compressed, or a hex string left uncompressed,
    /// which only the decoded-string search of that stream's body can read.
    /// </summary>
    private static byte[] StreamAfter(string before, bool compressed, string keyword = "stream\n", int? length = null)
    {
        var body = Encoding.Latin1.GetBytes(compressed
            ? $"BT /F1 12 Tf 20 700 Td (Louise {Hidden}) Tj ET\n"
            : $"BT /F1 12 Tf 20 700 Td <{Convert.ToHexString(Encoding.Latin1.GetBytes("Louise " + Hidden))}> Tj ET\n");
        if (compressed) body = Deflate(body);

        using var file = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); file.Write(b, 0, b.Length); }
        Ascii($"%PDF-1.7\n{before}\n2 0 obj\n<< /Length {length ?? body.Length}{(compressed ? " /Filter /FlateDecode" : "")} >>\n{keyword}");
        file.Write(body, 0, body.Length);
        Ascii("\nendstream\nendobj\n%%EOF\n");
        return file.ToArray();
    }

    public static TheoryData<string, bool> TheWordStreamBeforeTheKeyword()
    {
        var data = new TheoryData<string, bool>();
        foreach (var before in new[]
                 {
                     "1 0 obj\n<< /Title (Quarterly report) >>\nendobj",
                     "1 0 obj\n<< /Title (Live stream notes) /Parent 3 0 R >>\nendobj",
                     "1 0 obj\n<< /Producer (upstream writer) >>\nendobj",
                     "1 0 obj\n<< /Type /Annot /Subtype /Text /Rect [0 0 9 9] /Contents (see the stream) >>\nendobj",
                     "1 0 obj\n<< /Title (a (nested stream) b) >>\nendobj",
                     @"1 0 obj << /Title (a \( stream \) b) >> endobj",
                     @"1 0 obj << /Title (a \) stream) >> endobj",
                     $"1 0 obj\n<< /Title {Utf16Hex("Live stream")} >>\nendobj",
                     "% Live stream notes\n1 0 obj\n<< >>\nendobj",
                     "1 0 obj\n<< /stream true >>\nendobj",
                 })
        {
            data.Add(before, true);
            data.Add(before, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(TheWordStreamBeforeTheKeyword))]
    public void TheWordStreamBeforeTheKeyword_DoesNotHideTheStream(string before, bool compressed)
    {
        var saved = StreamAfter(before, compressed);

        var hits = SavedPdfLeakScanner.FindTerm(saved, Hidden);
        hits.Should().Contain(h => h.StartsWith(compressed ? "inflated stream #0:" : "stream #0:"),
            "the content stream shows the term; a string, comment or name holding the word " +
            "\"stream\" must not move where its body starts");
        hits.Should().NotContain(h => h.Contains(ByteSearchFallback),
            "a well-formed file is read by the syntax walk, not by the fallback");

        SavedPdfLeakScanner.StreamBodies(saved).Should().ContainSingle(
            "one stream in the file is one body: a second one would be counted twice by the benchmark")
            .Which.Should().StartWith("BT ", "the body starts after the keyword, not after the word in a string");
        SavedPdfLeakScanner.AllCarriersText(saved).Should().Contain("Louise " + Hidden);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(99999)]
    public void AStreamWhoseLengthLies_IsReadToItsEndstream(int length)
    {
        // /Length is the file describing itself; the scanner checks the file,
        // so it reads to endstream whatever /Length claims.
        var saved = StreamAfter("1 0 obj\n<< /Title (Live stream notes) >>\nendobj", compressed: true, length: length);

        SavedPdfLeakScanner.FindTerm(saved, Hidden).Should().Contain(h => h.StartsWith("inflated stream #0:"));
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("stream\r")]
    public void ANonConformingStreamKeyword_IsStillTheKeyword(string keyword)
    {
        // §7.3.8.1 wants an end-of-line after "stream". Without one the keyword
        // runs straight into the zlib header ('x'), or ends in a bare CR.
        var saved = StreamAfter("1 0 obj\n<< /Title (Live stream notes) >>\nendobj", compressed: true, keyword: keyword);

        SavedPdfLeakScanner.FindTerm(saved, Hidden).Should().Contain(h => h.StartsWith("inflated stream #0:"));
    }

    [Fact]
    public void AStreamWhoseBodyHoldsTheWord_IsReadWhole()
    {
        var saved = BytesWithContent($"BT (Live stream) Tj <{Convert.ToHexString(Encoding.Latin1.GetBytes(Hidden))}> Tj ET");

        SavedPdfLeakScanner.StreamBodies(saved).Should().ContainSingle()
            .Which.Should().StartWith("BT (Live stream) Tj", "the word inside a body does not start another one");
        SavedPdfLeakScanner.FindTerm(saved, Hidden).Should().Contain(h => h.StartsWith("stream #0:"));
    }

    [Fact]
    public void AnUnbalancedParenthesis_FallsBackToTheByteSearch_AndSaysSo()
    {
        // The string never closes, so it swallows the stream keyword: the walk
        // cannot read the rest of the file. The byte search can.
        var saved = StreamAfter("1 0 obj\n<< /Title (unbalanced live stream notes >>\nendobj", compressed: true);

        SavedPdfLeakScanner.FindTerm(saved, Hidden).Should().Contain(
            h => h.StartsWith("inflated stream #0") && h.Contains(ByteSearchFallback),
            "a stream the walk lost is found by the byte search, and the hit says the scanner fell back");
        SavedPdfLeakScanner.StreamBodies(saved).Should().ContainSingle().Which.Should().StartWith("BT ");
    }

    [Fact]
    public void AnEndstreamTheWalkNeverOpened_FallsBackToTheByteSearch_AndSaysSo()
    {
        // The unbalanced string swallows the keyword and closes at the stray
        // ')' in the body, so the walk reaches endstream without a stream.
        var hex = Convert.ToHexString(Encoding.Latin1.GetBytes(Hidden));
        var saved = Encoding.Latin1.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Title (unbalanced >>\nendobj\n" +
            $"2 0 obj\n<< /Length 30 >>\nstream\n) BT <{hex}> Tj ET\nendstream\nendobj\n%%EOF\n");

        SavedPdfLeakScanner.FindTerm(saved, Hidden).Should().Contain(
            h => h.StartsWith("stream #0") && h.Contains(ByteSearchFallback));
        SavedPdfLeakScanner.StreamBodies(saved).Should().ContainSingle().Which.Should().StartWith(") BT ");
    }
}
