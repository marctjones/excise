using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PdfEditor.ViewModels;

namespace PdfEditor.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnVisitGitHubClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
            detail.Children.Add(new TextBlock { Text = "Select a package…", Opacity = 0.6 });
            return;
        }

        detail.Children.Add(new TextBlock
        {
            Text = $"{pkg.Id} {pkg.Version}",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        });

        if (!string.IsNullOrEmpty(pkg.LicenseName))
            detail.Children.Add(new TextBlock { Text = $"License: {pkg.LicenseName}", Opacity = 0.85 });

        if (!string.IsNullOrEmpty(pkg.Copyright))
            detail.Children.Add(new TextBlock { Text = pkg.Copyright, FontSize = 12, Opacity = 0.7 });
        else if (!string.IsNullOrEmpty(pkg.Authors))
            detail.Children.Add(new TextBlock { Text = $"by {pkg.Authors}", FontSize = 12, Opacity = 0.7 });

        if (pkg.ScancodeMismatch)
        {
            detail.Children.Add(new TextBlock
            {
                Text = "⚠ scancode-toolkit detected a different license than what the package metadata declares — verify before redistributing.",
                Foreground = new SolidColorBrush(Colors.OrangeRed),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            if (pkg.ScancodeDetectedSpdx is { Count: > 0 })
                detail.Children.Add(new TextBlock
                {
                    Text = $"scancode detected: {string.Join(", ", pkg.ScancodeDetectedSpdx)}",
                    FontSize = 11, Opacity = 0.7,
                });
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

        var hasText = !string.IsNullOrEmpty(pkg.LicenseText);
        var box = new TextBox
        {
            Text = hasText
                ? pkg.LicenseText
                : $"No verbatim license text was bundled with this package on NuGet.\n\n" +
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
}
