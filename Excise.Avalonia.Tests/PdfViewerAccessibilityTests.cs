using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Headless;
using AwesomeAssertions;
using Excise.Avalonia.Automation;
using Excise.Avalonia.Controls;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Headless tests for the document-content accessibility surface (#631):
/// the <see cref="PdfViewerAutomationPeer"/> tree that makes the rendered
/// page — otherwise an opaque bitmap — readable by screen readers. Covers
/// the synthetic page-text child (extractable text in reading order, updated
/// on navigation/swap/rewrite) and the tagged-PDF <c>/Alt</c> description
/// children for figures.
///
/// Runs through <see cref="HeadlessUnitTestSession"/> from plain [Fact]s
/// (not [AvaloniaFact]) to keep clear of the Avalonia.Headless.XUnit
/// TestContext bug (#337).
/// </summary>
public class PdfViewerAccessibilityTests
{
    // ── harness ──────────────────────────────────────────────────────────

    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessTestApp).Assembly)
            .Dispatch(body, CancellationToken.None);

    private static Task<T> OnUiThread<T>(Func<Task<T>> body) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessTestApp).Assembly)
            .Dispatch(body, CancellationToken.None);

    /// <summary>
    /// Wait (bounded) for the viewer's asynchronous page-change pipeline to
    /// notify the automation tree, until the peer serves
    /// <paramref name="expectedCount"/> /Alt description children.
    /// </summary>
    private static async Task<List<AutomationPeer>> AltChildrenEventually(
        AutomationPeer peer, int expectedCount)
    {
        for (int i = 0; i < 200; i++)
        {
            var alts = AltTextChildren(peer);
            if (alts.Count == expectedCount)
                return alts;
            await Task.Delay(10);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        return AltTextChildren(peer);
    }

    private static (PdfViewerControl Viewer, PdfViewerAutomationPeer Peer) CreateViewerWithPeer(
        PdfDocument? document)
    {
        var viewer = new PdfViewerControl();
        if (document != null)
            viewer.Document = document;
        var peer = (PdfViewerAutomationPeer)ControlAutomationPeer.CreatePeerForElement(viewer);
        return (viewer, peer);
    }

    private static AutomationPeer PageTextChild(AutomationPeer peer) =>
        peer.GetChildren().First(c => c.GetClassName() == "PdfPageText");

    private static List<AutomationPeer> AltTextChildren(AutomationPeer peer) =>
        peer.GetChildren().Where(c => c.GetClassName() == "PdfFigureAltText").ToList();

    private static string Normalized(string? s) =>
        string.Join(" ", (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ── fixtures ─────────────────────────────────────────────────────────

    /// <summary>Untagged two-page document with distinct per-page text.</summary>
    private static PdfDocument TwoPageTextDoc() =>
        PdfDocument.Open(PdfDocumentBuilder.Create()
            .Paragraph("Alpha page one")
            .PageBreak()
            .Paragraph("Beta page two")
            .SaveToBytes());

    /// <summary>
    /// Two-page document with a structure tree containing one /Figure whose
    /// /Alt describes an image on <paramref name="altPage"/>. The /Pg entry
    /// is the page dictionary itself (the parser resolves both direct
    /// dictionaries and references).
    /// </summary>
    private static PdfDocument DocWithFigureAlt(int altPage)
    {
        var doc = TwoPageTextDoc();

        var figure = new PdfDictionary();
        figure["S"] = new PdfName("Figure");
        figure["Alt"] = new PdfString("Bar chart of quarterly revenue");
        figure["Pg"] = doc.GetPage(altPage).Dictionary;

        var root = new PdfDictionary();
        root["Type"] = new PdfName("StructTreeRoot");
        root["K"] = figure;
        doc.Catalog["StructTreeRoot"] = root;
        return doc;
    }

    /// <summary>
    /// A complete single-page tagged PDF written byte-for-byte, where the
    /// /Figure's /Pg is a true indirect reference (3 0 R) — the shape every
    /// real-world tagged PDF has, exercising page resolution through the
    /// document's object cache rather than direct-dictionary identity.
    /// </summary>
    private static byte[] RawTaggedPdfWithFigureAlt()
    {
        const string content = "BT /F1 12 Tf 72 720 Td (Gamma loaded fixture) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /StructTreeRoot /K 7 0 R >>",
            "<< /Type /StructElem /S /Figure /Alt (Official signature stamp) /Pg 3 0 R >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new long[objects.Length + 1];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = sb.Length; // ASCII-only, so char count == byte offset
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Length; i++)
            sb.Append($"{offsets[i]:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A builder-authored tagged two-page PDF (struct elements carry no /Pg
    /// of their own — their pages live on /MCR reference kids), round-tripped
    /// through save/load, with an /Alt injected onto page 1's paragraph
    /// element. Exercises the MCR-kid page-resolution path.
    /// </summary>
    private static PdfDocument TaggedDocWithAltViaMcr()
    {
        var doc = PdfDocument.Open(PdfDocumentBuilder.Create()
            .Tagged()
            .Paragraph("Tagged alpha")
            .PageBreak()
            .Paragraph("Tagged beta")
            .SaveToBytes());

        // Find the first /P struct element (page 1's paragraph) and give it
        // an /Alt. Walk raw dictionaries — the injected fixture must not
        // depend on the code under test.
        var pElem = FindFirstStructElement(doc, "P")
            ?? throw new InvalidOperationException("tagged fixture has no /P element");
        pElem["Alt"] = new PdfString("Injected paragraph description");
        return doc;
    }

    private static PdfDictionary? FindFirstStructElement(PdfDocument doc, string structType)
    {
        var rootObj = doc.Catalog.GetOptional("StructTreeRoot");
        if (rootObj == null) return null;
        var root = doc.Resolve(rootObj) as PdfDictionary;

        PdfDictionary? Walk(PdfObject? k)
        {
            if (k == null) return null;
            var resolved = doc.Resolve(k);
            if (resolved is PdfArray arr)
                return arr.Select(Walk).FirstOrDefault(d => d != null);
            if (resolved is PdfDictionary d)
            {
                if (d.GetNameOrNull("S") == structType) return d;
                return Walk(d.GetOptional("K"));
            }
            return null;
        }

        return Walk(root?.GetOptional("K"));
    }

    // ── page-text peer ───────────────────────────────────────────────────

    [Fact]
    public async Task ViewerPeer_IsDocumentRole_WithPageTextFirstChild()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(document: null);

            peer.GetAutomationControlType().Should().Be(AutomationControlType.Document);

            var children = peer.GetChildren();
            children.Should().NotBeEmpty("the page-text node must exist even with no document");
            var textChild = children[0];
            textChild.GetClassName().Should().Be("PdfPageText",
                "the document text must be the first thing a screen reader reaches");
            textChild.GetAutomationControlType().Should().Be(AutomationControlType.Text);
            textChild.IsContentElement().Should().BeTrue();
            textChild.IsControlElement().Should().BeTrue();
            (textChild.GetName() ?? string.Empty).Should().BeEmpty("no document is loaded");
            textChild.GetParent().Should().BeSameAs(peer, "an orphaned peer is invisible to AT");
            return true;
        });
    }

    [Fact]
    public async Task PageTextPeer_ExposesCurrentPageText()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(TwoPageTextDoc());

            Normalized(PageTextChild(peer).GetName())
                .Should().Contain("Alpha page one", "page 1 text must be screen-reader visible")
                .And.NotContain("Beta", "page 2 content must not bleed into page 1");
            return true;
        });
    }

    [Fact]
    public async Task PageTextPeer_NameFollowsPageNavigation()
    {
        await OnUiThread(() =>
        {
            var (viewer, peer) = CreateViewerWithPeer(TwoPageTextDoc());
            var textChild = PageTextChild(peer);

            viewer.CurrentPage = 2;

            Normalized(textChild.GetName())
                .Should().Contain("Beta page two", "navigation must re-target the accessible text")
                .And.NotContain("Alpha");
            return true;
        });
    }

    [Fact]
    public async Task PageTextPeer_NameFollowsDocumentSwap()
    {
        await OnUiThread(() =>
        {
            var (viewer, peer) = CreateViewerWithPeer(TwoPageTextDoc());
            var textChild = PageTextChild(peer);

            viewer.Document = PdfDocument.Open(RawTaggedPdfWithFigureAlt());

            Normalized(textChild.GetName())
                .Should().Contain("Gamma loaded fixture", "a document swap must swap the accessible text")
                .And.NotContain("Alpha");
            return true;
        });
    }

    [Fact]
    public async Task RenderVersionBump_RaisesNameChangeOnTextPeer()
    {
        await OnUiThread(() =>
        {
            var (viewer, peer) = CreateViewerWithPeer(TwoPageTextDoc());
            var textChild = PageTextChild(peer);

            bool nameChanged = false;
            textChild.PropertyChanged += (_, e) =>
            {
                if (e.Property == AutomationElementIdentifiers.NameProperty)
                    nameChanged = true;
            };

            // A RenderVersion bump is the content-rewrite signal (e.g. a
            // redaction was applied); OnRenderVersionChanged notifies the
            // automation tree synchronously.
            viewer.RenderVersion++;

            nameChanged.Should().BeTrue(
                "screen readers must be told to re-read after the page content is rewritten");
            return true;
        });
    }

    // ── /Alt description peers ───────────────────────────────────────────

    [Fact]
    public async Task UntaggedDocument_HasNoAltDescriptionChildren()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(TwoPageTextDoc());
            AltTextChildren(peer).Should().BeEmpty();
            return true;
        });
    }

    [Fact]
    public async Task FigureAltOnCurrentPage_IsExposedAsImageChild()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(DocWithFigureAlt(altPage: 1));

            var alts = AltTextChildren(peer);
            alts.Should().HaveCount(1);
            alts[0].GetName().Should().Be("Bar chart of quarterly revenue");
            alts[0].GetAutomationControlType().Should().Be(AutomationControlType.Image);
            alts[0].IsContentElement().Should().BeTrue();
            alts[0].GetParent().Should().BeSameAs(peer);
            return true;
        });
    }

    [Fact]
    public async Task FigureAltOnOtherPage_IsNotExposed_UntilNavigatedThere()
    {
        await OnUiThread(async () =>
        {
            var (viewer, peer) = CreateViewerWithPeer(DocWithFigureAlt(altPage: 2));

            AltTextChildren(peer).Should().BeEmpty("the figure lives on page 2, not page 1");

            // End-to-end through the production wiring: the page-change
            // pipeline must notify the automation tree and invalidate the
            // cached children on its own.
            viewer.CurrentPage = 2;

            var alts = await AltChildrenEventually(peer, expectedCount: 1);
            alts.Should().HaveCount(1, "navigating to the figure's page must expose its description");
            alts[0].GetName().Should().Be("Bar chart of quarterly revenue");
            return true;
        });
    }

    [Fact]
    public async Task PageNavigation_RaisesChildrenChanged_WhenAltSetChanges()
    {
        await OnUiThread(() =>
        {
            var (viewer, peer) = CreateViewerWithPeer(DocWithFigureAlt(altPage: 2));
            _ = peer.GetChildren(); // materialize the synthetic children

            bool childrenChanged = false;
            peer.ChildrenChanged += (_, _) => childrenChanged = true;

            viewer.CurrentPage = 2;
            peer.NotifyPageTextChanged();

            childrenChanged.Should().BeTrue(
                "AT must be told the accessible children changed when descriptions appear");
            AltTextChildren(peer).Should().HaveCount(1);
            return true;
        });
    }

    [Fact]
    public async Task FigureAlt_FromLoadedPdf_ResolvesPageThroughIndirectReference()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(PdfDocument.Open(RawTaggedPdfWithFigureAlt()));

            Normalized(PageTextChild(peer).GetName()).Should().Contain("Gamma loaded fixture");
            var alts = AltTextChildren(peer);
            alts.Should().HaveCount(1, "/Pg given as an indirect reference must resolve to the page");
            alts[0].GetName().Should().Be("Official signature stamp");
            return true;
        });
    }

    [Fact]
    public async Task StructElementWithoutOwnPg_ResolvesPageThroughMcrKid()
    {
        await OnUiThread(async () =>
        {
            var (viewer, peer) = CreateViewerWithPeer(TaggedDocWithAltViaMcr());

            var alts = AltTextChildren(peer);
            alts.Should().HaveCount(1,
                "builder-authored elements carry /Pg only on their /MCR kids");
            alts[0].GetName().Should().Be("Injected paragraph description");

            viewer.CurrentPage = 2;
            (await AltChildrenEventually(peer, expectedCount: 0))
                .Should().BeEmpty("the described element belongs to page 1");
            return true;
        });
    }
}
