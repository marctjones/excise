using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1533's acceptance, checked against tools that are not excise: the version a
/// document has when its header and its catalog <c>/Version</c> disagree.
/// </summary>
/// <remarks>
/// <para>Two fixtures, identical but for the pair of numbers: header 1.4 with
/// catalog <c>/Version /1.7</c> (an update RAISED the version), and header 1.7
/// with catalog <c>/Version /1.4</c> (the catalog claims LESS than the header).
/// <c>CatalogVersionOverrideTests</c> in Excise.Core.Tests pins excise's answer
/// against excise; this pins it against qpdf and mutool.</para>
///
/// <para><b>What each oracle can and cannot say — measured 2026-09-21, qpdf
/// 12.3.2, mutool 1.27.2.</b></para>
/// <list type="bullet">
/// <item><description><b>qpdf reports the two INPUTS, not a verdict.</b>
/// <c>--json</c> gives <c>qpdf[0].pdfversion</c> = the header alone, and the
/// catalog <c>/Version</c> appears as a plain catalog key; <c>--check</c> prints
/// the header version; a plain rewrite keeps both untouched. qpdf never
/// computes "the later of". So qpdf's independent parse establishes what the
/// fixture really says, and the resolution is excise's stated policy applied
/// to those two independently-read values — an agreement about the inputs, not
/// about the rule.</description></item>
/// <item><description><b>mutool reports a verdict, and it differs on the second
/// fixture.</b> <c>mutool info</c> prints <c>PDF-1.7</c> for the first fixture
/// (agreement) and <c>PDF-1.4</c> for the second — it takes the catalog entry
/// literally, INCLUDING when it lowers the version. excise takes the later of
/// the two and reports 1.7. The spec's wording for the catalog entry is "if
/// later than the version specified in the file's header" (ISO 32000-1
/// Table 28), so a downgrade is not something the entry is defined to do;
/// excise's answer is the conservative one for a capability gate, and mutool's
/// is a lenient reading. That is a registered divergence, pinned exactly so a
/// change on either side is noticed, not a failure to agree.</description></item>
/// </list>
///
/// <para>The <b>saved header</b> is also pinned: excise writes the EFFECTIVE
/// version, and a raised version really does unlock object streams — read back
/// by qpdf (<c>--show-xref</c> lists compressed entries), never by excise.</para>
/// </remarks>
public class CatalogVersionOracleTests
{
    /// <summary>
    /// A minimal one-page PDF (with an empty <c>/Resources</c>, so qpdf has
    /// nothing to repair and exits clean) whose header and catalog versions are
    /// chosen by the caller. Offsets are computed, so the xref is real.
    /// </summary>
    private static byte[] BuildPdf(string headerVersion, string catalogVersionName)
    {
        var objects = new[]
        {
            $"<< /Type /Catalog /Pages 2 0 R /Version /{catalogVersionName} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
        };

        var sb = new StringBuilder();
        sb.Append($"%PDF-{headerVersion}\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Fixture(string header, string catalog)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"catver_{Guid.NewGuid():N}.pdf");
        System.IO.File.WriteAllBytes(path, BuildPdf(header, catalog));
        return path;
    }

    /// <summary>Run a tool, drain both pipes concurrently (#925), return stdout or null.</summary>
    private static string? RunTool(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc == null) return null;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            return null;
        }
        stderr.GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult();
    }

    /// <summary>What qpdf's parser reads out of the file: the header version and the catalog's /Version.</summary>
    private static (string Header, string? Catalog) QpdfInputs(string path)
    {
        var json = RunTool("qpdf", "--json", "--json-key=qpdf", path);
        json.Should().NotBeNullOrEmpty("qpdf must produce JSON for the fixture");
        using var doc = JsonDocument.Parse(json!);
        var qpdf = doc.RootElement.GetProperty("qpdf");
        var header = qpdf[0].GetProperty("pdfversion").GetString()!;

        string? catalog = null;
        foreach (var entry in qpdf[1].EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                continue;
            if (value.TryGetProperty("/Type", out var type) && type.GetString() == "/Catalog"
                && value.TryGetProperty("/Version", out var version))
                catalog = version.GetString()!.TrimStart('/');
        }
        return (header, catalog);
    }

    /// <summary>What <c>mutool info</c> prints as the document's PDF version.</summary>
    private static string MutoolVersion(string path)
    {
        var text = RunTool("mutool", "info", path);
        var m = Regex.Match(text ?? "", @"^PDF-(\d\.\d)\s*$", RegexOptions.Multiline);
        m.Success.Should().BeTrue($"mutool info must print a PDF-x.y line. Output: {text}");
        return m.Groups[1].Value;
    }

    private static string HeaderOf(byte[] pdf)
    {
        var m = Regex.Match(Encoding.ASCII.GetString(pdf, 0, 16), @"^%PDF-(\d\.\d)");
        m.Success.Should().BeTrue("the file starts with a %PDF-x.y header");
        return m.Groups[1].Value;
    }

    [Theory]
    [InlineData("1.4", "1.7", "1.7")] // raised: the update case §7.5.5 exists for
    [InlineData("1.7", "1.4", "1.7")] // lowered: the catalog never takes the version DOWN
    public void Qpdf_ReadsTheSameTwoInputs_ThatExciseResolvesToTheLater(
        string header, string catalog, string effective)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable,
            "qpdf is the independent structure parser for this check and is not installed (brew install qpdf)");

        var path = Fixture(header, catalog);
        try
        {
            // qpdf's parser, not ours, says what the fixture actually contains.
            var (qpdfHeader, qpdfCatalog) = QpdfInputs(path);
            qpdfHeader.Should().Be(header, "qpdf reads the same header the fixture was built with");
            qpdfCatalog.Should().Be(catalog, "qpdf reads the same catalog /Version the fixture was built with");

            // ...and excise resolves those two values to the later of them.
            using var document = PdfDocument.Open(System.IO.File.ReadAllBytes(path));
            document.Version.Should().Be(effective);
            var later = string.CompareOrdinal(qpdfCatalog, qpdfHeader) > 0 ? qpdfCatalog : qpdfHeader;
            document.Version.Should().Be(later,
                "excise's effective version is the later of the two values qpdf independently read");
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void Mutool_AgreesWithExcise_WhenTheCatalogRaisesTheVersion()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool is the independent verdict for this check and is not installed (brew install mupdf-tools)");

        var path = Fixture("1.4", "1.7");
        try
        {
            using var document = PdfDocument.Open(System.IO.File.ReadAllBytes(path));
            document.Version.Should().Be("1.7");
            MutoolVersion(path).Should().Be(document.Version,
                "#1533 acceptance: mutool independently reports the raised version");
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void Mutool_Diverges_WhenTheCatalogLowersTheVersion_RegisteredDivergence()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool is the independent verdict for this check and is not installed (brew install mupdf-tools)");

        var path = Fixture("1.7", "1.4");
        try
        {
            using var document = PdfDocument.Open(System.IO.File.ReadAllBytes(path));
            document.Version.Should().Be("1.7", "the catalog entry raises the version, it never lowers it");

            // Measured, not assumed: mutool takes the catalog entry literally.
            // Pinned EXACTLY so that a mutool that starts agreeing (or an excise
            // that starts lowering) is a noticed change and not a silent one.
            MutoolVersion(path).Should().Be("1.4",
                "mutool honours a catalog /Version that is EARLIER than the header (a lenient " +
                "reading of the spec's 'if later than' wording). If this is now 1.7 the divergence " +
                "is retired and this test should become an agreement check");
        }
        finally { System.IO.File.Delete(path); }
    }

    [Theory]
    [InlineData("1.4", "1.7")]
    [InlineData("1.7", "1.4")]
    public void TheSavedHeader_CarriesTheEffectiveVersion_AndTheRaisedVersionUnlocksObjectStreams(
        string header, string catalog)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable,
            "qpdf reads the saved file back independently and is not installed (brew install qpdf)");

        var path = Fixture(header, catalog);
        var savedPath = System.IO.Path.ChangeExtension(path, ".saved.pdf");
        try
        {
            using var document = PdfDocument.Open(System.IO.File.ReadAllBytes(path));
            var saved = document.SaveToBytes();
            System.IO.File.WriteAllBytes(savedPath, saved);

            // The header the OUTPUT carries is the effective version — for
            // both fixtures 1.7, where the header alone would have said 1.4
            // for the first one.
            HeaderOf(saved).Should().Be("1.7",
                "#1533: Save emits the effective version (the later of header and catalog), " +
                "not the file's original header");
            QpdfInputs(savedPath).Header.Should().Be("1.7",
                "qpdf independently reads the same header off the saved bytes");

            // The compression gate reads it too. Only the first fixture
            // distinguishes 'effective' from 'header' (a 1.4 header alone
            // would forbid object streams); qpdf lists compressed entries in
            // --show-xref, so the verdict is not excise's own.
            var xref = RunTool("qpdf", "--show-xref", savedPath);
            xref.Should().NotBeNullOrEmpty();
            xref!.Should().Contain("compressed",
                "a 1.7 document is entitled to object streams, and the gate must have read 1.7, " +
                "not the 1.4 header, to use them");
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(savedPath);
        }
    }
}
