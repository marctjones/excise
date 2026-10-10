using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1920, the edges of the font edit that <see cref="RedactionFontSubsetCarrierTests"/>
/// does not reach: a character still drawn elsewhere, a form field value, a
/// font the edit refuses, sequential redactions across a save, an eviction of
/// every object before the save, and an encrypted document. The font carriers
/// are read through qpdf and the test-side readers of <see cref="FontCarrierProbe"/>;
/// what stays is checked with mutool.
/// </summary>
public sealed class RedactionFontGlyphScrubTests : IDisposable
{
    private const string Term = "Жуков";
    private readonly ITestOutputHelper _out;
    private readonly List<string> _temp = new();

    public RedactionFontGlyphScrubTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ACharacterDrawnOnAnotherPage_KeepsItsGlyphAndMapping()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var before = ExciseDoc(new[] { "Invoice approved by the finance office.", "Signed: " + Term }, new[] { "Жук" });
        var beforePath = Save(before);

        var afterPath = Save(Redact(before, Term));

        Measure(afterPath, "Жук").Should().OnlyContain(c => c.ToUnicodeCodes.Count > 0 && c.NonEmptyOutlineGids.Count > 0 && c.InWidths,
            "Ж, у and к are still drawn on page 2: they keep their outline, /ToUnicode entry and width");
        Measure(afterPath, "ов").Should().OnlyContain(c => c.ToUnicodeCodes.Count == 0 && c.NonEmptyOutlineGids.Count == 0,
            "о and в are drawn nowhere any more");
        MutoolTextExtractor.ExtractPage(afterPath, 2)!.Should().Contain("Жук");
        SamePixels(MutoolReferenceRenderer.RenderPage(beforePath, 2, 110), MutoolReferenceRenderer.RenderPage(afterPath, 2, 110))
            .Should().Be(0, "page 2 is untouched and must render exactly as before");
    }

    [Fact]
    public void AFieldValueUsingTheFont_KeepsItsCharacters()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var before = WithTextField(ExciseDoc(new[] { "Invoice approved by the finance office.", "Signed: " + Term }), "ов");

        var afterPath = Save(Redact(before, Term));

        Measure(afterPath, "ов").Should().OnlyContain(c => c.ToUnicodeCodes.Count > 0 && c.NonEmptyOutlineGids.Count > 0,
            "a viewer regenerating the field's appearance draws its value with the /DA font, so о and в stay");
        Measure(afterPath, "Жук").Should().OnlyContain(c => c.ToUnicodeCodes.Count == 0 && c.NonEmptyOutlineGids.Count == 0,
            "Ж, у and к are in no field value and drawn nowhere any more");
    }

    [Fact]
    public void AFontTheEditCannotHandle_IsReportedAndLeftUnedited()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var before = WithCidToGidMapStream(ExciseDoc(new[] { "Invoice approved by the finance office.", "Signed: " + Term }));
        byte[] after;
        using (var doc = PdfDocument.Open(before))
        {
            var report = doc.RedactText(Term, RedactionOptions.Default with { DrawBox = false });
            report.VerifiedRemovals.Should().BeGreaterThan(0);
            report.Carriers.Should().ContainSingle(c => c.Carrier.StartsWith("font ", StringComparison.Ordinal))
                .Which.RefusedReason.Should().Contain("/CIDToGIDMap is a stream");
            report.IsCleanSuccess.Should().BeFalse("a font that still holds the removed characters is not a clean result");
            report.ToString().Should().Contain("NOT scrubbed");
            after = doc.SaveToBytes();
        }

        var beforeFonts = FontStreams(before);
        var afterFonts = FontStreams(after);
        afterFonts.Should().BeEquivalentTo(beforeFonts, "a refused font is not half-edited: program and /ToUnicode stay byte for byte");
        Measure(Save(after), Term).Should().OnlyContain(c => c.ToUnicodeCodes.Count > 0,
            "anti-vacuity: the reported font really still maps the term");
    }

    [Fact]
    public void SequentialRedactionsAcrossASave_EachRemoveWhatTheyStopDrawing()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var before = ExciseDoc(new[] { "Invoice approved by the finance office.", "Signed: " + Term }, new[] { "Жук" });
        var traces = Measure(Save(before), Term).Where(c => c.ToUnicodeCodes.Count > 0).ToList();
        traces.Should().HaveCount(Term.Length);

        var first = Redact(before, Term);
        Measure(Save(first), "Жук").Should().OnlyContain(c => c.NonEmptyOutlineGids.Count > 0, "page 2 still draws them");

        var second = Redact(first, "Жук");
        var afterPath = Save(second);
        Measure(afterPath, Term).Should().OnlyContain(c => c.ToUnicodeCodes.Count == 0 && c.NonEmptyOutlineGids.Count == 0);
        FontCarrierProbe.Residue(afterPath, traces).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(afterPath, 1)!.Should().Contain("approved");
    }

    [Fact]
    public void TheEdit_SurvivesAnEvictionOfEveryObjectBeforeTheSave()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var before = ExciseDoc(new[] { "Invoice approved by the finance office.", "Signed: " + Term });
        var traces = Measure(Save(before), Term).Where(c => c.ToUnicodeCodes.Count > 0).ToList();

        byte[] saved;
        using (var doc = PdfDocument.Open(before))
        {
            Render(doc);
            doc.RedactText(Term, RedactionOptions.Default with { DrawBox = false }).VerifiedRemovals.Should().BeGreaterThan(0);
            Render(doc);
            var evicted = doc.ComputeReachableObjects().Order().Count(n => doc.TryEvictFromCache(doc.GetObject(n)));
            _out.WriteLine($"evicted {evicted} objects");
            evicted.Should().BeGreaterThan(0, "the stress must evict something");
            saved = doc.SaveToBytes();
        }

        var path = Save(saved);
        Measure(path, Term).Should().OnlyContain(c => c.ToUnicodeCodes.Count == 0 && c.NonEmptyOutlineGids.Count == 0,
            "an eviction must not bring the original font back");
        FontCarrierProbe.Residue(path, traces).Should().BeEmpty();
    }

    [Fact]
    public void AnEncryptedDocument_IsEditedThroughItsDecryptionAndSavedEncrypted()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        const string user = "font-1920-user", owner = "font-1920-owner";
        var plainPath = Save(ExciseDoc(new[] { "Invoice approved by the finance office.", "Signed: " + Term }));
        var traces = Measure(plainPath, Term).Where(c => c.ToUnicodeCodes.Count > 0).ToList();
        var encryptedPath = TempPath();
        QpdfReferenceTool.EncryptR4(plainPath, encryptedPath, user, owner).Should().BeTrue("qpdf must encrypt the fixture");

        byte[] saved;
        using (var doc = PdfDocument.Open(File.ReadAllBytes(encryptedPath), new PdfOpenOptions { UserPassword = user }))
        {
            var report = doc.RedactText(Term, RedactionOptions.Default with { DrawBox = false });
            report.VerifiedRemovals.Should().BeGreaterThan(0);
            report.Carriers.Where(c => c.Carrier.StartsWith("font ", StringComparison.Ordinal)).Should().BeEmpty();
            saved = doc.SaveToBytes(doc.GetReEncryptionOptions(user));
        }

        var savedPath = Save(saved);
        QpdfReferenceTool.IsEncrypted(savedPath).Should().BeTrue("the redacted copy is saved encrypted as it was opened");
        var decrypted = TempPath();
        QpdfReferenceTool.Decrypt(savedPath, decrypted, user).Should().BeTrue("qpdf must decrypt the redacted file");
        Measure(decrypted, Term).Should().OnlyContain(c => c.ToUnicodeCodes.Count == 0 && c.NonEmptyOutlineGids.Count == 0);
        FontCarrierProbe.Residue(decrypted, traces).Should().BeEmpty();
    }

    // ---- helpers ---------------------------------------------------------------

    private static IReadOnlyList<FontCarrierHit> Measure(string path, string chars) => FontCarrierProbe.Measure(path, chars);

    private static byte[] Redact(byte[] bytes, string term)
    {
        using var doc = PdfDocument.Open(bytes);
        var report = doc.RedactText(term, RedactionOptions.Default with { DrawBox = false });
        report.VerifiedRemovals.Should().BeGreaterThan(0, "the term must be matched and removed");
        report.Carriers.Where(c => c.Carrier.StartsWith("font ", StringComparison.Ordinal)).Should().BeEmpty();
        return doc.SaveToBytes();
    }

    /// <summary>Excise's writer, one subset Type0 / Identity-H DejaVu Sans font, one page per line list.</summary>
    private static byte[] ExciseDoc(params string[][] pages)
    {
        var font = TestRepoLayout.FindFile("Excise.Core.Tests", "Fixtures", "Fonts", "DejaVuSans.ttf");
        Assert.SkipWhen(font == null, TestRepoLayout.AbsenceReason(
            "DejaVu Sans fixture", "Excise.Core.Tests/Fixtures/Fonts/DejaVuSans.ttf"));
        var doc = PdfDocument.CreateNew();
        var pdfFont = PdfFont.FromTrueType(File.ReadAllBytes(font!), 18);
        foreach (var lines in pages)
        {
            var page = doc.Pages.AddBlank(612, 792);
            using var g = page.GetGraphics();
            var y = 100;
            foreach (var line in lines)
            {
                g.DrawString(line, pdfFont, PdfBrush.Black, 72, y);
                y += 40;
            }
        }
        return doc.SaveToBytes();
    }

    /// <summary>A text field (no widget) whose /DA names page 1's font and whose /V is <paramref name="value"/>.</summary>
    private static byte[] WithTextField(byte[] bytes, string value)
    {
        using var doc = PdfDocument.Open(bytes);
        var fonts = (PdfDictionary)doc.Resolve(doc.GetPage(1).Resources!.GetOptional("Font")!);
        var (name, fontRef) = fonts.Select(kv => (kv.Key.Value, kv.Value)).First();
        var utf16 = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(value)).ToArray();
        var field = new PdfDictionary();
        field.SetName("FT", "Tx");
        field.SetString("T", "witness");
        field.Set("V", new PdfString(utf16));
        field.SetString("DA", $"/{name} 12 Tf 0 g");
        var fieldRef = doc.AddIndirectObject(field);
        var dr = new PdfDictionary();
        var drFonts = new PdfDictionary();
        drFonts.Set(name, fontRef);
        dr.Set("Font", drFonts);
        var acroForm = new PdfDictionary();
        acroForm.Set("Fields", new PdfArray(fieldRef));
        acroForm.Set("DR", dr);
        doc.Catalog.Set("AcroForm", acroForm);
        return doc.SaveToBytes();
    }

    /// <summary>The descendant CIDFont's /CIDToGIDMap replaced by an equivalent identity stream.</summary>
    private static byte[] WithCidToGidMapStream(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        var fonts = (PdfDictionary)doc.Resolve(doc.GetPage(1).Resources!.GetOptional("Font")!);
        var type0 = (PdfDictionary)doc.Resolve(fonts.Values.First());
        var cidFont = (PdfDictionary)doc.Resolve(((PdfArray)doc.Resolve(type0.GetOptional("DescendantFonts")!))[0]);
        var map = new byte[2 * 65536];
        for (var cid = 0; cid < 65536; cid++)
        {
            map[2 * cid] = (byte)(cid >> 8);
            map[2 * cid + 1] = (byte)cid;
        }
        cidFont.Set("CIDToGIDMap", doc.AddIndirectObject(new PdfStream(map)));
        doc.ReplaceIndirectObject(cidFont.ObjectNumber!.Value, cidFont);
        return doc.SaveToBytes();
    }

    /// <summary>Every font program and /ToUnicode stream, decoded, keyed by BaseFont and role.</summary>
    private static Dictionary<string, string> FontStreams(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        var result = new Dictionary<string, string>();
        var fonts = (PdfDictionary)doc.Resolve(doc.GetPage(1).Resources!.GetOptional("Font")!);
        foreach (var value in fonts.Values)
        {
            var font = (PdfDictionary)doc.Resolve(value);
            var name = font.GetNameOrNull("BaseFont")!;
            if (doc.Resolve(font.GetOptional("ToUnicode") ?? PdfNull.Instance) is PdfStream tu)
                result[name + " ToUnicode"] = Convert.ToHexString(tu.DecodedData);
            var cidFont = (PdfDictionary)doc.Resolve(((PdfArray)doc.Resolve(font.GetOptional("DescendantFonts")!))[0]);
            var fd = (PdfDictionary)doc.Resolve(cidFont.GetOptional("FontDescriptor")!);
            result[name + " FontFile2"] = Convert.ToHexString(((PdfStream)doc.Resolve(fd.GetOptional("FontFile2")!)).DecodedData);
            result[name + " W"] = doc.Resolve(cidFont.GetOptional("W") ?? PdfNull.Instance).ToString() ?? "";
        }
        return result;
    }

    private static void Render(PdfDocument doc)
    {
        for (var p = 1; p <= doc.PageCount; p++)
            using (new SkiaRenderer().RenderPage(doc.GetPage(p), new RenderOptions { Dpi = 36 })) { }
    }

    private static int SamePixels(SkiaSharp.SKBitmap? x, SkiaSharp.SKBitmap? y)
    {
        using (x)
        using (y)
        {
            x.Should().NotBeNull();
            y.Should().NotBeNull();
            if (x!.Width != y!.Width || x.Height != y.Height) return int.MaxValue;
            var differing = 0;
            for (var row = 0; row < x.Height; row++)
                for (var col = 0; col < x.Width; col++)
                    if (x.GetPixel(col, row) != y.GetPixel(col, row)) differing++;
            return differing;
        }
    }

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"font-scrub-{Guid.NewGuid():N}.pdf");
        _temp.Add(path);
        return path;
    }

    private string Save(byte[] bytes)
    {
        var path = TempPath();
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        foreach (var p in _temp)
            try { File.Delete(p); } catch (IOException) { }
    }
}
