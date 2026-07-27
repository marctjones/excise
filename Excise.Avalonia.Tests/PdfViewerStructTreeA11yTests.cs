using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
/// Headless tests for the tagged-PDF structure layer of the accessibility
/// surface (#631): struct-tree reading order, the structure-role automation
/// peers (headings, lists, tables), and structure-based keyboard navigation.
///
/// Companion to <see cref="PdfViewerAccessibilityTests"/> (page-text and /Alt
/// peers). Runs through <see cref="HeadlessSessionGuard"/> from plain [Fact]s
/// so a headless-startup failure skips rather than crashing the host (#752),
/// and stays clear of the Avalonia.Headless.XUnit TestContext bug (#337).
/// SkiaSharp's font manager is process-wide, so this file must not parallelize
/// (the assembly's collection serialization is inherited).
/// </summary>
public class PdfViewerStructTreeA11yTests
{
    // ── harness ──────────────────────────────────────────────────────────

    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    private static (PdfViewerControl Viewer, PdfViewerAutomationPeer Peer) CreateViewerWithPeer(
        PdfDocument document)
    {
        var viewer = new PdfViewerControl { Document = document };
        var peer = (PdfViewerAutomationPeer)ControlAutomationPeer.CreatePeerForElement(viewer);
        return (viewer, peer);
    }

    private static AutomationPeer PageTextChild(AutomationPeer peer) =>
        peer.GetChildren().First(c => c.GetClassName() == "PdfPageText");

    private static List<PdfStructRoleAutomationPeer> RolePeers(AutomationPeer peer) =>
        peer.GetChildren().OfType<PdfStructRoleAutomationPeer>().ToList();

