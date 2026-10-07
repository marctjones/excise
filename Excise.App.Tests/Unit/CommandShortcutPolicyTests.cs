using Excise.App.Services.Input;
using Excise.Core.Automation;
using global::Avalonia.Controls;
using global::Avalonia.Input;

namespace Excise.App.Tests.Unit;

public class CommandShortcutPolicyTests
{
    [FixedAvaloniaFact]
    public void NativeMacMenu_UsesTheAdoptedCommandChords()
    {
        var vm = Excise.App.Tests.Utilities.MainWindowViewModelTestFactory.Create();
        var menu = Excise.App.Views.MacNativeMenuBuilder.Create(vm);
        var items = Flatten(menu).ToArray();
        Assert.Equal(new KeyGesture(Key.O, KeyModifiers.Meta),
            items.Single(item => item.Header == "Open...").Gesture);
        Assert.Equal(new KeyGesture(Key.G, KeyModifiers.Meta),
            items.Single(item => item.Header == "Find Next").Gesture);
        Assert.Equal(new KeyGesture(Key.G, KeyModifiers.Meta | KeyModifiers.Shift),
            items.Single(item => item.Header == "Find Previous").Gesture);
        Assert.Null(items.Single(item => item.Header == "Redaction Mode").Gesture);
    }

    private static IEnumerable<NativeMenuItem> Flatten(NativeMenu menu)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu is { } child)
                foreach (var nested in Flatten(child)) yield return nested;
        }
    }

    [Theory]
    [InlineData("app.open", "Ctrl+O", "Cmd+O", "⌘O")]
    [InlineData("edit.redo", "Ctrl+Y", "Cmd+Shift+Z", "⌘⇧Z")]
    [InlineData("search.next", "F3", "Cmd+G", "⌘G")]
    [InlineData("search.previous", "Shift+F3", "Cmd+Shift+G", "⌘⇧G")]
    public void PlatformChordsAndHelp_Agree(string id, string windows, string mac, string macHelp)
    {
        var metadata = PdfCommandRegistry.Get(id);
        Assert.Equal(windows, CommandShortcutPolicy.GetChord(metadata, false));
        Assert.Equal(windows, CommandShortcutPolicy.GetDisplayText(metadata, false));
        Assert.Equal(mac, CommandShortcutPolicy.GetChord(metadata, true));
        Assert.Equal(macHelp, CommandShortcutPolicy.GetDisplayText(metadata, true));
    }

    [Fact]
    public void ModeCommands_HaveNoAcceleratorsOnEitherPlatform()
    {
        foreach (var id in new[] { PdfCommandIds.ToggleRedactionMode, PdfCommandIds.SelectTextMode })
        foreach (var mac in new[] { false, true })
        {
            Assert.Null(CommandShortcutPolicy.GetChord(PdfCommandRegistry.Get(id), mac));
            Assert.Null(CommandShortcutPolicy.GetDisplayText(PdfCommandRegistry.Get(id), mac));
        }
    }
}
