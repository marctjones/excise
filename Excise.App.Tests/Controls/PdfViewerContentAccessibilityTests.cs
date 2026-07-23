using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.App.Tests.UI;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.Controls;

/// <summary>
/// Issue #631 — document content must be visible to screen readers.
///
/// <para>
/// These tests assert that the current page's text is REACHABLE through the
/// automation peer tree (not merely that a peer exists): they walk
/// <see cref="AutomationPeer.GetChildren"/> from the viewer's peer, find the
/// synthetic page-text node, and verify its Name carries the page text in
/// reading order, follows page navigation, and goes empty when no document
/// is loaded.
/// </para>
/// </summary>
[Collection("AvaloniaTests")]
public class PdfViewerContentAccessibilityTests
{
    private const string PageTextAutomationId = "PdfPageTextContent";

    [FixedAvaloniaFact]
    public async Task PageText_IsReachableThroughAutomationPeerTree_InReadingOrder()
    {
        using var doc = CreateDocument(page =>
        {
            // PDF space is Y-up: larger Y = higher on the page, i.e. read first.
            Draw(page, "Alpha heading line", y: 700);
            Draw(page, "Beta body line", y: 650);
        });

        var viewer = await ShowViewerAsync(doc);

        var textPeer = FindPageTextPeer(ControlAutomationPeer.CreatePeerForElement(viewer));

        textPeer.Should().NotBeNull("the viewer's automation peer must expose a page-text child (issue #631)");
        textPeer!.GetAutomationControlType().Should().Be(AutomationControlType.Text);
        textPeer.IsContentElement().Should().BeTrue("screen readers only surface content elements");
        textPeer.IsControlElement().Should().BeTrue();

        var name = textPeer.GetName();
        name.Should().Contain("Alpha heading line");
        name.Should().Contain("Beta body line");
        name!.IndexOf("Alpha heading line", System.StringComparison.Ordinal)
            .Should().BeLessThan(
                name.IndexOf("Beta body line", System.StringComparison.Ordinal),
                "page text must be exposed in reading order (top line first)");
    }

    [FixedAvaloniaFact]
    public async Task PageText_FollowsPageNavigation()
    {
        using var doc = CreateDocument(
            page => Draw(page, "Content of the first page", y: 700),
            page => Draw(page, "Content of the second page", y: 700));

        var viewer = await ShowViewerAsync(doc);
        var textPeer = FindPageTextPeer(ControlAutomationPeer.CreatePeerForElement(viewer));
        textPeer.Should().NotBeNull();

        textPeer!.GetName().Should().Contain("Content of the first page");

        viewer.CurrentPage = 2;
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var name = textPeer.GetName();
        name.Should().Contain("Content of the second page");
        name.Should().NotContain("Content of the first page",
            "the accessible text must track the displayed page");
    }

    [FixedAvaloniaFact]
    public async Task PageText_IsEmptyWithoutDocument()
    {
        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var textPeer = FindPageTextPeer(ControlAutomationPeer.CreatePeerForElement(viewer));

        textPeer.Should().NotBeNull();
        (textPeer!.GetName() ?? string.Empty).Should().BeEmpty(
            "no document means no page text to announce");

        window.Close();
    }

    private static PdfCoreDocument CreateDocument(params System.Action<PdfPage>[] pages)
    {
        using var builder = PdfCoreDocument.CreateNew();
        foreach (var drawPage in pages)
        {
            var page = builder.Pages.AddBlank();
            drawPage(page);
        }

        return PdfCoreDocument.Open(builder.SaveToBytes());
    }

    private static void Draw(PdfPage page, string text, double y)
    {
        using var g = page.GetGraphics();
        g.DrawString(text, PdfFont.Helvetica(12), PdfBrush.Black, 100, y);
        g.Flush();
    }

    private static async Task<PdfViewerControl> ShowViewerAsync(PdfCoreDocument doc)
    {
        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 800, Height = 600 };
        window.Show();
        viewer.Document = doc;
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        return viewer;
    }

    /// <summary>Depth-first search of the automation tree for the synthetic page-text peer.</summary>
    private static AutomationPeer? FindPageTextPeer(AutomationPeer root)
    {
        var stack = new Stack<AutomationPeer>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var peer = stack.Pop();
            if (peer.GetAutomationId() == PageTextAutomationId)
                return peer;
            foreach (var child in peer.GetChildren())
                stack.Push(child);
        }

        return null;
    }
}
