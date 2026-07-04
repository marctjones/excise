using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using PdfEditor.Tests.Controls;
using PdfEditor.Tests.Integration;
using PdfEditor.Tests.Unit;
using Xunit;

namespace PdfEditor.Tests.UI;

public class GuiWorkflowCoverageMatrixTests
{
    [Fact]
    public void SignificantGuiWorkflows_HaveNamedAutomatedCoverage()
    {
        var missing = new List<string>();

        foreach (var row in CoverageRows())
        {
            var testCount = row.TestClass
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Count(method => method.GetCustomAttributes(inherit: false)
                    .Any(attr => attr is Attribute attribute && IsRunnableFact(attribute)));

            if (testCount == 0)
            {
                missing.Add($"{row.Workflow}: {row.TestClass.FullName} has no runnable public fact tests");
            }
        }

        missing.Should().BeEmpty(
            "major mouse, keyboard, menu, and toolbar workflows should have explicit automated GUI coverage");
    }

    private static IReadOnlyList<CoverageRow> CoverageRows() =>
    [
        new("Open PDFs from the app and command/open-with entry points", typeof(GoldenPathTests)),
        new("Navigate long PDFs, thumbnails, zoom, fit width/page", typeof(PdfViewerControlTests)),
        new("Thumbnail cache and page preview workflow", typeof(ThumbnailCacheTests)),
        new("Search, select text, copy text", typeof(TextSelectionDragTests)),
        new("Search workflow and highlight overlays", typeof(SearchHighlightOverlayTests)),
        new("Fill common forms, save filled copy, reopen", typeof(FormWorkflowTests)),
        new("Flatten form copy, reopen, verify static output", typeof(FormWorkflowTests)),
        new("Add typewriter text to flat PDF, save copy, reopen", typeof(TypewriterWorkflowTests)),
        new("Highlight selected text and add sticky notes, save, reopen", typeof(AnnotationAuthoringWorkflowTests)),
        new("Reorder, rotate, extract, remove, and combine pages", typeof(PageOrganizationWorkflowTests)),
        new("Redact text/area, save copy, verify structural removal", typeof(RedactionMouseWorkflowTests)),
        new("Metadata and attachment scrub status for redacted copies", typeof(RedactedCopySafetyServiceTests)),
        new("Audit hidden text with clear user-facing states", typeof(RevealHiddenTextTests)),
        new("Audit signatures with clear user-facing states", typeof(SignatureVerificationWorkflowServiceTests)),
        new("Toolbar and menu command bindings", typeof(CommandBindingSweepTests)),
        new("Keyboard shortcuts", typeof(KeyboardShortcutTests)),
        new("Mouse link activation", typeof(InPageLinkClickTests)),
        new("Outline tree navigation", typeof(OutlineTreeNavigationTests)),
        new("Page viewer render smoke and visual baseline", typeof(PdfViewerHeadlessRenderTests)),
        new("Form field overlays and field editing", typeof(FormFieldsOverlayTests)),
        new("Form authoring mouse workflow", typeof(FormAuthoringTests)),
        new("Open, search, redact, close golden paths", typeof(GoldenPathTests)),
        new("GUI responsiveness budgets for open and direct input handlers", typeof(GuiResponsivenessBudgetTests)),
        new("Scripted GUI automation entry points", typeof(ScriptedGuiTests)),
    ];

    private sealed record CoverageRow(string Workflow, Type TestClass);

    private static bool IsRunnableFact(Attribute attr)
    {
        if (attr.GetType().Name is not ("FactAttribute" or "FixedAvaloniaFactAttribute"))
        {
            return false;
        }

        var skip = attr.GetType().GetProperty("Skip")?.GetValue(attr) as string;
        return string.IsNullOrWhiteSpace(skip);
    }
}
