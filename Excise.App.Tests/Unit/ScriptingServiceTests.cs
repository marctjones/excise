using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Unit tests for ScriptingService - Roslyn C# scripting functionality.
/// </summary>
public class ScriptingServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ValidScript_ReturnsSuccess()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act
        var result = await service.ExecuteAsync("return 42;");

        // Assert
        result.Success.Should().BeTrue();
        result.ReturnValue.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteAsync_ScriptWithError_ReturnsFailure()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act
        var result = await service.ExecuteAsync("invalid syntax {");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_AccessViewModel_CanReadProperties()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act - access IsDocumentLoaded from the ViewModel
        var result = await service.ExecuteAsync("return IsDocumentLoaded;");

        // Assert
        result.Success.Should().BeTrue();
        result.ReturnValue.Should().Be(false); // No document loaded
    }

    [Fact]
    public async Task ExecuteAsync_WithAsyncCode_WorksCorrectly()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act
        var result = await service.ExecuteAsync(@"
            await Task.Delay(10);
            return ""async completed"";
        ");

        // Assert
        result.Success.Should().BeTrue();
        result.ReturnValue.Should().Be("async completed");
    }

    [Fact]
    public void ValidateScript_ValidScript_ReturnsNoErrors()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act
        var errors = service.ValidateScript("return 42;");

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateScript_InvalidScript_ReturnsErrors()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act
        var errors = service.ValidateScript("invalid syntax {");

        // Assert
        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void ValidateScript_UndefinedVariable_ReturnsError()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act
        var errors = service.ValidateScript("return undefinedVariable;");

        // Assert
        errors.Should().NotBeEmpty();
        errors.Should().Contain(e => e.Contains("undefinedVariable"));
    }

    [Fact]
    public async Task ExecuteFileAsync_NonExistentFile_ReturnsError()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act
        var result = await service.ExecuteFileAsync("/nonexistent/path/script.csx");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeException_ReturnsError()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act
        var result = await service.ExecuteAsync(@"throw new System.Exception(""Test error"");");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Test error");
    }

    [Fact]
    public async Task ExecuteAsync_CanUseLinq_WithImportedNamespace()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act - use LINQ which should be available from imported namespaces
        var result = await service.ExecuteAsync(@"
            var numbers = new[] { 1, 2, 3, 4, 5 };
            return numbers.Where(n => n > 2).Sum();
        ");

        // Assert
        result.Success.Should().BeTrue();
        result.ReturnValue.Should().Be(12); // 3 + 4 + 5
    }

    [Fact]
    public async Task ExecuteAsync_NullReturn_HandlesGracefully()
    {
        // Arrange
        var viewModel = new MainWindowViewModel();
        var service = new ScriptingService(viewModel);

        // Act
        var result = await service.ExecuteAsync("return null;");

        // Assert
        result.Success.Should().BeTrue();
        result.ReturnValue.Should().BeNull();
    }

    [Fact]
    public void ScriptExecutionResult_ToString_FormatsCorrectly()
    {
        // Arrange & Act
        var success = ScriptExecutionResult.FromSuccess(42);
        var error = ScriptExecutionResult.FromError("Something went wrong");

        // Assert
        success.ToString().Should().Be("42");
        error.ToString().Should().Contain("Something went wrong");
    }
}
