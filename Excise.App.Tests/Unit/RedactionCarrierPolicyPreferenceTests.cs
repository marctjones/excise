using System;
using System.IO;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
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
        vm.RedactionPreferences.LinkUriPolicy.Should().Be(CarrierScrubMode.Strip,
            "#1187 requires defaults to reproduce the pre-option behaviour; " +
            "flipping the default to RemoveWhole is a product decision, not a side effect");
        vm.RedactionPreferences.MetadataPolicy.Should().Be(CarrierScrubMode.Strip);
    }

    [Fact]
    public void EveryDefault_IsThePreOptionBehaviour()
    {
        // Pinned per field: the defaults moved from six string fields, three view-model
        // properties and the dialog into one record, and none of them may have drifted.
        var defaults = new WindowSettings().Redaction;

        defaults.Should().Be(new RedactionPreferences());
        defaults.WholeWord.Should().BeFalse("#1000 kept substring as the default");
        defaults.KeepAttachments.Should().BeFalse("#1572: a redacted copy carries no attachments");
        defaults.Width.Should().Be(WidthPolicy.CollapsePreserveLayout);
        defaults.Profile.Should().Be(RedactionProfile.Standard);
        defaults.LinkUriPolicy.Should().Be(CarrierScrubMode.Strip);
        defaults.MetadataPolicy.Should().Be(CarrierScrubMode.Strip);
        MainWindowViewModelTestFactory.Create().RedactionPreferences.Should().Be(defaults);
        new PreferencesViewModel().RedactionPreferences.Should().Be(defaults);
    }

    [Fact]
    public void ResetToDefaults_RestoresStrip()
    {
        var vm = new PreferencesViewModel
        {
            RedactionPreferences = new RedactionPreferences
            {
                LinkUriPolicy = CarrierScrubMode.ReportOnly,
                MetadataPolicy = CarrierScrubMode.RemoveWhole,
            },
        };

        vm.ResetToDefaultsCommand.Execute().Subscribe();

        vm.RedactionPreferences.Should().Be(new RedactionPreferences());
    }

    [Fact]
    public void ToOptions_CarriesTheUserChoiceToTheEngine()
    {
        // The wiring test that matters: a preference that never reaches
        // PdfDocumentSanitizer is a setting the user believes in and does not
        // have.
        var main = MainWindowViewModelTestFactory.Create();
        main.RedactionPreferences = new RedactionPreferences
        {
            LinkUriPolicy = CarrierScrubMode.RemoveWhole,
            MetadataPolicy = CarrierScrubMode.ReportOnly,
        };

        var options = main.RedactionPreferences.ToOptions();

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
    public void ToOptions_UntouchedPreferences_AreTheDefaultPolicy()
    {
        var main = MainWindowViewModelTestFactory.Create();

        main.RedactionPreferences.ToOptions().CarrierPolicy
            .Should().Be(CarrierScrubPolicy.Default,
                "an unconfigured app redacts exactly as it did before #1188/#1169");
    }

    [Fact]
    public void ToOptions_MaximumProfile_CarriesTheMaximumFlagsThrough()
    {
        // Rule 6 (CLAUDE.md): the engine reads the option FLAGS, never Profile.
        // A preference that rebuilt the policy from CarrierScrubPolicy.Default
        // would leave Profile = Maximum and every flag off, and report Maximum.
        var options = new RedactionPreferences { Profile = RedactionProfile.Maximum }.ToOptions();

        options.RemoveBookmarks.Should().BeTrue();
        options.RemoveLinkAnnotations.Should().BeTrue();
        options.RemoveMarkupAnnotations.Should().BeTrue();
        options.RemoveFieldNames.Should().BeTrue();
        options.FlattenInteractiveContent.Should().BeTrue();
        options.CarrierPolicy.ModeFor(RedactionCarriers.Outlines).Should().Be(CarrierScrubMode.RemoveWhole);
        options.CarrierPolicy.ModeFor(RedactionCarriers.ActionUris).Should().Be(CarrierScrubMode.RemoveWhole,
            "link targets and metadata default to Strip, which must not undo Maximum's RemoveWhole");

        var standard = new RedactionPreferences().ToOptions();
        standard.RemoveBookmarks.Should().BeFalse();
        standard.FlattenInteractiveContent.Should().BeFalse();
        standard.CarrierPolicy.ModeFor(RedactionCarriers.Outlines).Should().Be(CarrierScrubMode.Strip);
    }

    [Fact]
    public void ToOptions_MaximumProfile_AUserMovedCarrierPolicyStillWins()
    {
        var options = new RedactionPreferences
        {
            Profile = RedactionProfile.Maximum,
            LinkUriPolicy = CarrierScrubMode.ReportOnly,
        }.ToOptions();

        options.CarrierPolicy.ModeFor(RedactionCarriers.ActionUris).Should().Be(CarrierScrubMode.ReportOnly);
        options.CarrierPolicy.ModeFor(RedactionCarriers.Outlines).Should().Be(CarrierScrubMode.RemoveWhole,
            "moving one carrier off Strip leaves the rest of the profile alone");
        options.RemoveBookmarks.Should().BeTrue();
    }

    [Fact]
    public void WholeWord_DefaultsOff_AndRoundTripsThroughPreferences()
    {
        // #1052: the toggle exists in the GUI, defaults to the #1000 substring
        // behaviour, and reaches the engine's carrier scrub.
        var main = MainWindowViewModelTestFactory.Create();
        main.RedactionPreferences.ToOptions().WholeWord.Should().BeFalse();

        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(main);
        prefs.RedactionPreferences.WholeWord.Should().BeFalse();

        prefs.RedactionPreferences.WholeWord = true;
        prefs.SaveToMainViewModel(main);

        main.RedactionPreferences.ToOptions().WholeWord.Should().BeTrue(
            "a toggle that does not reach PdfDocumentSanitizer is a setting the " +
            "user believes in and does not have");
    }

    [Fact]
    public void KeepAttachments_RoundTripsThroughPreferencesAndRestart()
    {
        // #1572, decided 2026-09-17: a redacted copy carries no attachments
        // unless the user keeps them, and the choice reaches the engine.
        var main = MainWindowViewModelTestFactory.Create();
        main.RedactionPreferences.ToOptions().KeepAttachments.Should().BeFalse();

        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(main);
        prefs.RedactionPreferences.KeepAttachments = true;
        prefs.SaveToMainViewModel(main);

        main.RedactionPreferences.ToOptions().KeepAttachments.Should().BeTrue(
            "keeping attachments must turn the safe-copy strip off, or the toggle does nothing");

        var settings = new WindowSettings();
        main.WritePreferencesTo(settings);
        settings.Redaction.KeepAttachments.Should().BeTrue();

        prefs.ResetToDefaultsCommand.Execute().Subscribe();
        prefs.RedactionPreferences.KeepAttachments.Should().BeFalse();
    }

    [Fact]
    public void TheDialogEditsItsOwnCopy_UntilSave()
    {
        // The record is settable so the controls can bind into it; the dialog must
        // never edit the instance the main view model (and every other window) holds.
        var main = MainWindowViewModelTestFactory.Create();
        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(main);

        prefs.RedactionPreferences.Profile = RedactionProfile.Maximum;

        main.RedactionPreferences.Profile.Should().Be(RedactionProfile.Standard, "Cancel must leave it alone");
        prefs.SaveToMainViewModel(main);
        main.RedactionPreferences.Profile.Should().Be(RedactionProfile.Maximum);
        main.RedactionPreferences.Should().NotBeSameAs(prefs.RedactionPreferences);
    }

    [Fact]
    public void WidthPolicy_DefaultsToCollapse_AndRoundTripsThroughPreferences()
    {
        // #1189. The default keeps today's behaviour: an exact-width box that
        // does not reflow the page — and that IS the ruler #1140 recorded, so
        // changing the default is a product decision, not a side effect.
        //
        // #1755 added WidthPolicy.FixedMarker as an available OPTION (it
        // closes the #1715 width channel and always draws a visible mark,
        // #1725) but deliberately NOT as the default yet: measured (mutool
        // -F stext, real glyph positions) to visually overlap the reflowed
        // neighbouring text in the COMMON case, not merely when a line has
        // little slack — see the remark on RedactionOptions.Width.
        var main = MainWindowViewModelTestFactory.Create();
        main.RedactionPreferences.Width.Should().Be(WidthPolicy.CollapsePreserveLayout);

        var prefs = new PreferencesViewModel();
        prefs.WidthPolicyOptions.Should().BeEquivalentTo(new[]
        {
            WidthPolicy.CollapsePreserveLayout,
            WidthPolicy.CloseGap,
            WidthPolicy.OvershootPreserveLayout,
            WidthPolicy.FixedMarker,
        });

        prefs.LoadFromMainViewModel(main);
        prefs.RedactionPreferences.Width = WidthPolicy.OvershootPreserveLayout;
        prefs.SaveToMainViewModel(main);

        main.RedactionPreferences.Width.Should().Be(WidthPolicy.OvershootPreserveLayout);
    }

    [Fact]
    public void RedactionPreferences_SurviveARestart_AndAnOldWindowJsonKeepsItsChoices()
    {
        // A SECURITY preference that silently resets to the less-safe default on
        // every launch is worse than no preference at all. window.json goes
        // through the isolated AppPaths override (ResetPersistedSettingsBeforeEachTest).
        var chosen = new RedactionPreferences
        {
            WholeWord = true,
            KeepAttachments = true,
            Width = WidthPolicy.FixedMarker,
            Profile = RedactionProfile.Maximum,
            LinkUriPolicy = CarrierScrubMode.RemoveWhole,
            MetadataPolicy = CarrierScrubMode.ReportOnly,
        };

        // New format: the nested object round-trips, enums by name.
        new WindowSettings { Redaction = chosen }.Save();
        File.ReadAllText(AppPaths.WindowSettingsPath).Should().Contain("\"Profile\": \"Maximum\"");
        WindowSettings.Load().Redaction.Should().Be(chosen);

        // Old format, as written by the app before #1840: six flat top-level keys.
        File.WriteAllText(AppPaths.WindowSettingsPath, """
            { "Width": 900,
              "RedactionWholeWord": true, "RedactionKeepAttachments": true,
              "RedactionWidthPolicy": "FixedMarker", "RedactionProfile": "Maximum",
              "LinkUriCarrierPolicy": "RemoveWhole", "MetadataCarrierPolicy": "ReportOnly" }
            """);
        var upgraded = WindowSettings.Load();
        upgraded.Width.Should().Be(900, "fixture: the file was read");
        upgraded.Redaction.Should().Be(chosen);

        // The restarted window applies what was loaded.
        var restarted = MainWindowViewModelTestFactory.Create();
        restarted.RedactionPreferences = upgraded.Redaction;
        restarted.RedactionPreferences.ToOptions().KeepAttachments.Should().BeTrue();
    }

    [Fact]
    public void AnOldWindowJsonWithAnUnrecognisedValue_KeepsThatFieldsDefault_NotAnotherPolicy()
    {
        File.WriteAllText(AppPaths.WindowSettingsPath, """
            { "RedactionProfile": "Nonsense", "RedactionWidthPolicy": 7,
              "LinkUriCarrierPolicy": "RemoveWhole", "RedactionWholeWord": "yes" }
            """);

        var loaded = WindowSettings.Load().Redaction;

        loaded.Profile.Should().Be(RedactionProfile.Standard);
        loaded.Width.Should().Be(WidthPolicy.CollapsePreserveLayout);
        loaded.MetadataPolicy.Should().Be(CarrierScrubMode.Strip, "absent");
        loaded.WholeWord.Should().BeFalse();
        loaded.LinkUriPolicy.Should().Be(CarrierScrubMode.RemoveWhole, "the valid field survives its neighbours");
    }

    [Fact]
    public void AWindowJsonWithoutAnyRedactionKeys_LoadsTheDefaults()
    {
        File.WriteAllText(AppPaths.WindowSettingsPath, """{ "Width": 1000 }""");

        WindowSettings.Load().Redaction.Should().Be(new RedactionPreferences());
    }

    [Fact]
    public void PreferencesRoundTrip_PreservesBothChoices()
    {
        var main = MainWindowViewModelTestFactory.Create();
        main.RedactionPreferences = new RedactionPreferences
        {
            LinkUriPolicy = CarrierScrubMode.ReportOnly,
            MetadataPolicy = CarrierScrubMode.RemoveWhole,
        };

        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(main);
        prefs.RedactionPreferences.LinkUriPolicy.Should().Be(CarrierScrubMode.ReportOnly);
        prefs.RedactionPreferences.MetadataPolicy.Should().Be(CarrierScrubMode.RemoveWhole);

        prefs.RedactionPreferences.LinkUriPolicy = CarrierScrubMode.RemoveWhole;
        prefs.SaveToMainViewModel(main);
        main.RedactionPreferences.LinkUriPolicy.Should().Be(CarrierScrubMode.RemoveWhole);
        main.RedactionPreferences.MetadataPolicy.Should().Be(CarrierScrubMode.RemoveWhole);
    }
}
