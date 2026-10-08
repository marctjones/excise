using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Excise.App.ViewModels;

namespace Excise.App.Views;

internal partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnVisitGitHubClicked(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is AboutWindowViewModel vm)
            OpenUrl(vm.ProjectUrl);
    }

    /// <summary>
    /// Repaint the detail pane with the selected package's full license
    /// notice, copyright, and primary URL. Building the panel imperatively
    /// here is simpler than templating each field — the layout is fixed
    /// and we want the license text in a read-only TextBox so the user
    /// can copy it.
    /// </summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var detail = this.FindControl<StackPanel>("DetailPanel");
        if (detail == null) return;
        detail.Children.Clear();

        if ((sender as ListBox)?.SelectedItem is not ThirdPartyPackage pkg)
        {
            detail.Children.Add(Help("Select a package…"));
            return;
        }

        detail.Children.Add(new TextBlock
        {
            Text = $"{pkg.Id} {pkg.Version}",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        });

        if (!string.IsNullOrEmpty(pkg.LicenseName))
            detail.Children.Add(Help($"License: {pkg.LicenseName}"));

        if (!string.IsNullOrEmpty(pkg.Copyright))
            detail.Children.Add(Help(pkg.Copyright));
        else if (!string.IsNullOrEmpty(pkg.Authors))
            detail.Children.Add(Help($"by {pkg.Authors}"));

        if (pkg.ScancodeMismatch)
        {
            var warning = new TextBlock
            {
                Text = "⚠ scancode-toolkit detected a different license than what the package metadata declares — verify before redistributing.",
                Margin = new Thickness(0, 4, 0, 0),
            };
            warning.Classes.Add("warning");
            detail.Children.Add(warning);
            if (pkg.ScancodeDetectedSpdx is { Count: > 0 })
                detail.Children.Add(Help($"scancode detected: {string.Join(", ", pkg.ScancodeDetectedSpdx)}"));
        }

        if (!string.IsNullOrEmpty(pkg.Description))
            detail.Children.Add(new TextBlock
            {
                Text = pkg.Description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 4),
            });

        if (!string.IsNullOrEmpty(pkg.PrimaryUrl))
        {
            var btn = new Button
            {
                Content = pkg.PrimaryUrl,
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 4, 0, 6),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            btn.Click += (_, _) => OpenUrl(pkg.PrimaryUrl);
            detail.Children.Add(btn);
        }

        // #831: show verbatim license text for EVERY package — the bundled
        // license file if present, otherwise the canonical SPDX body (e.g. the
        // MIT permission notice) with this package's copyright. Only fall back
        // to a URL reference when neither exists (the completeness gate forbids
        // that for a shipped package).
        var effectiveText = pkg.EffectiveLicenseText;
        var box = new TextBox
        {
            Text = !string.IsNullOrEmpty(effectiveText)
                ? effectiveText
                : $"No verbatim license text is available for this package.\n\n" +
                  (pkg.LicenseUrl != null
                    ? $"License URL declared by the package: {pkg.LicenseUrl}\n"
                    : "") +
                  (pkg.Spdx != null
                    ? $"SPDX expression declared by the package: {pkg.Spdx}\n  → {pkg.LicenseSpdxUrl}"
                    : ""),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Menlo, Monaco, monospace"),
            FontSize = 11,
            MinHeight = 220,
            Margin = new Thickness(0, 6, 0, 0),
        };
        detail.Children.Add(box);
    }

    private static void OpenUrl(string? url) => Services.UrlOpener.Open(url);

    // Secondary text takes the shared help style (token colour, platform size)
    // instead of fading with Opacity (#2006).
    private static TextBlock Help(string text)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add("help");
        return block;
    }
}
