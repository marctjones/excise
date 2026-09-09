using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle evidence for document-navigation structures (outlines,
/// optional-content groups, the structure tree, page count) that previously
/// rested only on excise reading back what excise itself authored. Every
/// count here comes from qpdf's own JSON object dump -- a tool that shares
/// no code with excise's parser -- so a systematic misunderstanding in
/// excise's own reader (the #636/#608/#637 shape: the bug and the self-test
/// agree with each other) cannot pass silently the way it would if excise
/// verified its own count.
///
/// Each fixture's expected count is asserted greater than zero as a guard:
/// a fixture that happens to have none of the target construct would let a
/// broken parser return zero and still "pass".
/// </summary>
public class DocumentStructureParityTests
{
    [Theory]
    [InlineData("test-pdfs/local-real-world/foss-primer.pdf")]
    [InlineData("test-pdfs/local-real-world/producingoss.pdf")]
    public void PageCount_MatchesQpdf(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"corpus fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = QpdfReferenceTool.PageCount(path!);
        Assert.SkipWhen(expected is null or <= 0, "qpdf could not read the fixture's page count");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        doc.PageCount.Should().Be(expected!.Value,
            "excise's own page count must agree with qpdf's independent count -- a page-tree " +
            "misreading (e.g. missing inherited /Count, a cycle guard that stops early) would " +
            "otherwise pass silently under excise's own self-consistent reading");
    }

    [Theory]
    [InlineData("test-pdfs/local-real-world/foss-primer.pdf")]
    public void OutlineItemCount_MatchesQpdf(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"corpus fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        // NOT a flat "objects with /Title" count: foss-primer.pdf measured 64
        // objects carrying /Title but only 63 reachable from /Outlines /First
        // via /Next -- one is an orphan (unreferenced, likely left over from
        // an editing tool), exactly the "dead object survives in the raw file
        // but isn't part of the live structure" shape already found once this
        // session (Popup, #1428-adjacent). Excise's own reachable count (63)
        // is the CORRECT answer; a naive raw count would have failed this
        // fixture for the wrong reason. The independent oracle here is this
        // walk itself -- reimplemented from qpdf's JSON object graph, not by
        // calling excise's PdfOutlineParser -- so it still doesn't trust
        // excise to grade itself.
        var expected = QpdfReachableOutlineCount(path!);
        Assert.SkipWhen(expected < 0, "qpdf could not read the fixture");
        expected.Should().BeGreaterThan(0,
            "guard: the fixture must actually contain outline items, or this row proves nothing");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        var actual = CountOutlineItems(PdfOutlineParser.Parse(doc));
        actual.Should().Be(expected,
            "excise's outline tree walk must find the same number of REACHABLE items an " +
            "independent /First-/Next-/Parent walk over qpdf's raw object graph does -- a " +
            "cycle-guard, depth-limit, or chain bug could silently drop items while excise's " +
            "own reader stayed internally consistent");
    }

    [Theory]
    [InlineData("test-pdfs/local-real-world/foss-primer.pdf")]
    public void OutlineItemCount_SurvivesAnOpenSaveRoundTrip(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"corpus fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = QpdfObjectCountWithKey(path!, "/Title");
        Assert.SkipWhen(expected < 0, "qpdf could not read the fixture");
        expected.Should().BeGreaterThan(0, "guard: the fixture must contain outline items");

        var output = Path.Combine(Path.GetTempPath(), $"excise-outline-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
                doc.Save(output);

            var after = QpdfObjectCountWithKey(output, "/Title");
            after.Should().BeGreaterThanOrEqualTo(0, "qpdf must be able to read what excise wrote");
            after.Should().Be(expected,
                "the outline (table of contents) must survive an open-save round trip unchanged " +
                "-- dropping a bookmark on save is silent data loss a reader would only notice by " +
                "opening the navigation panel, which no test does today");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("test-pdfs/poppler/unittestcases/NestedLayers.pdf")]
    public void OcgCount_MatchesQpdf(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"corpus fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = QpdfObjectCountWithTypeValue(path!, "/OCG");
        Assert.SkipWhen(expected < 0, "qpdf could not read the fixture");
        expected.Should().BeGreaterThan(0,
            "guard: the fixture must actually contain optional-content groups");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        doc.GetOptionalContentGroups().Count.Should().Be(expected,
            "excise's OCG list must agree with qpdf's independent count of /Type /OCG objects -- " +
            "an OCG excise's own reader misses is a layer a user cannot see or toggle, and is a " +
            "security question when it defaults to visible-but-unlisted");
    }

    [Theory]
    [InlineData("test-pdfs/poppler/unittestcases/NestedLayers.pdf")]
    public void OcgCount_SurvivesAnOpenSaveRoundTrip(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"corpus fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = QpdfObjectCountWithTypeValue(path!, "/OCG");
        Assert.SkipWhen(expected < 0, "qpdf could not read the fixture");
        expected.Should().BeGreaterThan(0, "guard: the fixture must contain OCGs");

        var output = Path.Combine(Path.GetTempPath(), $"excise-ocg-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
                doc.Save(output);

            var after = QpdfObjectCountWithTypeValue(output, "/OCG");
            after.Should().BeGreaterThanOrEqualTo(0, "qpdf must be able to read what excise wrote");
            after.Should().Be(expected,
                "optional-content groups must survive an open-save round trip -- dropping one " +
                "silently changes which layers a reopened document can toggle");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("test-pdfs/pdfium/tagged_nested.pdf")]
    public void StructElementCount_MatchesQpdf(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"corpus fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        // /Type on a structure element is OPTIONAL per spec (defaults to
        // /StructElem) -- measured directly: tagged_table_bad_elem.pdf in
        // this same corpus has 3 objects carrying /S + /P but only 1 with an
        // explicit /Type, so counting /Type would silently undercount on a
        // producer that omits it. /S (structure type) + /P (parent) are both
        // REQUIRED, so their joint presence is the robust independent signal.
        var expected = QpdfObjectCountWithKeys(path!, "/S", "/P");
        Assert.SkipWhen(expected < 0, "qpdf could not read the fixture");
        expected.Should().BeGreaterThan(0,
            "guard: the fixture must actually contain structure elements");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        var root = doc.GetStructureTree();
        // PdfStructTreeParser.ParseStructureTree returns the tree's single
        // real top-level element directly when there is exactly one (its
        // RawDictionary IS that element's own /S-bearing dictionary), or
        // synthesizes a wrapper /Document node (RawDictionary is the
        // StructTreeRoot dict itself, which never carries /S) when there are
        // several. tagged_nested.pdf is the single-real-element case: the
        // returned root corresponds to a genuine object qpdf also counts, so
        // excluding it (counting only root.Children) silently undercounted
        // by exactly one against qpdf's total -- caught by this fixture, not
        // assumed away by picking an easier one.
        var rootIsRealElement = root != null && root.RawDictionary.GetOptional("S") != null;
        var actual = root == null ? 0
            : (rootIsRealElement ? 1 : 0) + CountStructElements(root.Children);
        actual.Should().Be(expected,
            "excise's structure-tree walk must find the same element count qpdf's raw object " +
            "dump does -- a screen reader depends on this tree, and a silently truncated walk " +
            "would read as a page with less content than it has, not as an error");
    }

    [Theory]
    [InlineData("test-pdfs/pdfium/tagged_nested.pdf")]
    public void StructElementCount_SurvivesAnOpenSaveRoundTrip(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"corpus fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = QpdfObjectCountWithKeys(path!, "/S", "/P");
        Assert.SkipWhen(expected < 0, "qpdf could not read the fixture");
        expected.Should().BeGreaterThan(0, "guard: the fixture must contain structure elements");

        var output = Path.Combine(Path.GetTempPath(), $"excise-structelem-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
                doc.Save(output);

            var after = QpdfObjectCountWithKeys(output, "/S", "/P");
            after.Should().BeGreaterThanOrEqualTo(0, "qpdf must be able to read what excise wrote");
            after.Should().Be(expected,
                "the structure tree must survive an open-save round trip unchanged -- silently " +
                "dropping elements degrades accessibility with no visible symptom in the rendered " +
                "page");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RedactText_RemovesOutlineTitle_ConfirmedByQpdfNotExcisesOwnScanner()
    {
        // The existing #608 evidence for this (NonPageCarrierRedactionLeakTests)
        // asserts with SavedPdfLeakScanner -- excise's OWN carrier-agnostic byte
        // scanner. It is a real, useful check (generic, decompress-aware, not
        // page.Text), but it is still excise's code reading excise's output.
        // This asks qpdf to decode the saved file's object strings instead --
        // an independent reader, so a shared blind spot in excise's scanner
        // and excise's redactor can't both agree and both be wrong.
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        const string canary = "OUTLINEQPDFCANARY7Q2K";

        byte[] pdf;
        using (var doc = PdfDocument.Open(CreateMinimalPdf()))
        {
            doc.AddOutlineItem(canary, pageNumber: 1);
            pdf = doc.SaveToBytes();
        }

        var before = Path.Combine(Path.GetTempPath(), $"excise-outline-before-{Guid.NewGuid():N}.pdf");
        var after = Path.Combine(Path.GetTempPath(), $"excise-outline-after-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(before, pdf);
            QpdfContainsString(before, canary).Should().BeTrue(
                "guard: qpdf must independently confirm the canary is present before redaction, " +
                "or a clean result afterward would prove nothing");

            using (var doc = PdfDocument.Open(pdf))
            {
                doc.RedactText(canary);
                File.WriteAllBytes(after, doc.SaveToBytes());
            }

            QpdfContainsString(after, canary).Should().BeFalse(
                "the outline title must be gone from every object qpdf can decode -- not just " +
                "absent from excise's own page.Text or its own leak scanner's view of the file");
        }
        finally
        {
            try { File.Delete(before); } catch { /* best effort */ }
            try { File.Delete(after); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AddOutlineItem_ProducesAnOutlineQpdfIndependentlyReadsBack()
    {
        // AddOutlineItem_SurvivesASaveAndReload (PdfOutlineAuthoringTests)
        // proves excise's writer and excise's PdfOutlineParser agree with
        // each other -- the self-oracle shape called out for AcroForm parse
        // earlier this conversation. This asks qpdf, independently, whether
        // the authored item is reachable and carries the right title and
        // destination page.
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        const string title = "AUTHOREDOUTLINEQPDFCANARY";

        byte[] pdf;
        using (var doc = PdfDocument.Open(CreateTwoPagePdf()))
        {
            doc.AddOutlineItem(title, pageNumber: 2);
            pdf = doc.SaveToBytes();
        }

        var output = Path.Combine(Path.GetTempPath(), $"excise-authored-outline-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(output, pdf);
            QpdfContainsString(output, title).Should().BeTrue(
                "qpdf must independently find the authored outline item's title in the saved file");
            QpdfReachableOutlineCount(output).Should().Be(1,
                "qpdf's own /First-/Next walk from /Outlines must reach exactly the one item " +
                "excise authored -- not merely find the title string somewhere unreachable");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    private static byte[] CreateTwoPagePdf() =>
        Excise.Core.Authoring.PdfDocumentBuilder.Create()
            .Paragraph("Page one").PageBreak().Paragraph("Page two")
            .SaveToBytes();

    private static byte[] CreateMinimalPdf() =>
        Excise.Core.Authoring.PdfDocumentBuilder.Create().Paragraph("Body text").SaveToBytes();

    /// <summary>qpdf's JSON dump decodes every object's strings (including
    /// ones packed into a compressed /ObjStm), so a substring search over
    /// its output is a carrier-agnostic, independent presence check -- the
    /// same principle as SavedPdfLeakScanner, using a different reader.</summary>
    private static bool QpdfContainsString(string pdfPath, string term)
    {
        if (!QpdfJsonObjects(pdfPath, out var objects)) return false;
        foreach (var obj in objects.Values)
        {
            if (ContainsStringRecursive(obj, term)) return true;
        }
        return false;
    }

    private static bool ContainsStringRecursive(System.Text.Json.JsonElement element, string term)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                return (element.GetString() ?? "").Contains(term, StringComparison.Ordinal);
            case System.Text.Json.JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    if (ContainsStringRecursive(prop.Value, term)) return true;
                return false;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsStringRecursive(item, term)) return true;
                return false;
            default:
                return false;
        }
    }

    // ── recursive counters ──────────────────────────────────────────────────

    private static int CountOutlineItems(IReadOnlyList<PdfOutlineItem> items)
    {
        var total = 0;
        foreach (var item in items)
            total += 1 + CountOutlineItems(item.Children);
        return total;
    }

    private static int CountStructElements(IReadOnlyList<PdfStructElement> elements)
    {
        var total = 0;
        foreach (var element in elements)
            total += 1 + CountStructElements(element.Children);
        return total;
    }

    /// <summary>
    /// Independently reimplements the outline walk (root /Type /Outlines
    /// dict's /First, then /Next across siblings, recursing into each
    /// item's own /First for children) against qpdf's raw JSON object
    /// graph -- NOT by calling excise's own PdfOutlineParser. Deliberately a
    /// different, smaller implementation of the same standard traversal, so
    /// it can disagree with excise's parser instead of sharing its bugs.
    /// </summary>
    private static int QpdfReachableOutlineCount(string pdfPath)
    {
        if (!QpdfJsonObjects(pdfPath, out var objects)) return -1;
        System.Text.Json.JsonElement? root = null;
        foreach (var kv in objects)
        {
            if (kv.Value.TryGetProperty("/Type", out var t) && t.GetString() == "/Outlines")
            {
                root = kv.Value;
                break;
            }
        }
        if (root is null) return 0;
        if (!root.Value.TryGetProperty("/First", out var firstRef)) return 0;

        var visited = new HashSet<string>();
        return WalkOutlineChain(firstRef.GetString(), objects, visited);
    }

    private static int WalkOutlineChain(string? firstRefKey,
        Dictionary<string, System.Text.Json.JsonElement> objects, HashSet<string> visited)
    {
        var count = 0;
        var current = firstRefKey;
        while (current != null && visited.Add(current) && objects.TryGetValue(current, out var node))
        {
            count++;
            if (node.TryGetProperty("/First", out var childFirst))
                count += WalkOutlineChain(childFirst.GetString(), objects, visited);
            current = node.TryGetProperty("/Next", out var next) ? next.GetString() : null;
        }
        return count;
    }

    // ── qpdf JSON object-count oracle ───────────────────────────────────────

    private static int QpdfObjectCountWithTypeValue(string pdfPath, string typeValue) =>
        QpdfJsonObjects(pdfPath, out var objects)
            ? objects.Count(kv => kv.Value.TryGetProperty("/Type", out var t) &&
                                   t.GetString() == typeValue)
            : -1;

    private static int QpdfObjectCountWithKey(string pdfPath, string key) =>
        QpdfJsonObjects(pdfPath, out var objects)
            ? objects.Count(kv => kv.Value.TryGetProperty(key, out _))
            : -1;

    private static int QpdfObjectCountWithKeys(string pdfPath, params string[] keys) =>
        QpdfJsonObjects(pdfPath, out var objects)
            ? objects.Count(kv => keys.All(k => kv.Value.TryGetProperty(k, out _)))
            : -1;

    private static bool QpdfJsonObjects(string pdfPath,
        out Dictionary<string, System.Text.Json.JsonElement> objects)
    {
        objects = new Dictionary<string, System.Text.Json.JsonElement>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("qpdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--json=1");
            psi.ArgumentList.Add("--json-key=objects");
            psi.ArgumentList.Add(pdfPath);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return false; }
            if (stdout.Length == 0) return false;

            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("objects", out var objs)) return false;
            foreach (var prop in objs.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                    objects[prop.Name] = prop.Value.Clone();
            }
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static string? Resolve(string rel)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var c = Path.Combine(dir, rel);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
