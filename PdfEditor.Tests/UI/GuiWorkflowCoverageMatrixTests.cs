using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Xunit;

namespace PdfEditor.Tests.UI;

public class GuiWorkflowCoverageMatrixTests
{
    [Fact]
    public void SignificantGuiWorkflows_HaveNamedAutomatedCoverage()
    {
        var assembly = typeof(GuiWorkflowCoverageMatrixTests).Assembly;
        var missing = new List<string>();

        foreach (var row in CoverageRows())
        {
            var coverageType = assembly.GetType($"{typeof(GuiWorkflowCoverageMatrixTests).Namespace}.{row.TestClass}");
            if (coverageType == null)
            {
                missing.Add($"{row.Workflow}: missing {row.TestClass}");
                continue;
            }

            var testCount = coverageType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Count(method => method.GetCustomAttributes(inherit: false)
                    .Any(attr => attr is Attribute attribute && IsRunnableFact(attribute)));

            if (testCount == 0)
            {
                missing.Add($"{row.Workflow}: {row.TestClass} has no runnable public fact tests");
            }
        }

        missing.Should().BeEmpty(
            "major mouse, keyboard, menu, and toolbar workflows should have explicit automated GUI coverage");
    }

    private static IReadOnlyList<CoverageRow> CoverageRows() =>
    [
        new("Toolbar and menu command bindings", nameof(CommandBindingSweepTests)),
        new("Keyboard shortcuts", nameof(KeyboardShortcutTests)),
        new("Mouse text selection and copy", nameof(TextSelectionDragTests)),
        new("Mouse redaction, apply, save, structural removal, visual marker", nameof(RedactionMouseWorkflowTests)),
        new("Mouse link activation", nameof(InPageLinkClickTests)),
        new("Search workflow and highlight overlays", nameof(SearchHighlightOverlayTests)),
        new("Outline tree navigation", nameof(OutlineTreeNavigationTests)),
        new("Page organization workflows", nameof(PageOrganizationWorkflowTests)),
        new("Page viewer render smoke and visual baseline", nameof(PdfViewerHeadlessRenderTests)),
        new("Annotation review UI commands and persistence", nameof(AnnotationAuthoringWorkflowTests)),
        new("Form field overlays and field editing", nameof(FormFieldsOverlayTests)),
        new("Form authoring mouse workflow", nameof(FormAuthoringTests)),
        new("Form fill and flatten workflow", nameof(FormWorkflowTests)),
        new("Typewriter mouse workflow", nameof(TypewriterWorkflowTests)),
        new("Open, search, redact, close golden paths", nameof(GoldenPathTests)),
        new("Scripted GUI automation entry points", nameof(ScriptedGuiTests)),
    ];

    private sealed record CoverageRow(string Workflow, string TestClass);

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
