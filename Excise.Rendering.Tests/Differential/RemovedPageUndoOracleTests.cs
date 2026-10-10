using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Back = Excise.TestSupport.RemovedPageBackReference;
using F = Excise.TestSupport.RemovedPageFixtures;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2058: Remove Page, undo (re-insert the captured page), save. Everything that
/// pointed at the page must still point at page 2 in the saved file, read by
/// tools that are not excise: qpdf's JSON v2 (<c>pages</c>, <c>outlines</c>,
/// <c>acroform</c> and the object table) and mutool's text of page 2. Before the
/// fix the undo inserted a copy and the #2012 pre-save cut nulled the bookmark,
/// link, destination and open action, dropped the page's structure elements and
/// took its form field out of <c>/Fields</c>.
/// </summary>
public sealed class RemovedPageUndoOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"removed-page-undo-{Guid.NewGuid():N}");

    public RemovedPageUndoOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static TheoryData<Back> BackReferences() => new(Enum.GetValues<Back>().Where(b => b != Back.None));

    [Theory]
    [MemberData(nameof(BackReferences))]
    public void RemovePage_Undo_Save_QpdfResolvesEveryReferenceToPageTwo(Back back)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(back, "1.7")))
        {
            var captured = doc.GetPage(2);
            doc.Pages.RemoveAt(1);
            doc.Pages.Insert(1, captured);
            saved = doc.SaveToBytes();
        }

        var file = Write(saved);
        var check = QpdfReferenceTool.Check(file);
        check!.Value.Success.Should().BeTrue($"{back}: qpdf --check must pass: {check.Value.Output}");
        check.Value.Output.Should().NotContain("WARNING", $"{back}: qpdf --check must be clean");
        QpdfReferenceTool.PageCount(file).Should().Be(3, back.ToString());

        using var json = QpdfJson(file);
        var root = json.RootElement;
        var objects = root.GetProperty("qpdf")[1];
        var pageTwo = root.GetProperty("pages")[1].GetProperty("object").GetString();
        JsonElement Resolve(JsonElement value) =>
            value.ValueKind == JsonValueKind.String && value.GetString()!.EndsWith(" R", StringComparison.Ordinal)
                ? objects.GetProperty("obj:" + value.GetString()).GetProperty("value")
                : value;
        var catalog = Resolve(objects.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));

        switch (back)
        {
            case Back.Outline:
                root.GetProperty("outlines")[0].GetProperty("destpageposfrom1").GetInt32()
                    .Should().Be(2, "the bookmark goes to page 2");
                break;
            case Back.NamedDestination:
                var names = Resolve(Resolve(Resolve(catalog.GetProperty("/Names")).GetProperty("/Dests")).GetProperty("/Names"));
                Resolve(names[1])[0].GetString().Should().Be(pageTwo, "the named destination names page 2");
                break;
            case Back.LinkOnKeptPage:
                var pageOne = Resolve(root.GetProperty("pages")[0].GetProperty("object"));
                var link = Resolve(Resolve(pageOne.GetProperty("/Annots"))[0]);
                Resolve(link.GetProperty("/Dest"))[0].GetString().Should().Be(pageTwo, "the link goes to page 2");
                break;
            case Back.OpenAction:
                Resolve(catalog.GetProperty("/OpenAction"))[0].GetString().Should().Be(pageTwo, "the open action goes to page 2");
                break;
            case Back.StructureElement:
                var structRoot = Resolve(catalog.GetProperty("/StructTreeRoot"));
                var kids = Resolve(structRoot.GetProperty("/K")).EnumerateArray().ToList();
                kids.Should().HaveCount(3, "every page's element is still in the tree");
                var onTwo = kids.Where(k => Resolve(k).TryGetProperty("/Pg", out var pg) && pg.GetString() == pageTwo).ToList();
                onTwo.Should().ContainSingle("page 2's element names page 2 in /Pg");
                Resolve(onTwo[0]).GetProperty("/ActualText").GetString().Should().Contain(F.StructActualText);
                objects.GetProperty("obj:" + pageTwo).GetProperty("value").GetProperty("/StructParents").GetInt32().Should().Be(1);
                var nums = Resolve(Resolve(structRoot.GetProperty("/ParentTree")).GetProperty("/Nums"));
                var entry = Enumerable.Range(0, nums.GetArrayLength() / 2)
                    .Single(i => nums[2 * i].GetInt32() == 1);
                Resolve(nums[2 * entry + 1])[0].GetString().Should().Be(onTwo[0].GetString(), "/ParentTree 1 names page 2's element");
                break;
            case Back.AcroFormWidget:
            case Back.AcroFormFieldKids:
                var widgets = QpdfReferenceTool.AcroFormWidgets(file);
                widgets.Should().NotBeNull();
                widgets!.Should().ContainSingle("the field is still in the form");
                widgets[0].Value.Should().Be(F.FieldValue, "the field keeps its value");
                widgets[0].Page.Should().Be(2, "the field's widget is on page 2");
                break;
        }

        if (MutoolReferenceRenderer.IsAvailable)
        {
            // mutool reads tagged content through its element's /ActualText.
            var expected = back == Back.StructureElement ? F.StructActualText : F.PageText;
            string.Concat((MutoolTextExtractor.ExtractPage(file, 2) ?? "").Where(c => !char.IsWhiteSpace(c)))
                .Should().Contain(expected, "mutool reads the restored page 2");
        }
    }

    [Theory]
    [MemberData(nameof(BackReferences))]
    public void RemovePage_Undo_Redo_Save_RemovedPageIsNotInTheFile(Back back)
    {
        // The #2012 cut must still run once the page is removed again.
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(back, "1.7")))
        {
            var captured = doc.GetPage(2);
            doc.Pages.RemoveAt(1);
            doc.Pages.Insert(1, captured);
            doc.Pages.RemoveAt(1);
            saved = doc.SaveToBytes();
        }

        var dump = CarrierTrapIndependentCorroborationTests.QpdfDump(Write(saved));
        foreach (var token in F.RemovedTokens(back))
        {
            SavedPdfLeakScanner.FindTerm(saved, token).Should().BeEmpty($"{back}: {token} must not be in the saved bytes");
            dump.Should().NotContain(token, $"{back}: qpdf's dump must not hold {token}");
        }
        QpdfReferenceTool.PageCount(Write(saved)).Should().Be(2);
    }

    private JsonDocument QpdfJson(string file)
    {
        var bytes = CarrierTrapIndependentCorroborationTests.RunTool(
            "qpdf", "--json=2", "--json-key=pages", "--json-key=outlines", "--json-key=qpdf", file)
            ?? throw new InvalidOperationException("qpdf --json failed on " + file);
        return JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
    }

    private string Write(byte[] bytes)
    {
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(file, bytes);
        return file;
    }
}
