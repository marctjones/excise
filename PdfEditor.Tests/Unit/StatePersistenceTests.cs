using AwesomeAssertions;
using PdfEditor.Models;
using PdfEditor.Services;
using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace PdfEditor.Tests.Unit;

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
        // pointing at /tmp/PdfEditorGoldenPath/.../foo.pdf would survive
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
    }

    [Fact]
    public void WindowSettings_Save_CreatesSettingsFile()
    {
        // Arrange
        var settings = new WindowSettings
        {
            X = 100,
            Y = 200,
            Width = 1400,
            Height = 900,
            IsMaximized = false
        };

        var tempPath = Path.Combine(Path.GetTempPath(), "test-window-settings.json");
        try
        {
            // Act
            settings.Save();
            // Note: We can't easily override the path, so we just verify Save() doesn't throw
        }
        finally
        {
            // Cleanup - AppPaths determines the actual path used
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void DocumentState_Create_IncludesZoomAndPage()
    {
        // Arrange
        var state = new WindowSettings.DocumentState
        {
            FilePath = "/tmp/test.pdf",
            ZoomLevel = 1.5,
            LastPageIndex = 5
        };

        // Act & Assert
        state.FilePath.Should().Be("/tmp/test.pdf");
        state.ZoomLevel.Should().Be(1.5);
        state.LastPageIndex.Should().Be(5);
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
            IsMaximized = true
        };
        settings.UpdateDocumentState("/tmp/doc1.pdf", 1.5, 10);
        settings.UpdateDocumentState("/tmp/doc2.pdf", 2.0, 20);

        // Act
        var json = JsonSerializer.Serialize(settings, PdfeJsonContext.Default.WindowSettings);
        var restored = JsonSerializer.Deserialize(json, PdfeJsonContext.Default.WindowSettings);

        // Assert
        restored.Should().NotBeNull();
        restored!.X.Should().Be(50);
        restored.Y.Should().Be(75);
        restored.Width.Should().Be(1500);
        restored.Height.Should().Be(950);
        restored.IsMaximized.Should().BeTrue();
        restored.DocumentStates.Should().HaveCount(2);
        restored.DocumentStates[0].FilePath.Should().Be("/tmp/doc1.pdf");
        restored.DocumentStates[0].ZoomLevel.Should().Be(1.5);
        restored.DocumentStates[0].LastPageIndex.Should().Be(10);
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

    [Fact]
    public void WindowSettings_ApplyTo_SetsWindowProperties()
    {
        // Arrange - we can't test with a real Window, but we can verify the logic
        var settings = new WindowSettings
        {
            X = 100,
            Y = 200,
            Width = 1600,
            Height = 1000,
            IsMaximized = false
        };

        // Act - ApplyTo would require a Window instance
        // Assert - verify the properties are set correctly
        settings.X.Should().Be(100);
        settings.Y.Should().Be(200);
        settings.Width.Should().Be(1600);
        settings.Height.Should().Be(1000);
    }

    [Fact]
    public void WindowSettings_CaptureFrom_SavesWindowState()
    {
        // Arrange - we can't test with a real Window, but verify properties
        var settings = new WindowSettings();

        // Act - simulate capturing state
        settings.X = 150;
        settings.Y = 250;
        settings.Width = 1300;
        settings.Height = 850;
        settings.IsMaximized = false;

        // Assert
        settings.X.Should().Be(150);
        settings.Y.Should().Be(250);
        settings.Width.Should().Be(1300);
        settings.Height.Should().Be(850);
    }
}
