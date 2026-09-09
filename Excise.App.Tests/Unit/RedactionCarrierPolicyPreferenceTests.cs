using System;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.Core.Operations;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1169 — the GUI surface for per-carrier policy on non-displayed text.
///
/// <para>The security point, restated because it is easy to read this as a
/// formatting preference: substring-stripping the redacted term out of a KNOWN
/// string can hand the term back. <c>https://www.irs.gov/your-account</c> minus
/// <c>your</c> is <c>https://www.irs.gov/-account</c>, and anyone who knows the
/// site reads the missing word straight off the residue. So the user gets a
/// choice per carrier, and the choice has to reach the engine — a preference
/// that does not change what the redaction does is worse than none.</para>
/// </summary>
public class RedactionCarrierPolicyPreferenceTests
{
    [Fact]
    public void Preferences_OfferEveryMode_AndDefaultToTheUnchangedBehaviour()
    {
        var vm = new PreferencesViewModel();

        vm.CarrierScrubModeOptions.Should().BeEquivalentTo(new[]
        {
            CarrierScrubMode.Strip,
            CarrierScrubMode.RemoveWhole,
            CarrierScrubMode.ReportOnly,
        });
        vm.SelectedLinkUriCarrierPolicy.Should().Be(CarrierScrubMode.Strip,
            "#1187 requires defaults to reproduce the pre-option behaviour; " +
            "flipping the default to RemoveWhole is a product decision, not a side effect");
        vm.SelectedMetadataCarrierPolicy.Should().Be(CarrierScrubMode.Strip);
    }

    [Fact]
    public void ResetToDefaults_RestoresStrip()
    {
        var vm = new PreferencesViewModel
        {
            SelectedLinkUriCarrierPolicy = CarrierScrubMode.ReportOnly,
            SelectedMetadataCarrierPolicy = CarrierScrubMode.RemoveWhole,
        };

        vm.ResetToDefaultsCommand.Execute().Subscribe();

        vm.SelectedLinkUriCarrierPolicy.Should().Be(CarrierScrubMode.Strip);
        vm.SelectedMetadataCarrierPolicy.Should().Be(CarrierScrubMode.Strip);
    }

    [Fact]
    public void BuildRedactedCopySafetyOptions_CarriesTheUserChoiceToTheEngine()
    {
        // The wiring test that matters: a preference that never reaches
        // PdfDocumentSanitizer is a setting the user believes in and does not
        // have.
        var main = MainWindowViewModelTestFactory.Create();
        main.LinkUriCarrierPolicy = CarrierScrubMode.RemoveWhole;
        main.MetadataCarrierPolicy = CarrierScrubMode.ReportOnly;

        var options = main.BuildRedactedCopySafetyOptions();

        options.CarrierPolicy.ModeFor(RedactionCarriers.ActionUris)
            .Should().Be(CarrierScrubMode.RemoveWhole);
        options.CarrierPolicy.ModeFor(RedactionCarriers.Info)
            .Should().Be(CarrierScrubMode.ReportOnly);
        options.CarrierPolicy.ModeFor(RedactionCarriers.Xmp)
            .Should().Be(CarrierScrubMode.ReportOnly,
                "'document metadata' covers /Info and the XMP packet together");
        options.CarrierPolicy.ModeFor(RedactionCarriers.Outlines)
            .Should().Be(CarrierScrubMode.Strip,
                "carriers the user did not choose keep the default");
    }

    [Fact]
    public void BuildRedactedCopySafetyOptions_UntouchedPreferences_AreTheDefaultPolicy()
    {
        var main = MainWindowViewModelTestFactory.Create();

        main.BuildRedactedCopySafetyOptions().CarrierPolicy
            .Should().Be(CarrierScrubPolicy.Default,
                "an unconfigured app redacts exactly as it did before #1188/#1169");
    }

    [Fact]
    public void PreferencesRoundTrip_PreservesBothChoices()
    {
        var main = MainWindowViewModelTestFactory.Create();
        main.LinkUriCarrierPolicy = CarrierScrubMode.ReportOnly;
        main.MetadataCarrierPolicy = CarrierScrubMode.RemoveWhole;

        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(main);
        prefs.SelectedLinkUriCarrierPolicy.Should().Be(CarrierScrubMode.ReportOnly);
        prefs.SelectedMetadataCarrierPolicy.Should().Be(CarrierScrubMode.RemoveWhole);

        prefs.SelectedLinkUriCarrierPolicy = CarrierScrubMode.RemoveWhole;
        prefs.SaveToMainViewModel(main);
        main.LinkUriCarrierPolicy.Should().Be(CarrierScrubMode.RemoveWhole);
        main.MetadataCarrierPolicy.Should().Be(CarrierScrubMode.RemoveWhole);
    }
}
