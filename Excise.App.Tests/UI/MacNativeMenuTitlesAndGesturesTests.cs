using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// The macOS native menu's titles and key equivalents, pinned as the user sees
/// them (#1836 rule 2). The gestures are derived from <c>PdfCommandRegistry</c>,
/// not typed beside it, so this table is what proves that derivation
/// changes nothing. Each row is <c>menu path|Key+Modifiers</c>; a row with no
/// key equivalent ends in <c>|</c>.
/// </summary>
[Collection("AvaloniaTests")]
public class MacNativeMenuTitlesAndGesturesTests
{
    private static readonly string[] Expected =
    [
        "File|",
        "File/Open...|O+Meta",
        "File/Open Recent|",
        "File/Open Recent/No Recent Files|",
        "File/Save|S+Meta",
        "File/Save As...|S+Shift, Meta",
        "File/Save Flattened Form Copy...|",
        "File/Close Document|W+Meta",
        "Edit|",
        "Edit/Undo|Z+Meta",
        "Edit/Redo|Z+Shift, Meta",
        "Edit/Find...|F+Meta",
        "Edit/Find Next|F3+None",
        "Edit/Find Previous|F3+Shift",
        "Edit/Select Text Mode|T+Meta",
        "Edit/Typewriter Mode|",
        "Edit/Typewriter Text Color|",
        "Edit/Typewriter Text Color/Black|",
        "Edit/Typewriter Text Color/Gray|",
        "Edit/Typewriter Text Color/Red|",
        "Edit/Typewriter Text Color/Orange|",
        "Edit/Typewriter Text Color/Green|",
        "Edit/Typewriter Text Color/Blue|",
        "Edit/Typewriter Text Color/Purple|",
        "Edit/Typewriter Text Color/White|",
        "Edit/Form Authoring Mode|",
        "Edit/Go to Next Pending Type-over Edit|",
        "Edit/Discard Pending Type-over Edits|",
        "Edit/Select All Text|A+Meta",
        "Edit/Copy Selected Text|C+Meta",
        "Annotate|",
        "Annotate/Highlight Tool|",
        "Annotate/Add Highlight From Selection|",
        "Annotate/Add Sticky Note...|",
        "Annotate/Place Sticky Note (Click Page)|",
        "View|",
        "View/Zoom In|OemPlus+Meta",
        "View/Zoom Out|OemMinus+Meta",
        "View/Actual Size|D0+Meta",
        "View/Fit Width|D1+Meta",
        "View/Fit Page|D2+Meta",
        "View/Continuous Scroll|C+Shift, Meta",
        "View/Show Outline|O+Shift, Meta",
        "View/Show Thumbnails|T+Shift, Meta",
        "View/Show Attachments|",
        "View/Show Clipboard History|",
        "View/Annotation Toolbar|",
        "View/Floating Annotation Palette|",
        "Document|",
        "Document/Add Pages...|",
        "Document/Insert Pages Before Current...|",
        "Document/Insert Pages After Current...|",
        "Document/Extract Current Page...|",
        "Document/Extract Selected Pages...|",
        "Document/Move Page Earlier|",
        "Document/Move Page Later|",
        "Document/Move Selected Pages Earlier|",
        "Document/Move Selected Pages Later|",
        "Document/Remove Current Page|",
        "Document/Remove Selected Pages|",
        "Document/Clear Page Selection|",
        "Document/Rotate Left 90 degrees|L+Meta",
        "Document/Rotate Right 90 degrees|R+Meta",
        "Document/Rotate 180 degrees|",
        "Document/Export Current Page...|E+Meta",
        "Document/Export All Pages as Images...|",
        "Document/Print...|P+Meta",
        "Document/Security...|",
        "Document/Reduce File Size...|",
        "Redaction|",
        "Redaction/Redaction Mode|R+None",
        "Redaction/Apply Redaction|Return+None",
        "Redaction/Show Clipboard History|",
        "Tools|",
        "Tools/Verify Digital Signatures...|",
        "Tools/Attachments|",
        "Tools/Bates Numbering...|",
        "Tools/Reveal Hidden Text|",
        "Tools/Reveal Rasterized Hidden Text|",
        "Tools/Auto-detect Form Fields|",
        "Window|",
        "Window/Show Previous Tab|Tab+Control, Shift",
        "Window/Show Next Tab|Tab+Control",
        "Window/Show Previous Window Tab|",
        "Window/Show Next Window Tab|",
        "Window/Move Tab to New Window|",
        "Window/Merge All Windows|",
        "Window/Show or Hide Tab Bar|",
        "Help|",
        "Help/Keyboard Shortcuts|F1+None",
        "Help/Documentation|"
    ];

    [FixedAvaloniaFact]
    public void EveryMenuItem_KeepsItsTitleAndKeyEquivalent()
    {
        var menu = MacNativeMenuBuilder.Create(MainWindowViewModelTestFactory.Create());

        // Open Recent lists the user's recent files and Window lists the process's open documents: data,
        // not structure, and other tests in the same process leave both behind (chunk 07 of t1 showed a
        // recent file where "No Recent Files" is expected, and extra document rows). The submenu titles
        // stay pinned; the rows under Open Recent are left out of both sides, and rows under Window are
        // compared only when the test expects them.
        var rows = Rows(menu, "").Where(r => !IsRecentFile(r) && (Expected.Contains(r) || !r.StartsWith("Window/", StringComparison.Ordinal)));

        rows.Should().Equal(Expected.Where(r => !IsRecentFile(r)));
    }

    private static bool IsRecentFile(string row) => row.StartsWith("File/Open Recent/", StringComparison.Ordinal);

    private static IEnumerable<string> Rows(NativeMenu menu, string parent)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator))
        {
            var path = parent + item.Header;
            var gesture = item.Gesture is { } g ? $"{g.Key}+{g.KeyModifiers}" : "";
            yield return $"{path}|{gesture}";
            if (item.Menu is { } submenu)
                foreach (var row in Rows(submenu, path + "/"))
                    yield return row;
        }
    }
}