    private static string Normalized(string? s) =>
        string.Join(" ", (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ── fixtures ─────────────────────────────────────────────────────────

    /// <summary>
    /// A raw single-page tagged PDF with two top-level struct elements of type
    /// <paramref name="structType"/>, carrying <c>/ActualText</c>
    /// <paramref name="first"/> then <paramref name="second"/> in structure
    /// order. The content stream draws the words in the OPPOSITE (geometric)
    /// order — "second" glyphs above "first" glyphs — so struct order and
    /// geometric reading order diverge, which is the only way to prove reading
    /// order really follows the structure tree.
    /// </summary>
    private static byte[] RawTaggedPdfTwoElements(string structType, string first, string second)
    {
        // Geometric top-to-bottom: the higher line reads first. Draw a marker
        // for the second element higher, the first element lower.
        const string content =
            "BT /F1 12 Tf 72 720 Td (SecondGlyphs) Tj 0 -24 Td (FirstGlyphs) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] >>",
            $"<< /Type /StructElem /S /{structType} /ActualText ({first}) /Pg 3 0 R >>",
            $"<< /Type /StructElem /S /{structType} /ActualText ({second}) /Pg 3 0 R >>",
        };
        return AssembleObjects(objects);
    }

    private static byte[] AssembleObjects(string[] objects)
    {
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
    /// A builder-authored tagged single page with a heading, a bullet list,
    /// and a table — real H1 / L / LI / Table / TR / TH / TD struct elements.
    /// Their body text lives in MCID marked content (not reachable read-only),
    /// so the role peers carry the ROLE with empty text — exactly the case the
    /// role mapping must still expose.
    /// </summary>
    private static PdfDocument BuilderTaggedRichDoc() =>
        PdfDocument.Open(PdfDocumentBuilder.Create()
            .Tagged()
            .Heading("Quarterly Report", 1)
            .BulletList(new[] { "First point", "Second point" })
            .Table(new[]
            {
                new[] { "Region", "Revenue" },
                new[] { "North", "100" },
            }, headerRow: true)
            .SaveToBytes());

    /// <summary>Two-page builder-tagged doc, one H1 heading per page.</summary>
    private static PdfDocument BuilderTaggedTwoPageHeadings() =>
        PdfDocument.Open(PdfDocumentBuilder.Create()
            .Tagged()
            .Heading("Page One Heading", 1)
            .Paragraph("Body of page one")
            .PageBreak()
            .Heading("Page Two Heading", 1)
            .Paragraph("Body of page two")
            .SaveToBytes());

    // ── reading order (requirement 1) ────────────────────────────────────

    [Fact]
    public async Task ReadingOrderText_FollowsStructTree_NotGeometricOrder()
    {
        await OnUiThread(() =>
        {
            var (viewer, peer) = CreateViewerWithPeer(
                PdfDocument.Open(RawTaggedPdfTwoElements("P", "First", "Second")));

            // The page-text peer now serves struct-ordered text.
            Normalized(PageTextChild(peer).GetName())
                .Should().Be("First Second",
                    "reading order must follow the structure tree's document order");

            // Proof it is not merely the geometric text relabeled: the raw
            // glyph extraction reads the two lines top-to-bottom, the reverse.
            Normalized(viewer.GetAccessiblePageText())
                .Should().Contain("SecondGlyphs").And.Contain("FirstGlyphs");
            Normalized(viewer.GetAccessibleReadingOrderText())
                .Should().NotContain("Glyphs",
                    "struct /ActualText carriers replace the geometric glyph text");
            return true;
        });
    }

    [Fact]
    public async Task ReadingOrderText_UntaggedDocument_FallsBackToGeometric()
    {
        await OnUiThread(() =>
        {
            var doc = PdfDocument.Open(PdfDocumentBuilder.Create()
                .Paragraph("Plain untagged content")
                .SaveToBytes());
            var (viewer, peer) = CreateViewerWithPeer(doc);

            // No struct tree → the geometric reading-order text is used.
            Normalized(PageTextChild(peer).GetName())
                .Should().Contain("Plain untagged content");
            Normalized(viewer.GetAccessibleReadingOrderText())
                .Should().Be(Normalized(viewer.GetAccessiblePageText()));
            return true;
        });
    }

    // ── role peers (requirement 2) ───────────────────────────────────────

    [Fact]
    public async Task StructRolePeers_ExposeHeadingListAndTableRoles()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(BuilderTaggedRichDoc());
            var roles = RolePeers(peer);

            roles.Select(r => r.Role).Should().Contain(new[]
            {
                AccessibleStructRole.Heading,
                AccessibleStructRole.List,
                AccessibleStructRole.ListItem,
                AccessibleStructRole.Table,
                AccessibleStructRole.TableRow,
            }, "every mapped structural role on the page must be enumerable");

            // Cells appear as header or body cells.
            roles.Select(r => r.Role).Should().Contain(r =>
                r == AccessibleStructRole.TableHeaderCell || r == AccessibleStructRole.TableCell);

            // No figure duplicates the /Alt image peers.
            roles.Should().NotContain(r => r.Role == AccessibleStructRole.Figure);
            return true;
        });
    }

    [Fact]
    public async Task HeadingRolePeer_MapsToTextControlType_WithSpokenLevel()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(BuilderTaggedRichDoc());

            var heading = RolePeers(peer).First(r => r.Role == AccessibleStructRole.Heading);
            heading.HeadingLevel.Should().Be(1);
            heading.GetAutomationControlType().Should().Be(AutomationControlType.Text);
            heading.GetLocalizedControlType().Should().Be("heading level 1");
            heading.IsContentElement().Should().BeTrue();
            heading.GetParent().Should().BeSameAs(peer, "an orphaned peer is invisible to AT");
            return true;
        });
    }

    [Fact]
    public async Task ListAndTableRolePeers_MapToTheirControlTypes()
    {
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(BuilderTaggedRichDoc());
            var roles = RolePeers(peer);

            roles.First(r => r.Role == AccessibleStructRole.List)
                .GetAutomationControlType().Should().Be(AutomationControlType.List);
            roles.First(r => r.Role == AccessibleStructRole.ListItem)
                .GetAutomationControlType().Should().Be(AutomationControlType.ListItem);
            roles.First(r => r.Role == AccessibleStructRole.Table)
                .GetAutomationControlType().Should().Be(AutomationControlType.Table);
            roles.First(r => r.Role == AccessibleStructRole.TableRow)
                .GetAutomationControlType().Should().Be(AutomationControlType.DataItem);
            return true;
        });
    }

    [Fact]
    public async Task HeadingRolePeer_ExposesRealBodyText_ViaMcid()
    {
        // #776: the heading in BuilderTaggedRichDoc carries NO /ActualText — its
        // text lives only in MCID marked content. Before the MCID→letter bridge
        // its role peer read as role-only (empty name); now it must expose the
        // real glyphs "Quarterly Report".
        await OnUiThread(() =>
        {
            var (_, peer) = CreateViewerWithPeer(BuilderTaggedRichDoc());

            var heading = RolePeers(peer).First(r => r.Role == AccessibleStructRole.Heading);
            Normalized(heading.GetName())
                .Should().Be("Quarterly Report",
                    "a screen reader must read the heading's real body text, not just its role");
            return true;
        });
    }

    [Fact]
    public async Task UntaggedDocument_HasNoStructRolePeers()
    {
        await OnUiThread(() =>
        {
            var doc = PdfDocument.Open(PdfDocumentBuilder.Create()
                .Paragraph("No structure here")
                .SaveToBytes());
            var (_, peer) = CreateViewerWithPeer(doc);
            RolePeers(peer).Should().BeEmpty();
            return true;
        });
    }

    // ── keyboard navigation (requirement 3) ──────────────────────────────

    [Fact]
    public async Task NextHeading_LandsOnHeadingsInDocumentOrder()
    {
        await OnUiThread(() =>
        {
            var (viewer, _) = CreateViewerWithPeer(
                PdfDocument.Open(RawTaggedPdfTwoElements("H1", "Alpha Heading", "Beta Heading")));

            viewer.CurrentStructureNavigationTarget.Should().BeNull("no navigation yet");

            viewer.MoveToNextStructure(backward: false, headingsOnly: true).Should().BeTrue();
            var first = viewer.CurrentStructureNavigationTarget;
            first.Should().NotBeNull();
            first!.Value.Role.Should().Be(AccessibleStructRole.Heading);
            first.Value.Text.Should().Be("Alpha Heading");

            viewer.MoveToNextStructure(backward: false, headingsOnly: true).Should().BeTrue();
            viewer.CurrentStructureNavigationTarget!.Value.Text.Should().Be("Beta Heading");

            viewer.MoveToNextStructure(backward: false, headingsOnly: true)
                .Should().BeFalse("there is no heading after the last one");

            // Backward returns to the first heading.
            viewer.MoveToNextStructure(backward: true, headingsOnly: true).Should().BeTrue();
            viewer.CurrentStructureNavigationTarget!.Value.Text.Should().Be("Alpha Heading");
            return true;
        });
    }

    [Fact]
    public async Task NextHeading_CrossesPageBoundary_AndMovesCurrentPage()
    {
        await OnUiThread(() =>
        {
            var (viewer, _) = CreateViewerWithPeer(BuilderTaggedTwoPageHeadings());
            viewer.CurrentPage.Should().Be(1);

            // First heading is on page 1.
            viewer.MoveToNextStructure(backward: false, headingsOnly: true).Should().BeTrue();
            viewer.CurrentStructureNavigationTarget!.Value.Page.Should().Be(1);
            viewer.CurrentPage.Should().Be(1);

            // Next heading lives on page 2 — navigation must bring it on screen.
            viewer.MoveToNextStructure(backward: false, headingsOnly: true).Should().BeTrue();
            viewer.CurrentStructureNavigationTarget!.Value.Page.Should().Be(2);
            viewer.CurrentPage.Should().Be(2, "landing on a heading must show its page");
            return true;
        });
    }

    [Fact]
    public async Task StructureNavigation_UntaggedDocument_IsNoOp()
    {
        await OnUiThread(() =>
        {
            var doc = PdfDocument.Open(PdfDocumentBuilder.Create()
                .Paragraph("Nothing to navigate")
                .SaveToBytes());
            var (viewer, _) = CreateViewerWithPeer(doc);

            viewer.MoveToNextStructure(backward: false, headingsOnly: true).Should().BeFalse();
            viewer.CurrentStructureNavigationTarget.Should().BeNull();
            return true;
        });
    }
}
