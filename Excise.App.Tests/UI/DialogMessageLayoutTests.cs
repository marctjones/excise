using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1622: a dialog message must not push its buttons off the bottom of the
/// window.
/// </summary>
/// <remarks>
/// <para>
/// The redacted-copy safety report (#1586) is ~20 lines, and
/// <see cref="AvaloniaUserDialogService.ShowMessageAsync"/> used to show it in a
/// fixed 450x200 <c>CanResize=false</c> window whose content was a
/// <see cref="StackPanel"/>. Measured in the shipped app on 2026-09-17: the
/// message laid out 400x420 inside a 450x228 window with the OK button at
/// y=493. The user could read down to "so the file remains PDF/A" and no
/// further — the "NO LONGER accessible or interactive" warning, the whole point
/// of the Maximum profile's report, was off-screen, and so was the button.
/// </para>
/// <para>
/// These tests assert REAL laid-out geometry, like
/// <see cref="NoticeBarPlacementTests"/> — that a control exists says nothing
/// about whether anyone can see it. The last line of the message must be
/// reachable (visible, or within the scroll extent the ScrollViewer can scroll
/// to) and the footer button must sit inside the window's client bounds.
/// </para>
/// </remarks>
[Collection("AvaloniaTests")]
public class DialogMessageLayoutTests
{
    /// <summary>
    /// The shape of the #1586 Maximum-profile report, as
    /// <c>RedactedCopyDialogFormatter</c> builds it. Read out of the live app's
    /// accessibility tree on 2026-09-17, because on screen it was cut off.
    /// </summary>
    private const string LongReport = """
Redacted PDF saved to:
/tmp/claude-501/scratchpad/gui-w9_REDACTED.pdf

Original file preserved. Document reloaded.

Verification report:
- Content removal: verified 1 captured selection preview(s) no longer appear in extracted text
- Metadata scrub: 0 Info field(s) removed; XMP metadata removed except the PDF/A identification (pdfaid), which is kept so the file remains PDF/A
- Embedded files: none found
- Hidden text audit: no structurally hidden text found
- Raster redaction audit: no raster image content remains in redaction areas
- XFA form: /XFA (static XFA form; the AcroForm fields remain; removed whole)
- Output profile: Maximum
  - removed 3 JavaScript action(s)
  - removed 1 hidden optional-content group

Removed text is not repeated in this report. Open Clipboard History only if you need to review captured selection previews.

Warnings:
- Maximum profile: this copy is NO LONGER accessible or interactive. Forms and annotations are flattened, and bookmarks, links, comments, field names and alternate text have been removed. It will not pass PDF/UA and will not read correctly to a screen reader.
""";

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task LongMessage_KeepsTheLastLineReachableAndTheButtonOnScreen()
    {
        await WithMessageDialog(LongReport, (dialog, message, button) =>
        {
            var clientHeight = dialog.Bounds.Height;
            clientHeight.Should().BeGreaterThan(0, "the dialog must have laid out");

            // The button is what a user clicks to get rid of the dialog. It was
            // at y=493 in a 228-tall window; anywhere below the client area is
            // a dialog you cannot dismiss by clicking.
            var buttonBottom = button.TranslatePoint(new Point(0, button.Bounds.Height), dialog)!.Value.Y;
            buttonBottom.Should().BeLessThanOrEqualTo(clientHeight + 1,
                "the OK button must be inside the window, not below it");

            // The last line of the report must be reachable: either laid out
            // inside the window, or inside the extent the ScrollViewer can
            // scroll to. A message clipped by a fixed-height window is neither.
            var scroller = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single();
            var messageBottom = message.TranslatePoint(new Point(0, message.Bounds.Height), scroller)!.Value.Y;
            messageBottom.Should().BeLessThanOrEqualTo(scroller.Extent.Height + 1,
                "the end of the message must be within the scrollable extent");
            scroller.Extent.Height.Should().BeGreaterThanOrEqualTo(message.Bounds.Height - 1,
                "the ScrollViewer must be able to reach the end of the message");
            (scroller.Extent.Height - scroller.Viewport.Height).Should().BeGreaterThanOrEqualTo(0);

            // And the message itself must be as tall as its text needs, rather
            // than silently truncated to the window.
            message.Bounds.Height.Should().BeGreaterThan(200,
                "a report this long must lay out at its full text height");

            // ⚠️ The assertions above are not enough on their own, and this was
            // MEASURED, not assumed (2026-09-17): replacing the *,Auto grid
            // with a StackPanel — the very defect #1622 is — left every one of
            // them green. The headless window applies neither MinHeight nor
            // MaxHeight under SizeToContent, so it simply grew to the full
            // content height and the button fitted.
            //
            // So constrain the content the way a real display does — a window
            // shorter than the message — and check the contract that keeps the
            // button reachable: the footer row is measured first and the
            // message row takes what is left. A StackPanel puts the button
            // below the constraint; the grid does not.
            var content = (Control)dialog.Content!;
            content.Measure(new Size(450, 300));
            content.Arrange(new Rect(0, 0, 450, 300));
            var constrainedButtonBottom =
                button.TranslatePoint(new Point(0, button.Bounds.Height), content)!.Value.Y;
            constrainedButtonBottom.Should().BeLessThanOrEqualTo(300 + 1,
                "in a window too short for the message, the button must still be inside it");
            scroller.Viewport.Height.Should().BeLessThan(scroller.Extent.Height,
                "the message must be scrollable once it no longer fits");
        });
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ShortMessage_StillProducesASmallDialog()
    {
        await WithMessageDialog("Document saved.", (dialog, _, button) =>
        {
            // A RANGE, not a ceiling. Measured 2026-09-17: with only a ceiling
            // this assertion could not fail — a one-line dialog laid out 90 px
            // tall, and bumping the window MinHeight to 520 did not move it,
            // because SizeToContent ignores Window.MinHeight. The floor is what
            // catches that, and it is why the production floor lives on the
            // content rather than the window.
            dialog.Bounds.Height.Should().BeInRange(170, 280,
                "a one-line message must open about the size it always did — "
                + "neither collapsed to the text nor grown to the report's height");
            var buttonBottom = button.TranslatePoint(new Point(0, button.Bounds.Height), dialog)!.Value.Y;
            buttonBottom.Should().BeLessThanOrEqualTo(dialog.Bounds.Height + 1);
        });
    }

    /// <summary>
    /// Show the real dialog the real way — <c>ShowDialog</c> on a shown owner —
    /// without awaiting it (it does not return until the dialog closes), then
    /// inspect the laid-out tree and close it.
    /// </summary>
    private static async Task WithMessageDialog(
        string message, Action<Window, TextBlock, Button> assert)
    {
        var owner = new Window { Width = 1200, Height = 800 };
        owner.Show();
        var host = new FakeWindowHost { MainWindowResolver = () => owner };
        var service = new AvaloniaUserDialogService(
            NullLogger<AvaloniaUserDialogService>.Instance, host);

        var showing = service.ShowMessageAsync("Success", message);
        Window? dialog = null;
        try
        {
            for (var i = 0; i < 50 && dialog == null; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
                dialog = owner.OwnedWindows.FirstOrDefault();
            }

            dialog.Should().NotBeNull("the message dialog must have opened");
            for (var i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
                dialog!.UpdateLayout();
                await Task.Delay(10);
            }

            var text = dialog!.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == message);
            var button = dialog.GetVisualDescendants().OfType<Button>()
                .First(b => b.Content as string == "OK");

            assert(dialog, text, button);
        }
        finally
        {
            dialog?.Close();
            owner.Close();
            Dispatcher.UIThread.RunJobs();
            await showing;
        }
    }
}
