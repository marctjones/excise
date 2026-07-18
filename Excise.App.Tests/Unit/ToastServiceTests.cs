using AwesomeAssertions;
using Excise.App.Services;
using System;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Tests for the ToastService notification system.
/// Verifies that error, warning, info, and success toasts are properly
/// emitted and contain the correct severity levels (Task Q3).
/// </summary>
public class ToastServiceTests
{
    [Fact]
    public void ToastService_ShowError_EmitsErrorEvent()
    {
        // Arrange
        var service = new ToastService();
        ToastService.ToastEventArgs? capturedEvent = null;
        service.ToastRequested += (s, e) => capturedEvent = e;

        // Act
        service.ShowError("Test Error", "This is a test error");

        // Assert
        capturedEvent.Should().NotBeNull();
        capturedEvent!.Message.Should().Be("Test Error");
        capturedEvent.Details.Should().Be("This is a test error");
        capturedEvent.Severity.Should().Be(ToastService.ToastSeverity.Error);
    }

    [Fact]
    public void ToastService_ShowWarning_EmitsWarningEvent()
    {
        // Arrange
        var service = new ToastService();
        ToastService.ToastEventArgs? capturedEvent = null;
        service.ToastRequested += (s, e) => capturedEvent = e;

        // Act
        service.ShowWarning("Test Warning");

        // Assert
        capturedEvent.Should().NotBeNull();
        capturedEvent!.Message.Should().Be("Test Warning");
        capturedEvent.Severity.Should().Be(ToastService.ToastSeverity.Warning);
    }

    [Fact]
    public void ToastService_ShowInfo_EmitsInfoEvent()
    {
        // Arrange
        var service = new ToastService();
        ToastService.ToastEventArgs? capturedEvent = null;
        service.ToastRequested += (s, e) => capturedEvent = e;

        // Act
        service.ShowInfo("Test Info", "Optional details");

        // Assert
        capturedEvent.Should().NotBeNull();
        capturedEvent!.Message.Should().Be("Test Info");
        capturedEvent.Details.Should().Be("Optional details");
        capturedEvent.Severity.Should().Be(ToastService.ToastSeverity.Informational);
    }

    [Fact]
    public void ToastService_ShowSuccess_EmitsSuccessEvent()
    {
        // Arrange
        var service = new ToastService();
        ToastService.ToastEventArgs? capturedEvent = null;
        service.ToastRequested += (s, e) => capturedEvent = e;

        // Act
        service.ShowSuccess("Success!", "Operation completed");

        // Assert
        capturedEvent.Should().NotBeNull();
        capturedEvent!.Message.Should().Be("Success!");
        capturedEvent.Details.Should().Be("Operation completed");
        capturedEvent.Severity.Should().Be(ToastService.ToastSeverity.Success);
    }

    [Fact]
    public void ToastService_ShowError_WithoutDetails_SetsDetailsToNull()
    {
        // Arrange
        var service = new ToastService();
        ToastService.ToastEventArgs? capturedEvent = null;
        service.ToastRequested += (s, e) => capturedEvent = e;

        // Act
        service.ShowError("Error Message");

        // Assert
        capturedEvent.Should().NotBeNull();
        capturedEvent!.Details.Should().BeNull();
    }

    [Fact]
    public void ToastService_MultipleSubscribers_AllReceiveEvent()
    {
        // Arrange
        var service = new ToastService();
        var eventCount = 0;
        service.ToastRequested += (s, e) => eventCount++;
        service.ToastRequested += (s, e) => eventCount++;
        service.ToastRequested += (s, e) => eventCount++;

        // Act
        service.ShowInfo("Test");

        // Assert
        eventCount.Should().Be(3);
    }

    [Fact]
    public void ToastService_NoSubscribers_DoesNotThrow()
    {
        // Arrange
        var service = new ToastService();

        // Act & Assert
        var act = () => service.ShowError("Test");
        act.Should().NotThrow();
    }

    [Fact]
    public void ToastEventArgs_StoresAllProperties()
    {
        // Arrange & Act
        var args = new ToastService.ToastEventArgs
        {
            Message = "Test Message",
            Details = "Test Details",
            Severity = ToastService.ToastSeverity.Error
        };

        // Assert
        args.Message.Should().Be("Test Message");
        args.Details.Should().Be("Test Details");
        args.Severity.Should().Be(ToastService.ToastSeverity.Error);
    }

    [Fact]
    public void ToastSeverity_HasAllExpectedValues()
    {
        // Arrange & Act & Assert
        ToastService.ToastSeverity.Informational.Should().Be(ToastService.ToastSeverity.Informational);
        ToastService.ToastSeverity.Warning.Should().Be(ToastService.ToastSeverity.Warning);
        ToastService.ToastSeverity.Error.Should().Be(ToastService.ToastSeverity.Error);
        ToastService.ToastSeverity.Success.Should().Be(ToastService.ToastSeverity.Success);
    }

    [Fact]
    public void ToastService_Unsubscribe_StopsReceivingEvents()
    {
        // Arrange
        var service = new ToastService();
        var eventCount = 0;
        EventHandler<ToastService.ToastEventArgs> handler = (s, e) => eventCount++;
        service.ToastRequested += handler;

        // Act
        service.ShowInfo("First");
        service.ToastRequested -= handler;
        service.ShowInfo("Second");

        // Assert
        eventCount.Should().Be(1);
    }
}
