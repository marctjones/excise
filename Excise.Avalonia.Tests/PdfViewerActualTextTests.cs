using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation.Peers;
using AwesomeAssertions;
using Excise.Avalonia.Automation;
using Excise.Avalonia.Controls;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Headless tests for the tagged-PDF <c>/ActualText</c> accessibility surface
/// (#631): structure elements carrying replacement text (ISO 32000-2 §14.9.4
/// — what a span really says when its glyphs extract wrong) are exposed as
/// synthetic children of <see cref="PdfViewerAutomationPeer"/>, deduplicated
/// against the page-text child so a screen reader never hears the same
/// content twice.
///
/// Runs through <see cref="HeadlessSessionGuard"/> from plain [Fact]s
/// (not [AvaloniaFact]) for the same reasons as
/// <see cref="PdfViewerAccessibilityTests"/> (#337, #752).
/// </summary>
public class PdfViewerActualTextTests
{
    // ── harness (same shape as PdfViewerAccessibilityTests) ──────────────

    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    private static Task<T> OnUiThread<T>(Func<Task<T>> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    private static (PdfViewerControl Viewer, PdfViewerAutomationPeer Peer) CreateViewerWithPeer(
        PdfDocument? document)
    {
        var viewer = new PdfViewerControl();
        if (document != null)
            viewer.Document = document;
        var peer = (PdfViewerAutomationPeer)ControlAutomationPeer.CreatePeerForElement(viewer);
        return (viewer, peer);
    }

    private static List<AutomationPeer> ActualTextChildren(AutomationPeer peer) =>
        peer.GetChildren().Where(c => c.GetClassName() == "PdfActualText").ToList();

    /// <summary>
    /// Wait (bounded) for the viewer's asynchronous page-change pipeline to
    /// notify the automation tree, until the peer serves
    /// <paramref name="expectedCount"/> /ActualText children.
    /// </summary>
    private static async Task<List<AutomationPeer>> ActualTextChildrenEventually(
        AutomationPeer peer, int expectedCount)
    {
        for (int i = 0; i < 200; i++)
        {
            var actuals = ActualTextChildren(peer);
            if (actuals.Count == expectedCount)
                return actuals;
            await Task.Delay(10);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        return ActualTextChildren(peer);
    }

    // ── fixtures ─────────────────────────────────────────────────────────

    /// <summary>Untagged two-page document with distinct per-page text.</summary>
    private static PdfDocument TwoPageTextDoc(
        string pageOne = "Alpha page one", string pageTwo = "Beta page two") =>
        PdfDocument.Open(PdfDocumentBuilder.Create()
            .Paragraph(pageOne)
            .PageBreak()
            .Paragraph(pageTwo)
            .SaveToBytes());

    /// <summary>
    /// Attach a structure tree whose kids are the given (structType,
    /// ActualText-or-Alt, page) leaves, each /Pg-anchored to its page.
    /// </summary>
    private static PdfDocument WithStructLeaves(
        PdfDocument doc, params (string Type, string Key, string Text, int Page)[] leaves)
    {
        var kids = new PdfArray();
        foreach (var (type, key, text, page) in leaves)
        {
            var elem = new PdfDictionary();
            elem["S"] = new PdfName(type);
            elem[key] = new PdfString(text);
            elem["Pg"] = doc.GetPage(page).Dictionary;
            kids.Add(elem);
        }

        var root = new PdfDictionary();
        root["Type"] = new PdfName("StructTreeRoot");
        root["K"] = kids;
        doc.Catalog["StructTreeRoot"] = root;
        return doc;
    }

    private static PdfDocument DocWithSpanActualText(string actualText, int page) =>
        WithStructLeaves(TwoPageTextDoc(), ("Span", "ActualText", actualText, page));

    // ── exposure ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UntaggedDocument_HasNoActualTextChildren()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(TwoPageTextDoc());
            ActualTextChildren(peer).Should().BeEmpty();
            return true;
        });
    }

    [Fact]
    public async Task ActualTextNotInExtractedText_IsExposedAsTextChild()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(
                DocWithSpanActualText("Bunsen-Kirchhoff prize citation", page: 1));

            var actuals = ActualTextChildren(peer);
            actuals.Should().HaveCount(1,
                "replacement text the glyph layer does not carry must be announced");
            actuals[0].GetName().Should().Be("Bunsen-Kirchhoff prize citation");
            actuals[0].GetAutomationControlType().Should().Be(AutomationControlType.Text);
            actuals[0].IsContentElement().Should().BeTrue();
            actuals[0].IsControlElement().Should().BeTrue();
            actuals[0].GetParent().Should().BeSameAs(peer, "an orphaned peer is invisible to AT");
            return true;
        });
    }

    [Fact]
    public async Task HyphenatedGlyphs_ActualTextRejoin_IsExposed()
    {
        await OnUiThread(() =>
        {
            // The canonical /ActualText case: the line breaks as "Cross- word",
            // the author states the real word. Extraction reads the hyphenated
            // glyphs, so the rejoined form is not contained in the page text
            // and must be announced.
            var doc = WithStructLeaves(
                TwoPageTextDoc(pageOne: "Cross- word puzzle"),
                ("Span", "ActualText", "Crossword", 1));
            var (_, peer) = CreateViewerWithPeer(doc);

            var actuals = ActualTextChildren(peer);
            actuals.Should().HaveCount(1);
            actuals[0].GetName().Should().Be("Crossword");
            return true;
        });
    }

    // ── dedup vs the page-text child ─────────────────────────────────────

    [Fact]
    public async Task ActualTextAlreadyInExtractedText_IsSuppressed()
    {
        await OnUiThread(() =>
        {
            var (viewer, peer) = CreateViewerWithPeer(
                DocWithSpanActualText("Alpha page", page: 1));

            viewer.GetAccessiblePageText().Should().Contain("Alpha page",
                "precondition: the glyph layer already carries this text");
            ActualTextChildren(peer).Should().BeEmpty(
                "content the page-text child already reads must not be announced twice");
            return true;
        });
    }

    [Fact]
    public async Task DedupIsWhitespaceNormalized()
    {
        await OnUiThread(() =>
        {
            // Same words, different whitespace — still the same spoken content.
            var (_, peer) = CreateViewerWithPeer(
                DocWithSpanActualText("Alpha\n  page   one", page: 1));

            ActualTextChildren(peer).Should().BeEmpty(
                "whitespace differences alone must not defeat the dedup");
            return true;
        });
    }

    [Fact]
    public async Task WhitespaceOnlyActualText_IsIgnored()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(DocWithSpanActualText("   ", page: 1));
            ActualTextChildren(peer).Should().BeEmpty();
            return true;
        });
    }

    // ── page association and change notification ─────────────────────────

    [Fact]
    public async Task ActualTextOnOtherPage_IsNotExposed_UntilNavigatedThere()
    {
        await OnUiThread(async () =>
        {
            var (viewer, peer) = CreateViewerWithPeer(
                DocWithSpanActualText("Second-page replacement span", page: 2));

            ActualTextChildren(peer).Should().BeEmpty("the span belongs to page 2, not page 1");

            // End-to-end through the production wiring: the page-change
            // pipeline must notify the automation tree and invalidate the
            // cached children on its own.
            viewer.CurrentPage = 2;

            var actuals = await ActualTextChildrenEventually(peer, expectedCount: 1);
            actuals.Should().HaveCount(1,
                "navigating to the span's page must expose its replacement text");
            actuals[0].GetName().Should().Be("Second-page replacement span");
            return true;
        });
    }

    [Fact]
    public async Task PageNavigation_RaisesChildrenChanged_WhenActualTextSetChanges()
    {
        await OnUiThread(() =>
        {
            var (viewer, peer) = CreateViewerWithPeer(
                DocWithSpanActualText("Second-page replacement span", page: 2));
            _ = peer.GetChildren(); // materialize the synthetic children

            bool childrenChanged = false;
            peer.ChildrenChanged += (_, _) => childrenChanged = true;

            viewer.CurrentPage = 2;
            peer.NotifyPageTextChanged();

            childrenChanged.Should().BeTrue(
                "AT must be told the accessible children changed when replacements appear");
            ActualTextChildren(peer).Should().HaveCount(1);
            return true;
        });
    }

    // ── tree shape ───────────────────────────────────────────────────────

    [Fact]
    public async Task ChildOrder_IsPageText_ThenAlt_ThenActualText()
    {
        await OnUiThread(() =>
        {
            var doc = WithStructLeaves(
                TwoPageTextDoc(),
                ("Figure", "Alt", "Chart of results", 1),
                ("Span", "ActualText", "Ligature-corrected span", 1));
            var (_, peer) = CreateViewerWithPeer(doc);

            var classes = peer.GetChildren().Select(c => c.GetClassName()).ToList();
            classes.IndexOf("PdfPageText").Should().Be(0,
                "the document text stays the first thing a screen reader reaches");
            classes.IndexOf("PdfFigureAltText").Should().BePositive();
            classes.IndexOf("PdfActualText").Should()
                .BeGreaterThan(classes.IndexOf("PdfFigureAltText"),
                    "replacement spans follow the figure descriptions");
            return true;
        });
    }

    [Fact]
    public async Task MultipleActualTexts_KeepStructureTreeOrder_WithStableIds()
    {
        await OnUiThread(() =>
        {
            var doc = WithStructLeaves(
                TwoPageTextDoc(),
                ("Span", "ActualText", "First correction", 1),
                ("Span", "ActualText", "Second correction", 1));
            var (_, peer) = CreateViewerWithPeer(doc);

            var actuals = ActualTextChildren(peer);
            actuals.Select(a => a.GetName()).Should()
                .ContainInOrder("First correction", "Second correction");
            actuals.Select(a => a.GetAutomationId()).Should()
                .ContainInOrder("PdfActualText0", "PdfActualText1");
            return true;
        });
    }
}
