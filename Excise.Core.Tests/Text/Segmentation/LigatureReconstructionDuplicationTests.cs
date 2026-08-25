using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1156 — redaction must not duplicate a ligature glyph in SURVIVING text.
///
/// <para>Some fonts map a single ligature glyph code to a multi-character
/// destination in their /ToUnicode CMap (e.g. one code → "ft", "ff", "fi").
/// <see cref="Letter"/> then carries a 2-character <c>Value</c> for that one
/// glyph. <see cref="LetterFinder"/> emits one <see cref="LetterMatch"/> per
/// decoded CHARACTER, so a ligature yields several consecutive matches that
/// share one <see cref="Letter"/> and one source code. When
/// <see cref="OperationReconstructor"/> rebuilt a Tj after removing an
/// intersecting glyph, it concatenated each match's raw bytes and replayed the
/// ligature code once per character — turning a surviving <c>after</c> into
/// <c>aftfter</c>, <c>offer</c> into <c>offffer</c>. That is silent
/// data-corruption of untargeted text in a security tool.</para>
///
/// <para>These tests differ from <see cref="LatinLigatureRedactionTests"/>,
/// which use single-code-point ligatures (U+FB00–U+FB06, a 1-character
/// <c>Value</c>) and so never exercise the many-characters-per-glyph mapping
/// that causes this bug.</para>
///
/// <para>The oracle for "surviving word is intact" is mutool — an independent
/// extractor, per CLAUDE.md's no-self-oracle rule. The test is allow-listed as
/// <c>[requires: tool:mutool]</c> and skips where mutool is absent.</para>
/// </summary>
public class LigatureReconstructionDuplicationTests
{
    [Fact]
    public void RedactingNeighbour_LeavesLigatureWordIntact_ExciseExtractor()
    {
        // "afterXYZ" as codes A..G, where code B → "ft" (one glyph, 2 chars).
        // Anti-vacuity: the fixture must extract with a real 2-char ligature
        // Letter, or the test proves nothing.
        var pdf = BuildLigatureFixture();
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        page.Text.Should().Contain("afterXYZ",
            "sanity: the fixture must decode the ligature code to \"ft\"");
        page.Letters.Any(l => l.Value == "ft").Should().BeTrue(
            "sanity: exactly the multi-character-per-glyph mapping this bug needs " +
            "— a single Letter whose Value is the 2-character \"ft\" — must be present");

        var removed = doc.RedactText("XYZ").VerifiedRemovals;
        removed.Should().BeGreaterThan(0, "the neighbouring term must actually be removed");

        // excise's own extractor already sees the corruption (#1156 is content-
        // stream corruption, not a render artifact), so this catches it too.
        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var text = reopened.GetPage(1).Text;
        text.Should().Contain("after", "the surviving word must be intact");
        text.Should().NotContain("aftfter", "the ligature glyph must not be doubled (#1156)");
        text.Should().NotContain("XYZ", "the redacted term must be gone");
    }

    [Fact]
    public void RedactingNeighbour_LeavesLigatureWordIntact_MutoolOracle()
    {
        var mutool = FindOnPath("mutool");
        Assert.SkipWhen(mutool is null, "mutool not on PATH");

        var pdf = BuildLigatureFixture();
        using var doc = PdfDocument.Open(pdf);

        doc.RedactText("XYZ").VerifiedRemovals.Should().BeGreaterThan(0);

        var outPath = Path.Combine(Path.GetTempPath(),
            $"lig-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(outPath, doc.SaveToBytes());
            var text = MutoolText(mutool!, outPath);

            // Independent extractor: the surviving word must be byte-identical,
            // with no doubled ligature glyph.
            text.Should().Contain("after",
                "mutool must read the surviving word intact");
            text.Should().NotContainAny("aftfter", "ftft",
                "mutool must not see a doubled ligature glyph (#1156)");
            text.Should().NotContain("XYZ", "the redacted term must be gone");
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    /// <summary>
    /// A minimal single-page PDF whose one Tj shows codes A..G. The /ToUnicode
    /// CMap maps them to a, [ft], e, r, X, Y, Z — so code B is a ligature glyph
    /// with a 2-character destination. Decoded text: "afterXYZ".
    /// </summary>
    private static byte[] BuildLigatureFixture()
    {
        // src code → hex UTF-16BE destination. B is the 2-unit ligature "ft".
        var bfchar = new (int Code, string DstHex)[]
        {
            (0x41, "0061"),      // A → a
            (0x42, "00660074"),  // B → f t   (one glyph, two characters)
            (0x43, "0065"),      // C → e
            (0x44, "0072"),      // D → r
            (0x45, "0058"),      // E → X
            (0x46, "0059"),      // F → Y
            (0x47, "005A"),      // G → Z
        };

        var entries = new StringBuilder();
        foreach (var (code, dst) in bfchar)
            entries.Append($"<{code:X2}> <{dst}>\n");

        var cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n" +
            "/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            $"{bfchar.Length} beginbfchar\n{entries}endbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";

        const string content = "BT /F1 24 Tf 100 700 Td (ABCDEFG) Tj ET";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1, leaveOpen: true) { NewLine = "\n" };

        writer.WriteLine("%PDF-1.7");
        writer.Flush();

        var offsets = new long[7];

        offsets[1] = Flush(writer, ms);
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");

        offsets[2] = Flush(writer, ms);
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");

        offsets[3] = Flush(writer, ms);
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        writer.WriteLine("endobj");

        offsets[4] = Flush(writer, ms);
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.WriteLine(content);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");

        offsets[5] = Flush(writer, ms);
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                         "/FirstChar 32 /LastChar 127 /ToUnicode 6 0 R >>");
        writer.WriteLine("endobj");

        offsets[6] = Flush(writer, ms);
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Length {cmap.Length} >>");
        writer.WriteLine("stream");
        writer.WriteLine(cmap);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");

        long xrefPos = Flush(writer, ms);
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static long Flush(StreamWriter writer, MemoryStream ms)
    {
        writer.Flush();
        return ms.Position;
    }

    private static string MutoolText(string mutool, string pdfPath)
    {
        var psi = new ProcessStartInfo(mutool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "draw", "-F", "txt", "-o", "-", pdfPath, "1" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000).Should().BeTrue("mutool should exit within 30 seconds");
        proc.ExitCode.Should().Be(0, "mutool should read the redacted fixture");
        return stdout;
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(candidate + ".exe")) return candidate + ".exe";
        }
        return null;
    }
}
