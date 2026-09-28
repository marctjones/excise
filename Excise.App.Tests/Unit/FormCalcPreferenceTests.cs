using System;
using System.Text.Json;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// The Preferences switch for running a dynamic XFA form's FormCalc scripts (#1570): on by default,
/// saved to window.json, restored on launch, and handed to the library on the open path.
/// </summary>
public class FormCalcPreferenceTests
{
    [Fact]
    public void Default_IsOn_Everywhere()
    {
        new WindowSettings().RunFormCalc.Should().BeTrue();
        new PreferencesViewModel().RunFormCalc.Should().BeTrue();
        MainWindowViewModelTestFactory.Create().RunFormCalc.Should().BeTrue();
        new PdfDocumentService(NullLogger<PdfDocumentService>.Instance).XfaLayoutOptionsForOpen().RunFormCalc
            .Should().BeTrue();
    }

    [Fact]
    public void TurningItOff_ReachesTheOpenPath_AndSurvivesARestart()
    {
        var service = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        var vm = MainWindowViewModelTestFactory.Create(documentService: service);
        var preferences = new PreferencesViewModel();
        preferences.LoadFromMainViewModel(vm);
        preferences.RunFormCalc.Should().BeTrue();

        preferences.RunFormCalc = false;
        preferences.SaveToMainViewModel(vm);

        vm.RunFormCalc.Should().BeFalse();
        service.XfaLayoutOptionsForOpen().RunFormCalc.Should().BeFalse(
            "the next dynamic XFA form this session opens must be laid out without running its scripts");

        // Through window.json as the app writes and reads it.
        var settings = new WindowSettings();
        vm.WritePreferencesTo(settings);
        var json = JsonSerializer.Serialize(settings, ExciseJsonContext.Default.WindowSettings);
        var restored = JsonSerializer.Deserialize(json, ExciseJsonContext.Default.WindowSettings)!;
        restored.RunFormCalc.Should().BeFalse();

        var relaunched = MainWindowViewModelTestFactory.Create();
        relaunched.RunFormCalc = restored.RunFormCalc; // what MainWindow.ApplyPersistedPreferences does
        relaunched.RunFormCalc.Should().BeFalse();
    }

    [Fact]
    public void AWindowJsonWithoutTheKey_KeepsItOn()
    {
        var settings = JsonSerializer.Deserialize("{}", ExciseJsonContext.Default.WindowSettings)!;
        settings.RunFormCalc.Should().BeTrue("a window.json from before this setting existed must not turn scripts off");
    }

    [Fact]
    public void ResetToDefaults_TurnsItBackOn()
    {
        var preferences = new PreferencesViewModel { RunFormCalc = false };
        preferences.ResetToDefaultsCommand.Execute().Subscribe();
        preferences.RunFormCalc.Should().BeTrue();
    }
}
