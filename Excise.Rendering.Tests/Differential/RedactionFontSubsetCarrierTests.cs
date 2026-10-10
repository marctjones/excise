using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1920: after <c>RedactText</c> removes a term, the embedded font SUBSET it was
/// drawn with still carries the term's characters: the glyph outlines stay in the
/// font program and the <c>/ToUnicode</c> CMap still maps their codes to the
/// removed Unicode values. The content stream is clean, so every extractor
/// passes; the character SET of the removed term is recoverable from the font.
///
/// Each fixture draws a term whose characters appear nowhere else in the
/// document, from three producers so no shared assumption hides the case:
/// excise's own writer (Type0 / CIDFontType2 / FontFile2, Identity-H), WeasyPrint
/// (Type0 / CIDFontType0 / FontFile3 OpenType-CFF, the tracked CJK sample), and
/// macOS Quartz via <c>cupsfilter</c> (simple /TrueType / FontFile2 with a
/// per-script subset font; macOS only).
///
/// The font carriers are read through qpdf (structure and stream decoding) and a
/// test-side sfnt / CFF / CMap reader, never through excise's parser or font code.
/// </summary>
public sealed class RedactionFontSubsetCarrierTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _temp = new();

    public RedactionFontSubsetCarrierTests(ITestOutputHelper output) => _out = output;

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var fixture in new[] { "excise-type0-cyrillic", "excise-type0-latin", "weasyprint-cidcff-hangul", "quartz-truetype-cyrillic" })
        foreach (var profile in new[] { "standard", "maximum" })
            data.Add(fixture, profile);
        return data;
    }

    /// <summary>
    /// What holds today: the term is removed from the content, by two independent
    /// extractors. The font-carrier state is measured and written to the test
    /// output; the property #1920 wants is the skipped sibling below.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Term_IsGoneForIndependentExtractors_FontCarriersMeasured(string fixture, string profile)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not installed");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var (before, term, keep) = Fixture(fixture);
        var beforePath = SaveTemp(before);

        var beforeText = MutoolTextExtractor.ExtractPage(beforePath, 1);
        beforeText.Should().NotBeNull();
        foreach (var ch in term)
            Strip(beforeText!).Count(c => c == ch).Should().Be(1,
                $"anti-vacuity: '{ch}' must be drawn once, in the term, so the font is its only other trace");
        var beforeCarriers = FontCarrierProbe.Measure(beforePath, term);
        beforeCarriers.Where(c => c.ToUnicodeCodes.Count > 0).Should().HaveCount(term.Length,
            "anti-vacuity: the unredacted font's /ToUnicode maps every character of the term");

        var afterPath = SaveTemp(Redact(before, term, profile));
        foreach (var extracted in new[] { MutoolTextExtractor.ExtractPage(afterPath, 1), PdftotextTextExtractor.ExtractPage(afterPath, 1) })
        {
            extracted.Should().NotBeNull("both extractors must read the redacted file");
            Strip(extracted!).Should().NotContain(term, "the term must be gone from the content");
            Strip(extracted!).Should().Contain(keep, "only the term may go");
        }

        foreach (var c in FontCarrierProbe.Measure(afterPath, term))
            _out.WriteLine($"{fixture}/{profile}: {c}");
    }

    /// <summary>
    /// The property #1920 asks for: once no text draws a character any more, no
    /// font in the saved file maps a code to it and no glyph outline for it
    /// remains. Measured 2026-10-10 before the fix: every removed character
    /// kept its /ToUnicode entry and its non-empty outline, on every fixture and
    /// both profiles.
    /// </summary>
    /// <remarks>
    /// Read twice. Through the saved /ToUnicode, as a reader would; and, since
    /// pruning /ToUnicode alone would satisfy that read while the glyphs stay,
    /// through the codes and glyph ids the term used BEFORE the redaction: each
    /// must be gone from the outlines, from the program's own cmap, from its
    /// <c>post</c> names, from the Apple <c>Zapf</c> table (it records Unicode
    /// values) and from /W or /Widths.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public void RemovedCharacters_LeaveNoTraceInAnyFontCarrier(string fixture, string profile)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var (before, term, _) = Fixture(fixture);
        var traces = FontCarrierProbe.Measure(SaveTemp(before), term).Where(c => c.ToUnicodeCodes.Count > 0).ToList();
        traces.Select(c => c.Character).Distinct().Should().HaveCount(term.Distinct().Count(),
            "anti-vacuity: before the redaction a font maps every character of the term");
        traces.Should().OnlyContain(c => c.NonEmptyOutlineGids.Count > 0 && c.InWidths,
            "anti-vacuity: before the redaction each character has an outline and a width");

        var afterPath = SaveTemp(Redact(before, term, profile));

        var survivors = FontCarrierProbe.Measure(afterPath, term)
            .Where(c => c.ToUnicodeCodes.Count > 0 || c.NonEmptyOutlineGids.Count > 0)
            .ToList();
        survivors.Should().BeEmpty(
            "a character no text draws any more must not survive in a /ToUnicode CMap or as a glyph outline");

        var residue = FontCarrierProbe.Residue(afterPath, traces);
        foreach (var r in residue) _out.WriteLine(r);
        residue.Should().BeEmpty(
            "the codes and glyphs the term used must leave the outlines, the program's cmap, post and Zapf tables, and the widths");
    }

    /// <summary>
    /// The planted failure for the test above: the same redaction without the
    /// font edit leaves residue the probe reports, both through /ToUnicode and
    /// through the pre-redaction glyph ids. A probe that found nothing here
    /// would pass any fix.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void WithoutTheFontEdit_TheProbeFindsTheRemovedCharacters(string fixture, string profile)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var (before, term, _) = Fixture(fixture);
        var traces = FontCarrierProbe.Measure(SaveTemp(before), term).Where(c => c.ToUnicodeCodes.Count > 0).ToList();
        var afterPath = SaveTemp(Redact(before, term, profile, scrubFonts: false));

        FontCarrierProbe.Measure(afterPath, term).Where(c => c.ToUnicodeCodes.Count > 0 && c.NonEmptyOutlineGids.Count > 0)
            .Should().NotBeEmpty("without the edit the font keeps the term's characters");
        FontCarrierProbe.Residue(afterPath, traces).Should().NotBeEmpty("without the edit the glyphs stay");
    }

    public static TheoryData<string, string> IdentityCases()
    {
        var data = Cases();
        foreach (var profile in new[] { "standard", "maximum" })
            data.Add("excise-shared-latin", profile);
        return data;
    }

    /// <summary>
    /// The owner's constraint: the font edit changes nothing that stays. The
    /// same redaction with and without it must render to identical pixels in two
    /// independent renderers (MuPDF, Poppler) and extract to identical text in
    /// two independent extractors and in excise, page by page.
    /// "excise-shared-latin" removes a word most of whose letters are drawn
    /// elsewhere in the same font, so kept glyphs sit next to emptied ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(IdentityCases))]
    public void TheFontEdit_ChangesNoRenderedPixelAndNoExtractedCharacter(string fixture, string profile)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftoppmReferenceRenderer.IsAvailable, "pdftoppm not installed");
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not installed");
        var (before, term, _) = Fixture(fixture);
        var scrubbed = Redact(before, term, profile);
        var plain = Redact(before, term, profile, scrubFonts: false);
        scrubbed.Should().NotEqual(plain, "anti-vacuity: the font edit must have changed the file");

        var scrubbedPath = SaveTemp(scrubbed);
        var plainPath = SaveTemp(plain);
        using var a = PdfDocument.Open(scrubbed);
        using var b = PdfDocument.Open(plain);
        a.PageCount.Should().Be(b.PageCount);
        for (var page = 1; page <= a.PageCount; page++)
        {
            a.GetPage(page).Text.Should().Be(b.GetPage(page).Text, $"excise reads page {page} the same");
            MutoolTextExtractor.ExtractPage(scrubbedPath, page).Should().Be(MutoolTextExtractor.ExtractPage(plainPath, page),
                $"mutool reads page {page} the same");
            PdftotextTextExtractor.ExtractPage(scrubbedPath, page).Should().Be(PdftotextTextExtractor.ExtractPage(plainPath, page),
                $"pdftotext reads page {page} the same");
            AssertSamePixels(MutoolReferenceRenderer.RenderPage(scrubbedPath, page, 110), MutoolReferenceRenderer.RenderPage(plainPath, page, 110),
                $"mutool, page {page}");
            AssertSamePixels(PdftoppmReferenceRenderer.RenderPage(scrubbedPath, page, 110), PdftoppmReferenceRenderer.RenderPage(plainPath, page, 110),
                $"pdftoppm, page {page}");
        }
    }

    private static void AssertSamePixels(SkiaSharp.SKBitmap? x, SkiaSharp.SKBitmap? y, string label)
    {
        using (x)
        using (y)
        {
            x.Should().NotBeNull(label);
            y.Should().NotBeNull(label);
            x!.Width.Should().Be(y!.Width, label);
            x.Height.Should().Be(y.Height, label);
            var differing = 0;
            for (var row = 0; row < x.Height; row++)
                for (var col = 0; col < x.Width; col++)
                    if (x.GetPixel(col, row) != y.GetPixel(col, row)) differing++;
            differing.Should().Be(0, $"{label}: the font edit must not change a rendered pixel");
        }
    }

    // ---- fixtures ------------------------------------------------------------

    private const string Prose = "Invoice approved by the finance office.";

    private (byte[] Bytes, string Term, string Keep) Fixture(string name)
    {
        switch (name)
        {
            case "excise-type0-cyrillic": return (ExciseWriterFixture("Жуков"), "Жуков", "approved");
            case "excise-type0-latin": return (ExciseWriterFixture("QJXZ"), "QJXZ", "approved");
            case "excise-shared-latin": return (ExciseWriterFixture("Vance"), "Vance", "approved");
            case "weasyprint-cidcff-hangul":
            {
                var path = TestRepoLayout.FindFile("test-pdfs", "sample-pdfs", "multilingual-noto-cjk.pdf");
                Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason(
                    "WeasyPrint CJK sample", "test-pdfs/sample-pdfs/multilingual-noto-cjk.pdf"));
                return (File.ReadAllBytes(path!), "안녕하세요", "こんにちは");
            }
            case "quartz-truetype-cyrillic": return (QuartzFixture("Жуков"), "Жуков", "approved");
            default: throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    /// <summary>Excise's writer: one subset Type0 / Identity-H DejaVu Sans font for both lines.</summary>
    private static byte[] ExciseWriterFixture(string term)
    {
        var font = TestRepoLayout.FindFile("Excise.Core.Tests", "Fixtures", "Fonts", "DejaVuSans.ttf");
        Assert.SkipWhen(font == null, TestRepoLayout.AbsenceReason(
            "DejaVu Sans fixture", "Excise.Core.Tests/Fixtures/Fonts/DejaVuSans.ttf"));
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        var pdfFont = PdfFont.FromTrueType(File.ReadAllBytes(font!), 18);
        using (var g = page.GetGraphics())
        {
            g.DrawString(Prose, pdfFont, PdfBrush.Black, 72, 100);
            g.DrawString("Signed: " + term, pdfFont, PdfBrush.Black, 72, 140);
        }
        return doc.SaveToBytes();
    }

    /// <summary>
    /// macOS Quartz (an independent producer) prints plain text with Monaco and puts
    /// the Cyrillic run in its OWN simple TrueType subset with sequential codes and a
    /// /ToUnicode, so that font holds nothing but the term's characters.
    /// </summary>
    private byte[] QuartzFixture(string term)
    {
        const string Cups = "/usr/sbin/cupsfilter";
        Assert.SkipUnless(OperatingSystem.IsMacOS() && File.Exists(Cups), "needs macOS cupsfilter (Quartz PDFContext)");
        var txt = Path.Combine(Path.GetTempPath(), $"font-subset-{Guid.NewGuid():N}.txt");
        _temp.Add(txt);
        File.WriteAllText(txt, $"{Prose}\nSigned: {term}\nAll other text is plain Latin here.\n", new UTF8Encoding(false));
        var psi = new ProcessStartInfo(Cups) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("text/plain"); psi.ArgumentList.Add(txt);
        using var p = Process.Start(psi)!;
        var ms = new MemoryStream();
        var pump = p.StandardOutput.BaseStream.CopyToAsync(ms);
        _ = p.StandardError.ReadToEndAsync();
        p.WaitForExit(60_000).Should().BeTrue("cupsfilter must finish");
        pump.Wait(60_000);
        Assert.SkipWhen(p.ExitCode != 0 || ms.Length == 0, $"cupsfilter failed (exit {p.ExitCode})");
        return ms.ToArray();
    }

    private static byte[] Redact(byte[] bytes, string term, string profile, bool scrubFonts = true)
    {
        using var doc = PdfDocument.Open(bytes);
        var options = RedactionOptions.ForProfile(profile == "maximum" ? RedactionProfile.Maximum : RedactionProfile.Standard)
            with { DrawBox = false, ScrubFontGlyphs = scrubFonts };
        var report = doc.RedactText(term, options);
        report.VerifiedRemovals.Should().BeGreaterThan(0, "the term must be matched and removed");
        report.Carriers.Where(c => c.Carrier.StartsWith("font ", StringComparison.Ordinal)).Should().BeEmpty(
            "every font of these fixtures is one the redaction edits, so none may be reported as left unscrubbed");
        return doc.SaveToBytes();
    }

    // Extractors space out or wrap glyphs differently; compare letters only.
    private static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private string SaveTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"font-subset-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var p in _temp)
            try { File.Delete(p); } catch (IOException) { }
    }
}

