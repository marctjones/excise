using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Tests for window and document state persistence.
/// Verifies that window position/size and per-document zoom/page state
/// are correctly saved and restored (Issue #23).
/// </summary>
public class StatePersistenceTests
{
    [Fact]
    public void WindowSettings_Load_DropsDocumentStatesPointingAtMissingFiles()
    {
        // Reproduce the v2.1.0-rc4 manual-test bug: a saved DocumentState
        // pointing at /tmp/Excise.AppGoldenPath/.../foo.pdf would survive
        // across launches even after the file was deleted, then get
        // resurfaced by the next restore attempt. Load() must filter.
        var settings = new WindowSettings();
        settings.DocumentStates.Add(new WindowSettings.DocumentState
        {
            FilePath = "/tmp/this-path-definitely-does-not-exist-" + System.Guid.NewGuid().ToString("N") + ".pdf",
            ZoomLevel = 1.0,
            LastPageIndex = 0
        });

        // Save it, then reload — the reload should drop the missing entry.
        settings.Save();
        var reloaded = WindowSettings.Load();

        reloaded.DocumentStates.Should().BeEmpty(
            "Load() must filter out DocumentStates whose file no longer exists");
    }

    [Fact]
    public void WindowSettings_Default_HasExpectedInitialValues()
    {
        // Arrange — construct a fresh WindowSettings (defaults inline-initialized).
        // We don't call Load() here because Load() may return a previously-saved
        // user file from disk; the goal of this test is to lock in the default
        // values, not exercise the disk path.
        var settings = new WindowSettings();

        // Act & Assert
        settings.Should().NotBeNull();
        settings.Width.Should().Be(1200);
        settings.Height.Should().Be(800);
        settings.IsMaximized.Should().BeFalse();
        settings.ContinuousScrollEnabled.Should().BeTrue();
    }

    [Fact]
    public void WindowSettings_Save_WritesTheFileAndLoadReadsItBack()
    {
        var settings = new WindowSettings
        {
            X = 100,
            Y = 200,
            Width = 1400,
            Height = 900,
            IsMaximized = true
        };

        settings.Save();

        File.Exists(AppPaths.WindowSettingsPath).Should().BeTrue(
            "Save() swallows every exception, so a missing file is the only sign it failed");
        var reloaded = WindowSettings.Load();
        reloaded.X.Should().Be(100);
        reloaded.Y.Should().Be(200);
        reloaded.Width.Should().Be(1400);
        reloaded.Height.Should().Be(900);
        reloaded.IsMaximized.Should().BeTrue();
    }

    [Fact]
    public void WindowSettings_GetOrCreateDocumentState_CreatesNew()
    {
        // Arrange
        var settings = new WindowSettings();
        var filePath = "/tmp/document.pdf";

        // Act
        var state = settings.GetOrCreateDocumentState(filePath);

        // Assert
        state.Should().NotBeNull();
        state.FilePath.Should().Be(filePath);
        state.ZoomLevel.Should().Be(1.0);
        state.LastPageIndex.Should().Be(0);
        settings.DocumentStates.Should().Contain(state);
    }

    [Fact]
    public void WindowSettings_GetOrCreateDocumentState_ReturnsExisting()
    {
        // Arrange
        var settings = new WindowSettings();
        var filePath = "/tmp/document.pdf";
        var state1 = settings.GetOrCreateDocumentState(filePath);
        state1.ZoomLevel = 2.0;
        state1.LastPageIndex = 10;

        // Act
        var state2 = settings.GetOrCreateDocumentState(filePath);

        // Assert
        state2.Should().BeSameAs(state1);
        state2.ZoomLevel.Should().Be(2.0);
        state2.LastPageIndex.Should().Be(10);
        settings.DocumentStates.Should().HaveCount(1);
    }

    [Fact]
    public void WindowSettings_UpdateDocumentState_UpdatesExisting()
    {
        // Arrange
        var settings = new WindowSettings();
        var filePath = "/tmp/document.pdf";
        settings.GetOrCreateDocumentState(filePath);

        // Act
        settings.UpdateDocumentState(filePath, 1.75, 15);

        // Assert
        var state = settings.DocumentStates[0];
        state.ZoomLevel.Should().Be(1.75);
        state.LastPageIndex.Should().Be(15);
    }

    [Fact]
    public void WindowSettings_TrimToMaxDocuments_KeepsOnly50()
    {
        // Arrange
        var settings = new WindowSettings();
        for (int i = 0; i < 60; i++)
        {
            var state = settings.GetOrCreateDocumentState($"/tmp/doc{i}.pdf");
            System.Threading.Thread.Sleep(1); // Ensure different timestamps
        }

        // Act - the GetOrCreateDocumentState already trims
        // Assert
        settings.DocumentStates.Should().HaveCount(50);
    }

    [Fact]
    public void WindowSettings_SerializeDeserialize_RoundTrip()
    {
        // Arrange
        var settings = new WindowSettings
        {
            X = 50,
            Y = 75,
            Width = 1500,
            Height = 950,
            IsMaximized = true,
            ContinuousScrollEnabled = false
        };
        settings.UpdateDocumentState("/tmp/doc1.pdf", 1.5, 10);
        settings.UpdateDocumentState("/tmp/doc2.pdf", 2.0, 20);

        // Act
        var json = JsonSerializer.Serialize(settings, ExciseJsonContext.Default.WindowSettings);
        var restored = JsonSerializer.Deserialize(json, ExciseJsonContext.Default.WindowSettings);

        // Assert
        restored.Should().NotBeNull();
        restored!.X.Should().Be(50);
        restored.Y.Should().Be(75);
        restored.Width.Should().Be(1500);
        restored.Height.Should().Be(950);
        restored.IsMaximized.Should().BeTrue();
        restored.ContinuousScrollEnabled.Should().BeFalse();
        restored.DocumentStates.Should().HaveCount(2);
        restored.DocumentStates[0].FilePath.Should().Be("/tmp/doc1.pdf");
        restored.DocumentStates[0].ZoomLevel.Should().Be(1.5);
        restored.DocumentStates[0].LastPageIndex.Should().Be(10);
    }

    [Fact]
    public void WindowSettings_DeserializeOldConfig_DefaultsContinuousScrollOn()
    {
        var restored = JsonSerializer.Deserialize("{}", ExciseJsonContext.Default.WindowSettings);

        restored.Should().NotBeNull();
        restored!.ContinuousScrollEnabled.Should().BeTrue();
    }

    [Fact]
    public void WindowSettings_DocumentState_UpdatesLastAccessedOnGet()
    {
        // Arrange
        var settings = new WindowSettings();
        var filePath = "/tmp/doc.pdf";
        var state1 = settings.GetOrCreateDocumentState(filePath);
        var oldTime = state1.LastAccessed;
        System.Threading.Thread.Sleep(10);

        // Act
        var state2 = settings.GetOrCreateDocumentState(filePath);

        // Assert
        state2.LastAccessed.Should().BeAfter(oldTime);
    }

}
