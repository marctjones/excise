using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Excise.Core.Automation;
using System;

namespace Excise.App.Automation;

public static class CommandAccessibility
{
    public static readonly AttachedProperty<string?> CommandIdProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>(
            "CommandId",
            typeof(CommandAccessibility));

    public static readonly AttachedProperty<bool> ShowToolTipProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "ShowToolTip",
            typeof(CommandAccessibility),
            defaultValue: true);

    static CommandAccessibility()
    {
        CommandIdProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            Apply(control, args.NewValue as string);
        });

        ShowToolTipProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            var commandId = GetCommandId(control);
            if (string.IsNullOrWhiteSpace(commandId))
                return;

            ApplyMetadata(control, PdfCommandRegistry.Get(commandId));
        });
    }

    public static void SetCommandId(AvaloniaObject element, string? value) =>
        element.SetValue(CommandIdProperty, value);

    public static string? GetCommandId(AvaloniaObject element) =>
        element.GetValue(CommandIdProperty);

    public static void SetShowToolTip(AvaloniaObject element, bool value) =>
        element.SetValue(ShowToolTipProperty, value);

    public static bool GetShowToolTip(AvaloniaObject element) =>
        element.GetValue(ShowToolTipProperty);

    private static void Apply(Control control, string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return;

        var metadata = PdfCommandRegistry.Get(commandId);
        ApplyMetadata(control, metadata);
        control.AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(() => ApplyMetadata(control, metadata), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => ApplyMetadata(control, metadata), DispatcherPriority.Loaded);

        control.GetObservable(InputElement.IsEnabledProperty)
            .Subscribe(_ => UpdateItemStatus(control, metadata));
    }

    private static void ApplyMetadata(Control control, PdfCommandMetadata metadata)
    {
        AutomationProperties.SetName(control, metadata.Label);
        AutomationProperties.SetHelpText(control, BuildHelpText(metadata));
        UpdateItemStatus(control, metadata);

        ToolTip.SetTip(control, GetShowToolTip(control) ? BuildTooltip(metadata) : null);
    }

    private static string BuildHelpText(PdfCommandMetadata metadata)
    {
        var helpText = metadata.Description;
        if (!string.IsNullOrWhiteSpace(metadata.Shortcut))
            helpText += $" Shortcut: {metadata.Shortcut}.";
        if (metadata.IsSecuritySensitive)
            helpText += " Security-sensitive command; verify the result before sharing output.";
        return helpText;
    }

    private static string BuildTooltip(PdfCommandMetadata metadata) =>
        string.IsNullOrWhiteSpace(metadata.Shortcut)
            ? metadata.Label
            : $"{metadata.Label} ({metadata.Shortcut})";

    private static void UpdateItemStatus(Control control, PdfCommandMetadata metadata)
    {
        AutomationProperties.SetItemStatus(control, control.IsEnabled
            ? "Available"
            : $"Unavailable. {metadata.DisabledReason}");
    }
}
