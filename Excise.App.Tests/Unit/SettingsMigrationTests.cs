using System.IO;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Refs #1715: the one-time, versioned reset of a saved FixedMarker width (the old
/// default, stored as an explicit value by every save since 2026-09-27). Runs against
/// the redirected test config directory, never the real window.json.
/// </summary>
public class SettingsMigrationTests
{
    private static void Write(string json) => File.WriteAllText(AppPaths.WindowSettingsPath, json);

    [Fact]
    public void OldFileWithFixedMarker_IsResetOnce_AndStamped()
    {
        Write("""{"Redaction":{"Width":"FixedMarker","WholeWord":true}}""");

        var loaded = WindowSettings.Load();

        loaded.Redaction.Width.Should().Be(RedactionOptions.Default.Width);
        loaded.Redaction.WholeWord.Should().BeTrue("only the width is migrated");
        loaded.SettingsVersion.Should().Be(WindowSettings.CurrentSettingsVersion);
        File.ReadAllText(AppPaths.WindowSettingsPath).Should().Contain("\"SettingsVersion\": 1", "the migration is recorded on disk");
    }

    [Fact]
    public void OldFlatKeyFileWithFixedMarker_IsResetToo()
    {
        Write("""{"RedactionWidthPolicy":"FixedMarker"}""");

        WindowSettings.Load().Redaction.Width.Should().Be(RedactionOptions.Default.Width);
    }

    [Theory]
    [InlineData(WidthPolicy.CloseGap)]
    [InlineData(WidthPolicy.CollapsePreserveLayout)]
    [InlineData(WidthPolicy.OvershootPreserveLayout)]
    public void OldFileWithAnotherWidth_IsUntouched(WidthPolicy width)
    {
        Write($$$"""{"Redaction":{"Width":"{{{width}}}","Profile":"Maximum"}}""");

        var loaded = WindowSettings.Load();

        loaded.Redaction.Width.Should().Be(width);
        loaded.Redaction.Profile.Should().Be(RedactionProfile.Maximum);
    }

    [Fact]
    public void AlreadyMigratedFileWithFixedMarker_Stays()
    {
        Write("""{"SettingsVersion":1,"Redaction":{"Width":"FixedMarker"}}""");

        WindowSettings.Load().Redaction.Width.Should().Be(WidthPolicy.FixedMarker);
    }

    [Fact]
    public void FixedMarkerChosenAfterTheMigration_SurvivesSaveAndReload()
    {
        Write("""{"Redaction":{"Width":"FixedMarker"}}""");
        WindowSettings.Load().Redaction.Width.Should().Be(RedactionOptions.Default.Width);

        WindowSettings.Update(s => s.Redaction = s.Redaction with { Width = WidthPolicy.FixedMarker });

        WindowSettings.Load().Redaction.Width.Should().Be(WidthPolicy.FixedMarker);
        WindowSettings.Load().Redaction.Width.Should().Be(WidthPolicy.FixedMarker, "it never runs again");
    }

    [Fact]
    public void FixedMarkerChosenWithNoFileYet_SurvivesReload()
    {
        File.Exists(AppPaths.WindowSettingsPath).Should().BeFalse();

        WindowSettings.Update(s => s.Redaction = s.Redaction with { Width = WidthPolicy.FixedMarker });

        WindowSettings.Load().Redaction.Width.Should().Be(WidthPolicy.FixedMarker);
    }

    [Fact]
    public void MissingFile_GivesDefaults()
    {
        File.Exists(AppPaths.WindowSettingsPath).Should().BeFalse();

        WindowSettings.Load().Redaction.Should().Be(new RedactionPreferences());
    }

    [Fact]
    public void CorruptFile_GivesDefaults_AndDoesNotThrow()
    {
        Write("{ not json");

        WindowSettings.Load().Redaction.Should().Be(new RedactionPreferences());
    }
}
