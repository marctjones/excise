using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Verifies the About dialog wires up: the embedded
/// third-party-licenses.json manifest is found, parses, and surfaces at
/// least one package per license type we expect to ship.
/// </summary>
[Collection("AvaloniaTests")]
public class AboutWindowTests
{
    [Fact]
    public void Manifest_LoadsFromEmbeddedResource()
    {
        var vm = new AboutWindowViewModel();
        vm.Packages.Should().NotBeEmpty(
            "if the manifest is missing or fails to deserialize the About dialog has nothing to show");

        // Sanity: every entry has at least an Id + Version.
        vm.Packages.Should().OnlyContain(p =>
            !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.Version));
    }

    [Fact]
    public void Manifest_IncludesAvaloniaAndSkiaSharp()
    {
        var vm = new AboutWindowViewModel();
        var ids = vm.Packages.Select(p => p.Id).ToHashSet();
        ids.Should().Contain("Avalonia");
        ids.Should().Contain("SkiaSharp");
    }

    [Fact]
    public void Manifest_Jj2000Terms_AreNotClassifiedAsUnconditionalBsd()
    {
        // #1914: a generic package BSD link must not silently approve the
        // additional JJ2000 terms. This verifies attribution, not legal consent.
        var package = new AboutWindowViewModel().Packages.Single(p => p.Id == "CSJ2K");
        package.Spdx.Should().Be("LicenseRef-CSJ2K-JJ2000");
        package.LicenseName.Should().Contain("restricted-use");
        package.LicenseSpdxUrl.Should().Be(
            "https://github.com/cureos/csj2k/blob/master/COPYRIGHT-JJ2000-5.1");

        package.LicenseText.Should().NotBeNullOrWhiteSpace();
        const string marker = "COPYRIGHT:";
        var text = package.LicenseText!;
        text.Should().Contain(marker);
        var body = text[(text.IndexOf(marker, System.StringComparison.Ordinal) + marker.Length)..];
        var normalized = Regex.Replace(body, @"\s+", " ").Trim();
        // Whitespace-normalized upstream blob 61d89e7399179be27e682111bf9870656c78f3d3.
        // Pin the complete body, not just the presence of a restriction sentence.
        var hash = System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        hash.Should().Be("5a073c6ba83e6bc40ce9beb8dba7ffb7bcdc434d2dba89667bab72c15f58e7ff");
    }

    [Fact]
    public void Manifest_AppVersion_IsParseable()
    {
        var vm = new AboutWindowViewModel();
        vm.AppVersion.Should().NotBeNullOrWhiteSpace();
    }

    [FixedAvaloniaFact]
    public void AboutWindow_OpensAndPaintsDetailPane()
    {
        var window = new AboutWindow();
        window.Show();

        var list = window.FindControl<ListBox>("PackagesList");
        list.Should().NotBeNull("master list must exist");
        list!.ItemCount.Should().BeGreaterThan(10,
            "the manifest should have many packages — if this is small the embedded resource probably isn't loading");

        // Picking the first item should populate the detail pane.
        list.SelectedIndex = 0;
        var detail = window.FindControl<StackPanel>("DetailPanel");
        detail.Should().NotBeNull();
        detail!.Children.Count.Should().BeGreaterThan(1,
            "selecting a package must repaint the detail pane (header + license text at minimum)");

        // Compliance needs the verbatim license TEXT reachable in the dialog,
        // not merely present in the ViewModel. Select a package that bundles
        // license text and assert the detail pane surfaces it in a (copyable)
        // TextBox.
        var vm = (AboutWindowViewModel)window.DataContext!;
        var withText = vm.Packages.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.LicenseText));
        withText.Should().NotBeNull(
            "at least one shipped package must carry embedded verbatim license text");

        list.SelectedItem = withText;
        var licenseBox = detail.Children.OfType<TextBox>().LastOrDefault();
        licenseBox.Should().NotBeNull("the detail pane must render the license text in a TextBox");
        licenseBox!.Text.Should().Be(withText!.LicenseText,
            "the rendered license text must be the package's verbatim notice");
        licenseBox.Text!.Length.Should().BeGreaterThan(100,
            "a bundled license notice is substantial text, not a stub");

        window.Close();
    }
}
