using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Navigation carriers read back by qpdf and mutool, not by excise: a page-label
/// prefix (#1853) and a named-destination key (#1852). qpdf must see the term in
/// the input, must not see it after redaction, and must pass the output with
/// <c>--check</c>; mutool must resolve every bookmark and link to the page it
/// resolved to before.
/// </summary>
public sealed class NavigationCarrierOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"navigation-carriers-{Guid.NewGuid():N}");

    public NavigationCarrierOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Utf16Hex(string text) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    [Theory]
    [InlineData("literal", RedactionProfile.Standard)]
    [InlineData("literal", RedactionProfile.Maximum)]
    [InlineData("arabic", RedactionProfile.Standard)]
    [InlineData("arabic", RedactionProfile.Maximum)]
    public void PageLabelPrefix_IsGoneToQpdf(string shape, RedactionProfile profile)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent reader (brew install qpdf)");
        var (prefix, term) = shape == "arabic"
            ? (Utf16Hex("سلام chapter"), "سلام")
            : ("(KESTREL chapter)", "KESTREL");
        var input = Path.Combine(_dir, $"{shape}-in.pdf");
        File.WriteAllBytes(input, CarrierTrapFixtures.WithCatalog($"/PageLabels << /Nums [0 << /S /D /P {prefix} >>] >>"));
        CarrierTrapIndependentCorroborationTests.QpdfDump(input).Should().Contain(term, "input-side control");

        var output = Path.Combine(_dir, $"{shape}-{profile}.pdf");
        using (var document = PdfDocument.Open(File.ReadAllBytes(input)))
        {
            document.RedactText(term, RedactionOptions.ForProfile(profile));
            document.Save(output);
        }

        QpdfReferenceTool.Check(output)!.Value.Success.Should().BeTrue("the redacted file must stay valid to qpdf");
        var dump = CarrierTrapIndependentCorroborationTests.QpdfDump(output);
        dump.Should().NotContain(term, "qpdf decodes every string in the file, the prefix included");
        if (profile == RedactionProfile.Standard)
            dump.Should().Contain(" chapter", "Strip keeps the rest of the prefix");
    }

    // mutool's own resolver: the page index each bookmark and link lands on, -1 when its name resolves to nothing.
    private const string NavigationScript = """
        var doc = Document.openDocument(scriptArgs[0]);
        function walk(items) {
          for (var i = 0; i < items.length; i++) {
            print("outline " + items[i].title + " -> " + doc.resolveLink(items[i].uri));
            if (items[i].down) walk(items[i].down);
          }
        }
        walk(doc.loadOutline() || []);
        for (var p = 0; p < doc.countPages(); p++) {
          var links = doc.loadPage(p).getLinks();
          for (var j = 0; j < links.length; j++)
            print("link " + p + "." + j + " -> " + doc.resolveLink(links[j].getURI ? links[j].getURI() : links[j].uri));
        }
        """;

    private static readonly string[] ExpectedNavigation =
        ["outline Chapter -> 1", "link 0.0 -> 1", "link 0.1 -> 0"];

    public static TheoryData<string, bool> DestinationCases() => new()
    {
        { "literal", false }, { "literal", true }, { "arabic", false }, { "arabic", true },
    };

    /// <summary>
    /// A two-page document whose destination on page 2 is named after the term,
    /// in the first of two leaves; a bookmark, a link and the /OpenAction reach it
    /// by name, and a second link reaches an untouched name on page 1. Standard
    /// cuts the term from the key; the Maximum policy replaces the key (applied
    /// through ScrubTerms, because Maximum's RedactText also removes the bookmark
    /// and the links whose navigation this checks).
    /// </summary>
    [Theory]
    [MemberData(nameof(DestinationCases))]
    public void NamedDestinationKey_IsGoneToQpdf_AndMutoolStillLandsOnTheSamePages(string shape, bool maximum)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable && MutoolReferenceRenderer.IsAvailable,
            "qpdf and mutool are the independent readers (brew install qpdf mupdf-tools)");
        var (key, term) = shape == "arabic" ? (Utf16Hex("سلام chapter"), "سلام") : ("(KESTREL chapter)", "KESTREL");
        var input = Path.Combine(_dir, $"dest-{shape}-in.pdf");
        File.WriteAllBytes(input, TwoPageDestinations(key));
        var script = Path.Combine(_dir, "navigation.js");
        File.WriteAllText(script, NavigationScript);
        Navigation(script, input).Should().Equal(ExpectedNavigation, "input-side control: mutool resolves every name");
        CarrierTrapIndependentCorroborationTests.QpdfDump(input).Should().Contain(term, "input-side control");

        var output = Path.Combine(_dir, $"dest-{shape}-{maximum}.pdf");
        using (var document = PdfDocument.Open(File.ReadAllBytes(input)))
        {
            if (maximum)
                PdfDocumentSanitizer.ScrubTerms(document, new[] { term }, caseSensitive: false,
                    RedactionCarriers.All, RedactionOptions.Maximum.CarrierPolicy);
            else
                document.RedactText(term, RedactionOptions.Default);
            document.Save(output);
        }

        QpdfReferenceTool.Check(output)!.Value.Success.Should().BeTrue("the rewritten name tree must be valid to qpdf");
        Navigation(script, output).Should().Equal(ExpectedNavigation,
            "every bookmark and link must land where it did before the key was renamed");
        CarrierTrapIndependentCorroborationTests.QpdfDump(output).Should().NotContain(term);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), term).Should().BeEmpty();

        // By hand from qpdf's object dump: the key the references name maps to page 2.
        var qpdf = QpdfObjects(output);
        var catalog = qpdf.Deref(qpdf.Trailer.GetProperty("/Root"));
        var pages = qpdf.Deref(catalog.GetProperty("/Pages")).GetProperty("/Kids").EnumerateArray().Select(k => k.GetString()).ToList();
        var leaf = qpdf.Deref(qpdf.Deref(catalog.GetProperty("/Names")).GetProperty("/Dests")).GetProperty("/Names")
            .EnumerateArray().ToList();
        var dests = Enumerable.Range(0, leaf.Count / 2).ToDictionary(i => leaf[2 * i].GetString()!, i => qpdf.Deref(leaf[2 * i + 1]));
        var expectedKey = "u:" + (maximum ? "[redacted]" : "chapter");
        dests.Keys.Should().Equal(new[] { "u:Alpha", "u:Zulu", expectedKey }, "sorted by bytes, and no key keeps the term");
        dests[expectedKey][0].GetString().Should().Be(pages[1], "the renamed destination is still page 2");
        var outline = qpdf.Deref(qpdf.Deref(catalog.GetProperty("/Outlines")).GetProperty("/First"));
        outline.GetProperty("/Dest").GetString().Should().Be(expectedKey);
        qpdf.Deref(catalog.GetProperty("/OpenAction")).GetProperty("/D").GetString().Should().Be(expectedKey);
    }

    private static IReadOnlyList<string> Navigation(string script, string pdf) =>
        Encoding.UTF8.GetString(CarrierTrapIndependentCorroborationTests.RunTool("mutool", "run", script, pdf)
                                ?? throw new InvalidOperationException("mutool run failed on " + pdf))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record QpdfJson(JsonElement Objects, JsonElement Trailer)
    {
        public JsonElement Deref(JsonElement value) =>
            value.ValueKind == JsonValueKind.String && value.GetString() is { } s && s.EndsWith(" R", StringComparison.Ordinal)
                ? Objects.GetProperty("obj:" + s) is var o && o.TryGetProperty("value", out var v) ? v : o.GetProperty("stream").GetProperty("dict")
                : value;
    }

    private static QpdfJson QpdfObjects(string path)
    {
        var json = CarrierTrapIndependentCorroborationTests.RunTool("qpdf", "--json", "--json-key=qpdf", path)
                   ?? throw new InvalidOperationException("qpdf --json failed on " + path);
        var objects = JsonDocument.Parse(json).RootElement.GetProperty("qpdf")[1].Clone();
        return new QpdfJson(objects, objects.GetProperty("trailer").GetProperty("value"));
    }

    private static byte[] TwoPageDestinations(string key)
    {
        var objects = new[]
        {
            $"<< /Type /Catalog /Pages 2 0 R /Names << /Dests 5 0 R >> /Outlines 8 0 R /OpenAction << /S /GoTo /D {key} >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [10 0 R 11 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Kids [6 0 R 7 0 R] >>",
            $"<< /Limits [(Alpha) {key}] /Names [(Alpha) [3 0 R /XYZ 0 700 0] {key} [4 0 R /XYZ 0 500 0]] >>",
            "<< /Limits [(Zulu) (Zulu)] /Names [(Zulu) [3 0 R /XYZ 0 100 0]] >>",
            "<< /Type /Outlines /First 9 0 R /Last 9 0 R /Count 1 >>",
            $"<< /Title (Chapter) /Parent 8 0 R /Dest {key} >>",
            $"<< /Type /Annot /Subtype /Link /Rect [72 600 372 620] /A << /S /GoTo /D {key} >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 500 372 520] /Dest (Zulu) >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
