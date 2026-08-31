using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Parse + round-trip tests for document/page-level actions (issue #331):
/// /Catalog/OpenAction, /Catalog/AA, a page's /AA, and the /Catalog/Names/JavaScript
/// name tree. excise never executes any parsed action — these tests assert the
/// model is correct and that JavaScript actions are captured as inert data
/// (<see cref="PdfAction.JavaScriptSource"/>), never run.
/// </summary>
public class PdfActionTests
{
    // ─── /Catalog/OpenAction ────────────────────────────────────────────────

    [Fact]
    public void OpenAction_Absent_ReturnsNull()
    {
        var pdf = BuildPdf(catalogExtra: null, pageCount: 1);
        using var doc = PdfDocument.Open(pdf);

        doc.OpenAction.Should().BeNull();
    }

    [Fact]
    public void OpenAction_GoToActionDictionary_ResolvesDestinationPage()
    {
        // 2-page doc; extra object 5 (3 + pageCount(2)) is a GoTo action targeting page 2 (object 4).
        var pdf = BuildPdf(
            catalogExtra: "/OpenAction 5 0 R",
            pageCount: 2,
            extras: new ExtraObj[] { new DictObj("<< /S /GoTo /D [4 0 R /Fit] >>") });
        using var doc = PdfDocument.Open(pdf);

        var action = doc.OpenAction;
        action.Should().NotBeNull();
        action!.Type.Should().Be("GoTo");
        action.DestinationPage.Should().Be(2);
        action.IsJavaScript.Should().BeFalse();
    }

    [Fact]
    public void OpenAction_LegacyBareDestinationArray_ResolvesAsImplicitGoTo()
    {
        // Old-style /OpenAction is sometimes a raw destination array, not an action dict.
        var pdf = BuildPdf(catalogExtra: "/OpenAction [3 0 R /Fit]", pageCount: 1);
        using var doc = PdfDocument.Open(pdf);

        var action = doc.OpenAction;
        action.Should().NotBeNull();
        action!.Type.Should().Be("GoTo");
        action.DestinationPage.Should().Be(1);
    }

    [Fact]
    public void OpenAction_JavaScriptAsTextString_DecodesSourceButNeverRuns()
    {
        var pdf = BuildPdf(
            catalogExtra: "/OpenAction 4 0 R",
            pageCount: 1,
            extras: new ExtraObj[] { new DictObj("<< /S /JavaScript /JS (app.alert\\('hi'\\);) >>") });
        using var doc = PdfDocument.Open(pdf);

        var action = doc.OpenAction;
        action.Should().NotBeNull();
        action!.IsJavaScript.Should().BeTrue();
        action.JavaScriptSource.Should().Be("app.alert('hi');");
        // No execution surface exists anywhere in excise for this string — modeling
        // it as data is the entire contract (issue #331's explicit non-goal).
    }

    [Fact]
    public void OpenAction_JavaScriptAsStream_DecodesSourceFromStreamBytes()
    {
        var jsBytes = Encoding.ASCII.GetBytes("var x = 1;\napp.alert(x);");
        var pdf = BuildPdf(
            catalogExtra: "/OpenAction 4 0 R",
            pageCount: 1,
            extras: new ExtraObj[]
            {
                new DictObj("<< /S /JavaScript /JS 5 0 R >>"),
                new StreamObj("<< /Type /Action", jsBytes),
            });
        using var doc = PdfDocument.Open(pdf);

        var action = doc.OpenAction;
        action.Should().NotBeNull();
        action!.IsJavaScript.Should().BeTrue();
        action.JavaScriptSource.Should().Be("var x = 1;\napp.alert(x);");
    }

    [Fact]
    public void OpenAction_NamedAction_CapturesName()
    {
        var pdf = BuildPdf(
            catalogExtra: "/OpenAction 4 0 R",
            pageCount: 1,
            extras: new ExtraObj[] { new DictObj("<< /S /Named /N /LastPage >>") });
        using var doc = PdfDocument.Open(pdf);

        var action = doc.OpenAction;
        action!.Type.Should().Be("Named");
        action.NamedActionName.Should().Be("LastPage");
    }

    [Fact]
    public void OpenAction_UriAction_CapturesUri()
    {
        var pdf = BuildPdf(
            catalogExtra: "/OpenAction 4 0 R",
            pageCount: 1,
            extras: new ExtraObj[] { new DictObj("<< /S /URI /URI (https://example.com/) >>") });
        using var doc = PdfDocument.Open(pdf);

        var action = doc.OpenAction;
        action!.Type.Should().Be("URI");
        action.Uri.Should().Be("https://example.com/");
    }

    [Fact]
    public void OpenAction_DangerousActionTypes_AreStillModeled_JustNeverExecuted()
    {
        // GoToR/GoToE/Launch are the same "leaves the document" family PdfLink
        // refuses to navigate to (#625). Here they must still parse into a
        // PdfAction (round-trip requires it); only navigation is refused elsewhere.
        var pdf = BuildPdf(
            catalogExtra: "/OpenAction 4 0 R",
            pageCount: 1,
            extras: new ExtraObj[] { new DictObj("<< /S /Launch /F (evil.exe) >>") });
        using var doc = PdfDocument.Open(pdf);

        var action = doc.OpenAction;
        action!.Type.Should().Be("Launch");
    }

    [Fact]
    public void OpenAction_NextChain_ParsesFollowOnActions()
    {
        // OpenAction is a GoTo (object 4) whose /Next is a URI action (object 5).
        var pdf = BuildPdf(
            catalogExtra: "/OpenAction 4 0 R",
            pageCount: 1,
            extras: new ExtraObj[]
            {
                new DictObj("<< /S /GoTo /D [3 0 R /Fit] /Next 5 0 R >>"),
                new DictObj("<< /S /URI /URI (https://example.com/next) >>"),
            });
        using var doc = PdfDocument.Open(pdf);

        var action = doc.OpenAction!;
        action.Type.Should().Be("GoTo");
        action.NextActions.Should().HaveCount(1);
        action.NextActions[0].Type.Should().Be("URI");
        action.NextActions[0].Uri.Should().Be("https://example.com/next");
    }

    // ─── /Catalog/AA (document-level additional actions) ───────────────────

    [Fact]
    public void DocumentAdditionalActions_Absent_ReturnsEmpty()
    {
        var pdf = BuildPdf(catalogExtra: null, pageCount: 1);
        using var doc = PdfDocument.Open(pdf);

        doc.AdditionalActions.Should().BeEmpty();
    }

    [Fact]
    public void DocumentAdditionalActions_WcAndWs_ParsedByTriggerKey()
    {
        var pdf = BuildPdf(
            catalogExtra: "/AA << /WC 4 0 R /WS 5 0 R >>",
            pageCount: 1,
            extras: new ExtraObj[]
            {
                new DictObj("<< /S /JavaScript /JS (beforeClose\\(\\);) >>"),
                new DictObj("<< /S /URI /URI (https://example.com/save) >>"),
            });
        using var doc = PdfDocument.Open(pdf);

        doc.AdditionalActions.Should().HaveCount(2);
        doc.AdditionalActions["WC"].IsJavaScript.Should().BeTrue();
        doc.AdditionalActions["WC"].JavaScriptSource.Should().Be("beforeClose();");
        doc.AdditionalActions["WS"].Type.Should().Be("URI");
    }

    // ─── page /AA ────────────────────────────────────────────────────────

    [Fact]
    public void PageAdditionalActions_Absent_ReturnsEmpty()
    {
        var pdf = BuildPdf(catalogExtra: null, pageCount: 1);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).AdditionalActions.Should().BeEmpty();
    }

    [Fact]
    public void PageAdditionalActions_OAndC_ParsedByTriggerKey()
    {
        // Page /AA lives directly on the page dict, not built by BuildPdf's generic
        // page stamp, so this test hand-rolls a minimal fixture.
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                       "/AA << /O 4 0 R /C 5 0 R >> >>");
        sb.AppendLine("endobj");

        long actOPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /S /Named /N /NextPage >>");
        sb.AppendLine("endobj");

        long actCPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /S /URI /URI (https://example.com/closed) >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{actOPos:D10} 00000 n ");
        sb.AppendLine($"{actCPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        using var doc = PdfDocument.Open(Encoding.ASCII.GetBytes(sb.ToString()));

        var aa = doc.GetPage(1).AdditionalActions;
        aa.Should().HaveCount(2);
        aa["O"].Type.Should().Be("Named");
        aa["O"].NamedActionName.Should().Be("NextPage");
        aa["C"].Type.Should().Be("URI");
        aa["C"].Uri.Should().Be("https://example.com/closed");
    }

    // ─── /Catalog/Names/JavaScript ──────────────────────────────────────────

    [Fact]
    public void DocumentJavaScriptActions_Absent_ReturnsEmpty()
    {
        var pdf = BuildPdf(catalogExtra: null, pageCount: 1);
        using var doc = PdfDocument.Open(pdf);

        doc.DocumentJavaScriptActions.Should().BeEmpty();
    }

    [Fact]
    public void DocumentJavaScriptActions_NameTreeLeaf_ParsesEachEntry()
    {
        var pdf = BuildPdf(
            catalogExtra: "/Names << /JavaScript 4 0 R >>",
            pageCount: 1,
            extras: new ExtraObj[]
            {
                new DictObj("<< /Names [(Init) 5 0 R (Utils) 6 0 R] >>"),
                new DictObj("<< /S /JavaScript /JS (function init\\(\\) {}) >>"),
                new DictObj("<< /S /JavaScript /JS (function util\\(\\) {}) >>"),
            });
        using var doc = PdfDocument.Open(pdf);

        var scripts = doc.DocumentJavaScriptActions;
        scripts.Should().HaveCount(2);
        scripts["Init"].JavaScriptSource.Should().Be("function init() {}");
        scripts["Utils"].JavaScriptSource.Should().Be("function util() {}");
    }

    [Fact]
    public void DocumentJavaScriptActions_KidsSubtree_WalksAllBranches()
    {
        // Root has /Kids pointing at two leaf subtrees (mirrors the page-labels
        // Kids-recursion coverage in PdfPageLabelTests).
        var pdf = BuildPdf(
            catalogExtra: "/Names << /JavaScript 4 0 R >>",
            pageCount: 1,
            extras: new ExtraObj[]
            {
                new DictObj("<< /Kids [5 0 R 6 0 R] >>"),
                new DictObj("<< /Names [(A) 7 0 R] >>"),
                new DictObj("<< /Names [(B) 8 0 R] >>"),
                new DictObj("<< /S /JavaScript /JS (scriptA\\(\\);) >>"),
                new DictObj("<< /S /JavaScript /JS (scriptB\\(\\);) >>"),
            });
        using var doc = PdfDocument.Open(pdf);

        var scripts = doc.DocumentJavaScriptActions;
        scripts.Should().HaveCount(2);
        scripts["A"].JavaScriptSource.Should().Be("scriptA();");
        scripts["B"].JavaScriptSource.Should().Be("scriptB();");
    }

    [Fact]
    public void Sanitizer_AfterActionCachesAreMaterialized_RebuildsScrubbedViews()
    {
        const string secret = "SecretMarker";
        var pdf = BuildPdf(
            catalogExtra: "/OpenAction 4 0 R /AA << /WS 5 0 R >> /Names << /JavaScript 6 0 R >>",
            pageCount: 1,
            extras: new ExtraObj[]
            {
                new DictObj($"<< /S /JavaScript /JS ({secret}\\(\\);) >>"),
                new DictObj($"<< /S /URI /URI (https://example.com/{secret}) >>"),
                new DictObj("<< /Names [(Boot) 7 0 R] >>"),
                new DictObj($"<< /S /JavaScript /JS ({secret}Boot\\(\\);) >>"),
            });
        using var doc = PdfDocument.Open(pdf);
        var openBefore = doc.OpenAction;
        var additionalBefore = doc.AdditionalActions;
        var scriptsBefore = doc.DocumentJavaScriptActions;

        PdfDocumentSanitizer.ScrubTerms(
            doc,
            new[] { secret },
            caseSensitive: true,
            RedactionCarriers.JavaScript | RedactionCarriers.ActionUris).Should().BeTrue();

        doc.OpenAction.Should().NotBeSameAs(openBefore);
        doc.OpenAction!.JavaScriptSource.Should().Be("();");
        doc.AdditionalActions.Should().NotBeSameAs(additionalBefore);
        doc.AdditionalActions["WS"].Uri.Should().Be("https://example.com/");
        doc.DocumentJavaScriptActions.Should().NotBeSameAs(scriptsBefore);
        doc.DocumentJavaScriptActions["Boot"].JavaScriptSource.Should().Be("Boot();");
    }

    // ─── Round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void Actions_RoundTrip_SurviveSaveAndReopen()
    {
        var pdf = BuildPdf(
            catalogExtra: "/OpenAction 4 0 R /AA << /WS 5 0 R >> /Names << /JavaScript 6 0 R >>",
            pageCount: 1,
            extras: new ExtraObj[]
            {
                new DictObj("<< /S /GoTo /D [3 0 R /Fit] >>"),
                new DictObj("<< /S /JavaScript /JS (onSave\\(\\);) >>"),
                new DictObj("<< /Names [(Boot) 7 0 R] >>"),
                new DictObj("<< /S /JavaScript /JS (boot\\(\\);) >>"),
            });
        using var doc = PdfDocument.Open(pdf);

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);

        reopened.OpenAction!.Type.Should().Be("GoTo");
        reopened.OpenAction!.DestinationPage.Should().Be(1);
        reopened.AdditionalActions["WS"].JavaScriptSource.Should().Be("onSave();");
        reopened.DocumentJavaScriptActions["Boot"].JavaScriptSource.Should().Be("boot();");
    }

    [Fact]
    public void Actions_RoundTrip_DoNotAppearOnPlainDocument()
    {
        var pdf = BuildPdf(catalogExtra: null, pageCount: 1);
        using var doc = PdfDocument.Open(pdf);

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);

        reopened.OpenAction.Should().BeNull();
        reopened.AdditionalActions.Should().BeEmpty();
        reopened.DocumentJavaScriptActions.Should().BeEmpty();
        reopened.GetPage(1).AdditionalActions.Should().BeEmpty();
    }

    // ─── Helper: flexible PDF builder ───────────────────────────────────────

    private abstract record ExtraObj;
    private sealed record DictObj(string Body) : ExtraObj;
    private sealed record StreamObj(string DictPrefixNoClosingAngle, byte[] Data) : ExtraObj;

    /// <summary>
    /// Build a minimal PDF: object 1 = catalog (with optional extra dict entries),
    /// object 2 = page tree, objects 3..(2+pageCount) = plain pages, then any
    /// <paramref name="extras"/> numbered sequentially from (3+pageCount).
    /// References between extras use plain forward "N 0 R" object numbers —
    /// PDF resolves by number via xref, so declaration order never matters.
    /// </summary>
    private static byte[] BuildPdf(string? catalogExtra, int pageCount, IReadOnlyList<ExtraObj>? extras = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        var offsets = new List<long> { 0 }; // index 0 = free entry placeholder

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine($"<< /Type /Catalog /Pages 2 0 R{(catalogExtra != null ? " " + catalogExtra : "")} >>");
        sb.AppendLine("endobj");
        offsets.Add(catalogPos);

        var kids = string.Join(" ", System.Linq.Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"));
        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        sb.AppendLine("endobj");
        offsets.Add(pagesPos);

        for (int i = 0; i < pageCount; i++)
        {
            long pos = sb.Length;
            sb.AppendLine($"{3 + i} 0 obj");
            sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
            sb.AppendLine("endobj");
            offsets.Add(pos);
        }

        int nextNum = 3 + pageCount;
        if (extras != null)
        {
            foreach (var extra in extras)
            {
                long pos = sb.Length;
                sb.AppendLine($"{nextNum} 0 obj");
                switch (extra)
                {
                    case DictObj d:
                        sb.AppendLine(d.Body);
                        break;
                    case StreamObj s:
                        sb.AppendLine($"{s.DictPrefixNoClosingAngle} /Length {s.Data.Length} >>");
                        sb.AppendLine("stream");
                        sb.Append(Encoding.ASCII.GetString(s.Data));
                        sb.AppendLine();
                        sb.AppendLine("endstream");
                        break;
                }
                sb.AppendLine("endobj");
                offsets.Add(pos);
                nextNum++;
            }
        }

        long xrefPos = sb.Length;
        int size = nextNum;
        sb.AppendLine("xref");
        sb.AppendLine($"0 {size}");
        sb.AppendLine("0000000000 65535 f ");
        for (int i = 1; i < size; i++)
            sb.AppendLine($"{offsets[i]:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine($"<< /Size {size} /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