/// <summary>One removed character, as one font in the saved file still carries it.</summary>
internal sealed record FontCarrierHit(
    string Font, string Subtype, char Character,
    IReadOnlyList<int> ToUnicodeCodes, IReadOnlyList<int> NonEmptyOutlineGids, bool InWidths, string? BaseFont = null)
{
    public override string ToString() =>
        $"{Font} ({Subtype}) U+{(int)Character:X4} '{Character}': ToUnicode codes " +
        $"[{string.Join(",", ToUnicodeCodes.Select(c => c.ToString("X", CultureInfo.InvariantCulture)))}], " +
        $"non-empty outline gids [{string.Join(",", NonEmptyOutlineGids)}], width entry {InWidths}";
}

/// <summary>
/// Reads font carriers through qpdf only: the object structure from its QDF
/// rewrite, stream bodies through <see cref="QpdfReferenceTool.FilteredStreamData"/>,
/// and the CMap / sfnt / CFF bytes with the small readers below. Covers the
/// fixtures' font kinds: Type0 with Identity CID-to-GID, and simple TrueType
/// through its cmap (formats 0, 4 and 6).
/// </summary>
internal static class FontCarrierProbe
{
    public static IReadOnlyList<FontCarrierHit> Measure(string pdfPath, string term)
    {
        var dump = QpdfReferenceTool.DecodedObjectDump(pdfPath);
        dump.Should().NotBeNull("qpdf must rewrite the file");
        // QDF renumbers objects, so streams are read back from the dump itself.
        var dumpPath = Path.Combine(Path.GetTempPath(), $"font-subset-qdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(dumpPath, dump!);
        try { return Measure(dumpPath, SplitObjects(Encoding.Latin1.GetString(dump!)), term); }
        finally { try { File.Delete(dumpPath); } catch (IOException) { } }
    }

    private static IReadOnlyList<FontCarrierHit> Measure(string pdfPath, Dictionary<int, string> objects, string term)
    {
        var hits = new List<FontCarrierHit>();

        foreach (var (num, dict) in objects)
        {
            if (!Regex.IsMatch(dict, @"/Type\s*/Font\b")) continue;
            var subtype = Name(dict, "Subtype");
            if (subtype is not ("Type0" or "TrueType")) continue;
            var toUnicodeRef = Ref(dict, "ToUnicode");
            var toUnicode = toUnicodeRef is int tu ? ParseToUnicode(Stream(pdfPath, tu)) : new Dictionary<int, string>();

            var descendant = subtype == "Type0" && Regex.Match(dict, @"/DescendantFonts\s*\[\s*(\d+)\s+0\s+R") is { Success: true } m
                ? objects.GetValueOrDefault(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) : dict;
            if (descendant == null) continue;
            var fdRef = Ref(descendant, "FontDescriptor");
            var fd = fdRef is int f ? objects.GetValueOrDefault(f) : null;
            var programRef = fd == null ? null : Ref(fd, "FontFile2") ?? Ref(fd, "FontFile3");
            if (programRef == null) continue;
            var program = Stream(pdfPath, programRef.Value);
            var cidKind = subtype == "Type0" ? Name(descendant, "Subtype") : null;
            if (cidKind == "CIDFontType2")
                (Name(descendant, "CIDToGIDMap") ?? "Identity").Should().Be("Identity", "the probe reads Identity CID-to-GID only");

            var outlines = Ref(fd!, "FontFile2") != null ? GlyfLengths(program) : CffCharStringLengths(program);
            var cmap = subtype == "TrueType" ? SfntCmap(program) : null;
            var widths = WidthCodes(descendant, subtype!);

            foreach (var ch in term.Distinct())
            {
                var codes = toUnicode.Where(kv => kv.Value.Contains(ch)).Select(kv => kv.Key).OrderBy(c => c).ToList();
                var gids = new SortedSet<int>();
                foreach (var code in codes)
                {
                    if (cmap == null) gids.Add(code);
                    else foreach (var cc in new[] { code, 0xF000 | code })
                        if (cmap.TryGetValue(cc, out var g)) gids.Add(g);
                }
                var nonEmpty = gids.Where(g => g < outlines.Count && outlines[g] > (Ref(fd!, "FontFile2") != null ? 0 : 2)).ToList();
                hits.Add(new FontCarrierHit(
                    (Name(dict, "BaseFont") ?? $"obj {num}") + $" obj {num}",
                    cidKind == null ? subtype! : $"Type0/{cidKind}",
                    ch, codes, nonEmpty, codes.Any(widths.Contains), Name(dict, "BaseFont")));
            }
        }
        return hits;
    }

    private static Dictionary<int, string> SplitObjects(string qdf)
    {
        var result = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(qdf, @"(?m)^(\d+) 0 obj\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = qdf.IndexOf("endobj", start, StringComparison.Ordinal);
            int stream = qdf.IndexOf("\nstream", start, StringComparison.Ordinal);
            if (stream >= 0 && stream < end) end = stream;
            result[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = qdf[start..end];
        }
        return result;
    }

    private static string? Name(string dict, string key) =>
        Regex.Match(dict, $@"/{key}\s*/([^\s/\[\]<>()]+)") is { Success: true } m ? m.Groups[1].Value : null;

    private static int? Ref(string dict, string key) =>
        Regex.Match(dict, $@"/{key}\s+(\d+)\s+0\s+R") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    private static byte[] Stream(string pdfPath, int obj)
    {
        var s = QpdfReferenceTool.FilteredStreamData(pdfPath, obj);
        s.Status.Should().Be(QpdfStreamDataStatus.Ok, $"qpdf must decode stream {obj}: {s.Diagnostics}");
        return s.Bytes;
    }

    /// <summary>Codes with an explicit width: simple /Widths entries that are non-zero, or CIDs in /W.</summary>
    private static HashSet<int> WidthCodes(string dict, string subtype)
    {
        var set = new HashSet<int>();
        if (subtype == "TrueType")
        {
            var first = Regex.Match(dict, @"/FirstChar\s+(\d+)");
            var w = Regex.Match(dict, @"/Widths\s*\[([^\]]*)\]");
            if (first.Success && w.Success)
            {
                int code = int.Parse(first.Groups[1].Value, CultureInfo.InvariantCulture);
                foreach (var tok in w.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (double.Parse(tok, CultureInfo.InvariantCulture) != 0) set.Add(code);
                    code++;
                }
            }
            return set;
        }
        var wm = Regex.Match(dict, @"/W\s*\[((?:[^\[\]]|\[[^\]]*\])*)\]");
        if (!wm.Success) return set;
        var parts = Regex.Matches(wm.Groups[1].Value, @"\[[^\]]*\]|[-\d.]+").Select(x => x.Value).ToList();
        for (int i = 0; i + 1 < parts.Count;)
        {
            int a = int.Parse(parts[i], CultureInfo.InvariantCulture);
            if (parts[i + 1].StartsWith('['))
            {
                int n = Regex.Matches(parts[i + 1], @"[-\d.]+").Count;
                for (int k = 0; k < n; k++) set.Add(a + k);
                i += 2;
            }
            else
            {
                int b = int.Parse(parts[i + 1], CultureInfo.InvariantCulture);
                for (int k = a; k <= b; k++) set.Add(k);
                i += 3;
            }
        }
        return set;
    }

    private static Dictionary<int, string> ParseToUnicode(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        var map = new Dictionary<int, string>();
        static string U(string hex) => Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex));
        foreach (Match block in Regex.Matches(text, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
        foreach (Match e in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]*)>"))
            map[Convert.ToInt32(e.Groups[1].Value, 16)] = U(e.Groups[2].Value);
        foreach (Match block in Regex.Matches(text, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
        foreach (Match e in Regex.Matches(block.Groups[1].Value,
                     @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(\[[^\]]*\]|<[0-9A-Fa-f]+>)"))
        {
            int lo = Convert.ToInt32(e.Groups[1].Value, 16), hi = Convert.ToInt32(e.Groups[2].Value, 16);
            var dst = e.Groups[3].Value;
            if (dst.StartsWith('['))
            {
                int i = 0;
                foreach (Match h in Regex.Matches(dst, "<([0-9A-Fa-f]+)>")) map[lo + i++] = U(h.Groups[1].Value);
            }
            else
            {
                var b = Convert.FromHexString(dst[1..^1]);
                for (int i = 0; i <= hi - lo; i++)
                {
                    var bb = (byte[])b.Clone();
                    bb[^1] = (byte)(bb[^1] + i);
                    map[lo + i] = Encoding.BigEndianUnicode.GetString(bb);
                }
            }
        }
        return map;
    }

    private static int U16(byte[] f, int o) => (f[o] << 8) | f[o + 1];
    private static int S16(byte[] f, int o) => (short)U16(f, o);
    private static int U32(byte[] f, int o) => (f[o] << 24) | (f[o + 1] << 16) | (f[o + 2] << 8) | f[o + 3];

    private static Dictionary<string, (int Offset, int Length)> SfntTables(byte[] f)
    {
        var t = new Dictionary<string, (int, int)>();
        for (int i = 0, n = U16(f, 4); i < n; i++)
            t[Encoding.ASCII.GetString(f, 12 + 16 * i, 4)] = (U32(f, 20 + 16 * i), U32(f, 24 + 16 * i));
        return t;
    }

    /// <summary>Byte length of each glyph's glyf entry, by gid (0 = no outline).</summary>
    private static IReadOnlyList<int> GlyfLengths(byte[] f)
    {
        var t = SfntTables(f);
        int glyphs = U16(f, t["maxp"].Offset + 4);
        bool longLoca = S16(f, t["head"].Offset + 50) == 1;
        int loca = t["loca"].Offset;
        int Loc(int i) => longLoca ? U32(f, loca + 4 * i) : U16(f, loca + 2 * i) * 2;
        return Enumerable.Range(0, glyphs).Select(i => Loc(i + 1) - Loc(i)).ToList();
    }

    /// <summary>Byte length of each CharStrings entry of a bare CFF or an OpenType 'CFF ' table.</summary>
    private static IReadOnlyList<int> CffCharStringLengths(byte[] f)
    {
        if (Encoding.ASCII.GetString(f, 0, 4) == "OTTO")
        {
            var (o, l) = SfntTables(f)["CFF "];
            f = f[o..(o + l)];
        }
        var (_, afterNames) = CffIndex(f, f[2]);
        var (tops, _) = CffIndex(f, afterNames);
        var top = f[tops[0].Offset..(tops[0].Offset + tops[0].Length)];
        int charStrings = CffDictOperand(top, 17);
        return CffIndex(f, charStrings).Items.Select(x => x.Length).ToList();
    }

    private static (List<(int Offset, int Length)> Items, int End) CffIndex(byte[] f, int pos)
    {
        int count = U16(f, pos);
        if (count == 0) return (new(), pos + 2);
        int size = f[pos + 2];
        int Off(int i) { int v = 0; for (int k = 0; k < size; k++) v = (v << 8) | f[pos + 3 + i * size + k]; return v; }
        int data0 = pos + 3 + (count + 1) * size - 1;
        var items = Enumerable.Range(0, count).Select(i => (data0 + Off(i), Off(i + 1) - Off(i))).ToList();
        return (items, data0 + Off(count));
    }

    private static int CffDictOperand(byte[] d, int op)
    {
        var operands = new List<int>();
        for (int i = 0; i < d.Length;)
        {
            int b = d[i];
            if (b <= 21)
            {
                int key = b == 12 ? 1200 + d[i + 1] : b;
                i += b == 12 ? 2 : 1;
                if (key == op) return operands[0];
                operands.Clear();
            }
            else if (b == 28) { operands.Add(S16(d, i + 1)); i += 3; }
            else if (b == 29) { operands.Add(U32(d, i + 1)); i += 5; }
            else if (b == 30) { i++; while ((d[i] & 0xF) != 0xF && (d[i] >> 4) != 0xF) i++; i++; operands.Add(0); }
            else if (b is >= 32 and <= 246) { operands.Add(b - 139); i++; }
            else if (b is >= 247 and <= 250) { operands.Add((b - 247) * 256 + d[i + 1] + 108); i += 2; }
            else if (b is >= 251 and <= 254) { operands.Add(-(b - 251) * 256 - d[i + 1] - 108); i += 2; }
            else i++;
        }
        throw new InvalidDataException($"CFF Top DICT has no operator {op}");
    }

    /// <summary>Code-to-gid from every cmap subtable in format 0, 4, 6 or 12, merged (first subtable wins).</summary>
    private static Dictionary<int, int> SfntCmap(byte[] f)
    {
        var map = new Dictionary<int, int>();
        foreach (var (code, gid) in SfntCmapAll(f)) map.TryAdd(code, gid);
        return map;
    }

    /// <summary>Every (code, gid) entry of every cmap subtable in format 0, 4, 6 or 12, in subtable order.</summary>
    private static List<(int Code, int Gid)> SfntCmapAll(byte[] f)
    {
        var all = new List<(int, int)>();
        var t = SfntTables(f);
        if (!t.TryGetValue("cmap", out var cm)) return all;
        for (int i = 0, n = U16(f, cm.Offset + 2); i < n; i++)
        {
            int s = cm.Offset + U32(f, cm.Offset + 8 + 8 * i);
            switch (U16(f, s))
            {
                case 0:
                    for (int c = 0; c < 256; c++) if (f[s + 6 + c] != 0) all.Add((c, f[s + 6 + c]));
                    break;
                case 6:
                    for (int c = 0, first = U16(f, s + 6), cnt = U16(f, s + 8); c < cnt; c++)
                        if (U16(f, s + 10 + 2 * c) is var g and not 0) all.Add((first + c, g));
                    break;
                case 12:
                    for (int k = 0, groups = U32(f, s + 12); k < groups; k++)
                    {
                        int p = s + 16 + 12 * k;
                        for (int c = U32(f, p); c <= U32(f, p + 4); c++) all.Add((c, U32(f, p + 8) + (c - U32(f, p))));
                    }
                    break;
                case 4:
                {
                    int segs = U16(f, s + 6) / 2;
                    int ends = s + 14, starts = ends + 2 * segs + 2, deltas = starts + 2 * segs, ranges = deltas + 2 * segs;
                    for (int k = 0; k < segs; k++)
                    for (int c = U16(f, starts + 2 * k); c <= U16(f, ends + 2 * k) && c != 0xFFFF; c++)
                    {
                        int ro = U16(f, ranges + 2 * k), g;
                        if (ro == 0) g = (c + S16(f, deltas + 2 * k)) & 0xFFFF;
                        else
                        {
                            g = U16(f, ranges + 2 * k + ro + 2 * (c - U16(f, starts + 2 * k)));
                            if (g != 0) g = (g + S16(f, deltas + 2 * k)) & 0xFFFF;
                        }
                        if (g != 0) all.Add((c, g));
                    }
                    break;
                }
            }
        }
        return all;
    }

    /// <summary>
    /// What a font in <paramref name="pdfPath"/> still holds of the codes and
    /// glyph ids <paramref name="traces"/> recorded before the redaction, found
    /// by BaseFont: non-empty outlines, program cmap entries selecting those
    /// glyphs, <c>post</c> 2.0 names, a <c>Zapf</c> table, and width entries.
    /// </summary>
    public static IReadOnlyList<string> Residue(string pdfPath, IReadOnlyList<FontCarrierHit> traces)
    {
        var dump = QpdfReferenceTool.DecodedObjectDump(pdfPath);
        dump.Should().NotBeNull("qpdf must rewrite the file");
        var dumpPath = Path.Combine(Path.GetTempPath(), $"font-subset-qdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(dumpPath, dump!);
        try
        {
            var objects = SplitObjects(Encoding.Latin1.GetString(dump!));
            var found = new List<string>();
            foreach (var group in traces.GroupBy(t => t.BaseFont))
            {
                var codes = group.SelectMany(t => t.ToUnicodeCodes).ToHashSet();
                var gids = group.SelectMany(t => t.NonEmptyOutlineGids).ToHashSet();
                var fonts = objects.Where(o => Regex.IsMatch(o.Value, @"/Type\s*/Font\b") &&
                                               Name(o.Value, "BaseFont") == group.Key &&
                                               Name(o.Value, "Subtype") is "Type0" or "TrueType").ToList();
                fonts.Should().NotBeEmpty($"the redacted file must still hold font {group.Key}");
                foreach (var (num, dict) in fonts)
                {
                    var subtype = Name(dict, "Subtype")!;
                    var descendant = subtype == "Type0" && Regex.Match(dict, @"/DescendantFonts\s*\[\s*(\d+)\s+0\s+R") is { Success: true } m
                        ? objects[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] : dict;
                    var widths = WidthCodes(descendant, subtype);
                    foreach (var code in codes.Where(widths.Contains))
                        found.Add($"{group.Key} obj {num}: code {code:X} keeps a width entry");
                    var fd = Ref(descendant, "FontDescriptor") is int f ? objects[f] : null;
                    var programRef = fd == null ? null : Ref(fd, "FontFile2") ?? Ref(fd, "FontFile3");
                    if (programRef == null) continue;
                    var program = Stream(dumpPath, programRef.Value);
                    var isGlyf = Ref(fd!, "FontFile2") != null;
                    var outlines = isGlyf ? GlyfLengths(program) : CffCharStringLengths(program);
                    foreach (var g in gids.Where(g => g < outlines.Count && outlines[g] > (isGlyf ? 0 : 2)))
                        found.Add($"{group.Key} obj {num}: glyph {g} keeps its outline ({outlines[g]} bytes)");
                    if (Encoding.ASCII.GetString(program, 0, 4) != "OTTO" && !(program[0] == 0 && program[1] == 1)) continue;
                    var tables = SfntTables(program);
                    foreach (var (code, g) in SfntCmapAll(program).Where(e => gids.Contains(e.Gid)))
                        found.Add($"{group.Key} obj {num}: the program's cmap maps {code:X} to removed glyph {g}");
                    if (tables.ContainsKey("Zapf"))
                        found.Add($"{group.Key} obj {num}: the program keeps a Zapf table (glyph Unicode values)");
                    if (tables.TryGetValue("post", out var post) && U32(program, post.Offset) == 0x00020000)
                        foreach (var g in gids.Where(g => g < U16(program, post.Offset + 32) && U16(program, post.Offset + 34 + 2 * g) != 0))
                            found.Add($"{group.Key} obj {num}: glyph {g} keeps its post name");
                }
            }
            return found;
        }
        finally { try { File.Delete(dumpPath); } catch (IOException) { } }
    }
}
